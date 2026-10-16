using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using SmartFinance.API.Controllers;
using SmartFinance.Application.Interfaces;
using SmartFinance.Domain.Entities;
using SmartFinance.Domain.Enums;
using SmartFinance.Infrastructure.Context;
using SmartFinance.Infrastructure.Repositories;
using SmartFinance.Infrastructure.Services;

namespace SmartFinance.Tests;

public class CategorySummaryTests
{
    private static (TransactionService service, SmartFinanceDbContext db) Kur()
    {
        var db = new SmartFinanceDbContext(new DbContextOptionsBuilder<SmartFinanceDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var current = new Mock<ICurrentUserService>();
        current.SetupGet(c => c.UserId).Returns(1);
        return (new TransactionService(new GenericRepository<Transaction>(db), db, current.Object,
            new Mock<INotificationService>().Object), db);
    }

    private static Transaction Islem(decimal amount = 10m, int user = 1, DateTime? date = null,
        int? category = null, TransactionType type = TransactionType.Expense, bool deleted = false) => new()
    {
        UserId = user, Amount = amount, Description = "Sentetik islem", Type = type,
        TransactionDate = date ?? new DateTime(2026, 10, 15), CategoryId = category, IsDeleted = deleted
    };

    // NEDEN: Mobil kirilim toplamiyla ustteki aylik toplam ayni olmali; silinenler sayilmamali.
    [Fact]
    public async Task KategoriOzeti_AylikToplamlaEsittir()
    {
        var (service, db) = Kur();
        using var context = db;
        db.Categories.AddRange(new Category { Id = 5, UserId = 1, Name = "Deneme Market", Type = TransactionType.Expense },
            new Category { Id = 6, UserId = 1, Name = "Deneme Yol", Type = TransactionType.Expense });
        db.Transactions.AddRange(Islem(12.25m, category: 5), Islem(20.50m, category: 5),
            Islem(50m, category: 6), Islem(2m), Islem(999m, deleted: true), Islem(1000m, type: TransactionType.Income));
        await db.SaveChangesAsync();
        var summary = await service.GetMonthlySummaryAsync(10, 2026);
        var rows = (await service.GetCategorySummaryAsync(10, 2026)).ToList();
        Assert.Equal(summary.TotalExpense, rows.Sum(r => r.Total));
        Assert.Equal(84.75m, rows.Sum(r => r.Total));
        Assert.Equal(new[] { 50m, 32.75m, 2m }, rows.Select(r => r.Total).ToArray());
        Assert.Equal("Deneme Market", rows.Single(r => r.CategoryId == 5).CategoryName);
        Assert.Equal(2, rows.Single(r => r.CategoryId == 5).Count);
    }

    // NEDEN: Kullanici filtresi tum toplama sorgularinda korunmali.
    [Fact]
    public async Task KategoriOzeti_BaskaKullanicininIslemleriniSaymaz()
    {
        var (service, db) = Kur();
        using var context = db;
        db.Transactions.AddRange(Islem(), Islem(900m, user: 2));
        await db.SaveChangesAsync();
        Assert.Equal(10m, Assert.Single(await service.GetCategorySummaryAsync(10, 2026)).Total);
    }

    // NEDEN: Ayin ilk ani dahil, onceki ve sonraki ayin islemleri haric olmali.
    [Fact]
    public async Task KategoriOzeti_AyDisindakiIslemleriSaymaz()
    {
        var (service, db) = Kur();
        using var context = db;
        var bas = new DateTime(2026, 10, 1);
        var son = bas.AddMonths(1);
        db.Transactions.AddRange(Islem(1m, date: bas.AddTicks(-1)), Islem(2m, date: bas),
            Islem(3m, date: son.AddTicks(-1)), Islem(4m, date: son));
        await db.SaveChangesAsync();
        Assert.Equal(5m, Assert.Single(await service.GetCategorySummaryAsync(10, 2026)).Total);
        Assert.Equal(5m, (await service.GetMonthlySummaryAsync(10, 2026)).TotalExpense);
    }

    // NEDEN: Null kategorili islemler ayri ayri satirlar olusturmamali.
    [Fact]
    public async Task KategoriOzeti_KategorisizlerTekSatir()
    {
        var (service, db) = Kur();
        using var context = db;
        db.Transactions.AddRange(Islem(3m), Islem(7m));
        await db.SaveChangesAsync();
        var row = Assert.Single(await service.GetCategorySummaryAsync(10, 2026));
        Assert.Null(row.CategoryId);
        Assert.Equal("Kategorisiz", row.CategoryName);
        Assert.Equal(10m, row.Total);
        Assert.Equal(2, row.Count);
    }

    // NEDEN: Eski mobil hesaplama ilk yuz kayitta kesiliyordu.
    [Fact]
    public async Task KategoriOzeti_YuzdenFazlaIslemiEksiksizToplar()
    {
        var (service, db) = Kur();
        using var context = db;
        db.Transactions.AddRange(Enumerable.Range(0, 150).Select(_ => Islem(1.25m)));
        await db.SaveChangesAsync();
        var row = Assert.Single(await service.GetCategorySummaryAsync(10, 2026));
        Assert.Equal(187.50m, row.Total);
        Assert.Equal(150, row.Count);
    }

    // NEDEN: Gecersiz ay sunucuda 500 yerine sozlesmedeki 400 cevabini vermeli.
    [Theory]
    [InlineData(0)]
    [InlineData(13)]
    public async Task KategoriOzeti_GecersizAy400(int month)
    {
        var service = new Mock<ITransactionService>(MockBehavior.Strict);
        var result = await new TransactionController(service.Object).GetCategorySummaryAsync(2026, month);
        Assert.Equal(400, Assert.IsType<BadRequestObjectResult>(result).StatusCode);
        service.VerifyNoOtherCalls();
    }

    // NEDEN: Sozlesme yalnizca 1 gelir ve 2 gider tiplerini kabul ediyor.
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task KategoriOzeti_GecersizTip400(int type)
    {
        var result = await new TransactionController(new Mock<ITransactionService>(MockBehavior.Strict).Object)
            .GetCategorySummaryAsync(2026, 10, type);
        Assert.Equal(400, Assert.IsType<BadRequestObjectResult>(result).StatusCode);
    }

    // NEDEN: Gelir secildiginde giderler kirilima karismamali.
    [Fact]
    public async Task KategoriOzeti_GelirTipiYalnizGelirleriToplar()
    {
        var (service, db) = Kur();
        using var context = db;
        db.Transactions.AddRange(Islem(9m), Islem(20m, type: TransactionType.Income));
        await db.SaveChangesAsync();
        Assert.Equal(20m, Assert.Single(await service.GetCategorySummaryAsync(10, 2026, 1)).Total);
    }
}
