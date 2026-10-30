using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SmartFinance.Application.DTOs.Auth;
using SmartFinance.Application.Exceptions;
using SmartFinance.Infrastructure.Context;
using SmartFinance.Infrastructure.Services;

namespace SmartFinance.Tests;

/// 29.10.2026 denetimi: e-posta degisikligi sifre sormuyordu. Premium muafiyeti
/// e-postaya bagli ve e-posta sifre sifirlama kanali; acik unutulmus bir
/// oturumla hesap ele gecirilebilirdi.
/// Hatalar 400 (BadRequest) olmali: mobilde 401 oturum yenileme/kapatma akisini tetikler.
public class ProfileEmailChangeTests
{
    private const string Sifre = "Sifre123!";
    private const string EskiEmail = "eski@test.com";

    private static async Task<(AuthService service, SmartFinanceDbContext context, int userId, HttpContextAccessor accessor)> Kur()
    {
        var context = new SmartFinanceDbContext(new DbContextOptionsBuilder<SmartFinanceDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
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

        await service.RegisterAsync(new RegisterDto { FullName = "Eski Ad", Email = EskiEmail, Password = Sifre });
        var userId = context.Users.Single().Id;
        return (service, context, userId, accessor);
    }

    private static void GirisYap(HttpContextAccessor accessor, int userId)
    {
        var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) });
        accessor.HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
    }

    // NEDEN: Sifre gondermeyen istemci e-postayi degistirememeli; 401 degil 400.
    [Fact]
    public async Task EpostaDegisikligi_SifreYoksa_BadRequestVeEpostaDegismez()
    {
        var (service, context, userId, accessor) = await Kur();
        GirisYap(accessor, userId);

        var ex = await Assert.ThrowsAsync<BadRequestException>(() => service.UpdateProfileAsync(
            new UpdateProfileDto { FullName = "Eski Ad", Email = "yeni@test.com" }));

        Assert.Equal("E-posta değiştirmek için mevcut şifreniz gerekli.", ex.Message);
        Assert.Equal(EskiEmail, (await context.Users.FindAsync(userId))!.Email);
    }

    // NEDEN: Bos string de "sifre yok" sayilmali, BCrypt'e gitmemeli.
    [Fact]
    public async Task EpostaDegisikligi_BosSifre_BadRequest()
    {
        var (service, _, userId, accessor) = await Kur();
        GirisYap(accessor, userId);

        var ex = await Assert.ThrowsAsync<BadRequestException>(() => service.UpdateProfileAsync(
            new UpdateProfileDto { FullName = "Eski Ad", Email = "yeni@test.com", CurrentPassword = "" }));

        Assert.Equal("E-posta değiştirmek için mevcut şifreniz gerekli.", ex.Message);
    }

    // NEDEN: Yanlis sifre 400 donmeli (401 mobilde oturumu kapatir) ve hicbir alan degismemeli.
    [Fact]
    public async Task EpostaDegisikligi_YanlisSifre_BadRequestVeHicbirSeyDegismez()
    {
        var (service, context, userId, accessor) = await Kur();
        GirisYap(accessor, userId);

        var ex = await Assert.ThrowsAsync<BadRequestException>(() => service.UpdateProfileAsync(
            new UpdateProfileDto { FullName = "Yeni Ad", Email = "yeni@test.com", CurrentPassword = "Yanlis1!" }));

        Assert.Equal("Mevcut şifre hatalı.", ex.Message);
        var user = (await context.Users.FindAsync(userId))!;
        Assert.Equal(EskiEmail, user.Email);
        Assert.Equal("Eski Ad", user.FullName);
    }

    // NEDEN: Dogru sifreyle e-posta degisikligi eskisi gibi calismali.
    [Fact]
    public async Task EpostaDegisikligi_DogruSifre_Guncellenir()
    {
        var (service, context, userId, accessor) = await Kur();
        GirisYap(accessor, userId);

        await service.UpdateProfileAsync(
            new UpdateProfileDto { FullName = "Yeni Ad", Email = "yeni@test.com", CurrentPassword = Sifre });

        var user = (await context.Users.FindAsync(userId))!;
        Assert.Equal("yeni@test.com", user.Email);
        Assert.Equal("Yeni Ad", user.FullName);
    }

    // NEDEN: Eski istemciler CurrentPassword gondermiyor; yalniz ad degisikligi
    // sifresiz calismaya devam etmeli.
    [Fact]
    public async Task YalnizAdDegisikligi_SifresizGecer()
    {
        var (service, context, userId, accessor) = await Kur();
        GirisYap(accessor, userId);

        await service.UpdateProfileAsync(new UpdateProfileDto { FullName = "Yeni Ad", Email = EskiEmail });

        var user = (await context.Users.FindAsync(userId))!;
        Assert.Equal("Yeni Ad", user.FullName);
        Assert.Equal(EskiEmail, user.Email);
    }

    // NEDEN: Istemci e-postayi bosluklu gonderirse bu bir "degisiklik" sayilmamali;
    // karsilastirma kirpilmis deger uzerinden yapiliyor.
    [Fact]
    public async Task YalnizAdDegisikligi_EpostaBosluklu_SifresizGecer()
    {
        var (service, context, userId, accessor) = await Kur();
        GirisYap(accessor, userId);

        await service.UpdateProfileAsync(new UpdateProfileDto { FullName = "Yeni Ad", Email = $"  {EskiEmail} " });

        var user = (await context.Users.FindAsync(userId))!;
        Assert.Equal("Yeni Ad", user.FullName);
        Assert.Equal(EskiEmail, user.Email);
    }
}
