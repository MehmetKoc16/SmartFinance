using SmartFinance.Domain.Entities;
using SmartFinance.Infrastructure.Fundamentals;

namespace SmartFinance.Tests;

public class FundamentalRatioCalculatorTests
{
    private static CompanyFinancial D(int yil, int donem, decimal kar, decimal ozkaynak, bool konsolide = true) => new()
    {
        Symbol = "THYAO", Year = yil, Period = donem,
        NetProfitParent = kar, EquityParent = ozkaynak, Consolidated = konsolide,
    };

    /// THYAO'nun gercek KAP rakamlari (milyon TL). Gelir tablosu kumulatif:
    /// dort ceyregi toplamak yanlis olurdu.
    [Fact]
    public void AraDonemde_SonOnIkiAy_GecenYillikArtiBuYilEksiGecenYilAyniDonem()
    {
        var sonuc = FundamentalRatioCalculator.Compute(
        [
            D(2025, 1, -1_818, 717_346), D(2025, 2, 25_013, 752_073), D(2025, 3, 81_064, 843_939),
            D(2025, 4, 118_208, 911_222), D(2026, 1, 9_915, 966_388), D(2026, 2, 18_864, 1_018_517),
        ]);

        Assert.NotNull(sonuc);
        Assert.Equal(118_208m + 18_864m - 25_013m, sonuc!.TtmNetProfit);
        Assert.Equal(1_018_517m, sonuc.Equity);
        Assert.Equal(2026, sonuc.Year);
        Assert.Equal(2, sonuc.Period);
    }

    [Fact]
    public void SonDonemYillikIse_SonOnIkiAyOYilinKaridir()
    {
        var sonuc = FundamentalRatioCalculator.Compute(
            [D(2025, 2, 25_013, 752_073), D(2025, 4, 118_208, 911_222)]);

        Assert.Equal(118_208m, sonuc!.TtmNetProfit);
        Assert.Equal(911_222m, sonuc.Equity);
    }

    /// Gecen yilin ayni donemi yoksa son 12 ay bilinemez; uydurulmamali.
    [Fact]
    public void GerekenDonemEksikse_HesapYapilmaz() =>
        Assert.Null(FundamentalRatioCalculator.Compute([D(2025, 4, 118_208, 911_222), D(2026, 2, 18_864, 1_018_517)]));

    [Fact]
    public void AyniDonemdeKonsolideTabloSoloyaTercihEdilir()
    {
        var sonuc = FundamentalRatioCalculator.Compute(
            [D(2025, 4, 50, 400, konsolide: false), D(2025, 4, 100, 900)]);

        Assert.Equal(100m, sonuc!.TtmNetProfit);
        Assert.Equal(900m, sonuc.Equity);
    }

    [Fact]
    public void Veriyoksa_Null() => Assert.Null(FundamentalRatioCalculator.Compute([]));
}
