using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SmartFinance.Application.DTOs.PdfImport;
using SmartFinance.Domain.Entities;
using SmartFinance.Domain.Enums;
using SmartFinance.Infrastructure.Context;
using SmartFinance.Infrastructure.Services;

namespace SmartFinance.Tests;

/// Kullanici ayni ekstreyi ikinci kez yukledginde islemler cift kaydediliyor
/// ve giderler iki katina cikiyordu (27-28 Agustos 2026'da canlida yasandi).
public class PdfImportDuplicateTests
{
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

        var service = new PdfImportService(
            context, new CurrentUserService(accessor), NullLogger<PdfImportService>.Instance);
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
            Item("2026-08-28", 182.50m, "POS ALIŞVERİŞ BALIKKESIR KARESI OG"),
            Item("2026-08-28", 20.00m, "POS ALIŞVERİŞ ISPARTALIOGLU GIDA"),
            Item("2026-08-27", 108.00m, "POS ALIŞVERİŞ BALIKKESIR KARESI OG"));

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
            Item("2026-08-28", 20.00m, "POS ALIŞVERİŞ ISPARTALIOGLU GIDA"),
            Item("2026-08-28", 20.00m, "POS ALIŞVERİŞ ISPARTALIOGLU GIDA")));

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
            Item("2026-08-27", 108.00m, "POS ALIŞVERİŞ BALIKKESIR KARESI OG")));

        var sonuc = await service.ConfirmImportAsync(Dto(
            Item("2026-08-27", 108.00m, "POS ALIŞVERİŞ BALIKKESIR KARESI OG"),
            Item("2026-08-28", 182.50m, "POS ALIŞVERİŞ BALIKKESIR KARESI OG")));

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
            Item("2026-08-28", 20.00m, "POS ALIŞVERİŞ ISPARTALIOGLU GIDA")));

        var sonuc = await service.ConfirmImportAsync(Dto(
            Item("2026-08-28", 20.00m, "POS ALIŞVERİŞ ISPARTALIOGLU GIDA"),
            Item("2026-08-28", 20.00m, "POS ALIŞVERİŞ ISPARTALIOGLU GIDA")));

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
            Item("2026-08-28", 20.00m, "POS ALIŞVERİŞ ISPARTALIOGLU GIDA")));

        var sonuc = await service.ConfirmImportAsync(Dto(
            Item("2026-08-28", 21.00m, "POS ALIŞVERİŞ ISPARTALIOGLU GIDA"),
            Item("2026-08-29", 20.00m, "POS ALIŞVERİŞ ISPARTALIOGLU GIDA")));

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
            Item("2026-08-28", 20.00m, "POS ALIŞVERİŞ  ISPARTALIOGLU GIDA")));

        var sonuc = await service.ConfirmImportAsync(Dto(
            Item("2026-08-28", 20.00m, "POS ALIŞVERİŞ ispartalioglu gida")));

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
            Description = "POS ALIŞVERİŞ ISPARTALIOGLU GIDA",
            TransactionDate = new DateTime(2026, 8, 28),
            Type = TransactionType.Expense,
        });
        context.SaveChanges();

        var sonuc = await service.ConfirmImportAsync(Dto(
            Item("2026-08-28", 20.00m, "POS ALIŞVERİŞ ISPARTALIOGLU GIDA")));

        Assert.Equal(1, sonuc.SavedCount);
        Assert.Equal(0, sonuc.SkippedCount);
        Assert.Equal(1, context.Transactions.Count(t => t.UserId == userId));
    }
}
