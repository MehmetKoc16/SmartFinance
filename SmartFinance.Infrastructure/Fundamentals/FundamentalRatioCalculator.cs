using SmartFinance.Application.DTOs.MarketData;
using SmartFinance.Domain.Entities;

namespace SmartFinance.Infrastructure.Fundamentals;

/// <summary>
/// Son 12 ay (TTM) net kar ve son ozkaynak. Gelir tablosu KUMULATIF oldugu icin
/// (Q2 = ilk 6 ay) son dort ceyregi toplamak yanlis olur:
///
///     TTM = gecen yil yillik + bu yil kumulatif - gecen yil ayni donem kumulatif
///
/// Son donem yillik (Period 4) ise TTM dogrudan o yilin karidir. Gereken
/// donemlerden biri eksikse null — tahmin edilmez.
///
/// Bilinen sinirlar: (1) TMS 29 uygulayan sirketlerde donemler farkli alim
/// gucunde (MPARK F/K 12,97 — Is Yatirim 12,5); (2) fonksiyonel para birimi USD
/// olanlarda (THYAO) TL'ye farkli kurlarla cevrilmis donemler toplaniyor
/// (THYAO F/K 3,56 — Is Yatirim 3,0). PD/DD'yi etkilemiyor.
/// </summary>
public static class FundamentalRatioCalculator
{
    public static FundamentalSnapshotDto? Compute(IEnumerable<CompanyFinancial> rows)
    {
        var donemler = rows
            .GroupBy(r => (r.Year, r.Period))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.Consolidated).First());

        var adaylar = donemler.Where(d => d.Value.NetProfitParent.HasValue && d.Value.EquityParent.HasValue).ToList();
        if (adaylar.Count == 0) return null;

        var (yil, periyot) = adaylar.Max(d => d.Key);
        var son = donemler[(yil, periyot)];

        decimal ttm;
        if (periyot == 4)
        {
            ttm = son.NetProfitParent!.Value;
        }
        else
        {
            if (!donemler.TryGetValue((yil - 1, 4), out var gecenYillik) || gecenYillik.NetProfitParent is not decimal yillik
                || !donemler.TryGetValue((yil - 1, periyot), out var gecenAyni) || gecenAyni.NetProfitParent is not decimal ayni)
                return null;
            ttm = yillik + son.NetProfitParent!.Value - ayni;
        }

        return new FundamentalSnapshotDto(ttm, son.EquityParent!.Value, yil, periyot);
    }
}
