using SmartFinance.Domain.Common;

namespace SmartFinance.Domain.Entities;

/// <summary>
/// Premium satın alma öncesindeki ön bilgilendirme ve erken ifa onayının kaydıdır.
/// Mesafeli Sözleşmeler Yönetmeliği md.15/1-h kapsamındaki ayrı onayın kimden,
/// hangi ürün ve metin sürümü için, ne zaman alındığının ispatını sağlar.
/// Her satın alma denemesi ayrı kaydedilir; zaman sunucuda UTC olarak atanır.
/// </summary>
public class PurchaseConsent : BaseEntity
{
    /// <summary>Hesap silindiğinde kaldırılır; satın alma kanıtı kimliksiz saklanır.</summary>
    public int? UserId { get; set; }
    public User? User { get; set; }
    /// <summary>Yalnız sunucuda doğrulanmış satın almaya bağlandığında doldurulur.</summary>
    public string? PurchaseToken { get; set; }
    public string? OrderId { get; set; }
    public string ProductId { get; set; } = string.Empty;
    public string TextVersion { get; set; } = string.Empty;
    public DateTime ConsentedAt { get; set; }
}
