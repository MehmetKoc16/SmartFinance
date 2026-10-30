using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SmartFinance.Application.DTOs.Auth;
using SmartFinance.Application.Exceptions;
using SmartFinance.Infrastructure.Context;
using SmartFinance.Infrastructure.Services;

namespace SmartFinance.Tests;

/// 29.10.2026 denetimi: refresh token'lar DB'de duz metin duruyordu. DB ya da
/// yedek sizarsa 30 gunluk oturumlar dogrudan ele gecirilirdi. Artik DB'de
/// yalnizca SHA-256 ozeti tutuluyor; istemciye giden deger degismedi.
public class RefreshTokenHashTests
{
    private static (AuthService service, SmartFinanceDbContext context, HttpContextAccessor accessor) Kur()
    {
        var options = new DbContextOptionsBuilder<SmartFinanceDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            // Hesap silme transaction aciyor; InMemory desteklemiyor.
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        var context = new SmartFinanceDbContext(options);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            {"Jwt:Key","TestGizliAnahtar123456789012345678"},
            {"Jwt:Issuer","TestIssuer"},
            {"Jwt:Audience","TestAudience"},
            {"Jwt:ExpireMinutes","60"},
            {"Jwt:RefreshTokenExpireDays","30"}
        }).Build();
        var accessor = new HttpContextAccessor();
        var service = new AuthService(context, config, new CurrentUserService(accessor),
            new FakeEmailSender(), NullLogger<AuthService>.Instance);
        return (service, context, accessor);
    }

    private static void OturumAc(HttpContextAccessor accessor, int userId)
    {
        var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) });
        accessor.HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
    }

    private static Task<TokenDto> Kayit(AuthService service, string email = "ozet@test.com") =>
        service.RegisterAsync(new RegisterDto { FullName = "Ozet Deneme", Email = email, Password = "Sifre123!" });

    // NEDEN: Sizan DB satiri istemcinin elindeki degerle ayni olursa hash'lemenin
    // hicbir anlami kalmaz. Satir, yardimcinin ozetine esit olmali.
    [Fact]
    public async Task Giris_DbyeYazilanToken_IstemciyeDonenDegerDegil_Ozetidir()
    {
        var (service, context, _) = Kur();
        await Kayit(service);
        var giris = await service.LoginAsync(new LoginDto { Email = "ozet@test.com", Password = "Sifre123!" });

        var kayitlar = context.RefreshTokens.Select(r => r.Token).ToList();

        Assert.DoesNotContain(giris.RefreshToken, kayitlar);
        Assert.Contains(AuthService.HashToken(giris.RefreshToken), kayitlar);
        Assert.All(kayitlar, t => Assert.Equal(44, t.Length));
    }

    // NEDEN: Migration'daki SQL (HASHBYTES + base64) bu bicimi uretmek zorunda.
    // Bicim degisirse donusturulmus eski oturumlar eslesmez; bu test bicimi sabitler.
    // Beklenen: Base64(SHA-256(UTF-8("abc"))), dolgulu, 0x oneki yok.
    [Fact]
    public void HashToken_Bicimi_Base64Sha256Utf8()
    {
        Assert.Equal("ungWv48Bz+pBQUDeXa4iI7ADYaOWF3qctBD/YfIAFa0=", AuthService.HashToken("abc"));
    }

    // NEDEN: Asil guvenlik ozelligi: DB'den sizan ozet, istemci token'i gibi
    // kullanilarak oturum yenilenememeli.
    [Fact]
    public async Task DbdekiOzetGonderilirse_RefreshReddedilir()
    {
        var (service, context, _) = Kur();
        var kayit = await Kayit(service);
        var sizanDeger = context.RefreshTokens.Single().Token;

        await Assert.ThrowsAsync<UnauthorizedException>(() => service.RefreshTokenAsync(sizanDeger));

        // Duz deger hala calisiyor.
        Assert.NotNull(await service.RefreshTokenAsync(kayit.RefreshToken));
    }

    // NEDEN: Refresh rotasyonunda yeni token da ozetli yazilmali; aksi halde
    // ilk yenilemeden sonra duz metin geri doner.
    [Fact]
    public async Task Refresh_YeniTokenDaOzetliYazilir()
    {
        var (service, context, _) = Kur();
        var kayit = await Kayit(service);

        var yeni = await service.RefreshTokenAsync(kayit.RefreshToken);

        var yeniOzet = AuthService.HashToken(yeni.RefreshToken);
        var satir = await context.RefreshTokens.SingleAsync(r => r.Token == yeniOzet);
        Assert.Null(satir.RevokedAt);
        Assert.DoesNotContain(context.RefreshTokens, r => r.Token == yeni.RefreshToken);
    }

    // NEDEN: Logout ozet uzerinden arama yapmazsa kaydi bulamaz ve token'i
    // sessizce acik birakir (LogoutAsync bulamayinca hata vermiyor).
    [Fact]
    public async Task Logout_OzetliKaydiIptalEder()
    {
        var (service, context, _) = Kur();
        var kayit = await Kayit(service);

        await service.LogoutAsync(kayit.RefreshToken);

        var ozet = AuthService.HashToken(kayit.RefreshToken);
        Assert.NotNull((await context.RefreshTokens.SingleAsync(r => r.Token == ozet)).RevokedAt);
    }

    // NEDEN: Bos/null deger ozetlenirken patlayip 500 donmemeli; eski davranis
    // (401 / sessiz cikis) korunmali.
    [Fact]
    public async Task BosToken_RefreshteUnauthorized_LogoutSessiz()
    {
        var (service, _, _) = Kur();

        await Assert.ThrowsAsync<UnauthorizedException>(() => service.RefreshTokenAsync(""));
        Assert.Null(await Record.ExceptionAsync(() => service.LogoutAsync("")));
    }

    // NEDEN: Hesap silme, gercek giris akisiyla (ozetli) yazilmis token'lari da
    // kaldirmali ve silinen hesabin token'i yenileme yapamamali.
    [Fact]
    public async Task HesapSilme_OzetliTokenlariKaldirir()
    {
        var (service, context, accessor) = Kur();
        var kayit = await Kayit(service);
        var digeri = await Kayit(service, "kalan@test.com");
        var userId = context.Users.Single(u => u.Email == "ozet@test.com").Id;
        OturumAc(accessor, userId);

        await service.DeleteAccountAsync(new DeleteAccountDto { Password = "Sifre123!" });

        Assert.Empty(context.RefreshTokens.IgnoreQueryFilters().Where(r => r.UserId == userId));
        await Assert.ThrowsAsync<UnauthorizedException>(() => service.RefreshTokenAsync(kayit.RefreshToken));
        Assert.NotNull(await service.RefreshTokenAsync(digeri.RefreshToken));
    }
}
