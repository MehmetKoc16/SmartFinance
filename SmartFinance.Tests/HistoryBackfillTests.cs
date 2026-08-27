using SmartFinance.Application.DTOs.MarketData;
using SmartFinance.Application.Interfaces;
using SmartFinance.Infrastructure.MarketData;

namespace SmartFinance.Tests;

/// Kullanici isteklerinin dis servise gitmesini engelleyen kapsama mantigi.
/// Bu kontrol bir kez hatali yazilmisti: depoda veri "olmasi" yeterli sanilmis,
/// istenen araligi KAPSAMASI aranmamisti — 6 aylik grafik 8 barla donuyordu.
public class HistoryBackfillTests
{
    private static readonly DateTime Today = new(2026, 8, 26);

    private sealed class FakeSource : IHistorySource
    {
        public List<(DateTime From, DateTime To)> Calls { get; } = new();
        public TimeSpan InterSymbolDelay => TimeSpan.Zero;

        public Task<IReadOnlyList<PriceBarDto>> FetchDailyBarsAsync(
            string symbol, DateTime from, DateTime to, CancellationToken ct = default)
        {
            Calls.Add((from.Date, to.Date));
            var bars = new List<PriceBarDto>();
            for (var d = from.Date; d <= to.Date; d = d.AddDays(1))
                bars.Add(new PriceBarDto { Date = d, Open = 10, High = 10, Low = 10, Close = 10, Volume = 0 });
            return Task.FromResult<IReadOnlyList<PriceBarDto>>(bars);
        }
    }

    private sealed class FakeStore : IPriceHistoryStore
    {
        private readonly Dictionary<DateTime, PriceBarDto> _bars = new();

        public FakeStore(params DateTime[] dates)
        {
            foreach (var d in dates)
                _bars[d.Date] = new PriceBarDto { Date = d.Date, Close = 5 };
        }

        public Task<IReadOnlyList<PriceBarDto>> GetRangeAsync(
            string symbol, string investmentType, DateTime from, DateTime to, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PriceBarDto>>(
                _bars.Values.Where(b => b.Date >= from.Date && b.Date <= to.Date).OrderBy(b => b.Date).ToList());

        public Task<DateTime?> GetLatestDateAsync(string symbol, string investmentType, CancellationToken ct = default)
            => Task.FromResult(_bars.Count == 0 ? (DateTime?)null : _bars.Keys.Max());

        public Task<int> UpsertAsync(
            string symbol, string investmentType, IEnumerable<PriceBarDto> bars, CancellationToken ct = default)
        {
            var added = 0;
            foreach (var b in bars)
            {
                if (!_bars.ContainsKey(b.Date.Date)) added++;
                _bars[b.Date.Date] = b;
            }
            return Task.FromResult(added);
        }

        public Task<IReadOnlyList<string>> GetTrackedSymbolsAsync(string investmentType, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }

    private static Task<IReadOnlyList<PriceBarDto>> Read(FakeStore store, FakeSource source, int days)
        => HistoryBackfill.ReadWithBackfillAsync(store, source, "AFA", "fund", Today.AddDays(-days), Today);

    [Fact]
    public async Task DepoBos_TumAralikKaynaktanCekilir()
    {
        var store = new FakeStore();
        var source = new FakeSource();

        var bars = await Read(store, source, 30);

        Assert.Single(source.Calls);
        Assert.Equal(Today.AddDays(-30), source.Calls[0].From);
        Assert.Equal(Today, source.Calls[0].To);
        Assert.Equal(31, bars.Count);
    }

    [Fact]
    public async Task DepoAraligiKapsiyorsa_KaynagaHicGidilmez()
    {
        var dates = Enumerable.Range(0, 31).Select(i => Today.AddDays(-i)).ToArray();
        var store = new FakeStore(dates);
        var source = new FakeSource();

        var bars = await Read(store, source, 30);

        Assert.Empty(source.Calls);
        Assert.Equal(31, bars.Count);
    }

    /// Asil regresyon testi: guncel fiyat icin yalnizca son 10 gun cekilmisken
    /// 6 aylik grafik istendiginde eksik on kisim tamamlanmali.
    [Fact]
    public async Task DepoKismi_EksikOnKisimTamamlanir()
    {
        var dates = Enumerable.Range(0, 10).Select(i => Today.AddDays(-i)).ToArray();
        var store = new FakeStore(dates);
        var source = new FakeSource();

        var bars = await Read(store, source, 180);

        Assert.Single(source.Calls);
        Assert.Equal(Today.AddDays(-180), source.Calls[0].From);
        // Depodaki en eski gunun bir oncesine kadar cekilir; mevcut veri
        // gereksiz yere yeniden istenmez.
        Assert.Equal(Today.AddDays(-10), source.Calls[0].To);
        Assert.Equal(181, bars.Count);
    }

    /// Senkron isi bu sembol icin henuz calismamis: depo eski, ileriye dogru
    /// tamamlanmali. Son saklanan gun ARALIGA DAHIL edilir — o gunun bari
    /// seans surerken kismi yazilmis olabilir.
    [Fact]
    public async Task DepoGuncelDegil_IleriyeDogruTamamlanir()
    {
        var dates = Enumerable.Range(0, 30).Select(i => Today.AddDays(-30 - i)).ToArray();
        var store = new FakeStore(dates);
        var source = new FakeSource();

        await Read(store, source, 60);

        Assert.Single(source.Calls);
        Assert.Equal(Today.AddDays(-30), source.Calls[0].From);
        Assert.Equal(Today, source.Calls[0].To);
    }

    /// Hafta sonu/tatil bosluklari "eksik veri" degildir; tolerans olmasaydi
    /// her istekte bosuna dis servise gidilirdi.
    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    public async Task KucukBosluklar_KaynagaGitmeyiTetiklemez(int gapDays)
    {
        var dates = Enumerable.Range(0, 20).Select(i => Today.AddDays(-gapDays - i)).ToArray();
        var store = new FakeStore(dates);
        var source = new FakeSource();

        await Read(store, source, 20 + gapDays);

        Assert.Empty(source.Calls);
    }

    [Fact]
    public async Task ToleransiAsanBosluk_KaynagaGidilir()
    {
        var dates = Enumerable.Range(0, 20).Select(i => Today.AddDays(-6 - i)).ToArray();
        var store = new FakeStore(dates);
        var source = new FakeSource();

        await Read(store, source, 26);

        Assert.Single(source.Calls);
    }
}
