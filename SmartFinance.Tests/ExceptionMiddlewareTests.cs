using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;
using SmartFinance.API.Middleware;
using SmartFinance.Application.Exceptions;

namespace SmartFinance.Tests;

public class ExceptionMiddlewareTests
{
    /// Middleware'i verilen exception'i firlatan sahte bir pipeline ile calistirir,
    /// donen govdeyi metin olarak ve kullanilan logger'i geri verir.
    private static async Task<(int statusCode, string body, Mock<ILogger<ExceptionMiddleware>> logger)>
        RunAsync(Exception thrown)
    {
        var logger = new Mock<ILogger<ExceptionMiddleware>>();
        var middleware = new ExceptionMiddleware(_ => throw thrown, logger.Object);

        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/api/test";
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        return (context.Response.StatusCode, body, logger);
    }

    /// Moq ile ILogger dogrulamasi: LogError/LogWarning birer uzanti metodu
    /// oldugu icin dogrudan dogrulanamaz, altta cagrilan Log(...) dogrulanir.
    private static void VerifyLogged(Mock<ILogger<ExceptionMiddleware>> logger, LogLevel level, Times times)
    {
        logger.Verify(
            x => x.Log(
                level,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                (Func<It.IsAnyType, Exception?, string>)It.IsAny<object>()),
            times);
    }

    [Theory]
    [InlineData(typeof(BadRequestException), 400)]
    [InlineData(typeof(NotFoundException), 404)]
    [InlineData(typeof(UnauthorizedException), 401)]
    [InlineData(typeof(ExternalServiceException), 502)]
    public async Task BilinenHatalar_DogruStatusKoduDoner(Type exceptionType, int beklenenStatus)
    {
        var exception = (Exception)Activator.CreateInstance(exceptionType, "Test mesaji")!;

        var (statusCode, _, _) = await RunAsync(exception);

        Assert.Equal(beklenenStatus, statusCode);
    }

    /// Govdedeki "message" alanini cozulmus haliyle verir. Ham metin uzerinden
    /// karsilastirma yapilmaz: JsonSerializer Turkce karakterleri ı gibi
    /// kacisla yazar (istemci tarafi jsonDecode ile dogru cozer).
    private static string MesajiOku(string body)
        => JsonDocument.Parse(body).RootElement.GetProperty("message").GetString()!;

    [Fact]
    public async Task BilinenHata_GercekMesajiIstemciyeDoner()
    {
        var (_, body, _) = await RunAsync(new NotFoundException("Kategori bulunamadı!"));

        Assert.Equal("Kategori bulunamadı!", MesajiOku(body));
    }

    [Fact]
    public async Task BilinenHata_UyariSeviyesindeLoglanir()
    {
        var (_, _, logger) = await RunAsync(new NotFoundException("Kategori bulunamadı!"));

        VerifyLogged(logger, LogLevel.Warning, Times.Once());
        VerifyLogged(logger, LogLevel.Error, Times.Never());
    }

    [Fact]
    public async Task BilinmeyenHata_500Doner()
    {
        var (statusCode, _, _) = await RunAsync(new InvalidOperationException("Veritabani baglantisi koptu"));

        Assert.Equal(500, statusCode);
    }

    /// En kritik test: ic hata detayinin (baglanti dizesi, SQL, dosya yolu vb.)
    /// istemciye sizmadigini garanti eder.
    [Fact]
    public async Task BilinmeyenHata_IcDetayiIstemciyeSizdirmaz()
    {
        const string icDetay = "Server=localhost;Password=gizli123";

        var (_, body, _) = await RunAsync(new InvalidOperationException(icDetay));

        Assert.DoesNotContain(icDetay, body);
        Assert.DoesNotContain("Password", body);
        Assert.StartsWith("Beklenmeyen bir hata oluştu", MesajiOku(body));
    }

    [Fact]
    public async Task BilinmeyenHata_ExceptionIleBirlikteHataSeviyesindeLoglanir()
    {
        var (_, _, logger) = await RunAsync(new InvalidOperationException("Veritabani baglantisi koptu"));

        VerifyLogged(logger, LogLevel.Error, Times.Once());
        VerifyLogged(logger, LogLevel.Warning, Times.Never());
    }

