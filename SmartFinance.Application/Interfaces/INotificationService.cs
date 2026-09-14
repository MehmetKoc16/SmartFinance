using SmartFinance.Application.DTOs.Notification;

namespace SmartFinance.Application.Interfaces;

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
}
