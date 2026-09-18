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

    // ─── GetStatisticsAsync: guvenilmeyen temel veriler ──────────────────
    //
    // 18.09.2026'da Is Yatirim ve Midas ile karsilastirildi: THYAO
    // finansallarini USD raporluyor, Yahoo bunlari TL fiyatla karistiriyor
    // (PD/DD 18,2 — gercegi 0,4; kar eden sirket "Zararda" gorundu). TL
    // raporlayan MPARK'ta da F/K 14,77 (Is Yatirim 12,5, Midas 12,72).
    // Asagidaki JSON'lar o gunun gercek Yahoo degerleri.

    [Fact]
    public async Task Istatistik_FKVePDDDYahoodanAlinmaz()
    {
        var provider = Create(HttpStatusCode.OK, """
            {"quoteSummary":{"result":[{
                "summaryDetail":{"trailingPE":{"raw":14.774259},"previousClose":{"raw":443.0},"marketCap":{"raw":83469074432}},
                "defaultKeyStatistics":{"trailingEps":{"raw":-6.73},"priceToBook":{"raw":1.9111}},
                "financialData":{"currentPrice":{"raw":438.5},"financialCurrency":"TRY"}
            }]}}
            """);

        var istatistik = await provider.GetStatisticsAsync("MPARK");

        Assert.Null(istatistik!.TrailingPE);
        Assert.Null(istatistik.PriceToBook);
        Assert.Null(istatistik.EquityValue);
        Assert.False(istatistik.IsLossMaking);
        Assert.Equal(83469074432m, istatistik.MarketCap);
    }

    /// FAVOK bir tutar: USD raporlayan sirkette Yahoo onu USD veriyor, biz
    /// onune ₺ koyup gosteriyorduk. Oranlarda pay ve payda ayni para
    /// biriminde, onlar etkilenmiyor.
    [Fact]
    public async Task Istatistik_DolarRaporlayanSirkette_FavokDonmezAmaOranlarKalir()
    {
        var provider = Create(HttpStatusCode.OK, """
            {"quoteSummary":{"result":[{
                "summaryDetail":{"previousClose":{"raw":289.5}},
                "financialData":{"financialCurrency":"USD","ebitda":{"raw":2185999872},
                                 "profitMargins":{"raw":0.10189},"returnOnEquity":{"raw":0.13129}}
            }]}}
            """);

        var istatistik = await provider.GetStatisticsAsync("THYAO");

        Assert.Null(istatistik!.Ebitda);
        Assert.Equal(0.10189m, istatistik.ProfitMargin);
        Assert.Equal(0.13129m, istatistik.ReturnOnEquity);
    }

    [Fact]
    public async Task Istatistik_TLRaporlayanSirkette_FavokDoner()
    {
        var provider = Create(HttpStatusCode.OK, """
            {"quoteSummary":{"result":[{
                "summaryDetail":{"previousClose":{"raw":438.5}},
                "financialData":{"financialCurrency":"TRY","ebitda":{"raw":13090094080}}
            }]}}
            """);

        var istatistik = await provider.GetStatisticsAsync("MPARK");

        Assert.Equal(13090094080m, istatistik!.Ebitda);
    }
}
