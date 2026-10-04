using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SmartFinance.Application.Common;
using SmartFinance.Application.DTOs.Subscription;
using SmartFinance.Application.Exceptions;
using SmartFinance.Application.Interfaces;
using SmartFinance.Domain.Entities;
using SmartFinance.Infrastructure.Billing;
using SmartFinance.Infrastructure.Context;

namespace SmartFinance.Infrastructure.Services;

/// <summary>
/// Uygulamanın paywall'ı çizebilmesi için abonelik durumunu ve kullanım
/// sayaçlarını derler.
/// </summary>
public class SubscriptionService : ISubscriptionService
{
    private readonly SmartFinanceDbContext _context;
    private readonly ICurrentUserService _currentUserService;
    private readonly IEntitlementService _entitlementService;
    private readonly IPlayBillingClient? _playBillingClient;
    private readonly IConfiguration? _configuration;
    private readonly ILogger<SubscriptionService> _logger;

    private const string VerificationUnavailable =
        "Satın alma şu an doğrulanamıyor. Lütfen biraz sonra tekrar deneyin.";

    public SubscriptionService(
        SmartFinanceDbContext context,
        ICurrentUserService currentUserService,
        IEntitlementService entitlementService,
        IPlayBillingClient? playBillingClient = null,
        IConfiguration? configuration = null,
        ILogger<SubscriptionService>? logger = null)
    {
        _context = context;
        _currentUserService = currentUserService;
        _entitlementService = entitlementService;
        _playBillingClient = playBillingClient;
        _configuration = configuration;
        _logger = logger ?? NullLogger<SubscriptionService>.Instance;
    }

    public string AccountToken(int userId)
    {
        var key = _configuration?["Jwt:Key"];
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("Hesap bağlama anahtarı yapılandırılmamış.");
        var payload = "walletmark-user:" + userId.ToString(CultureInfo.InvariantCulture);
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public async Task<SubscriptionStatusDto> VerifyPurchaseAsync(
        string productId, string purchaseToken, CancellationToken ct)
    {
        if (!SubscriptionUpdater.IsProductSupported(productId))
            throw new BadRequestException("Geçersiz ürün.");
        if (string.IsNullOrWhiteSpace(purchaseToken) || purchaseToken.Length > 450)
            throw new BadRequestException("Geçersiz satın alma jetonu.");
        if (_playBillingClient is null || !_playBillingClient.IsConfigured)
            throw Unavailable();

        PlaySubscriptionInfo? info;
        try
        {
            info = await _playBillingClient.GetSubscriptionAsync(purchaseToken, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning("Play satın alımı sorgulanamadı. Hata türü: {ErrorType}.", ex.GetType().Name);
            throw Unavailable();
        }
        if (info is null) throw new BadRequestException("Satın alma bulunamadı.");
        if (info.ProductId != productId) throw new BadRequestException("Satın alınan ürün eşleşmiyor.");

        var userId = _currentUserService.UserId;
        if (!string.IsNullOrWhiteSpace(info.ObfuscatedAccountId)
            && !string.Equals(info.ObfuscatedAccountId, AccountToken(userId), StringComparison.Ordinal))
            throw new BadRequestException("Bu satın alma başka bir hesaba ait.");

        var row = await _context.Subscriptions.IgnoreQueryFilters()
            .SingleOrDefaultAsync(s => s.PurchaseToken == purchaseToken, ct);
        if (row is not null && row.UserId != userId)
            throw new BadRequestException("Bu satın alma başka bir hesaba ait.");

        if (info.State == "SUBSCRIPTION_STATE_PENDING")
        {
            var pending = await GetStatusAsync(ct);
            pending.PurchasePending = true;
            return pending;
        }

        var isNew = row is null;
        row ??= new Subscription { UserId = userId, ProductId = productId, PurchaseToken = purchaseToken };
        // Ürün aynı jeton için Google tarafından değişmişse sunucu kaydı da güncellenir.
        row.ProductId = productId;
        await SubscriptionUpdater.ApplyAsync(_context, row, info, _playBillingClient, _logger, DateTime.UtcNow, ct);
        row.IsDeleted = false;
        if (isNew) _context.Subscriptions.Add(row);
        await _context.SaveChangesAsync(ct);
        return await GetStatusAsync(ct);
    }

    private static ExternalServiceException Unavailable()
        => new("Google Play satın alma doğrulaması kullanılamıyor.", userMessage: VerificationUnavailable);

    public async Task<SubscriptionStatusDto> GetStatusAsync(CancellationToken ct = default)
    {
        var userId = _currentUserService.UserId;
        var now = DateTime.UtcNow;

        // Bitis tarihi ve otomatik yenileme bilgisi icin gercek abonelik
        // satiri gerekiyor. Birden fazla satir olabilir (yenilemeler, gecmis
        // kayitlar); gecerli olan en ilerideki bitis tarihine sahip olandir.
        // Odeme disi tanimli hesaplarda bu satir YOK, o yuzden premium
        // karari asagida ayrica soruluyor.
        var aktif = await _context.Subscriptions
            .AsNoTracking()
            .Where(s => s.UserId == userId && s.ExpiresAt > now)
            .OrderByDescending(s => s.ExpiresAt)
            .FirstOrDefaultAsync(ct);

        // Premium karari TEK yerden gelmeli. Burasi tabloyu dogrudan
        // sorguluyordu ve EntitlementService'in tanidigi odeme disi
        // hesaplari (Play inceleme hesabi) gormuyordu: sunucu sinirsiz
        // davranirken uygulama "Ucretsiz plan, 4/5 yatirim" gosteriyordu.
        // Ayni soruya iki ayri yerde cevap veren kod, er ya da gec iki
        // farkli cevap verir.
        // Doğrulama aynı scope'ta abonelik değiştirmiş olabilir; eski hak önbelleğini kullanma.
        var isPremium = await _entitlementService.IsPremiumAsync(userId, ct);

        var ayBasi = new DateTime(now.Year, now.Month, 1);

        var yatirimSayisi = await _context.Investments.CountAsync(x => x.UserId == userId, ct);
        var butceSayisi = await _context.Budgets.CountAsync(x => x.UserId == userId, ct);
        var iceAktarmaSayisi = await _context.ImportLogs
            .CountAsync(x => x.UserId == userId && x.CreatedDate >= ayBasi, ct);

        // Premium'da sinir null (sinirsiz) donuyor; uygulama "5/5" yerine
        // hicbir sayac gostermiyor.
        return new SubscriptionStatusDto
        {
            IsPremium = isPremium,
            // Eski, üç bağımlılıklı test/çağrı kurulumları yapılandırma taşımıyor.
            AccountToken = string.IsNullOrWhiteSpace(_configuration?["Jwt:Key"]) ? null : AccountToken(userId),
            ExpiresAt = aktif?.ExpiresAt,
            AutoRenewing = aktif?.AutoRenewing ?? false,
            IndicatorsIncluded = isPremium || FreeTierLimits.IndicatorsIncluded,
            Investments = new LimitUsageDto
            {
                Used = yatirimSayisi,
                Limit = isPremium ? null : FreeTierLimits.Investments,
            },
            Budgets = new LimitUsageDto
            {
                Used = butceSayisi,
                Limit = isPremium ? null : FreeTierLimits.Budgets,
            },
            MonthlyImports = new LimitUsageDto
            {
                Used = iceAktarmaSayisi,
                Limit = isPremium ? null : FreeTierLimits.ImportsPerMonth,
            },
        };
    }
}
