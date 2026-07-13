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
        string symbol, string investmentType, int days = 180, CancellationToken ct = default)
    {
        var provider = ResolveProvider(investmentType);
        var to = DateTime.Today;
        var from = to.AddDays(-days);

        var bars = await provider.GetHistoricalPricesAsync(symbol, investmentType, from, to, ct);
        if (bars.Count == 0)
            throw new ExternalServiceException($"'{symbol}' için geçmiş fiyat verisi bulunamadı.");

        return TechnicalIndicatorCalculator.Calculate(symbol, investmentType, bars);
    }
}
