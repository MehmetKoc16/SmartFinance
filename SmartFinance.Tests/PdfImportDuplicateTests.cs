using Microsoft.Extensions.Configuration;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SmartFinance.Application.DTOs.PdfImport;
using SmartFinance.Domain.Entities;
using SmartFinance.Domain.Enums;
using SmartFinance.Infrastructure.Context;
using SmartFinance.Infrastructure.Services;
using ClosedXML.Excel;
using SmartFinance.Application.Common;
using SmartFinance.Application.Exceptions;
using SmartFinance.Infrastructure.BankParsers;

namespace SmartFinance.Tests;

/// Kullanici ayni ekstreyi ikinci kez yukledginde islemler cift kaydediliyor
/// ve giderler iki katina cikiyordu (27-28 Agustos 2026'da canlida yasandi).
public class PdfImportDuplicateTests
{
    [Theory]
    [InlineData(-12)]
    [InlineData(0)]
    public async Task Onay_EksiTutar_Reddedilir_HicbirSeyKaydedilmez(decimal amount)
    {
        // NEDEN: Son satır geçersizse önceki geçerli satır, öğrenme ve log bile izlenmemeli.
        var (service, context, userId) = CreateService();
        var category = new Category { UserId = userId, Name = "Uydurma gider", Type = TransactionType.Expense };
        context.Categories.Add(category); context.SaveChanges();
        var valid = Item("2026-09-02", 23, "Uydurma dükkan"); valid.CategoryId = category.Id;
        var ex = await Assert.ThrowsAsync<BadRequestException>(() => service.ConfirmImportAsync(
            Dto(valid, Item("2026-09-02", amount, "Geçersiz satır"))));
        Assert.Equal("İçe aktarılacak işlemlerde geçersiz tutar ya da tür var.", ex.Message);
        await context.SaveChangesAsync();
        Assert.Empty(context.Transactions); Assert.Empty(context.CategoryMappings); Assert.Empty(context.ImportLogs);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task Onay_GecersizTur_Reddedilir(int type)
    {
        // NEDEN: Bilinmeyen tür sessizce gider olarak saklanmamalı.
        var (service, context, _) = CreateService(); var item = Item("2026-09-02", 23, "Uydurma satır");
        item.Type = type;
        var ex = await Assert.ThrowsAsync<BadRequestException>(() => service.ConfirmImportAsync(Dto(item)));
        Assert.Equal("İçe aktarılacak işlemlerde geçersiz tutar ya da tür var.", ex.Message);
        Assert.Empty(context.Transactions); Assert.Empty(context.ImportLogs);
    }

    [Fact]
    public async Task Onay_KolonaSigmayanTutar_Reddedilir()
    {
        // NEDEN: InMemory kolon taşmasını kendiliğinden yakalamaz.
        var (service, context, _) = CreateService();
        await Assert.ThrowsAsync<BadRequestException>(() => service.ConfirmImportAsync(
            Dto(Item("2026-09-02", MoneyRules.Max + 0.01m, "Uydurma satır"))));
        Assert.Empty(context.Transactions); Assert.Empty(context.ImportLogs);
    }

    [Fact]
    public async Task Onay_UcHaneliTutar_IkiHaneyeYuvarlanir()
    {
        // NEDEN: Eski istemcilerin üç haneli tutarı kabul edilmeli; tekrarında aynı anahtar oluşmalı.
        var (service, context, _) = CreateService();
        var dto = Dto(Item("2026-09-02", 23.125m, "Uydurma satır"));
        Assert.Equal(1, (await service.ConfirmImportAsync(dto)).SavedCount);
        Assert.Equal(23.13m, (await context.Transactions.AsNoTracking().SingleAsync()).Amount);
        Assert.Equal(1, (await service.ConfirmImportAsync(dto)).SkippedCount);
    }

    [Fact]
    public async Task Onay_MaskelenenAciklama_IkinciYuklemedeAtlanir()
    {
        // NEDEN: Ham açıklamayla kaydedilmiş maskeli açıklama aynı anahtara dönüşmeli.
        var (service, context, _) = CreateService();
        var dto = Dto(Item("2026-09-02", 23, "Uydurma havale 9876543210123456"));
        await service.ConfirmImportAsync(dto);
        var saved = (await context.Transactions.AsNoTracking().SingleAsync()).Description;
        Assert.Equal("Uydurma havale ***", saved);
        Assert.Equal(saved, HassasVeriMaskeleyici.Maskele(saved));
        var second = await service.ConfirmImportAsync(dto);
        Assert.Equal(0, second.SavedCount); Assert.Equal(1, second.SkippedCount);
        Assert.Single(context.Transactions);
    }

    private static async Task<PdfParseResultDto> Onizle(PdfImportService service,
        params (decimal amount, string description)[] rows)
    {
        using var workbook = new XLWorkbook(); var sheet = workbook.AddWorksheet("Uydurma");
        for (var i = 0; i < rows.Length; i++)
        {
            sheet.Cell(i + 1, 1).Value = "02.09.2026";
            sheet.Cell(i + 1, 3).Value = rows[i].description;
            sheet.Cell(i + 1, 4).Value = rows[i].amount;
        }
        using var stream = new MemoryStream(); workbook.SaveAs(stream); stream.Position = 0;
        return await service.ParsePdfAsync(stream, "uydurma.xlsx");
    }

    [Fact]
    public async Task Onizleme_FarkliTur_MukerrerDegil()
    {
        // NEDEN: Aynı gün/tutar/açıklamadaki gelir ve gider ayrı hareketlerdir.
        var (service, _, _) = CreateService();
        await service.ConfirmImportAsync(Dto(Item("2026-09-02", 23, "Uydurma satır")));
        var result = await Onizle(service, (23m, "Uydurma satır"));
        Assert.Equal(1, Assert.Single(result.Transactions).Type);
        Assert.False(result.Transactions[0].IsDuplicate);
    }

    [Fact]
    public async Task Onizleme_BosIsyeriAdi_HerSeyleEslesmez()
    {
        // NEDEN: Boş işyeri adı Contains("") nedeniyle ilgisiz açıklamayı eşleştirmemeli.
        var (service, _, _) = CreateService();
        await service.ConfirmImportAsync(Dto(Item("2026-09-02", 23, "Başka uydurma satır")));
        var result = await Onizle(service, (-23m, "ISYERI: MUTABAKAT: Uydurma"));
        Assert.True(string.IsNullOrEmpty(Assert.Single(result.Transactions).MerchantName));
        Assert.False(result.Transactions[0].IsDuplicate);
    }

    [Fact]
    public async Task Onizleme_IkiOzdesSatirBirKayit_YalnizBiriMukerrer()
    {
        // NEDEN: Sayım, aynı tutarlı iki gerçek hareketten yalnız kayıtlı olanı düşürmeli.
        var (service, _, _) = CreateService();
        const string description = "Uydurma havale 9876543210123456";
        await service.ConfirmImportAsync(Dto(Item("2026-09-02", 23, description)));
        var result = await Onizle(service, (-23m, description), (-23m, description));
        Assert.Equal(new[] { true, false }, result.Transactions.Select(t => t.IsDuplicate));
        Assert.Equal(1, result.DuplicateCount);
    }

    [Fact]
    public async Task Onizleme_SifirAyiklanir_EksiTutarTurleMutlakDegereDonusur()
    {
        // NEDEN: Ayrıştırıcının sıfır satırı onayda tüm isteğin reddine yol açmamalı.
        var (service, _, _) = CreateService();
        var result = await Onizle(service, (0m, "Uydurma sıfır"), (-23m, "Uydurma gider"));
        var item = Assert.Single(result.Transactions);
        Assert.Equal(23m, item.Amount); Assert.Equal(2, item.Type);
    }

    [Fact]
    public async Task Onizleme_YuvarlamaVeMaskeleme_OnaylaAyniAnahtariKullanir()
    {
        // NEDEN: Önizleme kuruş yuvarlamasında da onayla aynı mükerreri göstermeli.
        var (service, _, _) = CreateService();
        const string description = "Uydurma havale 9876543210123456";
        await service.ConfirmImportAsync(Dto(Item("2026-09-02", 23.125m, description)));
        var result = await Onizle(service, (-23.125m, description));
        Assert.True(Assert.Single(result.Transactions).IsDuplicate);
    }

    private static (PdfImportService service, SmartFinanceDbContext context, int userId) CreateService()
    {
        var options = new DbContextOptionsBuilder<SmartFinanceDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString()).Options;
        var context = new SmartFinanceDbContext(options);

        var user = new User { FullName = "Test Kullanıcı", Email = $"{Guid.NewGuid()}@test.com", PasswordHash = "x" };
        context.Users.Add(user);
        context.SaveChanges();

        var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()) });
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };

        var currentUser = new CurrentUserService(accessor);
        var service = new PdfImportService(
            context, currentUser, NullLogger<PdfImportService>.Instance,
            new EntitlementService(context, currentUser, BosYapilandirma()));
        return (service, context, user.Id);
    }

    private static ConfirmTransactionItemDto Item(string date, decimal amount, string description) => new()
    {
        Amount = amount,
        Description = description,
        MerchantName = description,
        TransactionDate = DateTime.Parse(date),
        Type = 2,
    };

    private static ConfirmImportDto Dto(params ConfirmTransactionItemDto[] items)
        => new() { Transactions = items.ToList() };

    [Fact]
    public async Task AyniEkstreIkinciKez_HicbiriEklenmez()
    {
        var (service, context, _) = CreateService();
        var dosya = Dto(
            Item("2026-08-28", 182.50m, "POS ALIŞVERİŞ ORNEK KAFE SUBESI"),
            Item("2026-08-28", 20.00m, "POS ALIŞVERİŞ ORNEK GIDA MARKET"),
            Item("2026-08-27", 108.00m, "POS ALIŞVERİŞ ORNEK KAFE SUBESI"));

        var ilk = await service.ConfirmImportAsync(dosya);
        var ikinci = await service.ConfirmImportAsync(dosya);

        Assert.Equal(3, ilk.SavedCount);
        Assert.Equal(0, ilk.SkippedCount);
        Assert.Equal(0, ikinci.SavedCount);
        Assert.Equal(3, ikinci.SkippedCount);
        Assert.Equal(3, context.Transactions.Count());
    }

    /// Kullanici ayni gun ayni yerden ayni tutarda iki alisveris yapmis
    /// olabilir. Kume kullanilsaydi ikincisi mukerrer sanilip yutulurdu.
    [Fact]
    public async Task AyniGunAyniTutardaIkiGercekIslem_IkisiDeEklenir()
    {
        var (service, context, _) = CreateService();

        var sonuc = await service.ConfirmImportAsync(Dto(
            Item("2026-08-28", 20.00m, "POS ALIŞVERİŞ ORNEK GIDA MARKET"),
            Item("2026-08-28", 20.00m, "POS ALIŞVERİŞ ORNEK GIDA MARKET")));

        Assert.Equal(2, sonuc.SavedCount);
        Assert.Equal(0, sonuc.SkippedCount);
        Assert.Equal(2, context.Transactions.Count());
    }

    /// Kismi ortusme: onceki yuklemede olan atlanir, yeni olan eklenir.
    [Fact]
    public async Task KismiOrtusme_YalnizcaYeniIslemlerEklenir()
    {
        var (service, context, _) = CreateService();
        await service.ConfirmImportAsync(Dto(
            Item("2026-08-27", 108.00m, "POS ALIŞVERİŞ ORNEK KAFE SUBESI")));

        var sonuc = await service.ConfirmImportAsync(Dto(
            Item("2026-08-27", 108.00m, "POS ALIŞVERİŞ ORNEK KAFE SUBESI"),
            Item("2026-08-28", 182.50m, "POS ALIŞVERİŞ ORNEK KAFE SUBESI")));

        Assert.Equal(1, sonuc.SavedCount);
        Assert.Equal(1, sonuc.SkippedCount);
        Assert.Equal(2, context.Transactions.Count());
    }

    /// Veritabaninda 1, dosyada 2 varsa yalnizca FAZLASI eklenir.
    [Fact]
    public async Task VeritabanindaBirDosyadaIki_YalnizcaFazlasiEklenir()
    {
        var (service, context, _) = CreateService();
        await service.ConfirmImportAsync(Dto(
            Item("2026-08-28", 20.00m, "POS ALIŞVERİŞ ORNEK GIDA MARKET")));

        var sonuc = await service.ConfirmImportAsync(Dto(
            Item("2026-08-28", 20.00m, "POS ALIŞVERİŞ ORNEK GIDA MARKET"),
            Item("2026-08-28", 20.00m, "POS ALIŞVERİŞ ORNEK GIDA MARKET")));

        Assert.Equal(1, sonuc.SavedCount);
        Assert.Equal(1, sonuc.SkippedCount);
        Assert.Equal(2, context.Transactions.Count());
    }

    /// Tutar veya tarih farkliysa ayri islemdir, atlanmamali.
    [Fact]
    public async Task FarkliTutarVeyaTarih_MukerrerSayilmaz()
    {
        var (service, context, _) = CreateService();
        await service.ConfirmImportAsync(Dto(
            Item("2026-08-28", 20.00m, "POS ALIŞVERİŞ ORNEK GIDA MARKET")));

        var sonuc = await service.ConfirmImportAsync(Dto(
            Item("2026-08-28", 21.00m, "POS ALIŞVERİŞ ORNEK GIDA MARKET"),
            Item("2026-08-29", 20.00m, "POS ALIŞVERİŞ ORNEK GIDA MARKET")));

        Assert.Equal(2, sonuc.SavedCount);
        Assert.Equal(0, sonuc.SkippedCount);
        Assert.Equal(3, context.Transactions.Count());
    }

    /// Fazla bosluk ve harf buyuklugu farki ayni islemi farkli gostermemeli.
    /// Not: Turkce I/i cifti ordinal karsilastirmada esitlenmez (ALIŞVERİŞ vs
    /// alişveriş). Ayni dosyanin tekrar yuklenmesinde metin birebir ayni
    /// geldigi icin bu pratikte sorun degil; kulture duyarli donusum ise
    /// "i" harfini bozarak daha buyuk bir hataya yol aciyordu.
    [Fact]
    public async Task AciklamadakiBoslukVeHarfFarki_MukerreriKacirmaz()
    {
        var (service, context, _) = CreateService();
        await service.ConfirmImportAsync(Dto(
            Item("2026-08-28", 20.00m, "POS ALIŞVERİŞ  ORNEK GIDA MARKET")));

        var sonuc = await service.ConfirmImportAsync(Dto(
            Item("2026-08-28", 20.00m, "POS ALIŞVERİŞ ornek gida market")));

        Assert.Equal(0, sonuc.SavedCount);
        Assert.Equal(1, sonuc.SkippedCount);
        Assert.Equal(1, context.Transactions.Count());
    }

    /// Baska bir kullanicinin ayni islemi mukerrer sayilmamali.
    [Fact]
    public async Task BaskaKullanicininIslemi_MukerrerSayilmaz()
    {
        var (service, context, userId) = CreateService();
        var digerKullanici = new User { FullName = "Diğer", Email = $"{Guid.NewGuid()}@test.com", PasswordHash = "x" };
        context.Users.Add(digerKullanici);
        context.SaveChanges();

        context.Transactions.Add(new Transaction
        {
            UserId = digerKullanici.Id,
            Amount = 20.00m,
            Description = "POS ALIŞVERİŞ ORNEK GIDA MARKET",
            TransactionDate = new DateTime(2026, 8, 28),
            Type = TransactionType.Expense,
        });
        context.SaveChanges();

        var sonuc = await service.ConfirmImportAsync(Dto(
            Item("2026-08-28", 20.00m, "POS ALIŞVERİŞ ORNEK GIDA MARKET")));

        Assert.Equal(1, sonuc.SavedCount);
        Assert.Equal(0, sonuc.SkippedCount);
        Assert.Equal(1, context.Transactions.Count(t => t.UserId == userId));
    }

    /// EntitlementService yapilandirmadan "odeme olmadan premium" listesini
    /// okuyor. Testlerde bu liste BOS olmali: dolu olsaydi limit testleri
    /// sessizce premium yolundan gecip hicbir sey dogrulamaz hale gelirdi.
    private static IConfiguration BosYapilandirma() =>
        new ConfigurationBuilder().Build();
}
