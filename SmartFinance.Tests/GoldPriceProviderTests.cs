using System.Net;
using System.Text;
using SmartFinance.Application.DTOs.MarketData;
using SmartFinance.Application.Exceptions;
using SmartFinance.Application.Interfaces;
using SmartFinance.Infrastructure.MarketData;

namespace SmartFinance.Tests;

/// Altin fiyati Binance PAXG/TRY paritesinden turetiliyor: 1 PAXG = 1 troy ons.
/// Ons -> gram cevrimi bu saglayicidaki tek matematiksel islem ve yanlis olursa
/// kullanici portfoyunu 31 kat yanlis gorur — bu yuzden ayrica test ediliyor.
public class GoldPriceProviderTests
{
    private const decimal GramsPerOunce = 31.1034768m;

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _json;
        public List<string> Urls { get; } = new();

        public StubHandler(string json) => _json = json;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class EmptyStore : IPriceHistoryStore
    {
        public int UpsertCalls { get; private set; }

        public Task<IReadOnlyList<PriceBarDto>> GetRangeAsync(
            string symbol, string investmentType, DateTime from, DateTime to, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PriceBarDto>>(Array.Empty<PriceBarDto>());

        public Task<DateTime?> GetLatestDateAsync(string symbol, string investmentType, CancellationToken ct = default)
            => Task.FromResult<DateTime?>(null);

        public Task<int> UpsertAsync(
            string symbol, string investmentType, IEnumerable<PriceBarDto> bars, CancellationToken ct = default)
        {
            UpsertCalls++;
            return Task.FromResult(bars.Count());
        }

        public Task<IReadOnlyList<string>> GetTrackedSymbolsAsync(string investmentType, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }

    // Binance kline dizisi: [acilisZamani, acilis, yuksek, dusuk, kapanis, hacim, ...]
    // 29.08.2026 gercek degerlerine yakin ornek (ons cinsinden).
    private const string TekBarJson = """
        [[1787961600000,"215000.00000000","216000.00000000","214000.00000000","215151.00000000","10.50000000",1788047999999,"0",0,"0","0","0"]]
        """;

    private static (GoldPriceProvider provider, StubHandler handler, EmptyStore store) Create(string json = TekBarJson)
    {
        var handler = new StubHandler(json);
        var store = new EmptyStore();
        var provider = new GoldPriceProvider(new HttpClient(handler), store);
        return (provider, handler, store);
    }

    [Fact]
    public async Task GuncelFiyat_OnsFiyatiniGramaCevirir()
    {
        var (provider, _, _) = Create();

        var quote = await provider.GetCurrentPriceAsync("GRAM ALTIN", "gold");

        // 215151 / 31,1034768 = 6916,63...
        Assert.Equal(215151m / GramsPerOunce, quote.Price);
        Assert.InRange(quote.Price, 6900m, 6930m);
    }

    [Fact]
    public async Task GuncelFiyat_TamAdiDoldurur()
    {
        var (provider, _, _) = Create();

        var quote = await provider.GetCurrentPriceAsync("GRAM ALTIN", "gold");

        Assert.Equal("Gram Altın", quote.LongName);
    }

    /// OHLC alanlarinin her biri ayri ayri cevrilmeli; hepsine kapanis
    /// yazilsaydi mum grafigi duz cizgi olurdu.
    [Fact]
    public async Task GunlukBar_TumOHLCAlanlariAyriCevrilir()
    {
        var (provider, _, _) = Create();

        var bars = await provider.FetchDailyBarsAsync("GRAM ALTIN", new DateTime(2026, 8, 29), new DateTime(2026, 8, 29));

        var bar = Assert.Single(bars);
        Assert.Equal(215000m / GramsPerOunce, bar.Open);
        Assert.Equal(216000m / GramsPerOunce, bar.High);
        Assert.Equal(214000m / GramsPerOunce, bar.Low);
        Assert.Equal(215151m / GramsPerOunce, bar.Close);
        Assert.True(bar.High > bar.Low, "Yuksek, dusukten buyuk olmali.");
    }

    /// Fiyat grama cevrildigi icin hacim de grama cevrilmeli; aksi halde
    /// fiyat x hacim = TL cirosu tutarsiz olur.
    [Fact]
    public async Task GunlukBar_HacimGramaCevrilir()
    {
        var (provider, _, _) = Create();

        var bars = await provider.FetchDailyBarsAsync("GRAM ALTIN", new DateTime(2026, 8, 29), new DateTime(2026, 8, 29));

        Assert.Equal(10.5m * GramsPerOunce, Assert.Single(bars).Volume);
    }

    [Fact]
    public async Task TanimsizSembol_Reddedilir()
    {
        var (provider, handler, _) = Create();

        await Assert.ThrowsAsync<ExternalServiceException>(
            () => provider.GetCurrentPriceAsync("GUMUS", "gold"));

        Assert.Empty(handler.Urls);
    }

    [Theory]
    [InlineData("GRAM ALTIN")]
    [InlineData("altin")]
    [InlineData(" GOLD ")]
    public async Task BilinenSembollerKabulEdilir(string symbol)
    {
        var (provider, _, _) = Create();

        var quote = await provider.GetCurrentPriceAsync(symbol, "gold");

        Assert.True(quote.Price > 0);
    }

    /// Gun ici (from==to) istek depoya hic ugramamali: 5 dakikalik barlar
    /// ertesi gun degersiz, gunluk barlarla ayni tabloda tutulmamali.
    [Fact]
    public async Task GunIciIstek_DepoyaYazilmaz_5DakikalikCekilir()
    {
        var (provider, handler, store) = Create();
        var bugun = new DateTime(2026, 8, 29);

        await provider.GetHistoricalPricesAsync("GRAM ALTIN", "gold", bugun, bugun);

        Assert.Equal(0, store.UpsertCalls);
        Assert.Contains("interval=5m", Assert.Single(handler.Urls));
    }

    /// Cok gunluk istek depo uzerinden gecmeli ve kaynaktan gunluk bar istemeli.
    [Fact]
    public async Task CokGunlukIstek_DepoyaYazilir_GunlukCekilir()
    {
        var (provider, handler, store) = Create();

        await provider.GetHistoricalPricesAsync(
            "GRAM ALTIN", "gold", new DateTime(2026, 3, 1), new DateTime(2026, 8, 29));

        Assert.Equal(1, store.UpsertCalls);
        Assert.Contains("interval=1d", handler.Urls[0]);
    }

    /// Toplu yenileyici tum altin sembollerini tek istekle karsilamali.
    [Fact]
    public async Task ToplubarIstegi_TekDisIstekAtar()
    {
        var (provider, handler, _) = Create();

        var bars = await provider.GetTodayBarsAsync(new[] { "GRAM ALTIN", "ALTIN" });

        Assert.Equal(2, bars.Count);
        Assert.Single(handler.Urls);
    }

    [Fact]
    public async Task ToplubarIstegi_TanimsizSembolleriYokSayar()
    {
        var (provider, handler, _) = Create();

        var bars = await provider.GetTodayBarsAsync(new[] { "THYAO", "BTC" });

        Assert.Empty(bars);
        Assert.Empty(handler.Urls);
    }
}
