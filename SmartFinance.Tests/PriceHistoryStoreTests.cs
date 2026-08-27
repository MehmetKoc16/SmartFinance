using Microsoft.EntityFrameworkCore;
using SmartFinance.Application.DTOs.MarketData;
using SmartFinance.Domain.Entities;
using SmartFinance.Infrastructure.Context;
using SmartFinance.Infrastructure.MarketData;

namespace SmartFinance.Tests;

/// Fon ve hisse gecmisi ayni tabloda tutuluyor; tekillik sembol + TIP + tarih
/// uzerinden kuruldugu icin ayni kodun iki piyasada cakismamasi kritik.
public class PriceHistoryStoreTests
{
    private static readonly DateTime Day = new(2026, 8, 26);

    private static (PriceHistoryStore store, SmartFinanceDbContext context) Create()
    {
        var options = new DbContextOptionsBuilder<SmartFinanceDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString()).Options;
        var context = new SmartFinanceDbContext(options);
        return (new PriceHistoryStore(context), context);
    }

    private static PriceBarDto Bar(DateTime date, decimal close) =>
        new() { Date = date, Open = close, High = close, Low = close, Close = close, Volume = 100 };

    [Fact]
    public async Task Upsert_YeniKayitlariEkler_SayisiniDoner()
    {
        var (store, _) = Create();

        var added = await store.UpsertAsync("THYAO", "stock",
            new[] { Bar(Day, 300), Bar(Day.AddDays(-1), 295) });

        Assert.Equal(2, added);
    }

    [Fact]
    public async Task Upsert_AyniGunTekrarGelirse_YeniKayitEklemez_DegeriGunceller()
    {
        var (store, _) = Create();
        await store.UpsertAsync("THYAO", "stock", new[] { Bar(Day, 300) });

        // Seans surerken yazilan kismi bar, kapanis degeriyle duzeltilir.
        var added = await store.UpsertAsync("THYAO", "stock", new[] { Bar(Day, 305) });

        Assert.Equal(0, added);
        var bars = await store.GetRangeAsync("THYAO", "stock", Day, Day);
        Assert.Equal(305, Assert.Single(bars).Close);
    }

    [Fact]
    public async Task AyniKod_FarkliTipler_BirbiriniEzmez()
    {
        var (store, _) = Create();

        await store.UpsertAsync("AFA", "fund", new[] { Bar(Day, 1.28m) });
        await store.UpsertAsync("AFA", "stock", new[] { Bar(Day, 42m) });

        var fund = await store.GetRangeAsync("AFA", "fund", Day, Day);
        var stock = await store.GetRangeAsync("AFA", "stock", Day, Day);

        Assert.Equal(1.28m, Assert.Single(fund).Close);
        Assert.Equal(42m, Assert.Single(stock).Close);
    }

    [Fact]
    public async Task SembolVeTip_BuyukKucukHarfDuyarsizdir()
    {
        var (store, _) = Create();
        await store.UpsertAsync(" thyao ", "Stock", new[] { Bar(Day, 300) });

        var bars = await store.GetRangeAsync("THYAO", "stock", Day, Day);

        Assert.Single(bars);
    }

    /// Saklanan bicim tek olmali: sembol buyuk, tip kucuk harf. Aksi halde
    /// eslesme veritabaninin harf duyarliligi ayarina baglanir ve veri var
    /// oldugu halde bulunamayabilir.
    [Fact]
    public async Task Kayit_SembolBuyuk_TipKucukHarfleSaklanir()
    {
        var (store, context) = Create();
        await store.UpsertAsync(" thyao ", "STOCK", new[] { Bar(Day, 300) });

        var row = await context.PriceHistories.SingleAsync();
        Assert.Equal("THYAO", row.Symbol);
        Assert.Equal("stock", row.InvestmentType);
    }

    [Fact]
    public async Task GetRange_AralikDisindakileriElemeli_TariheGoreSiralamali()
    {
        var (store, _) = Create();
        await store.UpsertAsync("THYAO", "stock", new[]
        {
            Bar(Day, 300), Bar(Day.AddDays(-1), 295), Bar(Day.AddDays(-10), 280),
        });

        var bars = await store.GetRangeAsync("THYAO", "stock", Day.AddDays(-2), Day);

        Assert.Equal(2, bars.Count);
        Assert.Equal(Day.AddDays(-1), bars[0].Date);
        Assert.Equal(Day, bars[1].Date);
    }

    [Fact]
    public async Task GetLatestDate_KayitYoksaNullDoner()
    {
        var (store, _) = Create();
        Assert.Null(await store.GetLatestDateAsync("THYAO", "stock"));
    }

    [Fact]
    public async Task GetLatestDate_YalnizcaAyniTipiDikkateAlir()
    {
        var (store, _) = Create();
        await store.UpsertAsync("AFA", "fund", new[] { Bar(Day, 1.28m) });
        await store.UpsertAsync("AFA", "stock", new[] { Bar(Day.AddDays(-5), 42m) });

        Assert.Equal(Day.AddDays(-5), await store.GetLatestDateAsync("AFA", "stock"));
    }

    /// Senkron isi yalnizca kullanicilarin gercekten tuttugu sembolleri
    /// gunceller — piyasadaki tum sembolleri cekmek hiz sinirini bosa harcardi.
    [Fact]
    public async Task GetTrackedSymbols_YalnizcaIstenenTipiTekilOlarakDoner()
    {
        var (store, context) = Create();
        var user = new User { FullName = "T", Email = $"{Guid.NewGuid()}@t.com", PasswordHash = "x" };
        context.Users.Add(user);
        context.SaveChanges();

        context.Investments.AddRange(
            new Investment { UserId = user.Id, Name = "thyao", InvestmentType = "stock", Quantity = 1, PurchasePrice = 1 },
            new Investment { UserId = user.Id, Name = "THYAO", InvestmentType = "stock", Quantity = 2, PurchasePrice = 1 },
            new Investment { UserId = user.Id, Name = "AFA", InvestmentType = "fund", Quantity = 1, PurchasePrice = 1 });
        context.SaveChanges();

        var stocks = await store.GetTrackedSymbolsAsync("stock");
        var funds = await store.GetTrackedSymbolsAsync("fund");

        Assert.Equal(new[] { "THYAO" }, stocks);
        Assert.Equal(new[] { "AFA" }, funds);
    }
}
