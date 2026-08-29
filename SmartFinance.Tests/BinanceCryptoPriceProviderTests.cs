using System.Net;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using SmartFinance.Application.DTOs.MarketData;
using SmartFinance.Application.Interfaces;
using SmartFinance.Infrastructure.MarketData;

namespace SmartFinance.Tests;

/// Kripto fiyatlari CoinGecko'dan Binance'e tasindi. Kritik davranis, sembolun
/// hangi parite uzerinden fiyatlanacagina karar veren cozumleme sirasi:
/// dogrudan TRY -> USDT x USDTTRY -> CoinGecko.
public class BinanceCryptoPriceProviderTests
{
    /// URL'e gore farkli yanit donduren sahte sunucu.
    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly Func<string, string?> _route;
        public List<string> Urls { get; } = new();

        public RoutingHandler(Func<string, string?> route) => _route = route;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = Uri.UnescapeDataString(request.RequestUri!.ToString());
            Urls.Add(url);
            var body = _route(url);
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.BadRequest)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                });
        }
    }

    private sealed class EmptyStore : IPriceHistoryStore
    {
        public Task<IReadOnlyList<PriceBarDto>> GetRangeAsync(
            string symbol, string investmentType, DateTime from, DateTime to, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PriceBarDto>>(Array.Empty<PriceBarDto>());
        public Task<DateTime?> GetLatestDateAsync(string symbol, string investmentType, CancellationToken ct = default)
            => Task.FromResult<DateTime?>(null);
        public Task<int> UpsertAsync(string symbol, string investmentType, IEnumerable<PriceBarDto> bars, CancellationToken ct = default)
            => Task.FromResult(bars.Count());
        public Task<IReadOnlyList<string>> GetTrackedSymbolsAsync(string investmentType, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }

    // BTC ve POL'un TRY paritesi var; IMX yalnizca USDT'de; TON hic yok.
    private const string ExchangeInfoJson = """
        {"symbols":[
          {"symbol":"BTCTRY","status":"TRADING"},
          {"symbol":"BTCUSDT","status":"TRADING"},
          {"symbol":"POLTRY","status":"TRADING"},
          {"symbol":"RENDERTRY","status":"TRADING"},
          {"symbol":"IMXUSDT","status":"TRADING"},
          {"symbol":"USDTTRY","status":"TRADING"},
          {"symbol":"ESKICOINTRY","status":"BREAK"}
        ]}
        """;

    private static string Kline(string close) =>
        $"""[[1787961600000,"{close}","{close}","{close}","{close}","1.0",1788047999999,"0",0,"0","0","0"]]""";

    private static string TradingDay(params (string sym, string price)[] items) =>
        "[" + string.Join(",", items.Select(i =>
            $$"""{"symbol":"{{i.sym}}","openPrice":"{{i.price}}","highPrice":"{{i.price}}","lowPrice":"{{i.price}}","lastPrice":"{{i.price}}","volume":"1.0"}""")) + "]";

    private static string? DefaultRoute(string url)
    {
        if (url.Contains("exchangeInfo")) return ExchangeInfoJson;
        if (url.Contains("ticker/tradingDay"))
            return TradingDay(("BTCTRY", "3772250"), ("IMXUSDT", "2.5"), ("USDTTRY", "48.20"), ("POLTRY", "10.5"));
        if (url.Contains("symbol=BTCTRY")) return Kline("3772250");
        if (url.Contains("symbol=POLTRY")) return Kline("10.5");
        if (url.Contains("symbol=RENDERTRY")) return Kline("120.0");
        if (url.Contains("symbol=IMXUSDT")) return Kline("2.5");
        if (url.Contains("symbol=USDTTRY")) return Kline("48.20");
        return null;
    }

    private static string? CoinGeckoRoute(string url)
    {
        if (url.Contains("search")) return """{"coins":[{"id":"the-open-network","symbol":"TON"}]}""";
        if (url.Contains("simple/price")) return """{"the-open-network":{"try":150.5}}""";
        return null;
    }

    private static (BinanceCryptoPriceProvider provider, RoutingHandler binance, RoutingHandler gecko) Create()
    {
        var binance = new RoutingHandler(DefaultRoute);
        var gecko = new RoutingHandler(CoinGeckoRoute);
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var fallback = new CoinGeckoPriceProvider(new HttpClient(gecko), config);
        var provider = new BinanceCryptoPriceProvider(
            new HttpClient(binance),
            new EmptyStore(),
            new MemoryCache(new MemoryCacheOptions()),
            fallback);
        return (provider, binance, gecko);
    }

    [Fact]
    public async Task TRYParitesiVarsa_DogrudanKullanilir()
    {
        var (provider, binance, gecko) = Create();

        var quote = await provider.GetCurrentPriceAsync("BTC", "crypto");

        Assert.Equal(3772250m, quote.Price);
        Assert.Contains(binance.Urls, u => u.Contains("symbol=BTCTRY"));
        // Dogrudan TRY paritesi varken kur cevrimi yapilmamali.
        Assert.DoesNotContain(binance.Urls, u => u.Contains("symbol=USDTTRY"));
        Assert.Empty(gecko.Urls);
    }

    /// TRY paritesi olmayan coin USDT uzerinden cevriliyor: 2,5 x 48,20 = 120,50
    [Fact]
    public async Task TRYParitesiYoksa_USDTUzerindenCevrilir()
    {
        var (provider, binance, gecko) = Create();

        var quote = await provider.GetCurrentPriceAsync("IMX", "crypto");

        Assert.Equal(2.5m * 48.20m, quote.Price);
        Assert.Contains(binance.Urls, u => u.Contains("symbol=IMXUSDT"));
        Assert.Contains(binance.Urls, u => u.Contains("symbol=USDTTRY"));
        Assert.Empty(gecko.Urls);
    }

    /// Kullanici eski kodu yazmis olabilir; Binance yalnizca yeni kodu listeliyor.
    [Theory]
    [InlineData("MATIC", "POLTRY")]
    [InlineData("RNDR", "RENDERTRY")]
    public async Task YenidenAdlandirilanCoinler_YeniKodaEslenir(string eski, string beklenenParite)
    {
        var (provider, binance, _) = Create();

        await provider.GetCurrentPriceAsync(eski, "crypto");

        Assert.Contains(binance.Urls, u => u.Contains($"symbol={beklenenParite}"));
    }

    /// Binance'te hic listelenmeyen coin icin kapsama kaybi olmamali.
    [Fact]
    public async Task BinancedeYoksa_CoinGeckoyaDuser()
    {
        var (provider, _, gecko) = Create();

        var quote = await provider.GetCurrentPriceAsync("TON", "crypto");

        Assert.Equal(150.5m, quote.Price);
        Assert.NotEmpty(gecko.Urls);
    }

    /// Islem gormeyen (BREAK) pariteler kullanilmamali.
    [Fact]
    public async Task IslemGormeyenParite_YokSayilir()
    {
        var (provider, _, gecko) = Create();

        await provider.GetCurrentPriceAsync("ESKICOIN", "crypto");

        // Binance'te aktif parite yok -> CoinGecko'ya dusmeli.
        Assert.NotEmpty(gecko.Urls);
    }

    /// Toplu yenileyicinin tek istekte tum sembolleri alabilmesi, dis istek
    /// sayisinin kullanici sayisindan bagimsiz kalmasinin temeli.
    [Fact]
    public async Task ToplubarIstegi_TekIstekteTumSemboller()
    {
        var (provider, binance, _) = Create();

        var bars = await provider.GetTodayBarsAsync(new[] { "BTC", "MATIC", "IMX" });

        Assert.Equal(3, bars.Count);
        Assert.Single(binance.Urls, u => u.Contains("ticker/tradingDay"));
        // IMX kur cevrimi gerektirdigi icin USDTTRY de ayni istege eklenmis olmali.
        var tradingDayUrl = binance.Urls.First(u => u.Contains("ticker/tradingDay"));
        Assert.Contains("USDTTRY", tradingDayUrl);
    }

    [Fact]
    public async Task ToplubarIstegi_KurCevrimiUygulanir()
    {
        var (provider, _, _) = Create();

        var bars = await provider.GetTodayBarsAsync(new[] { "IMX" });

        Assert.Equal(2.5m * 48.20m, bars["IMX"].Close);
    }

    /// Sembol listesi her istekte yeniden cekilseydi (yanit ~2 MB) bu tek
    /// basina bir performans sorunu olurdu.
    [Fact]
    public async Task SembolListesi_OnbelleklenirTekrarCekilmez()
    {
        var (provider, binance, _) = Create();

        await provider.GetCurrentPriceAsync("BTC", "crypto");
        await provider.GetCurrentPriceAsync("BTC", "crypto");
        await provider.GetCurrentPriceAsync("MATIC", "crypto");

        Assert.Single(binance.Urls, u => u.Contains("exchangeInfo"));
    }

    /// Kripto 7/24 islem goruyor; gun ici istek 5 dakikalik barlari
    /// dogrudan cekmeli, depoya yazmamali.
    [Fact]
    public async Task GunIciIstek_5DakikalikCekilir()
    {
        var (provider, binance, _) = Create();
        var bugun = new DateTime(2026, 8, 29);

        await provider.GetHistoricalPricesAsync("BTC", "crypto", bugun, bugun);

        Assert.Contains(binance.Urls, u => u.Contains("interval=5m"));
    }
}
