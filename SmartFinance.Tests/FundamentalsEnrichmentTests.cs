using Microsoft.Extensions.Caching.Memory;
using Moq;
using SmartFinance.Application.DTOs.MarketData;
using SmartFinance.Application.Interfaces;
using SmartFinance.Infrastructure.MarketData;

namespace SmartFinance.Tests;

/// PD/DD artik Yahoo'dan degil KAP bilancosundan: piyasa degeri (Yahoo, TL) /
/// ozkaynak. Rakamlar THYAO'nun 18.09.2026 degerleri (piyasa degeri Is
/// Yatirim'dan; o gun Is Yatirim PD/DD 0,4).
public class FundamentalsEnrichmentTests
{
    private static async Task<StockStatisticsDto> Istatistik(decimal? piyasaDegeri, FundamentalSnapshotDto? kap)
    {
        var provider = new Mock<IPriceProvider>();
        provider.Setup(p => p.SupportedInvestmentTypes).Returns(["stock"]);
        provider.Setup(p => p.GetHistoricalPricesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new PriceBarDto { Date = DateTime.Today, Open = 1, High = 1, Low = 1, Close = 1 }]);
        provider.Setup(p => p.GetStatisticsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StockStatisticsDto { MarketCap = piyasaDegeri });

        var fundamentals = new Mock<IFundamentalsService>();
        fundamentals.Setup(f => f.GetSnapshotAsync("THYAO", It.IsAny<CancellationToken>())).ReturnsAsync(kap);

        var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new MarketDataService([provider.Object], cache, new PriceCache(cache), fundamentals.Object);
        return (await service.GetTechnicalAnalysisAsync("THYAO", "stock", "6m", [])).Statistics!;
    }

    private static readonly FundamentalSnapshotDto Thyao = new(112_059_000_000m, 1_018_517_000_000m, 2026, 2);

    [Fact]
    public async Task PDDD_PiyasaDegeriVeKapBilancosundanHesaplanir()
    {
        var s = await Istatistik(399_165_000_000m, Thyao);

        Assert.Equal(0.392m, Math.Round(s.PriceToBook!.Value, 3));
        Assert.Equal(1_018_517_000_000m, s.EquityValue);
        Assert.Equal("6/2026", s.FundamentalsPeriod);
    }

    /// Kullanici karari (18.09.2026): F/K, dolar esasli sirketlerde (THYAO 3,56 —
    /// Is Yatirim 3,0) ve TMS 29'da sapiyor; duzeltilene kadar gosterilmiyor.
    /// "Zararda" ayni son 12 ay hesabindan geldigi icin o da.
    [Fact]
    public async Task FK_DuzeltilenekadarGosterilmez()
    {
        var s = await Istatistik(399_165_000_000m, Thyao);

        Assert.Null(s.TrailingPE);
        Assert.False(s.IsLossMaking);
    }

    [Fact]
    public async Task SonOnIkiAyZarardaOlsaBile_ZarardaIsaretlenmez()
    {
        var s = await Istatistik(399_165_000_000m, Thyao with { TtmNetProfit = -5_000_000_000m });

        Assert.Null(s.TrailingPE);
        Assert.False(s.IsLossMaking);
        Assert.NotNull(s.PriceToBook);
    }

    [Fact]
    public async Task KapVerisiYoksa_AlanlarBosKalir()
    {
        var s = await Istatistik(399_165_000_000m, null);

        Assert.Null(s.TrailingPE);
        Assert.Null(s.PriceToBook);
        Assert.Null(s.FundamentalsPeriod);
        Assert.False(s.IsLossMaking);
    }

    [Fact]
    public async Task PiyasaDegeriYoksa_OranHesaplanmaz()
    {
        var s = await Istatistik(null, Thyao);

        Assert.Null(s.TrailingPE);
        Assert.Null(s.PriceToBook);
    }
}
