using Microsoft.EntityFrameworkCore;
using SmartFinance.Infrastructure.Context;
using SmartFinance.Infrastructure.Fundamentals;

namespace SmartFinance.Tests;

public class KapFundamentalsSyncTests
{
    private static SmartFinanceDbContext Db() => new(new DbContextOptionsBuilder<SmartFinanceDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static KapFinancialRow R(int yil, int donem, decimal kar, bool konsolide = true, int gun = 1) =>
        new("TÜRK HAVA YOLLARI A.O.", "1", new DateTime(2026, 8, gun), yil, donem, konsolide, kar, kar * 10);

    [Fact]
    public async Task AyniDonemIcin_KonsolideTabloKaydedilir()
    {
        using var db = Db();

        await KapFundamentalsSyncService.SaveAsync(db, "THYAO", [R(2026, 2, 5, konsolide: false), R(2026, 2, 18_864)]);

        var kayit = Assert.Single(db.CompanyFinancials);
        Assert.Equal(18_864m, kayit.NetProfitParent);
        Assert.True(kayit.Consolidated);
    }

    /// Her senkron turu ayni donemleri tekrar getiriyor; kayit cogalmamali,
    /// yeni yayimlanan deger eskisinin uzerine yazilmali.
    [Fact]
    public async Task TekrarKaydetmek_KaydiGunceller_Cogaltmaz()
    {
        using var db = Db();
        await KapFundamentalsSyncService.SaveAsync(db, "THYAO", [R(2026, 2, 100)]);

        await KapFundamentalsSyncService.SaveAsync(db, "THYAO", [R(2026, 2, 18_864)]);

        Assert.Equal(18_864m, Assert.Single(db.CompanyFinancials).NetProfitParent);
    }

    [Fact]
    public void Mayista_BuYilVeGecenYilIstenir() =>
        Assert.Equal([[2026, 2025]], KapFundamentalsSyncService.YearGroups(new DateTime(2026, 5, 10)));

    /// Subatta gecen yilin yillik raporu henuz yok; son donem gecen yilin 3.
    /// ceyregi ve TTM icin iki yil oncesi de gerekiyor.
    [Fact]
    public void Subatta_IkiYilOncesiDeIstenir() =>
        Assert.Equal([[2027, 2026], [2025]], KapFundamentalsSyncService.YearGroups(new DateTime(2027, 2, 10)));
}
