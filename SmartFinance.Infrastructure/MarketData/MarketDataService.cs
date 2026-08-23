using Microsoft.Extensions.Caching.Memory;
using SmartFinance.Application.DTOs.MarketData;
using SmartFinance.Application.Exceptions;
using SmartFinance.Application.Interfaces;

namespace SmartFinance.Infrastructure.MarketData;

public class MarketDataService : IMarketDataService
{
    private readonly IEnumerable<IPriceProvider> _providers;
    private readonly IMemoryCache _cache;

    public MarketDataService(IEnumerable<IPriceProvider> providers, IMemoryCache cache)
    {
        _providers = providers;
        _cache = cache;
    }

    private IPriceProvider ResolveProvider(string investmentType)
    {
        var provider = _providers.FirstOrDefault(p =>
            p.SupportedInvestmentTypes.Contains(investmentType, StringComparer.OrdinalIgnoreCase));

        if (provider == null)
            throw new ExternalServiceException($"'{investmentType}' yatırım tipi için tanımlı bir fiyat sağlayıcısı yok.");

        return provider;
    }

    // Dis servislere (Yahoo, TEFAS, TCMB, CoinGecko) yapilan her istek hem yavas
    // hem de saglayicinin hiz sinirina takilma riski tasiyor — TEFAS ozelinde tek
    // istek 90 saniyeye kadar surebiliyor. Ayni sembol/aralik icin gelen tekrar
    // istekleri onbellekten karsilaniyor.
    private static TimeSpan CurrentPriceTtl => TimeSpan.FromMinutes(5);

    private static TimeSpan HistoryTtl(string investmentType, string range) =>
        range == "1d" ? TimeSpan.FromMinutes(5)              // gun-ici veri canli akiyor, kisa tutulur
        : IsFund(investmentType) ? TimeSpan.FromHours(6)     // TEFAS NAV'i gunde bir yayinlar, istek cok pahali
        : TimeSpan.FromMinutes(30);                          // gunluk barlarda 30 dk bayatlik grafikte fark etmez

    private static TimeSpan StatisticsTtl => TimeSpan.FromMinutes(30);

    private static bool IsFund(string investmentType) =>
        string.Equals(investmentType, "fund", StringComparison.OrdinalIgnoreCase);

    /// Anahtarlara gunun tarihi de giriyor: aralik hesabi DateTime.Today'e
    /// dayandigi icin gece yarisini gecen bir kayit bayat aralik dondururdu.
    private static string PriceKey(string symbol, string investmentType) =>
        $"price:{investmentType.ToLowerInvariant()}:{symbol.ToUpperInvariant()}";

    private static string HistoryKey(string symbol, string investmentType, string range, DateTime to) =>
        $"history:{investmentType.ToLowerInvariant()}:{symbol.ToUpperInvariant()}:{range}:{to:yyyy-MM-dd}";

    private static string StatisticsKey(string symbol) =>
        $"stats:{symbol.ToUpperInvariant()}";

    public async Task<PriceQuoteDto> GetCurrentPriceAsync(string symbol, string investmentType, CancellationToken ct = default)
    {
        var key = PriceKey(symbol, investmentType);
        if (_cache.TryGetValue(key, out PriceQuoteDto? cached) && cached != null)
            return cached;

        var quote = await ResolveProvider(investmentType).GetCurrentPriceAsync(symbol, investmentType, ct);
        _cache.Set(key, quote, CurrentPriceTtl);
        return quote;
    }

    public async Task<TechnicalAnalysisDto> GetTechnicalAnalysisAsync(
        string symbol, string investmentType, string range, IEnumerable<string> indicatorKeys, CancellationToken ct = default)
    {
        var provider = ResolveProvider(investmentType);
        var to = DateTime.Today;
        // "1d" -> from==to, saglayicilar bunu gun-ici (saatlik) istek sinyali olarak kullanir.
        var from = range switch
        {
            "1d" => to,
            "1w" => to.AddDays(-7),
            "1m" => to.AddDays(-30),
            "ytd" => new DateTime(to.Year, 1, 1),
            "1y" => to.AddDays(-365),
            "5y" => to.AddDays(-1825),
            _ => to.AddDays(-180), // "6m" ve gecersiz/bos deger icin varsayilan
        };

        // Onbellege sadece dis servisten gelen ham veri (barlar ve istatistikler)
        // alinir. Gostergeler kullanicinin sectigi listeye gore degistigi ve
        // hesaplamasi ucuz oldugu icin her istekte yeniden hesaplanir.
        var historyKey = HistoryKey(symbol, investmentType, range, to);
        if (!_cache.TryGetValue(historyKey, out IReadOnlyList<PriceBarDto>? bars) || bars == null)
        {
            bars = await provider.GetHistoricalPricesAsync(symbol, investmentType, from, to, ct);
            if (bars.Count == 0)
                throw new ExternalServiceException($"'{symbol}' için geçmiş fiyat verisi bulunamadı.");

            _cache.Set(historyKey, bars, HistoryTtl(investmentType, range));
        }

        // Fon (TEFAS) NAV verisi gunluk tek fiyat olarak gelir (Open=High=Low=Close,
        // Volume=0'a yakin) — hacim/aralik tabanli gostergelerin cogu dejenere olur,
        // bu yuzden fonlarda hic gosterge hesaplanmaz (frontend zaten gondermiyor,
        // burasi ikinci bir guvenlik agi).
        var keys = IsFund(investmentType)
            ? Enumerable.Empty<string>()
            : indicatorKeys;

        // Sirket temelli istatistikler (F/K, FAVOK, kar marjlari vb.) sadece hisse
        // senedi icin anlamli — diger tiplerde saglayicinin varsayilan implementasyonu
        // zaten null donuyor.
        var statisticsKey = StatisticsKey(symbol);
        if (!_cache.TryGetValue(statisticsKey, out StockStatisticsDto? statistics))
        {
            statistics = await provider.GetStatisticsAsync(symbol, ct);
            // null sonuc da onbellege alinir: istatistik desteklemeyen tiplerde
            // her istekte bosuna dis servise gidilmesini onler.
            _cache.Set(statisticsKey, statistics, StatisticsTtl);
        }

        return new TechnicalAnalysisDto
        {
            Symbol = symbol,
            InvestmentType = investmentType,
            PriceBars = bars.OrderBy(b => b.Date).ToList(),
            Indicators = TechnicalIndicatorCalculator.Calculate(bars, keys),
            Statistics = statistics,
        };
    }
}
