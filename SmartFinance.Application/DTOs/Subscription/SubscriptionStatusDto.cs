namespace SmartFinance.Application.DTOs.Subscription;

/// <summary>
/// Uygulamanın paywall'ı ve "3/5 yatırım" gibi göstergeleri çizebilmesi için
/// gereken bilgiler.
///
/// Sınırlar sunucudan bildiriliyor: uygulamada ayrıca yazılsaydı, sınır
/// değiştiğinde eski sürüm kullananlar yanlış rakam görürdü.
/// </summary>
public class SubscriptionStatusDto
{
    public bool IsPremium { get; set; }

    /// Premium ise aboneliğin bittiği an, değilse null.
    public DateTime? ExpiresAt { get; set; }

    /// Kullanıcı otomatik yenilemeyi kapattıysa false — uygulama
    /// "aboneliğiniz 12 Ekim'de sona erecek" uyarısı gösterebilir.
    public bool AutoRenewing { get; set; }

    /// Ücretsiz katman sınırları ve kullanıcının mevcut durumu.
    public LimitUsageDto Investments { get; set; } = new();
    public LimitUsageDto Budgets { get; set; } = new();
    public LimitUsageDto MonthlyImports { get; set; } = new();

    /// Teknik göstergeler bu kullanıcıya açık mı.
    public bool IndicatorsIncluded { get; set; }
}

public class LimitUsageDto
{
    /// Şu anki kullanım.
    public int Used { get; set; }

    /// Ücretsiz katman sınırı. Premium kullanıcıda null (sınırsız).
    public int? Limit { get; set; }
}
