using SmartFinance.Application.DTOs.MarketData;

namespace SmartFinance.Application.Interfaces;

/// <summary>
/// Güncel fiyat önbelleği. Hem kullanıcı isteklerini karşılayan
/// MarketDataService hem de arka planda toplu yenileme yapan
/// PriceRefreshService aynı önbelleği kullanır.
///
/// Ortak bir arayüz olmasının sebebi: anahtar biçimi iki yerde ayrı ayrı
/// üretilseydi, birinin yazdığını diğeri okuyamaz ve arka plan yenilemesi
/// sessizce işe yaramaz hale gelirdi.
/// </summary>
public interface IPriceCache
{
    bool TryGet(string symbol, string investmentType, out PriceQuoteDto? quote);

    void Set(string symbol, string investmentType, PriceQuoteDto quote, TimeSpan ttl);
}
