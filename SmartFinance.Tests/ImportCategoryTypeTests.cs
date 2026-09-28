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

namespace SmartFinance.Tests;

/// Regresyon (28.09.2026, gelistiricinin hesabi): iki "MESAJ UCRETI" gideri
/// gelir kategorisi Maas'ta duruyordu. Elle ekleme/duzenleme turu kontrol
/// ediyordu, ice aktarma etmiyordu; ustelik yanlis eslesmeyi ogrenip sonraki
/// ice aktarmalarda tekrarlayabiliyordu.
public class ImportCategoryTypeTests
{
    private static (PdfImportService servis, SmartFinanceDbContext db, int maas, int fatura) Kur()
    {
        var db = new SmartFinanceDbContext(new DbContextOptionsBuilder<SmartFinanceDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var u = new User { FullName = "Test", Email = $"{Guid.NewGuid()}@test.com", PasswordHash = "x" };
        db.Users.Add(u);
        db.SaveChanges();
        var maas = new Category { Name = "Maaş", Type = TransactionType.Income, UserId = u.Id };
        var fatura = new Category { Name = "Fatura", Type = TransactionType.Expense, UserId = u.Id };
        db.Categories.AddRange(maas, fatura);
        db.SaveChanges();

        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, u.Id.ToString()) })),
            },
        };
        var currentUser = new CurrentUserService(accessor);
        var servis = new PdfImportService(db, currentUser, NullLogger<PdfImportService>.Instance,
            new EntitlementService(db, currentUser, new ConfigurationBuilder().Build()));
        return (servis, db, maas.Id, fatura.Id);
    }

    private static ConfirmTransactionItemDto Gider(string aciklama, int? kategori) => new()
    {
        Amount = 0.37m, Description = aciklama, MerchantName = aciklama,
        TransactionDate = new DateTime(2026, 9, 18), Type = 2, CategoryId = kategori,
    };

    [Fact]
    public async Task GiderGelirKategorisiyleGelirse_KategorisizKaydedilir()
    {
        var (servis, db, maas, _) = Kur();

        await servis.ConfirmImportAsync(new ConfirmImportDto { Transactions = [Gider("MESAJ ÜCRETİ TUTARI", maas)] });

        var t = Assert.Single(db.Transactions);
        Assert.Null(t.CategoryId);
    }

    [Fact]
    public async Task TuruUymayanEslesmeOgrenilmez()
    {
        var (servis, db, maas, _) = Kur();

        await servis.ConfirmImportAsync(new ConfirmImportDto { Transactions = [Gider("MESAJ ÜCRETİ TUTARI", maas)] });

        Assert.Empty(db.CategoryMappings);
    }

    [Fact]
    public async Task TuruUyanKategori_KorunurVeOgrenilir()
    {
        var (servis, db, _, fatura) = Kur();

        await servis.ConfirmImportAsync(new ConfirmImportDto { Transactions = [Gider("MESAJ ÜCRETİ TUTARI", fatura)] });

        Assert.Equal(fatura, Assert.Single(db.Transactions).CategoryId);
        Assert.Equal(fatura, Assert.Single(db.CategoryMappings).CategoryId);
    }
}
