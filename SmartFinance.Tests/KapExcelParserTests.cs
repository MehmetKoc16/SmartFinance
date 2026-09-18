using SmartFinance.Infrastructure.Fundamentals;

namespace SmartFinance.Tests;

/// Girdi, KAP "Finansal Tablo Kalem Sorgulama"nin 18.09.2026'da THYAO ve MPARK
/// icin indirilen gercek Excel ciktisi (2025 ve 2026, dort donem, net kar ve
/// ozkaynak). THYAO milyon TL, MPARK bin TL raporluyor.
public class KapExcelParserTests
{
    private static IReadOnlyList<KapFinancialRow> Oku()
    {
        using var akis = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "kap_kalem_thyao_mpark.xlsx"));
        return KapExcelParser.Parse(akis);
    }

    private static KapFinancialRow Satir(string unvanBasi, int yil, int donem) =>
        Oku().Single(r => r.CompanyTitle.StartsWith(unvanBasi, StringComparison.Ordinal) && r.Year == yil && r.Period == donem);

    [Fact]
    public void TumDonemSatirlariOkunur() => Assert.Equal(12, Oku().Count);

    [Fact]
    public void MilyonTLBirimi_TLyeCevrilir()
    {
        var r = Satir("TÜRK HAVA", 2026, 2);

        Assert.Equal(18_864_000_000m, r.NetProfitParent);
        Assert.Equal(1_018_517_000_000m, r.EquityParent);
        Assert.Equal("1643238", r.DisclosureId);
        Assert.Equal(new DateTime(2026, 8, 5, 8, 0, 21), r.PublishedAt);
        Assert.True(r.Consolidated);
    }

    [Fact]
    public void BinTLBirimi_TLyeCevrilir()
    {
        var r = Satir("MLP", 2025, 4);

        Assert.Equal(5_536_663_000m, r.NetProfitParent);
        Assert.Equal(34_830_773_000m, r.EquityParent);
    }

    [Fact]
    public void NegatifDeger_EksiIsaretiyleOkunur() =>
        Assert.Equal(-1_818_000_000m, Satir("TÜRK HAVA", 2025, 1).NetProfitParent);

    [Theory]
    [InlineData("1000000TL", 1_000_000)]
    [InlineData("1000TL", 1_000)]
    [InlineData("TL", 1)]
    public void BirimCarpani_OkunurVarsayilmaz(string birim, int beklenen) =>
        Assert.Equal((decimal)beklenen, KapExcelParser.BirimCarpani(birim));

    /// Yanlis olcekle kaydetmek (ornegin dolar tutari TL sanmak) sessiz ve
    /// buyuk bir hata olur; satir atlanmali.
    [Fact]
    public void TaninmayanBirim_NullDoner() => Assert.Null(KapExcelParser.BirimCarpani("1000USD"));
}
