using SmartFinance.API.Cli;

namespace SmartFinance.Tests;

public class DuyuruKomutuTests
{
    [Fact]
    public void YalnizcaDuyuruKomutuYakalanir_NormalBaslangicEtkilenmez()
    {
        Assert.True(DuyuruKomutu.KomutMu(["duyuru", "k", "b", "m"]));
        Assert.False(DuyuruKomutu.KomutMu([]));
        Assert.False(DuyuruKomutu.KomutMu(["--urls", "http://127.0.0.1:5059"]));
    }

    [Fact]
    public void Argumanlar_Ayristirilir()
    {
        var i = DuyuruKomutu.Ayristir(["duyuru", "surum11", "Yeni sürüm", "Güncelleyin.", "--yalnizca", "a@b.com"]);
        Assert.Equal(new DuyuruKomutu.Istek("surum11", "Yeni sürüm", "Güncelleyin.", "a@b.com"), i);

        var herkes = DuyuruKomutu.Ayristir(["duyuru", "surum11", "Başlık", "Metin"]);
        Assert.Null(herkes.YalnizcaEposta);
        Assert.False(herkes.Zorunlu);
    }

    // NEDEN: Tercihi yok sayan gonderim yalnizca acik bayrakla olmali ve yardim metni
    // bayragin hangi bildirimler icin oldugunu soylemeli (KVKK / 6563).
    [Fact]
    public void ZorunluBayragi_Ayristirilir_VeYardimdaAciklanir()
    {
        var i = DuyuruKomutu.Ayristir(["duyuru", "guvenlik-1", "Başlık", "Metin", "--zorunlu", "--yalnizca", "a@b.com"]);
        Assert.True(i.Zorunlu);
        Assert.Equal("a@b.com", i.YalnizcaEposta);
        Assert.Equal("guvenlik-1", i.Anahtar);
        Assert.Contains("--zorunlu", DuyuruKomutu.Kullanim);
        Assert.Contains("Yalnız güvenlik, veri ihlali ve hukuki zorunlu bildirimler için", DuyuruKomutu.Kullanim);
    }

    [Theory]
    [InlineData("duyuru", "surum11", "Başlık")]                    // metin eksik
    [InlineData("duyuru", "surum11", "Başlık", "Metin", "fazla")]  // fazla arguman
    [InlineData("duyuru", "surum11", "Başlık", "Metin", "--yalnizca")]
    public void EksikYaDaFazlaArguman_Reddedilir(params string[] args)
    {
        Assert.Throws<ArgumentException>(() => DuyuruKomutu.Ayristir(args));
    }
}
