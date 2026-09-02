using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SmartFinance.Application.DTOs.Auth;
using SmartFinance.Application.Exceptions;
using SmartFinance.Domain.Entities;
using SmartFinance.Infrastructure.Context;
using SmartFinance.Infrastructure.Services;

namespace SmartFinance.Tests;

/// Sifre sifirlama, hesabin anahtarini degistiren bir akis: yanlis yazilmasi
/// hesap ele gecirmeye acik kapi birakir. Bu yuzden yalnizca "calisiyor mu"
/// degil, kotuye kullanim yollari da test ediliyor.
public class PasswordResetTests
{
    private static (AuthService service, SmartFinanceDbContext context, FakeEmailSender posta) Kur()
    {
        var options = new DbContextOptionsBuilder<SmartFinanceDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString()).Options;
        var context = new SmartFinanceDbContext(options);

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            {"Jwt:Key","TestGizliAnahtar123456789012345678"},
            {"Jwt:Issuer","TestIssuer"},
            {"Jwt:Audience","TestAudience"},
            {"Jwt:ExpireMinutes","60"},
            {"Jwt:RefreshTokenExpireDays","30"},
            {"App:WebBaseUrl","https://walletmark.com.tr"},
            // Zamanlama tabani testlerde kapali: her cagriya 1,2 sn eklerdi.
            {"Auth:ForgotPasswordMinResponseMs","0"},
        }).Build();

        var posta = new FakeEmailSender();
        var accessor = new HttpContextAccessor();
        var service = new AuthService(context, config, new CurrentUserService(accessor),
            posta, NullLogger<AuthService>.Instance);
        return (service, context, posta);
    }

    private static User KullaniciEkle(SmartFinanceDbContext c, string email, string sifre = "EskiSifre1!")
    {
        var u = new User
        {
            FullName = "Test Kullanıcı",
            Email = email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(sifre),
        };
        c.Users.Add(u);
        c.SaveChanges();
        return u;
    }

    [Fact]
    public async Task Talep_EpostaGonderilirVeTokenUretilir()
    {
        var (service, context, posta) = Kur();
        var u = KullaniciEkle(context, "a@test.com");

        await service.ForgotPasswordAsync(new ForgotPasswordDto { Email = "a@test.com" });

        var mail = Assert.Single(posta.Kutu);
        Assert.Equal("a@test.com", mail.To);
        Assert.Contains("walletmark.com.tr/sifre-sifirla?token=", mail.HtmlBody);
        Assert.Single(context.PasswordResetTokens.Where(t => t.UserId == u.Id));
    }

    /// Token'in KENDISI veritabaninda durmamali: veritabani sizarsa elindeki
    /// kayitlarla kimse hesap ele geciremesin.
    [Fact]
    public async Task Token_VeritabaninaDuzMetinYazilmaz()
    {
        var (service, context, posta) = Kur();
        KullaniciEkle(context, "a@test.com");

        await service.ForgotPasswordAsync(new ForgotPasswordDto { Email = "a@test.com" });
        var token = posta.SonToken()!;

        var kayit = context.PasswordResetTokens.Single();
        Assert.NotEqual(token, kayit.TokenHash);
        Assert.DoesNotContain(token, kayit.TokenHash);
    }

    /// Kayitli olmayan adres icin de ayni sekilde donmeli ve HATA
    /// FIRLATMAMALI: aksi halde bu uc "hangi e-postalar kayitli" sorgulama
    /// araci olurdu.
    [Fact]
    public async Task KayitliOlmayanAdres_HataFirlatmaz_EpostaGondermez()
    {
        var (service, _, posta) = Kur();

        await service.ForgotPasswordAsync(new ForgotPasswordDto { Email = "yok@test.com" });

        Assert.Empty(posta.Kutu);
    }

    [Fact]
    public async Task GecerliToken_SifreyiDegistirir()
    {
        var (service, context, posta) = Kur();
        var u = KullaniciEkle(context, "a@test.com");
        await service.ForgotPasswordAsync(new ForgotPasswordDto { Email = "a@test.com" });
        var token = posta.SonToken()!;

        await service.ResetPasswordAsync(new ResetPasswordDto { Token = token, NewPassword = "YeniSifre1!" });

        var guncel = context.Users.Single(x => x.Id == u.Id);
        Assert.True(BCrypt.Net.BCrypt.Verify("YeniSifre1!", guncel.PasswordHash));
        Assert.False(BCrypt.Net.BCrypt.Verify("EskiSifre1!", guncel.PasswordHash));
    }

    /// Tek kullanimlik olmali: baglantiyi ele geciren biri, kullanici sifresini
    /// belirledikten sonra ayni baglantiyla tekrar degistirememeli.
    [Fact]
    public async Task Token_IkinciKezKullanilamaz()
    {
        var (service, context, posta) = Kur();
        KullaniciEkle(context, "a@test.com");
        await service.ForgotPasswordAsync(new ForgotPasswordDto { Email = "a@test.com" });
        var token = posta.SonToken()!;

        await service.ResetPasswordAsync(new ResetPasswordDto { Token = token, NewPassword = "YeniSifre1!" });

        await Assert.ThrowsAsync<BadRequestException>(
            () => service.ResetPasswordAsync(new ResetPasswordDto { Token = token, NewPassword = "BaskaSifre1!" }));
    }

    [Fact]
    public async Task SuresiDolmusToken_Reddedilir()
    {
        var (service, context, posta) = Kur();
        KullaniciEkle(context, "a@test.com");
        await service.ForgotPasswordAsync(new ForgotPasswordDto { Email = "a@test.com" });
        var token = posta.SonToken()!;

        var kayit = context.PasswordResetTokens.Single();
        kayit.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        context.SaveChanges();

        await Assert.ThrowsAsync<BadRequestException>(
            () => service.ResetPasswordAsync(new ResetPasswordDto { Token = token, NewPassword = "YeniSifre1!" }));
    }

    [Fact]
    public async Task GecersizToken_Reddedilir()
    {
        var (service, context, _) = Kur();
        KullaniciEkle(context, "a@test.com");

        await Assert.ThrowsAsync<BadRequestException>(
            () => service.ResetPasswordAsync(new ResetPasswordDto { Token = "uydurma-token", NewPassword = "YeniSifre1!" }));
    }

    /// Yeni talep, onceki bekleyen baglantilari gecersiz kilmali: kullanici
    /// arka arkaya istek atarsa yalnizca sonuncusu calissin.
    [Fact]
    public async Task YeniTalep_OncekiTokenıGecersizKilar()
    {
        var (service, context, posta) = Kur();
        KullaniciEkle(context, "a@test.com");

        await service.ForgotPasswordAsync(new ForgotPasswordDto { Email = "a@test.com" });
        var ilkToken = posta.SonToken()!;
        await service.ForgotPasswordAsync(new ForgotPasswordDto { Email = "a@test.com" });
        var ikinciToken = posta.SonToken()!;

        Assert.NotEqual(ilkToken, ikinciToken);
        await Assert.ThrowsAsync<BadRequestException>(
            () => service.ResetPasswordAsync(new ResetPasswordDto { Token = ilkToken, NewPassword = "YeniSifre1!" }));

        // Sonuncusu calismali.
        await service.ResetPasswordAsync(new ResetPasswordDto { Token = ikinciToken, NewPassword = "YeniSifre1!" });
    }

    /// Sifre degistiyse mevcut oturumlar dusmeli: hesabi ele geciren biri
    /// varsa yenileme token'iyla erisimini surdurememeli.
    [Fact]
    public async Task SifirlamaSonrasi_MevcutOturumlarIptalEdilir()
    {
        var (service, context, posta) = Kur();
        var u = KullaniciEkle(context, "a@test.com");
        context.RefreshTokens.Add(new RefreshToken
        {
            UserId = u.Id, Token = Guid.NewGuid().ToString(), ExpiresAt = DateTime.UtcNow.AddDays(30),
        });
        context.SaveChanges();

        await service.ForgotPasswordAsync(new ForgotPasswordDto { Email = "a@test.com" });
        await service.ResetPasswordAsync(new ResetPasswordDto
        { Token = posta.SonToken()!, NewPassword = "YeniSifre1!" });

        Assert.All(context.RefreshTokens.Where(r => r.UserId == u.Id),
            r => Assert.NotNull(r.RevokedAt));
    }

    /// Baska kullanicinin sifresi etkilenmemeli.
    [Fact]
    public async Task BaskaKullanicininSifresi_Etkilenmez()
    {
        var (service, context, posta) = Kur();
        KullaniciEkle(context, "a@test.com");
        var digeri = KullaniciEkle(context, "b@test.com", "DigerSifre1!");

        await service.ForgotPasswordAsync(new ForgotPasswordDto { Email = "a@test.com" });
        await service.ResetPasswordAsync(new ResetPasswordDto
        { Token = posta.SonToken()!, NewPassword = "YeniSifre1!" });

        var digerGuncel = context.Users.Single(x => x.Id == digeri.Id);
        Assert.True(BCrypt.Net.BCrypt.Verify("DigerSifre1!", digerGuncel.PasswordHash));
    }

    /// SMTP kesintisi CAGIRANA yansimamali.
    ///
    /// Yansisaydi: kayitli adres 500, kayitsiz adres 200 donerdi. O anda
    /// yanitlarin ayni olmasina dayanan hesap-sizdirmama korumasi tamamen
    /// bosa cikar, uc bir "bu e-posta kayitli mi" sorgulama aracina donerdi.
    /// Brevo anahtari 90 gun kullanilmazsa kendiliginden gecersiz oldugu icin
    /// bu varsayimsal degil, beklenen bir senaryo.
    [Fact]
    public async Task Gonderim_Coktugunde_HataFirlatilmaz()
    {
        var (service, context, posta) = Kur();
        KullaniciEkle(context, "a@test.com");
        posta.Hata = new InvalidOperationException("SMTP baglantisi kurulamadi");

        // Firlatirsa test burada patlar.
        await service.ForgotPasswordAsync(new ForgotPasswordDto { Email = "a@test.com" });
    }

    /// Gonderim coktugunde uretilmis token da kapatilmali: kullaniciya HIC
    /// ulasmamis bir baglanti 60 dakika acik kalmamali.
    [Fact]
    public async Task Gonderim_Coktugunde_TokenIptalEdilir()
    {
        var (service, context, posta) = Kur();
        KullaniciEkle(context, "a@test.com");
        posta.Hata = new InvalidOperationException("SMTP baglantisi kurulamadi");

        await service.ForgotPasswordAsync(new ForgotPasswordDto { Email = "a@test.com" });

        var kayit = Assert.Single(context.PasswordResetTokens);
        Assert.NotNull(kayit.UsedAt);
    }

    /// Suresi dolmus kayitlar birikmemeli: tablo yalnizca bu uc uzerinden
    /// buyuyor ve hicbir sey silmiyordu.
    [Fact]
    public async Task Talep_SuresiCoktanDolmusKayitlariTemizler()
    {
        var (service, context, _) = Kur();
        var u = KullaniciEkle(context, "a@test.com");

        context.PasswordResetTokens.AddRange(
            // 8 gun once dolmus -> silinmeli
            new PasswordResetToken { UserId = u.Id, TokenHash = "eski", ExpiresAt = DateTime.UtcNow.AddDays(-8) },
            // dun dolmus -> HENUZ silinmemeli (sorun ayiklama payi)
            new PasswordResetToken { UserId = u.Id, TokenHash = "dun", ExpiresAt = DateTime.UtcNow.AddDays(-1) });
        await context.SaveChangesAsync();

        await service.ForgotPasswordAsync(new ForgotPasswordDto { Email = "a@test.com" });

        var kalanlar = context.PasswordResetTokens.Select(t => t.TokenHash).ToList();
        Assert.DoesNotContain("eski", kalanlar);
        Assert.Contains("dun", kalanlar);
    }
}
