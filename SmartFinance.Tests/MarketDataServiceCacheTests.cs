using Microsoft.Extensions.Caching.Memory;
using Moq;
using SmartFinance.Application.DTOs.MarketData;
using SmartFinance.Application.Interfaces;
using SmartFinance.Infrastructure.MarketData;

namespace SmartFinance.Tests;

/// Dis servis (Yahoo/TEFAS/TCMB/CoinGecko) cagrilarinin onbellek sayesinde
/// tekrarlanmadigini dogrular. Amac hem hiz hem de saglayicinin hiz sinirina
/// takilmamak — TEFAS ozelinde tek istek 90 saniyeye kadar surebiliyor.
public class MarketDataServiceCacheTests
{
    private static List<PriceBarDto> OrnekBarlar() =>
    [
        new PriceBarDto { Date = DateTime.Today.AddDays(-1), Open = 10, High = 11, Low = 9, Close = 10.5m, Volume = 100 },
        new PriceBarDto { Date = DateTime.Today, Open = 10.5m, High = 12, Low = 10, Close = 11.5m, Volume = 120 },
    ];

    private static (MarketDataService service, Mock<IPriceProvider> provider) CreateService(string desteklenenTip = "stock")
    {
        var (service, provider, _) = CreateServiceWithPriceCache(desteklenenTip);
        return (service, provider);
    }

    private static (MarketDataService service, Mock<IPriceProvider> provider, PriceCache priceCache) CreateServiceWithPriceCache(
        string desteklenenTip = "stock")
    {
        var provider = new Mock<IPriceProvider>();
        provider.Setup(p => p.SupportedInvestmentTypes).Returns([desteklenenTip]);
        provider.Setup(p => p.GetCurrentPriceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string s, string _, CancellationToken _) => new PriceQuoteDto { Symbol = s, Price = 42m, AsOf = DateTime.UtcNow });
        provider.Setup(p => p.GetHistoricalPricesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OrnekBarlar());
        provider.Setup(p => p.GetStatisticsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((StockStatisticsDto?)null);

        var cache = new MemoryCache(new MemoryCacheOptions());
        // Guncel fiyat onbellegi ayri bir soyutlamada: arka plandaki
        // PriceRefreshService ile MarketDataService ayni anahtar bicimini
        // paylasmak zorunda oldugu icin.
        var priceCache = new PriceCache(cache);
        var service = new MarketDataService([provider.Object], cache, priceCache);
        return (service, provider, priceCache);
    }

    private static PriceQuoteDto Fiyat(decimal price) => new() { Symbol = "THYAO", Price = price, AsOf = DateTime.UtcNow };

    /// Regresyon (18.09.2026, test kullanicisi geri bildirimi): gunluk gecmis
    /// 30 dk onbellekte kaliyor, guncel fiyat ise 5 dk'da bir tazeleniyor.
    /// 1H/6A ekrani bugunun barini onbellekteki haliyle gosterdigi icin ayni
    /// anda 1G'de 290,50, 1H'de 290,75, 6A'da 289,50 goruldu.
    [Fact]
    public async Task GunlukAralik_BugununBariGuncelFiyatlaGuncellenir()
    {
        var (service, _, priceCache) = CreateServiceWithPriceCache();
        await service.GetTechnicalAnalysisAsync("THYAO", "stock", "6m", []);

        priceCache.Set("THYAO", "stock", Fiyat(12.25m), TimeSpan.FromMinutes(20));
        var sonuc = await service.GetTechnicalAnalysisAsync("THYAO", "stock", "6m", []);

        Assert.Equal(12.25m, sonuc.PriceBars[^1].Close);
        Assert.Equal(12.25m, sonuc.PriceBars[^1].High);
        Assert.Equal(10.5m, sonuc.PriceBars[^2].Close);
    }

