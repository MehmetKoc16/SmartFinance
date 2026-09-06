using SmartFinance.Infrastructure.BankParsers;

namespace SmartFinance.Tests;

/// <summary>
/// Ekstre açıklamaları kullanıcının KENDİ verisi değil; havale satırları karşı
/// tarafın IBAN'ını ve adını taşıyor. O kişinin rızası yok. Maskeleme çalışmazsa
/// üçüncü kişilerin hesap numaraları veritabanımızda birikir.
/// </summary>
public class HassasVeriMaskeleyiciTests
{
    // Asagidaki IBAN'lar belge ornegidir, gercek bir hesaba ait degildir.
    // "sahte-veri" isareti commit oncesi kancanin bu satirlari atlamasini
    // sagliyor; isaret olmasa kanca hakli olarak commit'i durduruyor.
    [Theory]
    [InlineData("HAVALE TR330006100519786457841326 AHMET Y", "HAVALE IBAN AHMET Y")]  // sahte-veri
    [InlineData("EFT TR33 0006 1005 1978 6457 8413 26 ODEME", "EFT IBAN ODEME")]  // sahte-veri
    [InlineData("tr330006100519786457841326", "IBAN")]  // sahte-veri
    public void IbanMaskelenir(string girdi, string beklenen)
    {
        Assert.Equal(beklenen, HassasVeriMaskeleyici.Maskele(girdi));
    }

    /// IBAN formatinda olmayan uzun hesap/kart numaralari da kalmamali.
    [Fact]
    public void UzunNumaralarMaskelenir()
    {
        Assert.Equal("HESABA *** GONDERIM",
            HassasVeriMaskeleyici.Maskele("HESABA 1234567890123 GONDERIM"));
    }

    /// Asiri maskeleme de zarar: kullanici islemi taniyamazsa ozellik ise
    /// yaramaz hale gelir. Tarih, tutar ve kisa referanslar korunmali.
    [Theory]
    [InlineData("MIGROS MARKET ALISVERIS")]
    [InlineData("POS 1234 ODEME")]
    [InlineData("FATURA 05.09.2026 TUTAR 1250,75")]
    [InlineData("ATM PARA CEKME 4521")]
    public void NormalAciklamaBozulmaz(string girdi)
    {
        Assert.Equal(girdi, HassasVeriMaskeleyici.Maskele(girdi));
    }

    [Fact]
    public void BosDegerCokmez()
    {
        Assert.Equal(string.Empty, HassasVeriMaskeleyici.Maskele(null));
        Assert.Equal(string.Empty, HassasVeriMaskeleyici.Maskele("   "));
    }
}
