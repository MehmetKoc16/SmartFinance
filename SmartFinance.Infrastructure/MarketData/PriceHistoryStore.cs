using Microsoft.EntityFrameworkCore;
using SmartFinance.Application.DTOs.MarketData;
using SmartFinance.Application.Interfaces;
using SmartFinance.Domain.Entities;
using SmartFinance.Infrastructure.Context;

namespace SmartFinance.Infrastructure.MarketData;

public class PriceHistoryStore : IPriceHistoryStore
{
    private readonly SmartFinanceDbContext _context;

    public PriceHistoryStore(SmartFinanceDbContext context)
    {
        _context = context;
    }

    // Semboller büyük harfe normalize edilerek saklanır: kullanıcı "thyao"
    // yazsa da "THYAO" ile aynı kaydı bulmalı.
    private static string NormalizeSymbol(string value) => value.Trim().ToUpperInvariant();

    // Tip küçük harfe normalize edilir — kod tabanının her yerinde ("fund",
    // "stock", "crypto") bu biçim kullanılıyor. SQL Server varsayılan olarak
    // harf duyarsız karşılaştırma yapsa da buna güvenmiyoruz: veritabanı
    // ayarına bağlı sessiz bir eşleşme hatası, verinin var olduğu halde
    // bulunamaması demek olurdu.
    private static string NormalizeType(string value) => value.Trim().ToLowerInvariant();

    public async Task<IReadOnlyList<PriceBarDto>> GetRangeAsync(
        string symbol, string investmentType, DateTime from, DateTime to, CancellationToken ct = default)
    {
        var sym = NormalizeSymbol(symbol);
        var type = NormalizeType(investmentType);
        var fromDate = from.Date;
        var toDate = to.Date;

        return await _context.PriceHistories
            .AsNoTracking()
            .Where(h => h.Symbol == sym && h.InvestmentType == type && h.Date >= fromDate && h.Date <= toDate)
            .OrderBy(h => h.Date)
            .Select(h => new PriceBarDto
            {
                Date = h.Date,
                Open = h.Open,
                High = h.High,
                Low = h.Low,
                Close = h.Close,
                Volume = h.Volume,
            })
            .ToListAsync(ct);
    }

    public async Task<DateTime?> GetLatestDateAsync(
        string symbol, string investmentType, CancellationToken ct = default)
    {
        var sym = NormalizeSymbol(symbol);
        var type = NormalizeType(investmentType);

        return await _context.PriceHistories
            .AsNoTracking()
            .Where(h => h.Symbol == sym && h.InvestmentType == type)
            .OrderByDescending(h => h.Date)
            .Select(h => (DateTime?)h.Date)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<int> UpsertAsync(
        string symbol, string investmentType, IEnumerable<PriceBarDto> bars, CancellationToken ct = default)
    {
        var sym = NormalizeSymbol(symbol);
        var type = NormalizeType(investmentType);

        // Aynı günün birden fazla kez gelmesine karşı (parçalı çekimlerde
        // sınırlar örtüşebiliyor) önce bellekte tekilleştiriliyor.
        var incoming = bars
            .GroupBy(b => b.Date.Date)
            .ToDictionary(g => g.Key, g => g.Last());

        if (incoming.Count == 0) return 0;

        var dates = incoming.Keys.ToList();
        var existing = await _context.PriceHistories
            .Where(h => h.Symbol == sym && h.InvestmentType == type && dates.Contains(h.Date))
            .ToListAsync(ct);

        var existingByDate = existing.ToDictionary(h => h.Date);
        var added = 0;

        foreach (var (date, bar) in incoming)
        {
            if (existingByDate.TryGetValue(date, out var row))
            {
                // Kaynak geçmişe dönük düzeltme yayınlayabiliyor (TEFAS'ta NAV
                // düzeltmesi, Yahoo'da gün içi kısmi barın kapanışa dönmesi);
                // değer değiştiyse güncelle, aynıysa boşuna yazma.
                if (row.Close != bar.Close || row.Open != bar.Open ||
                    row.High != bar.High || row.Low != bar.Low || row.Volume != bar.Volume)
                {
                    row.Open = bar.Open;
                    row.High = bar.High;
                    row.Low = bar.Low;
                    row.Close = bar.Close;
                    row.Volume = bar.Volume;
                    row.UpdatedDate = DateTime.UtcNow;
                }
                continue;
            }

            _context.PriceHistories.Add(new PriceHistory
            {
                Symbol = sym,
                InvestmentType = type,
                Date = date,
                Open = bar.Open,
                High = bar.High,
                Low = bar.Low,
                Close = bar.Close,
                Volume = bar.Volume,
            });
            added++;
        }

        await _context.SaveChangesAsync(ct);
        return added;
    }

    public async Task<IReadOnlyList<string>> GetTrackedSymbolsAsync(
        string investmentType, CancellationToken ct = default)
    {
        var type = NormalizeType(investmentType);

        return await _context.Investments
            .AsNoTracking()
            .Where(i => i.InvestmentType.ToLower() == type)
            .Select(i => i.Name.ToUpper())
            .Distinct()
            .ToListAsync(ct);
    }
}
