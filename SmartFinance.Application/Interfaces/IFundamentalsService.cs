using SmartFinance.Application.DTOs.MarketData;

namespace SmartFinance.Application.Interfaces;

public interface IFundamentalsService
{
    /// Veri yoksa ya da son 12 ay hesabi icin gereken donemlerden biri eksikse null.
    Task<FundamentalSnapshotDto?> GetSnapshotAsync(string symbol, CancellationToken ct = default);
}