    /// Kullanici destege basvurdugunda log kaydiyla eslestirilebilmesi icin
    /// yanitta traceId bulunmali.
    [Fact]
    public async Task Yanit_TraceIdIcerir()
    {
        var (_, body, _) = await RunAsync(new InvalidOperationException("hata"));

        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.TryGetProperty("traceId", out var traceId));
        Assert.False(string.IsNullOrWhiteSpace(traceId.GetString()));
    }

    // ─── ExternalServiceException: UserMessage istemciye, .Message loga ─────
    //
    // Fiyat sağlayıcıları (Yahoo, Binance, TEFAS, TCMB EVDS, CoinGecko) hata
    // mesajlarında kendi adlarını geçiriyor — bu teknik detay, log için doğru
    // ama kullanıcı hisse sembolünü yanlış yazdığında "Yahoo Finance'da
    // bulunamadı" görmemeli. Middleware bu iki mesajı ayırmak zorunda.

    /// En kritik test: sağlayıcı adı hiçbir şekilde istemciye ulaşmamalı.
    [Fact]
    public async Task DisServisHatasi_SaglayiciAdiIstemciyeSizmaz()
    {
        var teknikMesaj = "Yahoo Finance'da 'XXYZ' sembolü bulunamadı.";
        var hata = new ExternalServiceException(teknikMesaj,
            ExternalServiceFailureKind.SymbolNotFound, symbol: "XXYZ");

        var (_, body, _) = await RunAsync(hata);

        Assert.DoesNotContain("Yahoo Finance", body);
        Assert.DoesNotContain("Yahoo", body);
    }

    [Fact]
    public async Task SembolBulunamadi_MesajSemboluIcerirAmaSaglayiciyiIcermez()
    {
        var hata = new ExternalServiceException("CoinGecko'da 'XCOIN' sembolü bulunamadı.",
            ExternalServiceFailureKind.SymbolNotFound, symbol: "XCOIN");

        var (_, body, _) = await RunAsync(hata);
        var mesaj = MesajiOku(body);

        Assert.Contains("XCOIN", mesaj);
        Assert.DoesNotContain("CoinGecko", mesaj);
    }

    [Fact]
    public async Task SaglayiciKullanilamiyor_JenerikMesajDoner()
    {
        var hata = new ExternalServiceException("TEFAS istek sınırına takıldı: ABC");

        var (_, body, _) = await RunAsync(hata);

        Assert.DoesNotContain("TEFAS", body);
        Assert.Contains("tekrar deneyin", MesajiOku(body), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AralikVerisiYok_KendineOzguMesajDoner()
    {
        var hata = new ExternalServiceException("'THYAO' için geçmiş fiyat verisi bulunamadı.",
            ExternalServiceFailureKind.NoDataForRange);

        var (_, body, _) = await RunAsync(hata);

        Assert.Contains("aralık", MesajiOku(body), StringComparison.OrdinalIgnoreCase);
    }

    /// Gram altın/gümüş kendi mesajını zaten kullanıcı diline yazmıştı
    /// ("Kullanılabilir: GRAM ALTIN") — override edildiğinde bu ayrıntı
    /// jenerik şablonla ezilmemeli.
    [Fact]
    public async Task OverrideVerilmisse_OzelMesajKullanilir()
    {
        var hata = new ExternalServiceException(
            "'ALTIN2' tanımlı bir altın sembolü değil. Kullanılabilir: GRAM ALTIN.",
            ExternalServiceFailureKind.SymbolNotFound, symbol: "ALTIN2",
            userMessage: "'ALTIN2' tanımlı bir altın sembolü değil. Kullanılabilir: GRAM ALTIN.");

        var (_, body, _) = await RunAsync(hata);

        Assert.Equal("'ALTIN2' tanımlı bir altın sembolü değil. Kullanılabilir: GRAM ALTIN.", MesajiOku(body));
    }

    /// Teknik mesaj (sağlayıcı adı dahil) loglamada kaybolmamalı — sorun
    /// ayıklarken "hangi sağlayıcı, hangi sembol" bilgisi loglardan okunabilmeli.
    [Fact]
    public async Task DisServisHatasi_TeknikMesajYineDeUyariSeviyesindeLoglanir()
    {
        var hata = new ExternalServiceException("Yahoo Finance'da 'XXYZ' sembolü bulunamadı.",
            ExternalServiceFailureKind.SymbolNotFound, symbol: "XXYZ");

        var (_, _, logger) = await RunAsync(hata);

        VerifyLogged(logger, LogLevel.Warning, Times.Once());
    }
}
