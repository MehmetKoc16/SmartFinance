using SmartFinance.Domain.Common;

namespace SmartFinance.Domain.Entities;

/// <summary>
/// Şifre sıfırlama bağlantısının sunucudaki kaydı.
///
/// Token'ın KENDİSİ saklanmıyor, yalnızca SHA-256 özeti. Gerekçe parolayla
/// aynı: veritabanı bir şekilde sızarsa, elindeki kayıtlarla kimsenin hesabı
/// ele geçirilemesin. Token yüksek entropili rastgele bir değer olduğu için
/// bcrypt gibi yavaş bir özet gerekmiyor — sözlük saldırısına konu değil.
/// </summary>
public class PasswordResetToken : BaseEntity
{
    public int UserId { get; set; }
    public User User { get; set; } = null!;

    /// E-postayla gönderilen token'ın SHA-256 özeti (base64).
    public string TokenHash { get; set; } = string.Empty;

    /// Bağlantının geçerlilik süresi. Kısa tutuluyor: e-posta kutusuna
    /// sonradan erişen birinin eski bağlantıyı kullanabilmesi riski.
    public DateTime ExpiresAt { get; set; }

    /// Tek kullanımlık: kullanıldığı an dolduruluyor.
    public DateTime? UsedAt { get; set; }
}
