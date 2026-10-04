using System.Globalization;
using System.Net;
using Google;
using Google.Apis.AndroidPublisher.v3;
using Google.Apis.AndroidPublisher.v3.Data;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Microsoft.Extensions.Configuration;
using SmartFinance.Application.Interfaces;

namespace SmartFinance.Infrastructure.Billing;

public sealed class GooglePlayBillingClient : IPlayBillingClient, IDisposable
{
    private readonly string _packageName;
    private readonly Lazy<AndroidPublisherService>? _service;

    public GooglePlayBillingClient(IConfiguration configuration)
    {
        var packageName = configuration["GooglePlay:PackageName"];
        _packageName = string.IsNullOrWhiteSpace(packageName) ? "com.walletmark.app" : packageName;
        var credentialsPath = configuration["GooglePlay:CredentialsPath"];
        IsConfigured = !string.IsNullOrWhiteSpace(credentialsPath) && File.Exists(credentialsPath);
        if (IsConfigured)
        {
            // Yalnızca açıkça yapılandırılan dosya kullanılır; ADC/ortam değişkeni yok.
            // Dosya okuma ilk gerçek isteğe ertelenir; eksik dosya açılışı engellemez.
            _service = new Lazy<AndroidPublisherService>(() => new AndroidPublisherService(
                new BaseClientService.Initializer
                {
                    HttpClientInitializer = GoogleCredential.FromFile(credentialsPath!)
                        .CreateScoped(AndroidPublisherService.Scope.Androidpublisher),
                    ApplicationName = "Walletmark",
                }));
        }
    }

    public bool IsConfigured { get; }

    private AndroidPublisherService Service => IsConfigured && _service is not null
        ? _service.Value
        : throw new InvalidOperationException("Google Play doğrulaması yapılandırılmamış.");

    public async Task<PlaySubscriptionInfo?> GetSubscriptionAsync(string purchaseToken, CancellationToken ct)
    {
        SubscriptionPurchaseV2 purchase;
        try
        {
            purchase = await Service.Purchases.Subscriptionsv2.Get(_packageName, purchaseToken).ExecuteAsync(ct);
        }
        catch (GoogleApiException ex) when (ex.HttpStatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            return null;
        }

        var items = purchase.LineItems;
        if (items is null || items.Count == 0 || string.IsNullOrWhiteSpace(items[0].ProductId))
            throw new InvalidOperationException("Google Play yanıtında ürün bilgisi eksik.");

        var pending = purchase.SubscriptionState is "SUBSCRIPTION_STATE_PENDING"
            or "SUBSCRIPTION_STATE_PENDING_PURCHASE_CANCELED";
        // Bekleyen ödeme yanıtlarında başlangıç/bitiş henüz bulunmayabilir.
        var expiresAt = items.Max(item => ParseUtc(item.ExpiryTimeRaw, pending));
        return new PlaySubscriptionInfo(
            items[0].ProductId,
            purchase.SubscriptionState,
            expiresAt,
            ParseUtc(purchase.StartTimeRaw, pending),
            items[0].AutoRenewingPlan?.AutoRenewEnabled == true,
            purchase.AcknowledgementState == "ACKNOWLEDGEMENT_STATE_ACKNOWLEDGED",
            purchase.ExternalAccountIdentifiers?.ObfuscatedExternalAccountId,
            items[0].LatestSuccessfulOrderId,
            purchase.LinkedPurchaseToken);
    }

    public async Task AcknowledgeAsync(string productId, string purchaseToken, CancellationToken ct)
    {
        await Service.Purchases.Subscriptions.Acknowledge(
            new SubscriptionPurchasesAcknowledgeRequest(), _packageName, productId, purchaseToken).ExecuteAsync(ct);
    }

    private static DateTime ParseUtc(string? value, bool allowMissing)
    {
        if (string.IsNullOrWhiteSpace(value) && allowMissing)
            return DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var parsed))
            throw new InvalidOperationException("Google Play yanıtında geçerli tarih bilgisi eksik.");
        return parsed.UtcDateTime;
    }

    public void Dispose()
    {
        if (_service?.IsValueCreated == true) _service.Value.Dispose();
    }
}
