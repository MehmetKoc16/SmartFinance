using Microsoft.EntityFrameworkCore;
using SmartFinance.Application.DTOs.MarketData;
using SmartFinance.Application.Interfaces;
using SmartFinance.Domain.Entities;
using SmartFinance.Infrastructure.Context;

namespace SmartFinance.Infrastructure.MarketData;

public class FundHistoryStore : IFundHistoryStore
{
    private readonly SmartFinanceDbContext _context;

    public FundHistoryStore(SmartFinanceDbContext context)
    {
        _context = context;
    }

    private static string Normalize(string fundCode) => fundCode.Trim().ToUpperInvariant();

    public async Task<IReadOnlyList<PriceBarDto>> GetRangeAsync(
        string fundCode, DateTime from, DateTime to, CancellationToken ct = default)
    {
        var code = Normalize(fundCode);
        var fromDate = from.Date;
        var toDate = to.Date;

        return await _context.FundPriceHistories
            .AsNoTracking()
            .Where(h => h.FundCode == code && h.Date >= fromDate && h.Date <= toDate)
            .OrderBy(h => h.Date)
            // Fonda tek bir NAV fiyati var; OHLC alanlarinin hepsi ayni degeri
            // tasir, hacim bilgisi TEFAS tarafindan yayinlanmaz.
            .Select(h => new PriceBarDto
            {
                Date = h.Date,
                Open = h.Price,
                High = h.Price,
                Low = h.Price,
                Close = h.Price,
                Volume = 0,
            })
            .ToListAsync(ct);
    }

    public async Task<DateTime?> GetLatestDateAsync(string fundCode, CancellationToken ct = default)
    {
        var code = Normalize(fundCode);

        return await _context.FundPriceHistories
            .AsNoTracking()
            .Where(h => h.FundCode == code)
            .OrderByDescending(h => h.Date)
            .Select(h => (DateTime?)h.Date)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<int> UpsertAsync(
        string fundCode, IEnumerable<PriceBarDto> bars, CancellationToken ct = default)
    {
        var code = Normalize(fundCode);

        // Ayni gunun birden fazla kez gelmesine karsi (TEFAS parcalari
        // sinirlarda ortusebiliyor) once bellekte tekillestiriliyor.
        var incoming = bars
            .GroupBy(b => b.Date.Date)
            .ToDictionary(g => g.Key, g => g.Last().Close);

        if (incoming.Count == 0) return 0;

        var dates = incoming.Keys.ToList();
        var existing = await _context.FundPriceHistories
            .Where(h => h.FundCode == code && dates.Contains(h.Date))
            .ToListAsync(ct);

        var existingByDate = existing.ToDictionary(h => h.Date);
        var added = 0;

        foreach (var (date, price) in incoming)
        {
            if (existingByDate.TryGetValue(date, out var row))
            {
                // TEFAS gecmise donuk duzeltme yayinlayabiliyor; fiyat
                // degistiyse guncelle, aynıysa bosuna yazma.
                if (row.Price != price)
                {
                    row.Price = price;
                    row.UpdatedDate = DateTime.UtcNow;
                }
                continue;
            }

            _context.FundPriceHistories.Add(new FundPriceHistory
            {
                FundCode = code,
                Date = date,
                Price = price,
            });
            added++;
        }

        await _context.SaveChangesAsync(ct);
        return added;
    }

    public async Task<IReadOnlyList<string>> GetTrackedFundCodesAsync(CancellationToken ct = default)
    {
        return await _context.Investments
            .AsNoTracking()
            .Where(i => i.InvestmentType == "fund")
            .Select(i => i.Name.ToUpper())
            .Distinct()
            .ToListAsync(ct);
    }
}
