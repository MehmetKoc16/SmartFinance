using Microsoft.Extensions.Caching.Memory;
using SmartFinance.Application.DTOs.MarketData;
using SmartFinance.Application.Exceptions;
using SmartFinance.Application.Interfaces;

namespace SmartFinance.Infrastructure.MarketData;

public class MarketDataService : IMarketDataService
{
    private readonly IEnumerable<IPriceProvider> _providers;
    private readonly IMemoryCache _cache;
    private readonly IPriceCache _priceCache;

    public MarketDataService(IEnumerable<IPriceProvider> providers, IMemoryCache cache, IPriceCache priceCache)
    {
        _providers = providers;
        _cache = cache;
        _priceCache = priceCache;
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
    private static string HistoryKey(string symbol, string investmentType, string range, DateTime to) =>
        $"history:{investmentType.ToLowerInvariant()}:{symbol.ToUpperInvariant()}:{range}:{to:yyyy-MM-dd}";

    private static string StatisticsKey(string symbol) =>
        $"stats:{symbol.ToUpperInvariant()}";

    private static string SearchKey(string investmentType, string query) =>
        $"search:{investmentType.ToLowerInvariant()}:{query.Trim().ToLowerInvariant()}";

    public async Task<PriceQuoteDto> GetCurrentPriceAsync(string symbol, string investmentType, CancellationToken ct = default)
    {
        // Normal isleyiste bu onbellegi arka plandaki PriceRefreshService toplu
        // isteklerle doldurur; kullanici istekleri dis servise hic gitmez.
        // Asagidaki tek tek cekme yolu yalnizca yedek: yeni eklenmis bir sembol
        // henuz yenileme turuna girmemisse veya toplu istek desteklenmiyorsa.
        if (_priceCache.TryGet(symbol, investmentType, out var cached) && cached != null)
            return cached;

        var quote = await ResolveProvider(investmentType).GetCurrentPriceAsync(symbol, investmentType, ct);
        _priceCache.Set(symbol, investmentType, quote, CurrentPriceTtl);
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
                // Buraya gelindiginde yatirim zaten kayitli — sembol daha once
                // dogrulanmis demektir. 0 bar donmesi genelde secilen araligin
                // (orn. 5 yillik) varligin gecmisinden daha eski olmasindandir,
                // sembolun yanlis olmasindan degil.
                throw new ExternalServiceException($"'{symbol}' için geçmiş fiyat verisi bulunamadı.",
                    ExternalServiceFailureKind.NoDataForRange);

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

        // Gun-ici barlar da yamaniyor: kendi onbellekleri ayri zamanda doldugu icin
        // 1G ayni anda 285,25, diger araliklar 284,75 gosteriyordu. Fiyat hangi
        // aralik secilirse secilsin tek kaynaktan (guncel fiyat onbellegi) gelir.
        var series = WithLatestQuote(bars, symbol, investmentType);

        return new TechnicalAnalysisDto
        {
            Symbol = symbol,
            InvestmentType = investmentType,
            PriceBars = series.OrderBy(b => b.Date).ToList(),
            Indicators = TechnicalIndicatorCalculator.Calculate(series, keys),
            Statistics = statistics,
        };
    }

    // Gunluk gecmis 30 dk onbellekte kaliyor, guncel fiyat ise arka planda 5 dk'da
    // bir tazeleniyor. Bugunun bari onbellekteki haliyle donunce ayni ekranda
    // 1G/1H/6A secimine gore farkli "guncel" fiyat gorunuyordu. Onbellekteki liste
    // degistirilmiyor: son bar kopyalanip yeni liste donuluyor.
    private IReadOnlyList<PriceBarDto> WithLatestQuote(IReadOnlyList<PriceBarDto> bars, string symbol, string investmentType)
    {
        if (bars.Count == 0) return bars;
        var last = bars.MaxBy(b => b.Date)!;
        if (last.Date.Date != DateTime.Today) return bars;
        if (!_priceCache.TryGet(symbol, investmentType, out var quote) || quote == null || quote.Price <= 0)
            return bars;

        var guncel = new PriceBarDto
        {
            Date = last.Date,
            Open = last.Open,
            High = Math.Max(last.High, quote.Price),
            Low = Math.Min(last.Low, quote.Price),
            Close = quote.Price,
            Volume = last.Volume,
        };
        return bars.Select(b => ReferenceEquals(b, last) ? guncel : b).ToList();
    }

    public async Task<IReadOnlyList<SymbolSearchResultDto>> SearchSymbolsAsync(
        string investmentType, string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<SymbolSearchResultDto>();

        var key = SearchKey(investmentType, query);
        if (_cache.TryGetValue(key, out IReadOnlyList<SymbolSearchResultDto>? cached) && cached != null)
            return cached;

        var results = await ResolveProvider(investmentType).SearchSymbolsAsync(query, ct);
        // Kullanici yazdikca istek atiyor; ayni ön-ek kisa surede tekrar tekrar
        // sorulur (orn. "TH" -> "THY" -> "THYA"), o yuzden onbellek suresi kisa
        // ama sifir degil.
        _cache.Set(key, results, TimeSpan.FromMinutes(10));
        return results;
    }
}
