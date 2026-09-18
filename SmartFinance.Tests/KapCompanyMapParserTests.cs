using SmartFinance.Infrastructure.Fundamentals;

namespace SmartFinance.Tests;

/// kap.org.tr/tr/bist-sirketler sayfasindaki gomulu listenin bicimi: kacisli
/// JSON, her kayitta mkkMemberOid, kapMemberTitle, ... stockCode.
public class KapCompanyMapParserTests
{
    private static string Kayit(string oid, string unvan, string kodlar) =>
        $"{{\\\"mkkMemberOid\\\":\\\"{oid}\\\",\\\"kapMemberTitle\\\":\\\"{unvan}\\\",\\\"relatedMemberTitle\\\":\\\"PwC\\\",\\\"stockCode\\\":\\\"{kodlar}\\\",\\\"kapMemberType\\\":\\\"IGS\\\"}}";

    private static string Sayfa(params string[] kayitlar)
    {
        var dolgu = Enumerable.Range(0, 100).Select(i => Kayit($"oid{i}", $"SIRKET {i}", $"KOD{i}"));
        return "<script>self.__next_f.push([1,\"[" + string.Join(",", kayitlar.Concat(dolgu)) + "]\"])</script>";
    }

    [Fact]
    public void HisseKodu_KimlikVeUnvanlaEslenir()
    {
        var harita = KapCompanyMapParser.Parse(Sayfa(Kayit("4028e4a140f2ed720140f376bebb01a7", "TÜRK HAVA YOLLARI A.O.", "THYAO")));

        Assert.Equal(new KapCompany("4028e4a140f2ed720140f376bebb01a7", "TÜRK HAVA YOLLARI A.O."), harita["THYAO"]);
    }

    [Fact]
    public void BirdenFazlaHisseKoduAyniSirketeEslenir()
    {
        var harita = KapCompanyMapParser.Parse(Sayfa(Kayit("isbank", "TÜRKİYE İŞ BANKASI A.Ş.", "ISCTR, ISATR, ISBTR")));

        Assert.Equal("isbank", harita["ISCTR"].Oid);
        Assert.Equal("isbank", harita["ISBTR"].Oid);
    }

    /// Sayfa duzeni degisirse bos/eksik harita sessizce tum F/K'lari
    /// dusururdu; hata gorunur olmali.
    [Fact]
    public void SupheliKisaListe_HataVerir() =>
        Assert.Throws<InvalidOperationException>(() => KapCompanyMapParser.Parse(Kayit("x", "Y", "THYAO")));
}
