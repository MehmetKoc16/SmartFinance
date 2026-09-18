using Microsoft.EntityFrameworkCore;
using SmartFinance.Application.DTOs.MarketData;
using SmartFinance.Application.Interfaces;
using SmartFinance.Infrastructure.Context;

namespace SmartFinance.Infrastructure.Fundamentals;

public class FundamentalsService : IFundamentalsService
{
    private readonly SmartFinanceDbContext _context;

    public FundamentalsService(SmartFinanceDbContext context)
    {
        _context = context;
    }

    public async Task<FundamentalSnapshotDto?> GetSnapshotAsync(string symbol, CancellationToken ct = default)
    {
        var sembol = symbol.Trim().ToUpperInvariant();
        // TTM en fazla iki yil geriye bakiyor; bir yil pay birakiliyor.
        var enEskiYil = DateTime.UtcNow.Year - 3;
        var satirlar = await _context.CompanyFinancials
            .AsNoTracking()
            .Where(r => r.Symbol == sembol && r.Year >= enEskiYil)
            .ToListAsync(ct);
        return FundamentalRatioCalculator.Compute(satirlar);
    }
}
