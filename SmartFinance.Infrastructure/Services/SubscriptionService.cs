using Microsoft.EntityFrameworkCore;
using SmartFinance.Application.Common;
using SmartFinance.Application.DTOs.Subscription;
using SmartFinance.Application.Interfaces;
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

    public SubscriptionService(
        SmartFinanceDbContext context,
        ICurrentUserService currentUserService,
        IEntitlementService entitlementService)
    {
        _context = context;
        _currentUserService = currentUserService;
        _entitlementService = entitlementService;
    }

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
        var isPremium = await _entitlementService.IsPremiumAsync(ct);

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
