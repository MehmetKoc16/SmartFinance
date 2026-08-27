using SmartFinance.Application.DTOs.MarketData;
using SmartFinance.Application.Interfaces;

namespace SmartFinance.Infrastructure.MarketData;

/// <summary>
/// Kullanıcı isteğinin geçtiği yol: önce kendi veritabanımıza bakılır, dış
/// kaynağa yalnızca depo istenen aralığı KAPSAMIYORSA gidilir.
///
/// Hem TEFAS hem Yahoo aynı mantığı kullanıyor; iki yerde ayrı ayrı yazılsaydı
/// birindeki düzeltme diğerine yansımazdı (kapsama kontrolü bir kez zaten
/// böyle bir hataya yol açtı: depoda veri "olması" yeterli sanılmıştı).
/// </summary>
public static class HistoryBackfill
{
    // Hafta sonu, resmi tatil ve seans dışı günlerde fiyat yayınlanmaz; birkaç
    // günlük boşluk "eksik veri" değildir. Tolerans olmadan her istekte boşuna
    // dış servise gidilirdi.
    private const int GapToleranceDays = 5;

    public static async Task<IReadOnlyList<PriceBarDto>> ReadWithBackfillAsync(
        IPriceHistoryStore store,
        IHistorySource source,
        string symbol,
        string investmentType,
        DateTime from,
        DateTime to,
        CancellationToken ct = default)
    {
        var fromDate = from.Date;
        var toDate = to.Date;

        var stored = await store.GetRangeAsync(symbol, investmentType, fromDate, toDate, ct);

        var earliest = stored.Count > 0 ? stored[0].Date.Date : (DateTime?)null;
        var latest = stored.Count > 0 ? stored[^1].Date.Date : (DateTime?)null;

        // Geriye doğru eksik: sembol yeni eklendi ya da daha önce yalnızca kısa
        // bir pencere çekilmişti (güncel fiyat için son 10 gün gibi).
        var needsBackfill = earliest is null
            || (earliest.Value - fromDate).TotalDays > GapToleranceDays;

        // İleriye doğru eksik: gecelik senkron işi bu sembol için henüz çalışmadı.
        var needsForwardFill = latest is not null
            && (toDate - latest.Value).TotalDays > GapToleranceDays;

        var changed = false;

        if (needsBackfill)
        {
            // Hiç veri yoksa aralığın tamamı, varsa yalnızca eksik ön kısım çekilir.
            var fetchTo = earliest?.AddDays(-1) ?? toDate;
            changed |= await FetchAndStoreAsync(store, source, symbol, investmentType, fromDate, fetchTo, ct);
        }

        if (needsForwardFill)
        {
            // Son saklanan gün dahil edilir: o günün barı kısmi kaydedilmiş
            // olabilir (seans sürerken alınmış kapanış), üzerine yazılsın.
            changed |= await FetchAndStoreAsync(store, source, symbol, investmentType, latest!.Value, toDate, ct);
        }

        if (changed)
            stored = await store.GetRangeAsync(symbol, investmentType, fromDate, toDate, ct);

        return stored;
    }

    private static async Task<bool> FetchAndStoreAsync(
        IPriceHistoryStore store,
        IHistorySource source,
        string symbol,
        string investmentType,
        DateTime from,
        DateTime to,
        CancellationToken ct)
    {
        if (to < from) return false;

        var fetched = await source.FetchDailyBarsAsync(symbol, from, to, ct);
        if (fetched.Count == 0) return false;

        await store.UpsertAsync(symbol, investmentType, fetched, ct);
        return true;
    }
}
