using SmartFinance.Application.DTOs.MarketData;

namespace SmartFinance.Application.Interfaces;

/// <summary>
/// Günlük fiyat geçmişini DIŞ KAYNAKTAN çekebilen sağlayıcı.
///
/// <see cref="IPriceProvider.GetHistoricalPricesAsync"/> kullanıcı isteğinin
/// geçtiği yoldur ve önce depoya bakar; buradaki metot ise depoyu atlayıp
/// doğrudan kaynağa gider. Ayrımın nedeni: dış kaynağa gitme yetkisi tek bir
/// yerde, arka plan senkron işinde toplansın. Aksi halde her kullanıcı isteği
/// paylaşılan hız kotasını tüketebilirdi.
/// </summary>
public interface IHistorySource
{
    /// Kaynaktan günlük barları çeker. Uzun aralıkları gerekiyorsa kendi içinde
    /// parçalara böler (TEFAS istek başına 1 ayla sınırlı).
    Task<IReadOnlyList<PriceBarDto>> FetchDailyBarsAsync(
        string symbol, DateTime from, DateTime to, CancellationToken ct = default);

    /// Bu kaynağa ard arda istek atarken semboller arasında beklenmesi gereken
    /// süre. TEFAS dakikada ~6 istek kabul ediyor (11sn), Yahoo'nun sınırı
    /// belgelenmemiş olduğu için ihtiyatlı bir ara bırakılıyor.
    TimeSpan InterSymbolDelay { get; }
}
