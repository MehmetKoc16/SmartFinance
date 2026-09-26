using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SmartFinance.Application.DTOs.PdfImport;
using SmartFinance.Domain.Entities;
using SmartFinance.Domain.Enums;
using SmartFinance.Infrastructure.Context;
using SmartFinance.Infrastructure.Services;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace SmartFinance.Tests;

/// <summary>
/// Taranmis (goruntu tabanli) ekstre: sunucu PDF'ten metin cikaramaz. Telefon
/// sayfalari resme cevirip OCR yapar, sunucuya YALNIZCA kelimeleri ve
/// konumlarini gonderir (goruntu telefondan cikmaz). Sunucu bu kelimeleri
/// gomulu metinli PDF'lerle ayni sutun tabanli ayristiricidan gecirir.
/// </summary>
public class PdfImportOcrTests
{
    private static (PdfImportService service, SmartFinanceDbContext context, int userId) CreateService()
    {
        var options = new DbContextOptionsBuilder<SmartFinanceDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString()).Options;
        var context = new SmartFinanceDbContext(options);

        var user = new User { FullName = "Test Kullanıcı", Email = $"{Guid.NewGuid()}@test.com", PasswordHash = "x" };
        context.Users.Add(user);
        context.SaveChanges();
        context.Categories.Add(new Category { Name = "Fatura", UserId = user.Id });
        context.SaveChanges();

        var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()) });
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
        var currentUser = new CurrentUserService(accessor);
        var entitlement = new EntitlementService(context, currentUser, new ConfigurationBuilder().Build());
        return (new PdfImportService(context, currentUser, NullLogger<PdfImportService>.Instance, entitlement), context, user.Id);
    }

    // OCR piksel olcegiyle calisir (sayfa 2x cizilince A4 ~1190x1684); kelime
    // yuksekligi ~28 piksel, genislik karakter basina ~14.
    private static OcrWordDto W(string metin, double sol, double ust, int sayfa = 1) => new()
    {
        Text = metin, Left = sol, Top = ust, Right = sol + metin.Length * 14, Bottom = ust + 28, Page = sayfa,
    };

    private static List<OcrWordDto> OrnekTarama() =>
    [
        W("Tarih", 60, 300), W("Açıklama", 280, 300), W("Borç", 840, 300), W("Alacak", 1060, 300), W("Bakiye", 1290, 300),
        W("01.09.2026", 60, 360), W("ELEKTRIK", 280, 361), W("FATURASI", 410, 359), W("420,50", 840, 362), W("1.579,50", 1290, 360),
        W("02.09.2026", 60, 420), W("MAAS", 280, 421), W("5.000,00", 1060, 419), W("6.579,50", 1290, 420),
    ];

    [Fact]
    public async Task OcrKelimeleri_SutunTabanliAyristiricidanGecerVeKategoriEslesir()
    {
        var (service, _, _) = CreateService();

        var sonuc = await service.ParseWordsAsync(OrnekTarama());

        Assert.Equal([(2, 420.50m), (1, 5000m)], sonuc.Transactions.Select(t => (t.Type, t.Amount)));
        Assert.Equal("ELEKTRIK FATURASI", sonuc.Transactions[0].Description);
        // Metinli PDF'lerdeki adimlar OCR icin de calismali: kategori eslesmesi...
        Assert.Equal("Fatura", sonuc.Transactions[0].CategoryName);
        Assert.Equal(PdfImportService.OcrBankName, sonuc.BankName);
        Assert.False(sonuc.NeedsOcr);
    }

    [Fact]
    public async Task OcrKelimeleri_ZatenKayitliIslemiMukerrerIsaretler()
    {
        var (service, context, userId) = CreateService();
        context.Transactions.Add(new Transaction
        {
            UserId = userId, Amount = 5000m, Description = "MAAS", Type = TransactionType.Income,
            TransactionDate = new DateTime(2026, 9, 2), CategoryId = context.Categories.First().Id,
        });
        context.SaveChanges();

        var sonuc = await service.ParseWordsAsync(OrnekTarama());

        Assert.Equal([false, true], sonuc.Transactions.Select(t => t.IsDuplicate));
        Assert.Equal(1, sonuc.DuplicateCount);
    }

    [Fact]
    public async Task TabloBasligiOlmayanOcr_BosSonucDoner()
    {
        var (service, _, _) = CreateService();

        var sonuc = await service.ParseWordsAsync([W("Sayın", 60, 100), W("Müşterimiz", 200, 100), W("01.09.2026", 60, 200)]);

        Assert.Empty(sonuc.Transactions);
    }

    /// Telefon OCR'a yalnizca sunucu "metin yok" dediginde basvurur; bunu metin
    /// icermeyen mesaj parcasindan tahmin etmek yerine acik bir alanla bilmeli.
    [Fact]
    public async Task MetinsizPdf_OcrGerektiginiBildirir()
    {
        var (service, _, _) = CreateService();
        var builder = new PdfDocumentBuilder();
        var sayfa = builder.AddPage(PageSize.A4);
        sayfa.DrawRectangle(new PdfPoint(40, 40), 200, 100); // taranmis sayfa gibi: cizim var, metin yok

        var sonuc = await service.ParsePdfAsync(new MemoryStream(builder.Build()), "tarama.pdf");

        Assert.True(sonuc.NeedsOcr);
        Assert.Empty(sonuc.Transactions);
    }

    [Fact]
    public async Task MetinliPdf_OcrIstemez()
    {
        var (service, _, _) = CreateService();
        var builder = new PdfDocumentBuilder();
        var sayfa = builder.AddPage(PageSize.A4);
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        sayfa.AddText("Tarih Aciklama Tutar", 9, new PdfPoint(40, 700), font);

        var sonuc = await service.ParsePdfAsync(new MemoryStream(builder.Build()), "metin.pdf");

        Assert.False(sonuc.NeedsOcr);
    }

    // ── Istek dogrulama: uc nokta disaridan gelen listeyi oldugu gibi isliyor ──

    private static List<ValidationResult> Dogrula(object nesne)
    {
        var hatalar = new List<ValidationResult>();
        Validator.TryValidateObject(nesne, new ValidationContext(nesne), hatalar, validateAllProperties: true);
        return hatalar;
    }

    [Fact]
    public void BosKelimeListesi_Reddedilir()
    {
        Assert.NotEmpty(Dogrula(new ParseWordsRequestDto { Words = [] }));
    }

    [Fact]
    public void AsiriBuyukKelimeListesi_Reddedilir()
    {
        var cok = Enumerable.Range(0, ParseWordsRequestDto.MaxWords + 1).Select(_ => W("x", 0, 0)).ToList();
        Assert.NotEmpty(Dogrula(new ParseWordsRequestDto { Words = cok }));
    }

    [Theory]
    [InlineData("", 1)]        // bos metin
    [InlineData("uzun", 0)]    // sayfa 1'den baslar
    [InlineData("uzun", 51)]   // ekstre en fazla 50 sayfa
    public void GecersizKelime_Reddedilir(string metin, int sayfa)
    {
        Assert.NotEmpty(Dogrula(W(metin, 0, 0, sayfa)));
    }

    [Fact]
    public void CokUzunKelime_Reddedilir()
    {
        Assert.NotEmpty(Dogrula(W(new string('a', 201), 0, 0)));
    }

    [Fact]
    public void GecerliKelime_Kabul()
    {
        Assert.Empty(Dogrula(W("01.09.2026", 0, 0)));
    }
}
