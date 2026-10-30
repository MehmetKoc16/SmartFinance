using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SmartFinance.API.Controllers;
using SmartFinance.API.Infrastructure;
using SmartFinance.API.Middleware;
using SmartFinance.Application.DTOs.Auth;
using SmartFinance.Application.Exceptions;
using SmartFinance.Application.Interfaces;
using SmartFinance.Domain.Entities;
using SmartFinance.Infrastructure.Context;
using SmartFinance.Infrastructure.Services;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace SmartFinance.Tests;

/// <summary>
/// Ihlal tespiti loglari (29.10.2026 denetimi): veri ihlalinde Kurul'a 72 saat
/// icinde "ne oldu, kimler etkilendi" bildirilebilmesi icin 429'lar ve giris
/// denemeleri IP ile loglanmali; ama loglarin kendisi kisisel veri
/// (e-posta, dosya adindaki ad soyad, token) sizdirmamali.
/// </summary>
public class GuvenlikLogTests
{
    // Belgelendirme icin ayrilmis IP araligi (RFC 5737) ve sentetik veriler.
    private const string IstemciIp = "203.0.113.7";
    private const string Eposta = "ahmet.yilmaz@example.invalid";
    private const string Sifre = "DogruSifre1!";

    // ─── Rate limit ────────────────────────────────────────────────────────

    private static TestServer RateLimitSunucusu(YakalayiciLoggerProvider loglar) => new(new WebHostBuilder()
        .ConfigureServices(services =>
        {
            services.AddLogging(b => b.AddProvider(loglar));
            services.AddRouting();
            services.AddRateLimiter(ApiRateLimiting.Configure);
        })
        .Configure(app =>
        {
            app.UseRouting();
            app.UseRateLimiter();
            app.UseEndpoints(e => e.MapPost("/api/auth/login", () => "ok").RequireRateLimiting("auth"));
        }));

