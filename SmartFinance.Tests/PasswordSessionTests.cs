using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SmartFinance.API.Controllers;
using SmartFinance.Application.DTOs.Auth;
using SmartFinance.Application.Exceptions;
using SmartFinance.Application.Interfaces;
using SmartFinance.Infrastructure.Context;
using SmartFinance.Infrastructure.Services;

namespace SmartFinance.Tests;

public class PasswordSessionTests
{
    private static (AuthService service, SmartFinanceDbContext context, FakeEmailSender mail) Kur()
    {
        var context = new SmartFinanceDbContext(new DbContextOptionsBuilder<SmartFinanceDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = "SyntheticTestSigningKey01234567890123456789",
            ["Jwt:Issuer"] = "TestIssuer", ["Jwt:Audience"] = "TestAudience",
            ["Jwt:ExpireMinutes"] = "60", ["Jwt:RefreshTokenExpireDays"] = "30",
            ["App:WebBaseUrl"] = "https://example.invalid",
            ["Auth:ForgotPasswordMinResponseMs"] = "0"
        }).Build();
        var current = new Mock<ICurrentUserService>();
        current.SetupGet(c => c.UserId).Returns(1);
        var mail = new FakeEmailSender();
        return (new AuthService(context, config, current.Object, mail, NullLogger<AuthService>.Instance), context, mail);
    }

    private static Task<TokenDto> Kayit(AuthService service, string email = "session@example.invalid") =>
        service.RegisterAsync(new RegisterDto { FullName = "Sentetik Deneme", Email = email, Password = "EskiSifre1!" });

    private static ChangePasswordDto Degisiklik() => new() { CurrentPassword = "EskiSifre1!", NewPassword = "YeniSifre2!" };

    // NEDEN: Calinmis eski refresh token sifre degistirdikten sonra kullanilamamali.
    [Fact]
    public async Task SifreDegisince_EskiRefreshTokenlarGecersizOlur()
    {
        var (service, context, _) = Kur();
        using var db = context;
        var ilk = await Kayit(service);
        var ikinci = await service.LoginAsync(new LoginDto { Email = "session@example.invalid", Password = "EskiSifre1!" });
        var diger = await Kayit(service, "other@example.invalid");
        await service.ChangePasswordAsync(Degisiklik());
        context.ChangeTracker.Clear();
        foreach (var token in new[] { ilk.RefreshToken, ikinci.RefreshToken })
        {
            Assert.NotNull(await context.RefreshTokens.Where(r => r.Token == token).Select(r => r.RevokedAt).SingleAsync());
            await Assert.ThrowsAsync<UnauthorizedException>(() => service.RefreshTokenAsync(token));
        }
        Assert.Single(context.RefreshTokens.Where(r => r.UserId == 1 && r.RevokedAt == null));
        Assert.NotNull(await service.RefreshTokenAsync(diger.RefreshToken));
    }

    // NEDEN: Bu cihaz oturumunu surdurmeli ve mobil login ile ayni govdeyi almali.
    [Fact]
    public async Task SifreDegisince_YeniTokenCiftiDoner_VeYeniRefreshCalisir()
    {
        var (service, context, _) = Kur();
        using var db = context;
        var eski = await Kayit(service);
        var response = Assert.IsType<OkObjectResult>(await new AuthController(service).ChangePassword(Degisiklik()));
        Assert.Equal(200, response.StatusCode);
        var yeni = Assert.IsType<TokenDto>(response.Value);
        Assert.False(string.IsNullOrWhiteSpace(yeni.Token));
        Assert.NotEqual(eski.Token, yeni.Token);
        Assert.True(yeni.Expiration > DateTime.UtcNow);
        Assert.NotEqual(eski.RefreshToken, yeni.RefreshToken);
        var json = System.Text.Json.JsonSerializer.Serialize(yeni, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        using var document = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(new[] { "expiration", "refreshToken", "token" }, document.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray());
        Assert.False(string.IsNullOrWhiteSpace((await service.RefreshTokenAsync(yeni.RefreshToken)).Token));
        await Assert.ThrowsAsync<UnauthorizedException>(() => service.LoginAsync(
            new LoginDto { Email = "session@example.invalid", Password = "EskiSifre1!" }));
        Assert.NotNull(await service.LoginAsync(new LoginDto { Email = "session@example.invalid", Password = "YeniSifre2!" }));
    }

    // NEDEN: Sifirlama tum cihazlari kapatmali, yeni oturum acmamali.
    [Fact]
    public async Task SifreSifirlaninca_TumRefreshTokenlarIptalEdilir()
    {
        var (service, context, mail) = Kur();
        using var db = context;
        var ilk = await Kayit(service);
        var ikinci = await service.LoginAsync(new LoginDto { Email = "session@example.invalid", Password = "EskiSifre1!" });
        var diger = await Kayit(service, "other@example.invalid");
        await service.ForgotPasswordAsync(new ForgotPasswordDto { Email = "session@example.invalid" });
        var response = Assert.IsType<OkObjectResult>(await new AuthController(service).ResetPassword(
            new ResetPasswordDto { Token = mail.SonToken()!, NewPassword = "YeniSifre2!" }, default));
        Assert.IsNotType<TokenDto>(response.Value);
        Assert.Equal(2, context.RefreshTokens.Count(t => t.UserId == 1));
        Assert.All(context.RefreshTokens.Where(t => t.UserId == 1), t => Assert.NotNull(t.RevokedAt));
        foreach (var token in new[] { ilk.RefreshToken, ikinci.RefreshToken })
            await Assert.ThrowsAsync<UnauthorizedException>(() => service.RefreshTokenAsync(token));
        Assert.NotNull(await service.RefreshTokenAsync(diger.RefreshToken));
    }
}
