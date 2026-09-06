using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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
    private readonly IConfiguration _configuration;

    // Ayni istek icinde birden fazla kez sorulabiliyor (ornegin yatirim
    // ekleme: once limit kontrolu, sonra yanit). Scoped servis oldugu icin
    // istek boyunca hatirlamak yeterli.
    private bool? _cached;

    public EntitlementService(SmartFinanceDbContext context,
        ICurrentUserService currentUserService, IConfiguration configuration)
    {
        _context = context;
        _currentUserService = currentUserService;
        _configuration = configuration;
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
        var aboneligiVar = await _context.Subscriptions
            .AsNoTracking()
            .AnyAsync(s => s.UserId == userId && s.ExpiresAt > now, ct);

        return aboneligiVar || await UcretsizTanimliMiAsync(userId, ct);
    }

    /// <summary>
    /// Ödeme olmadan premium tanımlanmış hesapları belirler.
    ///
    /// Tek kullanım amacı Google Play inceleme hesabı. Play, inceleme
    /// ekibinin satın alma yapmasına veya ücretsiz deneme kullanmasına izin
    /// vermiyor; buna rağmen "bu hesap tüm içeriğe erişir" beyanını zorunlu
    /// tutuyor. İki kural ancak hesaba peşinen premium verilerek karşılanıyor.
    ///
    /// Veritabanına elle satır eklemek yerine yapılandırmadan okunuyor:
    /// yedekten geri yüklemede kaybolmuyor, kod incelemesinde görünüyor ve
    /// hangi hesabın neden ayrıcalıklı olduğu tek yerde yazıyor. Listede
    /// yalnızca E-POSTA var; parola yapılandırmada durmuyor.
    /// </summary>
    private async Task<bool> UcretsizTanimliMiAsync(int userId, CancellationToken ct)
    {
        // GetChildren + Value kullaniliyor; Get<string[]>() icin ayri bir
        // yapilandirma-baglayici paketi gerekiyordu, tek bir liste okumak
        // ugruna bagimlilik eklemeye degmez.
        var tanimlilar = _configuration
            .GetSection("Entitlement:ComplimentaryEmails")
            .GetChildren()
            .Select(x => x.Value)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToArray();

        // Normal durumda liste boş; o zaman ek sorgu hiç yapılmıyor.
        if (tanimlilar.Length == 0) return false;

        var eposta = await _context.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => u.Email)
            .FirstOrDefaultAsync(ct);

        return eposta is not null
            && tanimlilar.Contains(eposta, StringComparer.OrdinalIgnoreCase);
    }

    public async Task EnsureWithinFreeLimitAsync(
        int currentCount, int limit, string message, CancellationToken ct = default)
    {
        if (await IsPremiumAsync(ct)) return;
        if (currentCount < limit) return;

        throw new PremiumRequiredException(message);
    }
}
