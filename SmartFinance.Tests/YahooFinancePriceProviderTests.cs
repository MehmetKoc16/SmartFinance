using System.Net;
using System.Text;
using SmartFinance.Application.DTOs.MarketData;
using SmartFinance.Application.Exceptions;
using SmartFinance.Application.Interfaces;
using SmartFinance.Infrastructure.MarketData;

namespace SmartFinance.Tests;

/// <summary>
/// Yahoo'nun chart uç noktası bilinmeyen semboller için genelde HTTP 404
/// dönüyor — 200 + boş sonuç değil. Bu, "yanlış hisse sembolü yazıldı"
/// senaryosunun asıl geçtiği yol: kullanıcı "THY" yazınca (doğrusu "THYAO")
/// Yahoo isteği 404 ile reddediyor. Bu dosya o ayrımın (404 = yanlış sembol,
/// başka bir hata = sağlayıcı sorunu) doğru yapıldığını doğruluyor.
/// </summary>
public class YahooFinancePriceProviderTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _json;

        public StubHandler(HttpStatusCode status, string json)
        {
            _status = status;
            _json = json;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json"),
            });
    }

    private sealed class EmptyStore : IPriceHistoryStore
    {
        public Task<IReadOnlyList<PriceBarDto>> GetRangeAsync(
            string symbol, string investmentType, DateTime from, DateTime to, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PriceBarDto>>(Array.Empty<PriceBarDto>());

        public Task<DateTime?> GetLatestDateAsync(string symbol, string investmentType, CancellationToken ct = default)
            => Task.FromResult<DateTime?>(null);

        public Task<int> UpsertAsync(
            string symbol, string investmentType, IEnumerable<PriceBarDto> bars, CancellationToken ct = default)
            => Task.FromResult(bars.Count());

        public Task<IReadOnlyList<string>> GetTrackedSymbolsAsync(string investmentType, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }

    private static YahooFinancePriceProvider Create(HttpStatusCode status, string json)
        => new(new HttpClient(new StubHandler(status, json)), new EmptyStore());

    [Fact]
    public async Task Http404_YanlisSembolOlarakIsaretlenir()
    {
        var provider = Create(HttpStatusCode.NotFound,
            """{"chart":{"error":{"code":"Not Found","description":"No data found"}}}""");

        var hata = await Assert.ThrowsAsync<ExternalServiceException>(
            () => provider.GetCurrentPriceAsync("THY", "stock"));

        Assert.Equal(ExternalServiceFailureKind.SymbolNotFound, hata.Kind);
        Assert.DoesNotContain("Yahoo", hata.UserMessage);
        Assert.Contains("THY", hata.UserMessage);
    }

    /// 200 döner ama gövdede chart.error dolu — Yahoo'nun ikinci "sembol yok"
    /// yolu. Bu da SymbolNotFound olmalı, ProviderUnavailable değil.
    [Fact]
    public async Task Http200AmaChartHatasi_YanlisSembolOlarakIsaretlenir()
    {
        var provider = Create(HttpStatusCode.OK,
            """{"chart":{"error":{"code":"Not Found","description":"No data found"},"result":null}}""");

        var hata = await Assert.ThrowsAsync<ExternalServiceException>(
            () => provider.GetCurrentPriceAsync("THY", "stock"));

        Assert.Equal(ExternalServiceFailureKind.SymbolNotFound, hata.Kind);
    }

    /// 429/5xx gibi başka bir HTTP hatası kullanıcının suçu değil — sembol
    /// muhtemelen doğru, Yahoo o an cevap vermiyor.
    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task DigerHttpHatalari_SaglayiciSorunuOlarakIsaretlenir(HttpStatusCode status)
    {
        var provider = Create(status, "{}");

        var hata = await Assert.ThrowsAsync<ExternalServiceException>(
            () => provider.GetCurrentPriceAsync("THYAO", "stock"));

        Assert.Equal(ExternalServiceFailureKind.ProviderUnavailable, hata.Kind);
        Assert.DoesNotContain("Yahoo", hata.UserMessage);
        Assert.Contains("tekrar deneyin", hata.UserMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GecerliSembol_FiyatiDoner()
    {
        var provider = Create(HttpStatusCode.OK,
            """{"chart":{"result":[{"meta":{"regularMarketPrice":296.0,"longName":"Türk Hava Yolları"}}]}}""");

        var quote = await provider.GetCurrentPriceAsync("THYAO", "stock");

        Assert.Equal(296.0m, quote.Price);
    }

    // ─── GetStatisticsAsync: F/K yedek hesabi ────────────────────────────
    //
    // Yahoo, BIST hisselerinde summaryDetail.trailingPE'yi cogu zaman BOS
    // birakiyor — kar eden bir sirket (THYAO) icin bile. trailingEps genelde
    // doluyor; fiyat/EPS ile kendimiz hesapliyoruz.

    [Fact]
    public async Task Istatistik_TrailingPEBossaEpsVeFiyattanHesaplanir()
    {
        var provider = Create(HttpStatusCode.OK, """
            {"quoteSummary":{"result":[{
                "summaryDetail":{"previousClose":{"raw":296.0}},
                "defaultKeyStatistics":{"trailingEps":{"raw":15.5}},
                "financialData":{"currentPrice":{"raw":296.0}}
            }]}}
            """);

        var istatistik = await provider.GetStatisticsAsync("THYAO");

        Assert.NotNull(istatistik!.TrailingPE);
        Assert.Equal(296.0m / 15.5m, istatistik.TrailingPE!.Value, precision: 4);
        Assert.False(istatistik.IsLossMaking);
    }

    /// summaryDetail.trailingPE zaten doluysa, hesaplanan degere BAKILMAMALI —
    /// Yahoo'nun kendi verdigi deger her zaman oncelikli.
    [Fact]
    public async Task Istatistik_TrailingPEDoluysaHesaplamaYapilmaz()
    {
        var provider = Create(HttpStatusCode.OK, """
            {"quoteSummary":{"result":[{
                "summaryDetail":{"trailingPE":{"raw":12.5},"previousClose":{"raw":296.0}},
                "defaultKeyStatistics":{"trailingEps":{"raw":15.5}},
                "financialData":{"currentPrice":{"raw":296.0}}
            }]}}
            """);

        var istatistik = await provider.GetStatisticsAsync("THYAO");

        Assert.Equal(12.5m, istatistik!.TrailingPE);
    }

    /// Negatif EPS = sirket zarar ediyor (gercek THYAO verisiyle keşfedildi:
    /// trailingEps -6.73). F/K matematiksel olarak anlamsiz, null kalmali —
    /// uydurma bir "negatif F/K" gosterilmemeli. IsLossMaking=true olmali ki
    /// istemci "veri yok" yerine "Zararda" gosterebilsin.
    [Fact]
    public async Task Istatistik_NegatifEpsdeTrailingPENullKalirVeZarardaIsaretlenir()
    {
        var provider = Create(HttpStatusCode.OK, """
            {"quoteSummary":{"result":[{
                "summaryDetail":{"previousClose":{"raw":296.0}},
                "defaultKeyStatistics":{"trailingEps":{"raw":-3.2}},
                "financialData":{"currentPrice":{"raw":296.0}}
            }]}}
            """);

        var istatistik = await provider.GetStatisticsAsync("THYAO");

        Assert.Null(istatistik!.TrailingPE);
        Assert.True(istatistik.IsLossMaking);
    }

    /// EPS verisi hic yoksa (zarar degil, bilinmiyor) IsLossMaking false
    /// kalmali — istemci yanlislikla "Zararda" demeameli, "veri yok" gibi
    /// davranmali (satiri gizlemeli).
    [Fact]
    public async Task Istatistik_HicVeriYoksaTrailingPENullKalirVeZarardaIsaretlenmez()
    {
        var provider = Create(HttpStatusCode.OK, """
            {"quoteSummary":{"result":[{
                "summaryDetail":{"previousClose":{"raw":296.0}}
            }]}}
            """);

        var istatistik = await provider.GetStatisticsAsync("THYAO");

        Assert.Null(istatistik!.TrailingPE);
        Assert.False(istatistik.IsLossMaking);
    }
}
