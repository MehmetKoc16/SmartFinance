using SmartFinance.Infrastructure.BankParsers;

namespace SmartFinance.Tests;

/// <summary>
/// Regresyon (26.09.2026, gercek Ziraat ekstresi, test kullanicisi): kategoriler
/// cok yanlisti. Anahtar kelimeler metnin ICINDE parca olarak araniyordu:
/// BALIK -> BALIKESIR (Balikesir'deki her alisveris Yeme-Icme), SOK -> SOKAK,
/// ATM -> ATMACA, UCRET -> "MESAJ UCRETI" (banka masrafi Maas oldu).
/// </summary>
public class KeywordCategoryMatcherTests
{
    private static List<string> Adaylar(string metin) => KeywordCategoryMatcher.Candidates(metin).ToList();

    [Theory]
    [InlineData("POS ALIŞVERİŞ KART NO: 9999 İŞYERİ: ORNEK LTD BALIKESIR TR", "Yeme-İçme")]
    [InlineData("ATMACA SOKAK NO 5", "ATM")]
    [InlineData("ATMACA SOKAK NO 5", "Alışveriş")]
    [InlineData("KOOPERATIF AIDATI", "Ulaşım")]
    public void KelimeninParcasi_EslesmeSayilmaz(string metin, string olmamali)
    {
        Assert.DoesNotContain(olmamali, Adaylar(metin));
    }

    [Theory]
    [InlineData("BALIK EKMEK", "Yeme-İçme")]
    [InlineData("KARDESLER BALIKCI", "Yeme-İçme")]       // BALIK + CI eki
    [InlineData("MIGROS MARKETI", "Alışveriş")]           // MARKET + I eki
    [InlineData("ELEKTRİK FATURASI", "Fatura")]
    [InlineData("SU FATURASI ODEME", "Fatura")]          // iki kelimelik anahtar + ek
    [InlineData("MAAS ODEMESI", "Maaş")]                  // aksansiz
    [InlineData("MAAŞ ÖDEMESİ", "Maaş")]                  // aksanli
    [InlineData("PARA CEKME TEB00542 NOLU ATM", "ATM")]
    [InlineData("SHELL PETROL", "Ulaşım")]
    [InlineData("EFT GELEN KİRA", "Kira Geliri")]
    [InlineData("EYLUL KIRASI", "Kira Geliri")]
    [InlineData("KOMIS. BP ISTASYON", "Ulaşım")]          // eski " BP " anahtari
    public void KelimeVeTurkceEkli_Hali_Eslesir(string metin, string beklenen)
    {
        Assert.Contains(beklenen, Adaylar(metin));
    }

    /// "Ucret" gelirde maas, giderde banka masrafi olabilir; ikisi de aday
    /// olmali, tur secimi cagirana ait.
    [Fact]
    public void Ucret_HemMaasHemFaturaAdayi()
    {
        var adaylar = Adaylar("F20001 MESAJ ÜCRETİ TUTARI");
        Assert.Contains("Maaş", adaylar);
        Assert.Contains("Fatura", adaylar);
    }

    [Fact]
    public void EslesmeYoksa_Bos()
    {
        Assert.Empty(Adaylar("ORNEK LTD STI"));
    }
}
