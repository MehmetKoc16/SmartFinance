using Microsoft.EntityFrameworkCore;
using SmartFinance.Application.Exceptions;
using SmartFinance.Application.Interfaces;
using SmartFinance.Infrastructure.Context;

namespace SmartFinance.Infrastructure.Services;

/// <summary>
/// Premium hakkını sunucudaki abonelik kaydından belirler.
///
/// İstemcinin beyanına güvenilmez: uygulama değiştirilebilir, istekler elle
/// atılabilir. Sınırların uygulandığı tek yer burasıdır.
/// </summary>
public class EntitlementService : IEntitlementService
{
    private readonly SmartFinanceDbContext _context;
    private readonly ICurrentUserService _currentUserService;

    // Ayni istek icinde birden fazla kez sorulabiliyor (ornegin yatirim
    // ekleme: once limit kontrolu, sonra yanit). Scoped servis oldugu icin
    // istek boyunca hatirlamak yeterli.
    private bool? _cached;

    public EntitlementService(SmartFinanceDbContext context, ICurrentUserService currentUserService)
    {
        _context = context;
        _currentUserService = currentUserService;
    }

    public async Task<bool> IsPremiumAsync(CancellationToken ct = default)
    {
        _cached ??= await IsPremiumAsync(_currentUserService.UserId, ct);
        return _cached.Value;
    }

    public async Task<bool> IsPremiumAsync(int userId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        // Iptal edilmis abonelik de bitis tarihine kadar gecerlidir — kullanici
        // parasini odedigi donemin hakkini kaybetmemeli. Bu yuzden AutoRenewing
        // degil yalnizca ExpiresAt'e bakiliyor.
        return await _context.Subscriptions
            .AsNoTracking()
            .AnyAsync(s => s.UserId == userId && s.ExpiresAt > now, ct);
    }

    public async Task EnsureWithinFreeLimitAsync(
        int currentCount, int limit, string message, CancellationToken ct = default)
    {
        if (await IsPremiumAsync(ct)) return;
        if (currentCount < limit) return;

        throw new PremiumRequiredException(message);
    }
}
