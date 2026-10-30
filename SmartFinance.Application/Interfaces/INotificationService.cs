using SmartFinance.Application.DTOs.Notification;

namespace SmartFinance.Application.Interfaces;

/// <summary>
/// Duyurunun sonucu: kac kullaniciya yeni bildirim, kac cihaza push.
/// NotOptedInUsers: zorunlu olmayan duyuruda, duyurulara onay vermedigi icin
/// atlanan kullanici sayisi (zorunlu duyuruda her zaman 0).
/// </summary>
public sealed record BroadcastResult(int NotifiedUsers, int AlreadyNotifiedUsers, int PushTargets, int InvalidTokensRemoved,
    int NotOptedInUsers = 0, bool Mandatory = false);

public interface INotificationService
{
    Task<IEnumerable<NotificationDto>> GetAllAsync();
    Task<int> GetUnreadCountAsync();
    Task MarkAsReadAsync(int id);
    Task MarkAllAsReadAsync();

    // Giris yapan kullanicinin bildirim turu tercihleri.
    Task<NotificationPreferencesDto> GetPreferencesAsync();
    // null alanlar degismez. Duyuru tercihi false->true olunca AnnouncementsConsentAt,
    // true->false olunca AnnouncementsOptOutAt = UtcNow; degismiyorsa tarihlere dokunulmaz.
    Task<NotificationPreferencesDto> UpdatePreferencesAsync(UpdateNotificationPreferencesDto dto);

    // Bir gider islemi kaydedildikten/guncellendikten sonra cagrilir: ilgili
    // kategori icin bir butce tanimliysa ve bu ay icin ilk kez limit
    // asildiysa bir kez bildirim olusturur. Ayni ay icinde tekrar tekrar
    // cagrilmasi ek bildirim uretmez (DedupeKey uzerinden).
    // Uygulama ici kayit her zaman olusur; NotifyBudgetAlerts=false ise push gitmez.
    Task EvaluateBudgetForTransactionAsync(int userId, int? categoryId, DateTime transactionDate);

    // Tum kullanicilara (ya da onlyEmail verilirse yalnizca o kullaniciya)
    // bilgilendirme: yeni surum, gizlilik politikasi vb. Ayni anahtarla tekrar
    // calistirilirsa kimseye ikinci kez gitmez. HTTP ucu yok; sunucuda komut
    // satirindan calistirilir (API'deki "duyuru" komutu).
    // zorunlu=false: yalnizca NotifyAnnouncements=true olanlara (uygulama ici + push).
    // zorunlu=true: guvenlik, veri ihlali, hukuki zorunlu bildirim; tercih yok sayilir.
    Task<BroadcastResult> BroadcastAsync(string key, string title, string message,
        string? onlyEmail = null, bool zorunlu = false, CancellationToken ct = default);
}
