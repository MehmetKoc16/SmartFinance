using SmartFinance.Infrastructure.BankParsers;

namespace SmartFinance.Tests;

public class KeywordCategoryMatcherOcrTests
{
    // NEDEN: OCR ayırıcıyı atlayınca OBILET başına tek harf/rakam yapışabiliyor.
    [Theory]
    [InlineData("SOBILET 3")]
    [InlineData("7OBILET 3")]
    [InlineData("sobilet 3")]
    public void OcrBastaTekHarfFazlasi_Eslesir(string text)
    {
        Assert.Contains("Ulaşım", KeywordCategoryMatcher.Candidates(text));
    }

    // NEDEN: Dört harfli OPET anahtarı toleransla ilgisiz kelimelere dönüşmemeli.
    [Theory]
    [InlineData("SOPET")]
    [InlineData("7OPET")]
    public void OcrToleransi_KisaAnahtardaUygulanmaz(string text)
    {
        Assert.DoesNotContain("Ulaşım", KeywordCategoryMatcher.Candidates(text));
    }

    // NEDEN: İki fazla karakter veya baş ve son fazlası dar OCR kuralını aşar.
    [Theory]
    [InlineData("SSOBILET 3")]
    [InlineData("S7OBILET 3")]
    [InlineData("SOBILETI 3")]
    public void OcrToleransi_IkiHarfFazlaysaEslesmez(string text)
    {
        Assert.DoesNotContain("Ulaşım", KeywordCategoryMatcher.Candidates(text));
    }

    // NEDEN: Önceden düzeltilen kelime parçası hataları OCR toleransıyla geri gelmemeli.
    [Theory]
    [InlineData("BALIKESIR", "Yeme-İçme")]
    [InlineData("ATMACA", "ATM")]
    [InlineData("ATMACA", "Alışveriş")]
    [InlineData("BURSA", "Ek Gelir")]
    [InlineData("KOOPERATIF", "Ulaşım")]
    public void OcrToleransi_MevcutNegatifleriEslesmez(string text, string category)
    {
        Assert.DoesNotContain(category, KeywordCategoryMatcher.Candidates(text));
    }
}
