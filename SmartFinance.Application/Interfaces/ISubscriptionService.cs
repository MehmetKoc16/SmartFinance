using SmartFinance.Application.DTOs.Subscription;

namespace SmartFinance.Application.Interfaces;

public interface ISubscriptionService
{
    /// Uygulamanin paywall'i ve kullanim sayaclarini cizebilmesi icin
    /// abonelik durumu ve mevcut kullanim.
    Task<SubscriptionStatusDto> GetStatusAsync(CancellationToken ct = default);
}
