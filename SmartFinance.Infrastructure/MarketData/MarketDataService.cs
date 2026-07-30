using SmartFinance.Application.DTOs.MarketData;
using SmartFinance.Application.Exceptions;
using SmartFinance.Application.Interfaces;

namespace SmartFinance.Infrastructure.MarketData;

public class MarketDataService : IMarketDataService
{
    private readonly IEnumerable<IPriceProvider> _providers;

    public MarketDataService(IEnumerable<IPriceProvider> providers)
    {
        _providers = providers;
    }

    private IPriceProvider ResolveProvider(string investmentType)
    {
        var provider = _providers.FirstOrDefault(p =>
            p.SupportedInvestmentTypes.Contains(investmentType, StringComparer.OrdinalIgnoreCase));

        if (provider == null)
            throw new ExternalServiceException($"'{investmentType}' yatırım tipi için tanımlı bir fiyat sağlayıcısı yok.");

        return provider;
    }

    public Task<PriceQuoteDto> GetCurrentPriceAsync(string symbol, string investmentType, CancellationToken ct = default) =>
        ResolveProvider(investmentType).GetCurrentPriceAsync(symbol, investmentType, ct);

    public async Task<TechnicalAnalysisDto> GetTechnicalAnalysisAsync(
        string symbol, string investmentType, int days, IEnumerable<string> indicatorKeys, CancellationToken ct = default)
    {
        var provider = ResolveProvider(investmentType);
        var to = DateTime.Today;
        var from = to.AddDays(-days);

        var bars = await provider.GetHistoricalPricesAsync(symbol, investmentType, from, to, ct);
        if (bars.Count == 0)
            throw new ExternalServiceException($"'{symbol}' için geçmiş fiyat verisi bulunamadı.");

        // Fon (TEFAS) NAV verisi gunluk tek fiyat olarak gelir (Open=High=Low=Close,
        // Volume=0'a yakin) — hacim/aralik tabanli gostergelerin cogu dejenere olur,
        // bu yuzden fonlarda hic gosterge hesaplanmaz (frontend zaten gondermiyor,
        // burasi ikinci bir guvenlik agi).
        var keys = string.Equals(investmentType, "fund", StringComparison.OrdinalIgnoreCase)
            ? Enumerable.Empty<string>()
            : indicatorKeys;

        // Sirket temelli istatistikler (F/K, FAVOK, kar marjlari vb.) sadece hisse
        // senedi icin anlamli — diger tiplerde saglayicinin varsayilan implementasyonu
        // zaten null donuyor.
        var statistics = await provider.GetStatisticsAsync(symbol, ct);

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