    private static Task<HttpContext> GirisIstegi(TestServer server) => server.SendAsync(context =>
    {
        context.Connection.RemoteIpAddress = IPAddress.Parse(IstemciIp);
        context.Request.Method = "POST";
        context.Request.Path = "/api/auth/login";
        context.Request.QueryString = new QueryString($"?email={Eposta}");
        context.Request.Headers.Authorization = "Bearer gizli-token-degeri";
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(
            $"{{\"email\":\"{Eposta}\",\"password\":\"{Sifre}\"}}"));
    });

    // NEDEN: 429'lar hic loglanmiyordu; kaba kuvvet denemesi sonradan izlenemiyordu.
    // Log IP ve yolu icermeli, sorgu dizesi/govde/header/e-posta icermemeli.
    [Fact]
    public async Task RateLimitReddi_IpVeYolIcerir_EpostaVeGovdeIcermez()
    {
        var loglar = new YakalayiciLoggerProvider();
        using var server = RateLimitSunucusu(loglar);

        for (var i = 0; i < 5; i++)
            Assert.Equal(200, (await GirisIstegi(server)).Response.StatusCode);
        var reddedilen = await GirisIstegi(server);

        Assert.Equal(429, reddedilen.Response.StatusCode);
        var kayit = Assert.Single(loglar.Kayitlar, k => k.EventId.Name == "RateLimitReddi");
        Assert.Equal(LogLevel.Warning, kayit.Level);
        Assert.Contains(IstemciIp, kayit.TumMetin);
        Assert.Contains("/api/auth/login", kayit.TumMetin);
        Assert.Contains(kayit.Alanlar, a => a.Key == "Policy" && a.Value as string == "auth");
        // Yalnizca bizim kaydimiza bakilir: ASP.NET Core'un kendi istek loglari
        // (Hosting.Diagnostics) bu testin konusu degil.
        foreach (var yasak in new[] { "ahmet", "example.invalid", "?email", Sifre, "gizli-token", "Bearer" })
            Assert.DoesNotContain(yasak, kayit.TumMetin, StringComparison.OrdinalIgnoreCase);
    }

    // NEDEN: OnRejected eklemek istemciye donen 429 cevabini degistirmemeli
    // (eskiden govde bos, Retry-After yoktu).
    [Fact]
    public async Task RateLimitReddi_CevapEskisiyleAyni_BosGovdeRetryAfterYok()
    {
        using var server = RateLimitSunucusu(new YakalayiciLoggerProvider());

        for (var i = 0; i < 5; i++) await GirisIstegi(server);
        var reddedilen = await GirisIstegi(server);

        Assert.Equal(429, reddedilen.Response.StatusCode);
        var govde = await new StreamReader(reddedilen.Response.Body).ReadToEndAsync();
        Assert.Equal(string.Empty, govde);
        Assert.False(reddedilen.Response.Headers.ContainsKey("Retry-After"));
    }

    // ─── Giris / yenileme ──────────────────────────────────────────────────

    private static (AuthService service, SmartFinanceDbContext context) AuthKur()
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
        return (new AuthService(context, config, current.Object, new FakeEmailSender(), NullLogger<AuthService>.Instance), context);
    }

    private static Task KayitOl(AuthService service) =>
        service.RegisterAsync(new RegisterDto { FullName = "Sentetik Deneme", Email = Eposta, Password = Sifre });

    private static DefaultHttpContext Istek()
    {
        var context = new DefaultHttpContext { TraceIdentifier = "test-trace" };
        context.Connection.RemoteIpAddress = IPAddress.Parse(IstemciIp);
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static AuthController Kontrolcu(IAuthService service, YakalayiciLoggerProvider loglar, HttpContext context) =>
        new(service, loglar.CreateLogger<AuthController>()) { ControllerContext = new ControllerContext { HttpContext = context } };

    private static async Task<(int status, string body)> MiddlewareIleCalistir(RequestDelegate next, HttpContext context)
    {
        await new ExceptionMiddleware(next, NullLogger<ExceptionMiddleware>.Instance).InvokeAsync(context);
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        return (context.Response.StatusCode, await new StreamReader(context.Response.Body).ReadToEndAsync());
    }

    // NEDEN: Basarisiz girisler IP'siyle izlenebilmeli ama e-posta (kisisel veri)
    // loga yazilmamali. Yakalayip yeniden firlatmak cevabi degistirmemeli:
    // mobil 401'i token yenileme akisina bagliyor, govde ayni kalmali.
    [Fact]
    public async Task GirisBasarisiz_IpIcerir_EpostaIcermez_CevapEskisiyleAyni()
    {
        var (service, context) = AuthKur();
        using var db = context;
        await KayitOl(service);
        var yanlis = new LoginDto { Email = Eposta, Password = "YanlisSifre9!" };
        var loglar = new YakalayiciLoggerProvider();

        // Eski kontrolcu servisi dogrudan cagiriyordu; referans cevap budur.
        var (eskiStatus, eskiGovde) = await MiddlewareIleCalistir(_ => service.LoginAsync(yanlis), Istek());
        var (yeniStatus, yeniGovde) = await MiddlewareIleCalistir(
            ctx => Kontrolcu(service, loglar, ctx).Login(yanlis), Istek());

        Assert.Equal(401, eskiStatus);
        Assert.Equal(eskiStatus, yeniStatus);
        Assert.Equal(eskiGovde, yeniGovde);

        var kayit = Assert.Single(loglar.Kayitlar, k => k.EventId.Name == "GirisBasarisiz");
        Assert.Equal(LogLevel.Warning, kayit.Level);
        Assert.Contains(IstemciIp, kayit.TumMetin);
        Assert.DoesNotContain(loglar.Kayitlar, k => k.EventId.Name == "GirisBasarili");
        foreach (var k in loglar.Kayitlar)
        {
            Assert.DoesNotContain(Eposta, k.TumMetin, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ahmet", k.TumMetin, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("YanlisSifre9!", k.TumMetin);
        }
    }

    // NEDEN: throw; ayni istisnayi korumali; ExceptionMiddleware tipine ve
    // mesajina bakarak cevap uretiyor.
    [Fact]
    public async Task GirisBasarisiz_AyniIstisnaYenidenFirlatilir()
    {
        var beklenen = new UnauthorizedException("Email veya şifre hatalı!");
        var service = new Mock<IAuthService>();
        service.Setup(s => s.LoginAsync(It.IsAny<LoginDto>())).ThrowsAsync(beklenen);

        var hata = await Assert.ThrowsAsync<UnauthorizedException>(() =>
            Kontrolcu(service.Object, new YakalayiciLoggerProvider(), Istek())
                .Login(new LoginDto { Email = Eposta, Password = "x" }));

        Assert.Same(beklenen, hata);
    }

    // NEDEN: Ihlalde "kimler etkilendi" sorusu icin basarili giriste hesap kimligi
    // (UserId) ve IP loglanmali; e-posta yine yazilmamali.
    [Fact]
    public async Task GirisBasarili_UserIdVeIpIcerir()
    {
        var (service, context) = AuthKur();
        using var db = context;
        await KayitOl(service);
        var userId = (await context.Users.SingleAsync()).Id.ToString();
        var loglar = new YakalayiciLoggerProvider();

        var cevap = await Kontrolcu(service, loglar, Istek()).Login(new LoginDto { Email = Eposta, Password = Sifre });

        Assert.IsType<TokenDto>(Assert.IsType<OkObjectResult>(cevap).Value);
        var kayit = Assert.Single(loglar.Kayitlar, k => k.EventId.Name == "GirisBasarili");
        Assert.Equal(LogLevel.Information, kayit.Level);
        Assert.Contains(kayit.Alanlar, a => a.Key == "UserId" && a.Value?.ToString() == userId);
        Assert.Contains(IstemciIp, kayit.TumMetin);
        Assert.DoesNotContain(Eposta, kayit.TumMetin, StringComparison.OrdinalIgnoreCase);
    }

    // NEDEN: Calinmis/eski refresh token denemeleri IP ile izlenmeli; token
    // degeri bir sir oldugu icin loga yazilmamali.
    [Fact]
    public async Task YenilemeBasarisiz_IpIcerir_TokenIcermez()
    {
        var (service, context) = AuthKur();
        using var db = context;
        var loglar = new YakalayiciLoggerProvider();
        const string sahteToken = "sahte-refresh-token-degeri";

        await Assert.ThrowsAsync<UnauthorizedException>(() =>
            Kontrolcu(service, loglar, Istek()).Refresh(new RefreshTokenRequestDto { RefreshToken = sahteToken }));

        var kayit = Assert.Single(loglar.Kayitlar, k => k.EventId.Name == "YenilemeBasarisiz");
        Assert.Equal(LogLevel.Warning, kayit.Level);
        Assert.Contains(IstemciIp, kayit.TumMetin);
        Assert.DoesNotContain(sahteToken, kayit.TumMetin);
    }

    // ─── PDF ice aktarma ───────────────────────────────────────────────────

    // NEDEN: Ekstre dosya adi cogunlukla kisinin adini soyadini tasiyor
    // (bankalar "Ad_Soyad_ekstre.pdf" diye indiriyor); loga yalnizca uzanti
    // ve boyut yazilmali.
    [Fact]
    public async Task PdfIceAktarmaLogu_DosyaAdiniIcermez_UzantiVeBoyutIcerir()
    {
        var context = new SmartFinanceDbContext(new DbContextOptionsBuilder<SmartFinanceDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        using var db = context;
        var user = new User { FullName = "Test Kullanıcı", Email = $"{Guid.NewGuid()}@test.com", PasswordHash = "x" };
        context.Users.Add(user);
        context.SaveChanges();
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()) }))
            }
        };
        var currentUser = new CurrentUserService(accessor);
        var entitlement = new EntitlementService(context, currentUser, new ConfigurationBuilder().Build());
        var loglar = new YakalayiciLoggerProvider();
        var service = new PdfImportService(context, currentUser, loglar.CreateLogger<PdfImportService>(), entitlement);

        var builder = new PdfDocumentBuilder();
        var sayfa = builder.AddPage(PageSize.A4);
        sayfa.AddText("Tarih Aciklama Tutar", 9, new PdfPoint(40, 700), builder.AddStandard14Font(Standard14Font.Helvetica));
        var pdf = builder.Build();

        await service.ParsePdfAsync(new MemoryStream(pdf), "Ahmet_Yilmaz_ekstre.PDF");

        Assert.NotEmpty(loglar.Kayitlar);
        foreach (var k in loglar.Kayitlar)
        {
            Assert.DoesNotContain("Ahmet", k.TumMetin, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Yilmaz", k.TumMetin, StringComparison.OrdinalIgnoreCase);
        }
        var baslangic = Assert.Single(loglar.Kayitlar, k => k.Message.Contains("ice aktarma basladi"));
        Assert.Contains(".pdf", baslangic.Message); // kucuk harfe cevrilmis
        Assert.Contains(baslangic.Alanlar, a => a.Key == "SizeBytes" && a.Value?.ToString() == pdf.Length.ToString());
    }
}
