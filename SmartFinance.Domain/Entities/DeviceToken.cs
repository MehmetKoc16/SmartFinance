using SmartFinance.Domain.Common;

namespace SmartFinance.Domain.Entities;

/// <summary>
/// Kullanicinin bir cihazinin FCM (Firebase Cloud Messaging) kaydi. Token
/// cihazi tanimlar, kullaniciyi degil: ayni telefonda baska hesapla giris
/// yapilirsa token yeni hesaba tasinir.
/// </summary>
public class DeviceToken : BaseEntity
{
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    public string Token { get; set; } = string.Empty;

    // Uygulama her acilista token'i yeniden bildiriyor; uzun suredir
    // gorulmeyen cihazlar en eski kayit olarak ilk budanan olur.
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
}
