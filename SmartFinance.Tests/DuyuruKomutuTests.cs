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
