using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Globalization;
using System.Net;
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
    private readonly IEmailSender? _emailSender;

    private const string VerificationUnavailable =
        "Satın alma şu an doğrulanamıyor. Lütfen biraz sonra tekrar deneyin.";

    public SubscriptionService(
        SmartFinanceDbContext context,
        ICurrentUserService currentUserService,
        IEntitlementService entitlementService,
        IPlayBillingClient? playBillingClient = null,
        IConfiguration? configuration = null,
        ILogger<SubscriptionService>? logger = null,
        IEmailSender? emailSender = null)
    {
        _context = context;
        _currentUserService = currentUserService;
        _entitlementService = entitlementService;
        _playBillingClient = playBillingClient;
        _configuration = configuration;
        _logger = logger ?? NullLogger<SubscriptionService>.Instance;
        _emailSender = emailSender;
    }

    public async Task RecordPurchaseConsentAsync(string productId, string textVersion, CancellationToken ct)
    {
        if (!SubscriptionUpdater.IsProductSupported(productId))
            throw new BadRequestException("Geçersiz ürün.");
        if (!PurchaseConsentTexts.IsAllowed(textVersion))
            throw new BadRequestException("Geçersiz onay metni sürümü.");

        _context.PurchaseConsents.Add(new PurchaseConsent
        {
            UserId = _currentUserService.UserId,
            ProductId = productId,
            TextVersion = textVersion,
            ConsentedAt = DateTime.UtcNow,
        });
        await _context.SaveChangesAsync(ct);
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
        await BindPurchaseConsentAsync(row, ct);
        return await GetStatusAsync(ct);
    }

    private async Task BindPurchaseConsentAsync(Subscription subscription, CancellationToken ct)
    {
        // Restore/tekrar verify, kimliksiz saklananlar dahil aynı kanıtı yeniden bağlamaz.
        if (await _context.PurchaseConsents.IgnoreQueryFilters()
            .AnyAsync(x => x.PurchaseToken == subscription.PurchaseToken, ct)) return;

        var cutoff = DateTime.UtcNow.AddHours(-24);
        var consent = await _context.PurchaseConsents
            .Where(x => x.UserId == subscription.UserId && x.ProductId == subscription.ProductId
                && x.PurchaseToken == null && x.ConsentedAt >= cutoff)
            .OrderByDescending(x => x.ConsentedAt).ThenByDescending(x => x.Id)
            .FirstOrDefaultAsync(ct);
        if (consent is null)
        {
            _logger.LogWarning("OnaysizSatinAlma UserId={UserId} ProductId={ProductId}",
                subscription.UserId, subscription.ProductId);
            return;
        }

        consent.PurchaseToken = subscription.PurchaseToken;
        consent.OrderId = subscription.OrderId;
        await _context.SaveChangesAsync(ct);

        // Önce kanıt kalıcılaşır; gönderim hatası verify'ı bozmaz, tekrar verify mail göndermez.
        try
        {
            if (_emailSender is null) return;
            var email = await _context.Users.Where(x => x.Id == subscription.UserId)
                .Select(x => x.Email).SingleAsync(ct);
            static string Encode(string value) => WebUtility.HtmlEncode(value);
            static string TrDate(DateTime utc, string format) =>
                DateTime.SpecifyKind(utc, DateTimeKind.Utc).AddHours(3).ToString(format, CultureInfo.InvariantCulture);
            var plan = subscription.ProductId == "premium_monthly" ? "Aylık Premium" : "Yıllık Premium";
            var order = consent.OrderId is null ? "" : $"<p>Sipariş no: {Encode(consent.OrderId)}</p>";
            var body = $"""
                <p>Plan: {Encode(plan)}</p>
                <p>Başlangıç tarihi: {Encode(TrDate(subscription.StartsAt, "dd.MM.yyyy"))} (TSİ)</p>
                {order}
                <p>{Encode($"Satın almadan önce ön bilgilendirmeyi ve Kullanım ve Abonelik Koşulları'nı (sürüm {consent.TextVersion}) okuduğunuzu ve hizmetin cayma süresi dolmadan hemen başlamasını onayladığınızı {TrDate(consent.ConsentedAt, "dd.MM.yyyy HH:mm")} (TSİ) tarihinde bildirdiniz. Onayınızla ifasına başlanan hizmette cayma hakkı kullanılamaz (Mesafeli Sözleşmeler Yönetmeliği md.15/1-h).")}</p>
                <p>Aboneliğiniz siz iptal edene kadar her dönem sonunda otomatik yenilenir. İptal: Google Play &gt; Profil &gt; Ödemeler ve abonelikler &gt; Abonelikler.</p>
                <p><a href="https://play.google.com/store/account/subscriptions?package=com.walletmark.app">Aboneliği yönet</a></p>
                <p><a href="https://walletmark.com.tr/kosullar">Koşullar</a> · <a href="https://walletmark.com.tr/gizlilik">Gizlilik</a></p>
                <p>{Encode("Sağlayıcı: Mehmet Koç (Wallet Mark) · destek@walletmark.com.tr")}</p>
                """;
            await _emailSender.SendAsync(email, "Wallet Mark Premium aboneliğiniz başladı", body, ct);
        }
        catch (Exception ex)
        {
            // İstisna mesajı adres içerebilir; yalnız kullanıcı ve hata türü loglanır.
            _logger.LogWarning("Satın alma teyit e-postası gönderilemedi. UserId={UserId} ErrorType={ErrorType}",
                subscription.UserId, ex.GetType().Name);
        }
    }

    private static ExternalServiceException Unavailable()
        => new("Google Play satın alma doğrulaması kullanılamıyor.", userMessage: VerificationUnavailable);

    /// <summary>
    /// Aktif aboneliği olmayan ama süresi son 30 günde dolmuş, otomatik
    /// yenilenen en son satırı (2 dakikadan eski doğrulanmışsa) Google'a sorar.
    /// Google hatası durum ucunu ASLA bozmaz: uyarı loglanır, eski veriyle devam edilir.
    /// </summary>
    private static readonly TimeSpan GoogleTimeout = TimeSpan.FromSeconds(5);

    private async Task RefreshLapsedSubscriptionAsync(int userId, DateTime now, CancellationToken ct)
    {
        if (_playBillingClient is null || !_playBillingClient.IsConfigured) return;
        try
        {
            var activeExists = await _context.Subscriptions.AsNoTracking()
                .AnyAsync(s => s.UserId == userId && s.ExpiresAt > now, ct);
            if (activeExists) return;

            var from = now.Subtract(SubscriptionRefresher.ExpiredLookback);
            // Değişecek satır olduğu için izlenen (tracked) olarak yüklenir.
            var candidate = await _context.Subscriptions
                .Where(s => s.UserId == userId && s.ExpiresAt >= from && s.ExpiresAt <= now && s.AutoRenewing)
                .OrderByDescending(s => s.ExpiresAt).ThenByDescending(s => s.Id)
                .FirstOrDefaultAsync(ct);
            if (candidate is null) return;

            var staleLimit = now.Subtract(SubscriptionRefresher.StatusStaleAfter);
            if (candidate.LastVerifiedAt is not null && candidate.LastVerifiedAt >= staleLimit) return;

            // Google yanıt vermezse istemcinin varsayılan 100 sn beklemesi durum
            // ucunu (uygulama açılışını) kilitlerdi; en fazla 5 sn beklenir.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(GoogleTimeout);
            await SubscriptionRefresher.RefreshAsync(_context, candidate, _playBillingClient, _logger, now, timeout.Token);
            await _context.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Jeton/e-posta içerebilecek hata metni loglanmaz; yalnızca hata türü.
            _logger.LogWarning("Süresi dolmuş abonelik anında yenilenemedi. Hata türü: {ErrorType}.",
                ex.GetType().Name);
            // Yarım kalmış değişiklikler sonraki SaveChanges'e taşınmasın.
            foreach (var entry in _context.ChangeTracker.Entries()
                         .Where(e => e.State is EntityState.Modified or EntityState.Added or EntityState.Deleted)
                         .ToList())
                entry.State = EntityState.Detached;
        }
    }

    public async Task<SubscriptionStatusDto> GetStatusAsync(CancellationToken ct = default)
    {
        var userId = _currentUserService.UserId;
        var now = DateTime.UtcNow;

        // Google yenilemeyi bildirmiyor: süresi yeni dolmuş ama otomatik yenilenen
        // bir abonelik varsa kullanıcı sorduğu anda Google'a danışılır. Premium
        // kararından ÖNCE yapılmalı; yoksa yenilenmiş kullanıcı saatlerce ücretsiz görünür.
        await RefreshLapsedSubscriptionAsync(userId, now, ct);

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
