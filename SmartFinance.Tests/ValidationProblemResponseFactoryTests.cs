using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using SmartFinance.API.Infrastructure;
using SmartFinance.Application.DTOs.Budget;

namespace SmartFinance.Tests;

/// Dogrulama hatalarinin istemcinin bekledigi bicimde ("message" alani ile)
/// dondugunu garanti eder. Varsayilan ProblemDetails biciminde donerse
/// DTO'lardaki Turkce hata mesajlari kullaniciya ulasmaz.
public class ValidationProblemResponseFactoryTests
{
    private static ActionContext ContextWithErrors(params (string alan, string mesaj)[] hatalar)
    {
        var modelState = new ModelStateDictionary();
        foreach (var (alan, mesaj) in hatalar)
            modelState.AddModelError(alan, mesaj);

        return new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor(), modelState);
    }

    /// Anonim tipteki yanit govdesinden alan okur.
    private static object? Alan(IActionResult result, string ad)
    {
        var value = Assert.IsType<BadRequestObjectResult>(result).Value!;
        return value.GetType().GetProperty(ad)!.GetValue(value);
    }

    [Fact]
    public void TekHata_MesajAlaniIcindeDoner()
    {
        var result = ValidationProblemResponseFactory.Create(
            ContextWithErrors(("MonthlyLimit", "Aylık limit 0'dan büyük olmalıdır!")));

        Assert.Equal("Aylık limit 0'dan büyük olmalıdır!", Alan(result, "message"));
    }

    [Fact]
    public void Yanit_400VeTraceIdIcerir()
    {
        var result = ValidationProblemResponseFactory.Create(
            ContextWithErrors(("Amount", "Tutar zorunludur!")));

        Assert.Equal(StatusCodes.Status400BadRequest, Alan(result, "statusCode"));
        Assert.False(string.IsNullOrWhiteSpace((string?)Alan(result, "traceId")));
    }

    /// Kullanici formu tek seferde duzeltebilsin diye tum hatalar donmeli.
    [Fact]
    public void BirdenFazlaHata_HepsiSatirSatirDoner()
    {
        var result = ValidationProblemResponseFactory.Create(ContextWithErrors(
            ("Name", "Sembol zorunludur!"),
            ("Quantity", "Miktar 0'dan büyük olmalıdır!")));

        var message = (string)Alan(result, "message")!;
        Assert.Contains("Sembol zorunludur!", message);
        Assert.Contains("Miktar 0'dan büyük olmalıdır!", message);
        Assert.Contains("\n", message);
    }

    /// Mesaji olmayan hata (ornegin tip donusturme hatasi) bos "message"
    /// birakmamali — istemci en azindan anlamli bir sey gostersin.
    [Fact]
    public void MesajsizHata_GenelMetinDoner()
    {
        var result = ValidationProblemResponseFactory.Create(ContextWithErrors(("Amount", "")));

        Assert.Equal("Gönderilen veri geçersiz.", Alan(result, "message"));
    }
}

/// CreateBudgetDto uzerindeki dogrulama kurallari. Butce limiti daha once hic
/// dogrulanmiyordu: API'ye dogrudan istekle 0 veya negatif limit yazilabiliyordu
/// (arayuz kontrol ediyordu ama istemci tarafi kontrol guvenlik degildir).
public class CreateBudgetDtoValidationTests
{
    private static IList<ValidationResult> Dogrula(CreateBudgetDto dto)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(dto, new ValidationContext(dto), results, validateAllProperties: true);
        return results;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-1000)]
    public void SifirVeyaNegatifLimit_Reddedilir(decimal limit)
    {
        var sonuc = Dogrula(new CreateBudgetDto { CategoryId = 1, MonthlyLimit = limit });

        Assert.Contains(sonuc, r => r.ErrorMessage!.Contains("0'dan büyük"));
    }

    [Fact]
    public void GecersizKategoriId_Reddedilir()
    {
        var sonuc = Dogrula(new CreateBudgetDto { CategoryId = 0, MonthlyLimit = 100 });

        Assert.Contains(sonuc, r => r.ErrorMessage!.Contains("kategori"));
    }

    [Fact]
    public void GecerliDeger_KabulEdilir()
    {
        var sonuc = Dogrula(new CreateBudgetDto { CategoryId = 5, MonthlyLimit = 1500m });

        Assert.Empty(sonuc);
    }
}
