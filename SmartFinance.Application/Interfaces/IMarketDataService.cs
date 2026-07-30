using SmartFinance.Application.DTOs.MarketData;

namespace SmartFinance.Application.Interfaces;

public interface IMarketDataService
{
    Task<PriceQuoteDto> GetCurrentPriceAsync(string symbol, string investmentType, CancellationToken ct = default);

    Task<TechnicalAnalysisDto> GetTechnicalAnalysisAsync(
        string symbol, string investmentType, int days, IEnumerable<string> indicatorKeys, CancellationToken ct = default);
}
