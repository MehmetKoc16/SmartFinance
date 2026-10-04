using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartFinance.Application.Exceptions;
using SmartFinance.Application.Interfaces;
using SmartFinance.Domain.Entities;
using SmartFinance.Infrastructure.Context;

namespace SmartFinance.Infrastructure.Billing;

internal static class SubscriptionUpdater
{
    internal static bool IsProductSupported(string productId)
        => productId is "premium_monthly" or "premium_yearly";

    internal static async Task ApplyAsync(SmartFinanceDbContext context, Subscription row,
        PlaySubscriptionInfo info, IPlayBillingClient client, ILogger logger, DateTime nowUtc,
        CancellationToken ct)
    {
        if (!IsProductSupported(info.ProductId) || info.ProductId != row.ProductId)
            throw new BadRequestException("Satın alınan ürün eşleşmiyor.");

        var entitled = info.State is "SUBSCRIPTION_STATE_ACTIVE"
            or "SUBSCRIPTION_STATE_IN_GRACE_PERIOD" or "SUBSCRIPTION_STATE_CANCELED";
        if (!entitled && info.State is not ("SUBSCRIPTION_STATE_EXPIRED" or "SUBSCRIPTION_STATE_ON_HOLD"
                or "SUBSCRIPTION_STATE_PAUSED" or "SUBSCRIPTION_STATE_PENDING"
                or "SUBSCRIPTION_STATE_PENDING_PURCHASE_CANCELED"))
            throw new InvalidOperationException("Google Play abonelik durumu tanınmıyor.");

        Subscription? linked = null;
        if (!string.IsNullOrWhiteSpace(info.LinkedPurchaseToken) && info.LinkedPurchaseToken != row.PurchaseToken)
        {
            linked = await context.Subscriptions.IgnoreQueryFilters()
                .SingleOrDefaultAsync(s => s.PurchaseToken == info.LinkedPurchaseToken, ct);
            // Başka hesabın eski aboneliğini de sonlandırmaya izin verilmez.
            if (linked is not null && linked.UserId != row.UserId)
                throw new BadRequestException("Bu satın alma başka bir hesaba ait.");
        }

        row.OrderId = info.LatestOrderId;
        row.StartsAt = info.StartedAtUtc;
        row.ExpiresAt = entitled || info.ExpiresAtUtc <= nowUtc ? info.ExpiresAtUtc : nowUtc;
        row.AutoRenewing = info.AutoRenewing;
        row.LastVerifiedAt = nowUtc;
        if (linked is not null) linked.ExpiresAt = nowUtc;

        if (!info.Acknowledged && entitled)
        {
            try
            {
                await client.AcknowledgeAsync(info.ProductId, row.PurchaseToken, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // Google hata metni/URL'si jeton içerebilir; yalnızca hata türünü kaydet.
                logger.LogWarning("Play satın alımı onaylanamadı. Kayıt: {SubscriptionId}, hata türü: {ErrorType}.",
                    row.Id, ex.GetType().Name);
            }
        }
    }
}
