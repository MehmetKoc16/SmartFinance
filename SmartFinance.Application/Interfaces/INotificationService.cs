using SmartFinance.Application.DTOs.Notification;

namespace SmartFinance.Application.Interfaces;

/// <summary>Duyurunun sonucu: kac kullaniciya yeni bildirim, kac cihaza push.</summary>
public sealed record BroadcastResult(int NotifiedUsers, int AlreadyNotifiedUsers, int PushTargets, int InvalidTokensRemoved);

public interface INotificationService
{
    Task<IEnumerable<NotificationDto>> GetAllAsync();
    Task<int> GetUnreadCountAsync();
    Task MarkAsReadAsync(int id);
    Task MarkAllAsReadAsync();

    // Bir gider islemi kaydedildikten/guncellendikten sonra cagrilir: ilgili
    // kategori icin bir butce tanimliysa ve bu ay icin ilk kez limit
    // asildiysa bir kez bildirim olusturur. Ayni ay icinde tekrar tekrar
    // cagrilmasi ek bildirim uretmez (DedupeKey uzerinden).
    Task EvaluateBudgetForTransactionAsync(int userId, int? categoryId, DateTime transactionDate);

    // Tum kullanicilara (ya da onlyEmail verilirse yalnizca o kullaniciya)
    // bilgilendirme: yeni surum, gizlilik politikasi vb. Ayni anahtarla tekrar
    // calistirilirsa kimseye ikinci kez gitmez. HTTP ucu yok; sunucuda komut
    // satirindan calistirilir (API'deki "duyuru" komutu).
    Task<BroadcastResult> BroadcastAsync(string key, string title, string message,
        string? onlyEmail = null, CancellationToken ct = default);
}
