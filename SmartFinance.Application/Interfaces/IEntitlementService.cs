namespace SmartFinance.Application.Interfaces;

/// <summary>
/// Kullanıcının premium hakkı olup olmadığını belirler.
///
/// Tek doğruluk kaynağı sunucudaki abonelik kaydıdır; istemcinin "ben
/// premium'um" demesine güvenilmez.
/// </summary>
public interface IEntitlementService
{
    /// Oturumdaki kullanıcının şu anda geçerli bir aboneliği var mı.
    Task<bool> IsPremiumAsync(CancellationToken ct = default);

    /// Belirli bir kullanıcı için aynı kontrol (arka plan işleri için).
    Task<bool> IsPremiumAsync(int userId, CancellationToken ct = default);

    /// <summary>
    /// Premium değilse verilen sınırın aşılıp aşılmadığına bakar ve aşıldıysa
    /// kullanıcıya gösterilecek mesajla birlikte hata fırlatır.
    /// </summary>
    /// <param name="currentCount">Kullanıcının şu anki adedi.</param>
    /// <param name="limit">Ücretsiz katman sınırı.</param>
    /// <param name="message">Sınır aşıldığında gösterilecek mesaj.</param>
    Task EnsureWithinFreeLimitAsync(int currentCount, int limit, string message, CancellationToken ct = default);
}
