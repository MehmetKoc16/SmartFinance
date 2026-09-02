using SmartFinance.Domain.Common;

namespace SmartFinance.Domain.Entities;

/// <summary>
/// Google Play üzerinden alınmış premium aboneliğin sunucudaki kaydı.
///
/// Neden istemciye güvenilmiyor: uygulama "ben premium'um" dese de bu bilgi
/// değiştirilebilir. Satın alma jetonu (purchase token) sunucuda Google Play
/// Developer API'sine doğrulatılır ve premium durumu YALNIZCA bu tablodan
/// okunur.
///
/// Geçmiş kayıtlar silinmiyor: yenilemeler ve iptaller ayrı satırlar olarak
/// birikiyor, böylece bir kullanıcının abonelik geçmişi destek taleplerinde
/// izlenebiliyor.
/// </summary>
public class Subscription : BaseEntity
{
    public int UserId { get; set; }
    public User User { get; set; } = null!;

    /// Play Console'da tanımlı ürün kimliği ("premium_monthly" / "premium_yearly").
    public string ProductId { get; set; } = string.Empty;

    /// Google Play'in verdiği satın alma jetonu. Doğrulama bununla yapılır ve
    /// aynı jetonun iki kez işlenmesini engellemek için tekil.
    public string PurchaseToken { get; set; } = string.Empty;

    /// Play sipariş kimliği — destek taleplerinde kullanıcının makbuzuyla
    /// eşleştirmek için.
    public string? OrderId { get; set; }

    public DateTime StartsAt { get; set; }

    /// Aboneliğin bittiği an. Premium kontrolü bu alana bakıyor.
    public DateTime ExpiresAt { get; set; }

    /// Kullanıcı otomatik yenilemeyi kapattıysa true. Abonelik ExpiresAt'e
    /// kadar geçerli kalmaya devam eder — iptal, anında sonlanma demek değil.
    public bool AutoRenewing { get; set; }

    /// Sunucunun Google'a en son ne zaman sorduğu. Gecelik iş bunu kullanarak
    /// süresi yaklaşan abonelikleri yeniden doğruluyor.
    public DateTime? LastVerifiedAt { get; set; }
}