    /// Yama onbellekteki listeye yazilirsa bir sonraki istekte eski yamanin
    /// izi (yukselttigi High) kalir.
    [Fact]
    public async Task GuncelFiyatYamasi_OnbellektekiBarlariDegistirmez()
    {
        var (service, _, priceCache) = CreateServiceWithPriceCache();
        priceCache.Set("THYAO", "stock", Fiyat(12.25m), TimeSpan.FromMinutes(20));
        await service.GetTechnicalAnalysisAsync("THYAO", "stock", "6m", []);

        priceCache.Set("THYAO", "stock", Fiyat(11m), TimeSpan.FromMinutes(20));
        var sonuc = await service.GetTechnicalAnalysisAsync("THYAO", "stock", "6m", []);

        Assert.Equal(11m, sonuc.PriceBars[^1].Close);
        Assert.Equal(12m, sonuc.PriceBars[^1].High);
    }

    [Fact]
    public async Task GuncelFiyat_AyniSembolIkiKezIstenirse_SaglayiciBirKezCagrilir()
    {
        var (service, provider) = CreateService();

        await service.GetCurrentPriceAsync("THYAO", "stock");
        await service.GetCurrentPriceAsync("THYAO", "stock");

        provider.Verify(p => p.GetCurrentPriceAsync("THYAO", "stock", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GuncelFiyat_FarkliSembol_SaglayiciyaYenidenGider()
    {
        var (service, provider) = CreateService();

        await service.GetCurrentPriceAsync("THYAO", "stock");
        await service.GetCurrentPriceAsync("ASELS", "stock");

        provider.Verify(p => p.GetCurrentPriceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task GecmisFiyat_AyniSembolVeAralik_SaglayiciBirKezCagrilir()
    {
        var (service, provider) = CreateService();

        await service.GetTechnicalAnalysisAsync("THYAO", "stock", "6m", []);
        await service.GetTechnicalAnalysisAsync("THYAO", "stock", "6m", []);

        provider.Verify(p => p.GetHistoricalPricesAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// Aralik onbellek anahtarinin parcasi olmali: kullanici 6 aylik grafikten
    /// 1 yilliga gecince eski veriyi degil, yeni araligi gormeli.
    [Fact]
    public async Task GecmisFiyat_FarkliAralik_SaglayiciyaYenidenGider()
    {
        var (service, provider) = CreateService();

        await service.GetTechnicalAnalysisAsync("THYAO", "stock", "6m", []);
        await service.GetTechnicalAnalysisAsync("THYAO", "stock", "1y", []);

        provider.Verify(p => p.GetHistoricalPricesAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Istatistikler_AyniSembol_SaglayiciBirKezCagrilir()
    {
        var (service, provider) = CreateService();

        await service.GetTechnicalAnalysisAsync("THYAO", "stock", "6m", []);
        await service.GetTechnicalAnalysisAsync("THYAO", "stock", "1y", []);

        // Aralik degisse de istatistikler sembole bagli — tek cagri yeterli.
        provider.Verify(p => p.GetStatisticsAsync("THYAO", It.IsAny<CancellationToken>()), Times.Once);
    }

    /// Onbellek sadece cagri sayisini degil, donen veriyi de bozmamali.
    [Fact]
    public async Task OnbellektenDonenSonuc_IlkSonucIleAyniVeriyiIcerir()
    {
        var (service, _) = CreateService();

        var ilk = await service.GetTechnicalAnalysisAsync("THYAO", "stock", "6m", ["rsi"]);
        var ikinci = await service.GetTechnicalAnalysisAsync("THYAO", "stock", "6m", ["rsi"]);

        Assert.Equal(ilk.PriceBars.Count, ikinci.PriceBars.Count);
        Assert.Equal(ilk.PriceBars[^1].Close, ikinci.PriceBars[^1].Close);
        Assert.Equal(ilk.Symbol, ikinci.Symbol);
    }

    /// Gostergeler onbellege alinmamali: kullanici gosterge secimini
    /// degistirdiginde yeni secim hesaplanmali.
    [Fact]
    public async Task GostergeSecimiDegisirse_YeniGostergelerHesaplanir()
    {
        var (service, _) = CreateService();

        var rsiIle = await service.GetTechnicalAnalysisAsync("THYAO", "stock", "6m", ["rsi"]);
        var gostergesiz = await service.GetTechnicalAnalysisAsync("THYAO", "stock", "6m", []);

        Assert.NotEmpty(rsiIle.Indicators);
        Assert.Empty(gostergesiz.Indicators);
    }
}
