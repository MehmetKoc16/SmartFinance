namespace SmartFinance.Application.Interfaces;

public interface IPlayBillingClient
{
    bool IsConfigured { get; }
    Task<PlaySubscriptionInfo?> GetSubscriptionAsync(string purchaseToken, CancellationToken ct);
    Task AcknowledgeAsync(string productId, string purchaseToken, CancellationToken ct);
}

public record PlaySubscriptionInfo(
    string ProductId,
    string State,
    DateTime ExpiresAtUtc,
    DateTime StartedAtUtc,
    bool AutoRenewing,
    bool Acknowledged,
    string? ObfuscatedAccountId,
    string? LatestOrderId,
    string? LinkedPurchaseToken);
