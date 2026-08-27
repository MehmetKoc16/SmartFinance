using SmartFinance.Application.DTOs.MarketData;

namespace SmartFinance.Application.Interfaces;

/// <summary>
/// Günlük fiyat geçmişinin kendi veritabanımızdaki deposu — fon ve hisse ortak.
///
/// Dış servise (TEFAS / Yahoo) yalnızca senkron işi gider; kullanıcı istekleri
/// buradan okur. Böylece dış istek sayısı kullanıcı sayısına değil, yalnızca
/// takip edilen farklı sembol sayısına bağlı kalır.
/// </summary>
public interface IPriceHistoryStore
{
    /// Verilen aralıkta saklanan barları tarihe göre artan sırada döner.
    Task<IReadOnlyList<PriceBarDto>> GetRangeAsync(
        string symbol, string investmentType, DateTime from, DateTime to, CancellationToken ct = default);

    /// Sembol için saklanan en son tarih; hiç kayıt yoksa null.
    /// Senkron işi bunu kullanarak yalnızca eksik günleri çeker.
    Task<DateTime?> GetLatestDateAsync(string symbol, string investmentType, CancellationToken ct = default);

    /// Barları ekler; aynı (sembol, tip, tarih) için kayıt varsa günceller.
    /// Eklenen YENİ kayıt sayısını döner.
    Task<int> UpsertAsync(
        string symbol, string investmentType, IEnumerable<PriceBarDto> bars, CancellationToken ct = default);

    /// Kullanıcıların portföyünde bulunan, verilen tipteki farklı semboller.
    /// Senkron işi yalnızca gerçekten tutulan sembolleri günceller — piyasadaki
    /// binlerce sembolün tamamını çekmek hız sınırını boşa harcar.
    Task<IReadOnlyList<string>> GetTrackedSymbolsAsync(string investmentType, CancellationToken ct = default);
}
