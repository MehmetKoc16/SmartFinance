using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SmartFinance.Application.DTOs.Auth;
using SmartFinance.Application.Exceptions;
using SmartFinance.Application.Interfaces;
using SmartFinance.Domain.Entities;
using SmartFinance.Infrastructure.Context;
using SmartFinance.Infrastructure.Services;

namespace SmartFinance.Tests;

public class IdentitySecurityTests
{
    // InMemory SaveChanges atomik değildir: Added satırı yazıp ardından eski
    // token'ın eşzamanlılık kontrolünde düşebilir. SQL Server bu eklemeyi aynı
    // işlemin parçası olarak geri alır. Yalnız yarış testinde bu geri almayı
    // taklit ediyoruz; eşzamanlılık hatasını sağlayıcının kendisi üretmelidir.
    private sealed class InMemoryRotasyonBaglami(DbContextOptions<SmartFinanceDbContext> options)
        : SmartFinanceDbContext(options)
    {
        public bool GeriAlindi { get; private set; }

        public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess,
            CancellationToken cancellationToken = default)
        {
            try
            {
                return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                var adaylar = ChangeTracker.Entries<RefreshToken>()
                    .Where(e => e.State == EntityState.Added).Select(e => e.Entity.Token).ToArray();
                using var cleanup = new SmartFinanceDbContext(options);
                var yazilanlar = await cleanup.RefreshTokens.IgnoreQueryFilters()
                    .Where(t => adaylar.Contains(t.Token)).ToListAsync(cancellationToken);
                cleanup.RefreshTokens.RemoveRange(yazilanlar);
                await cleanup.SaveChangesAsync(cancellationToken);
                GeriAlindi = true;
                throw;
            }
        }
    }

    private sealed class KayitOncesi : SaveChangesInterceptor
    {
        public Func<Task>? ArayaGir { get; set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var action = ArayaGir;
            ArayaGir = null;
            if (action != null) await action();
            return result;
        }
    }

    private static DbContextOptions<SmartFinanceDbContext> Secenekler() =>
        new DbContextOptionsBuilder<SmartFinanceDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString(), new InMemoryDatabaseRoot()).Options;

    private static AuthService Servis(SmartFinanceDbContext db)
    {
        var current = new Mock<ICurrentUserService>();
        current.SetupGet(c => c.UserId).Returns(1);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = "SyntheticTestSigningKey01234567890123456789",
            ["Jwt:Issuer"] = "TestIssuer", ["Jwt:Audience"] = "TestAudience",
            ["Jwt:ExpireMinutes"] = "60", ["Jwt:RefreshTokenExpireDays"] = "30"
        }).Build();
        return new AuthService(db, config, current.Object, new FakeEmailSender(), NullLogger<AuthService>.Instance);
    }

    private static Task<TokenDto> Kayit(AuthService service) => service.RegisterAsync(new RegisterDto
    {
        FullName = "Sentetik Deneme", Email = "identity@example.invalid", Password = "EskiSifre1!"
    });

    private static async Task Baglantilar(SmartFinanceDbContext db)
    {
        db.PasswordResetTokens.AddRange(
            new PasswordResetToken { UserId = 1, TokenHash = AuthService.HashToken("ilk"), ExpiresAt = DateTime.UtcNow.AddHours(1) },
            new PasswordResetToken { UserId = 1, TokenHash = AuthService.HashToken("diger"), ExpiresAt = DateTime.UtcNow.AddHours(1) },
            new PasswordResetToken { UserId = 1, TokenHash = AuthService.HashToken("suresi-dolmus"), ExpiresAt = DateTime.UtcNow.AddHours(-1) },
            new PasswordResetToken { UserId = 2, TokenHash = AuthService.HashToken("baska-kullanici"), ExpiresAt = DateTime.UtcNow.AddHours(1) });
        await db.SaveChangesAsync();
    }

    private static async Task GecersizliginiDogrula(AuthService service, SmartFinanceDbContext db)
    {
        db.ChangeTracker.Clear();
        Assert.All(await db.PasswordResetTokens.Where(t => t.UserId == 1).ToListAsync(), t => Assert.NotNull(t.UsedAt));
        Assert.Null((await db.PasswordResetTokens.SingleAsync(t => t.UserId == 2)).UsedAt);
        foreach (var token in new[] { "ilk", "diger" })
            await Assert.ThrowsAsync<BadRequestException>(() => service.ResetPasswordAsync(
                new ResetPasswordDto { Token = token, NewPassword = "SaldirganSifre3!" }));
    }

    [Fact]
    public async Task RefreshToken_AyniTokenIkiBaglamdaKullanilirsa_YalnizBiriBasarili()
    {
        // NEDEN: İki istek iptalden önce aynı aktif satırı okusa da tek oturum üretebilmeli.
        var options = Secenekler();
        using var ilkDb = new SmartFinanceDbContext(options);
        using var ikinciDb = new InMemoryRotasyonBaglami(options);
        Assert.NotSame(ilkDb, ikinciDb);
        var ilkService = Servis(ilkDb);
        var ikinciService = Servis(ikinciDb);
        var token = await Kayit(ilkService);
        var hash = AuthService.HashToken(token.RefreshToken);
        Assert.True((await ilkDb.RefreshTokens.Include(r => r.User).SingleAsync(r => r.Token == hash)).IsActive);
        var ikinciOkuma = await ikinciDb.RefreshTokens.Include(r => r.User).SingleAsync(r => r.Token == hash);
        Assert.True(ikinciOkuma.IsActive);
        Assert.Equal(EntityState.Unchanged, ikinciDb.Entry(ikinciOkuma).State);
        Assert.Null(ikinciDb.Entry(ikinciOkuma).Property(r => r.RevokedAt).OriginalValue);

        var yeni = await ilkService.RefreshTokenAsync(token.RefreshToken);
        var error = await Assert.ThrowsAsync<UnauthorizedException>(() => ikinciService.RefreshTokenAsync(token.RefreshToken));
        Assert.True(ikinciDb.GeriAlindi); // Reddin kaynağı gerçekten SaveChanges eşzamanlılık hatası.
        Assert.Equal("Oturum süresi dolmuş, lütfen tekrar giriş yapın.", error.Message);
        Assert.DoesNotContain(ikinciDb.ChangeTracker.Entries<RefreshToken>(), e => e.State == EntityState.Added);
        await ikinciDb.SaveChangesAsync();
        using var kontrol = new SmartFinanceDbContext(options);
        var active = Assert.Single(await kontrol.RefreshTokens.Where(r => r.UserId == 1 && r.RevokedAt == null).ToListAsync());
        Assert.Equal(AuthService.HashToken(yeni.RefreshToken), active.Token);
        Assert.Equal(2, await kontrol.RefreshTokens.CountAsync());
    }

    [Fact]
    public async Task Cikis_AyniTokenIkiBaglamdaIptalEdilirse_HataVermez()
    {
        // NEDEN: Yeni eşzamanlılık belirteci zaten iptal edilmiş oturumdan çıkışı bozmamalı.
        var options = Secenekler();
        using var ilkDb = new SmartFinanceDbContext(options);
        using var ikinciDb = new SmartFinanceDbContext(options);
        var ilkService = Servis(ilkDb);
        var ikinciService = Servis(ikinciDb);
        var token = await Kayit(ilkService);
        await ikinciDb.RefreshTokens.SingleAsync();
        await ilkService.LogoutAsync(token.RefreshToken);
        await ikinciService.LogoutAsync(token.RefreshToken);
        using var kontrol = new SmartFinanceDbContext(options);
        Assert.NotNull((await kontrol.RefreshTokens.SingleAsync()).RevokedAt);
    }

    [Fact]
    public async Task RefreshToken_NormalAkis_DonmeyeDevamEder()
    {
        // NEDEN: Yarış koruması normal rotasyonu engellememeli; eski token yeniden kabul edilmemeli.
        using var db = new SmartFinanceDbContext(Secenekler());
        var service = Servis(db);
        var ilk = await Kayit(service);
        var yeni = await service.RefreshTokenAsync(ilk.RefreshToken);
        Assert.False(string.IsNullOrWhiteSpace(yeni.Token));
        Assert.NotEqual(ilk.RefreshToken, yeni.RefreshToken);
        await Assert.ThrowsAsync<UnauthorizedException>(() => service.RefreshTokenAsync(ilk.RefreshToken));
        Assert.NotNull(await service.RefreshTokenAsync(yeni.RefreshToken));
    }

    [Fact]
    public async Task SifreDegisirken_RotasyonKazanirsa_YeniEskiOturumlarIptalEdilir()
    {
        // NEDEN: Toplu iptal yarışı yeniden denediğinde arada üretilen token açık kalmamalı.
        var interceptor = new KayitOncesi();
        var options = new DbContextOptionsBuilder<SmartFinanceDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).AddInterceptors(interceptor).Options;
        using var db = new SmartFinanceDbContext(options);
        using var rakipDb = new SmartFinanceDbContext(options);
        var service = Servis(db);
        var rakipService = Servis(rakipDb);
        var eski = await Kayit(service);
        await Baglantilar(db);
        TokenDto? aradaUretilen = null;
        interceptor.ArayaGir = async () =>
        {
            // İptal yeniden denenmeden yeni oturum ekleme aşamasına geçilmemeli.
            Assert.DoesNotContain(db.ChangeTracker.Entries<RefreshToken>(), e => e.State == EntityState.Added);
            aradaUretilen = await rakipService.RefreshTokenAsync(eski.RefreshToken);
        };

        var yeni = await service.ChangePasswordAsync(new ChangePasswordDto
        {
            CurrentPassword = "EskiSifre1!", NewPassword = "YeniSifre2!"
        });

        using var kontrol = new SmartFinanceDbContext(options);
        var active = Assert.Single(await kontrol.RefreshTokens.Where(t => t.UserId == 1 && t.RevokedAt == null).ToListAsync());
        Assert.Equal(AuthService.HashToken(yeni.RefreshToken), active.Token);
        Assert.NotNull(aradaUretilen);
        Assert.Equal(3, await kontrol.RefreshTokens.CountAsync());
        Assert.DoesNotContain(db.ChangeTracker.Entries<RefreshToken>(), e => e.State == EntityState.Added);
        await Assert.ThrowsAsync<UnauthorizedException>(() => Servis(kontrol).RefreshTokenAsync(aradaUretilen!.RefreshToken));
        Assert.True(BCrypt.Net.BCrypt.Verify("YeniSifre2!", (await kontrol.Users.SingleAsync()).PasswordHash));
        Assert.All(await kontrol.PasswordResetTokens.Where(t => t.UserId == 1).ToListAsync(), t => Assert.NotNull(t.UsedAt));
    }

    [Fact]
    public async Task SifreDegisince_KullanilmamisSifirlamaBaglantisiGecersiz()
    {
        // NEDEN: Eski bağlantıyı elinde tutan biri yeni şifreyi geri değiştirememeli.
        using var db = new SmartFinanceDbContext(Secenekler());
        var service = Servis(db);
        await Kayit(service);
        await Baglantilar(db);
        await service.ChangePasswordAsync(new ChangePasswordDto { CurrentPassword = "EskiSifre1!", NewPassword = "YeniSifre2!" });
        await GecersizliginiDogrula(service, db);
    }

    [Fact]
    public async Task SifreSifirlaninca_DigerSifirlamaBaglantilariGecersiz()
    {
        // NEDEN: Başarılı sıfırlama yalnız kullanılanı değil diğer açık bağlantıları da tüketmeli.
        using var db = new SmartFinanceDbContext(Secenekler());
        var service = Servis(db);
        await Kayit(service);
        await Baglantilar(db);
        await service.ResetPasswordAsync(new ResetPasswordDto { Token = "ilk", NewPassword = "YeniSifre2!" });
        await GecersizliginiDogrula(service, db);
    }

    [Fact]
    public async Task EpostaDegisince_SifirlamaBaglantisiGecersiz()
    {
        // NEDEN: Eski posta kutusuna gönderilen bağlantı yeni e-postalı hesabı ele geçirememeli.
        using var db = new SmartFinanceDbContext(Secenekler());
        var service = Servis(db);
        await Kayit(service);
        await Baglantilar(db);
        await service.UpdateProfileAsync(new UpdateProfileDto
        {
            FullName = "Sentetik Deneme", Email = "yeni@example.invalid", CurrentPassword = "EskiSifre1!"
        });
        await GecersizliginiDogrula(service, db);
    }

    [Fact]
    public async Task EpostaDegismeyince_SifirlamaBaglantisiGecerliKalir()
    {
        // NEDEN: Yalnız ad veya e-postanın harf boyutu değiştiğinde bağlantılar iptal edilmemeli.
        using var db = new SmartFinanceDbContext(Secenekler());
        var service = Servis(db);
        await Kayit(service);
        await Baglantilar(db);
        await service.UpdateProfileAsync(new UpdateProfileDto { FullName = "Yeni Ad", Email = "IDENTITY@example.invalid" });
        db.ChangeTracker.Clear();
        Assert.All(await db.PasswordResetTokens.Where(t => t.UserId == 1).ToListAsync(), t => Assert.Null(t.UsedAt));
    }
}
