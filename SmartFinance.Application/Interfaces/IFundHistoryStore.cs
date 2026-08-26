using SmartFinance.Application.DTOs.MarketData;

namespace SmartFinance.Application.Interfaces;

/// <summary>
/// Fon NAV gecmisinin kendi veritabanimizdaki deposu.
/// TEFAS'a gitmeden okuma/yazma yapar; TEFAS'a giden tek yer senkron isidir.
/// </summary>
public interface IFundHistoryStore
{
    /// Verilen aralikta saklanan barlari tarihe gore artan sirada doner.
    Task<IReadOnlyList<PriceBarDto>> GetRangeAsync(string fundCode, DateTime from, DateTime to, CancellationToken ct = default);

    /// Fon icin saklanan en son tarihi doner; hic kayit yoksa null.
    /// Senkron isi bunu kullanarak yalnizca eksik gunleri ceker.
    Task<DateTime?> GetLatestDateAsync(string fundCode, CancellationToken ct = default);

    /// Barlari ekler; ayni (fon, tarih) icin kayit varsa fiyati gunceller.
    /// Eklenen yeni kayit sayisini doner.
    Task<int> UpsertAsync(string fundCode, IEnumerable<PriceBarDto> bars, CancellationToken ct = default);

    /// Kullanicilarin portfoyunde bulunan farkli fon kodlari.
    /// Senkron isi yalnizca gercekten tutulan fonlari gunceller — TEFAS'taki
    /// yuzlerce fonun tamamini cekmek gereksiz ve hiz sinirini bosa harcar.
    Task<IReadOnlyList<string>> GetTrackedFundCodesAsync(CancellationToken ct = default);
}
