using Microsoft.Extensions.Logging;
using SmartFinance.Application.Interfaces;
using SmartFinance.Domain.Entities;
using SmartFinance.Infrastructure.Context;

namespace SmartFinance.Infrastructure.Billing;

/// <summary>
/// Yenileme işi (SubscriptionRenewalService) ile durum ucunun
/// (SubscriptionService.GetStatusAsync) ortak kullandığı tek satırlık yenileme
/// adımı ve iki yerin de uyması gereken süre sabitleri.
///
/// Neden ortak: Google yenilemeyi bize bildirmiyor (RTDN yok). Premium kararı
/// yalnız Subscriptions.ExpiresAt'e bakıyor; yenilenmiş ama tablosu henüz
/// güncellenmemiş bir abonelik, Google parayı almışken kullanıcıyı ücretsiz
/// gösteriyordu. İş bunu 15 dakikada bir, durum ucu ise kullanıcı sorduğu anda
/// kapatır; ikisi aynı kuralı iki ayrı yerde yazmasın diye burada toplandı.
/// </summary>
internal static class SubscriptionRefresher
{
    /// Süresi bu kadar içinde dolacak abonelikler önceden yeniden doğrulanır.
    internal static readonly TimeSpan ExpiringWindow = TimeSpan.FromDays(2);

    /// Süresi yaklaşan bir satır en son bu kadar zaman önce doğrulandıysa tekrar sorulmaz.
    internal static readonly TimeSpan ExpiringStaleAfter = TimeSpan.FromHours(6);

    /// Süresi yeni dolmuş, otomatik yenilenen satırlar için geriye bakış süresi.
    internal static readonly TimeSpan ExpiredLookback = TimeSpan.FromDays(30);

    /// Süresi yeni dolmuş satır en son bu kadar zaman önce doğrulandıysa iş tekrar sormaz.
    internal static readonly TimeSpan ExpiredStaleAfter = TimeSpan.FromMinutes(10);

    /// Durum ucunda aynı satır için Google'a en sık bu aralıkla sorulur.
    internal static readonly TimeSpan StatusStaleAfter = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Satırı Google'a sorar ve sonucu uygular. SaveChanges ÇAĞIRMAZ; kaydı
    /// çağıran taraf yapar. Hata fırlatırsa satır değiştirilmemiş sayılmalıdır
    /// (çağıran, izlenen değişiklikleri temizlemekten sorumludur).
    /// </summary>
    internal static async Task RefreshAsync(SmartFinanceDbContext context, Subscription row,
        IPlayBillingClient client, ILogger logger, DateTime nowUtc, CancellationToken ct)
    {
        var info = await client.GetSubscriptionAsync(row.PurchaseToken, ct);
        if (info is null)
        {
            // Google jetonu artık tanımıyor: hak sona erer. Geçmişte bitmiş bir
            // tarih ileri alınmaz. AutoRenewing kapanır; yoksa süresi dolmuş satır
            // her turda Google'a boşuna sorulurdu.
            if (row.ExpiresAt > nowUtc) row.ExpiresAt = nowUtc;
            row.AutoRenewing = false;
            row.LastVerifiedAt = nowUtc;
            return;
        }

        await SubscriptionUpdater.ApplyAsync(context, row, info, client, logger, nowUtc, ct);
    }
}
