using SmartFinance.Application.DTOs.MarketData;

namespace SmartFinance.Application.Interfaces;

public interface IPriceProvider
{
    IEnumerable<string> SupportedInvestmentTypes { get; }

    Task<PriceQuoteDto> GetCurrentPriceAsync(string symbol, string investmentType, CancellationToken ct = default);

    Task<IReadOnlyList<PriceBarDto>> GetHistoricalPricesAsync(
        string symbol, string investmentType, DateTime from, DateTime to, CancellationToken ct = default);
}
