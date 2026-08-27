using SmartFinance.Application.DTOs.MarketData;

namespace SmartFinance.Application.Interfaces;

/// <summary>
/// Toplu güncel fiyat isteğinde yalnızca fiyatı değil, GÜNÜN BARINI da
/// (açılış/en yüksek/en düşük/son/hacim) dönebilen sağlayıcılar bu arayüzü
/// uygular.
///
/// Neden ayrı bir arayüz: gecelik senkron işi geçmişi dolduruyor ama bugünün
/// barını yazmıyor — bu yüzden seans sürerken grafiğin son mumu eksik kalırdı.
/// Yahoo'nun toplu quote yanıtı bu alanları zaten içerdiği için, arka plan
/// yenileyicisi EK BİR İSTEK ATMADAN bugünün (kısmi) barını depoya yazabiliyor.
/// Gecelik senkron ertesi gün aynı günü yeniden çekip kesin kapanışla düzeltir.
/// </summary>
public interface IBatchBarProvider
{
    /// Verilen sembollerin bugünkü barını döner.
    /// Anahtar: girişte verilen sembolün kendisi (büyük harfe normalize edilmiş).
    /// Verisi alınamayan semboller sonuçta yer almaz.
    Task<IReadOnlyDictionary<string, PriceBarDto>> GetTodayBarsAsync(
        IReadOnlyCollection<string> symbols, CancellationToken ct = default);
}
