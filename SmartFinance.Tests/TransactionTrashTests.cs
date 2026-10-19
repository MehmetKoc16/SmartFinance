using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using SmartFinance.API.Controllers;
using SmartFinance.Application.DTOs.Transaction;
using SmartFinance.Application.Interfaces;
using SmartFinance.Domain.Entities;
using SmartFinance.Domain.Enums;
using SmartFinance.Infrastructure.Context;
using SmartFinance.Infrastructure.Repositories;
using SmartFinance.Infrastructure.Services;

namespace SmartFinance.Tests;

public class TransactionTrashTests
{
    private static (TransactionService service, SmartFinanceDbContext db, Mock<INotificationService> notifications) Kur()
    {
        var db = new SmartFinanceDbContext(new DbContextOptionsBuilder<SmartFinanceDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var current = new Mock<ICurrentUserService>();
        current.SetupGet(c => c.UserId).Returns(1);
        var notifications = new Mock<INotificationService>(MockBehavior.Strict);
        return (new TransactionService(new GenericRepository<Transaction>(db), db, current.Object,
            notifications.Object), db, notifications);
    }

    private static Transaction Islem(int user = 1, bool deleted = true, DateTime? deletedAt = null) => new()
    {
        UserId = user, Amount = 12.25m, Description = "Sentetik işlem", MerchantName = "Deneme İşyeri",
        TransactionDate = new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc),
        CreatedDate = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
        Type = TransactionType.Expense, IsDeleted = deleted,
        UpdatedDate = deletedAt ?? DateTime.UtcNow.AddDays(-1)
    };

    // NEDEN: Silme zamanı işlem tarihinden farklıdır; mobil geri alma süresini buna göre gösterir.
    [Fact]
    public async Task Silinen_CopKutusundaGorunur_SilinmeZamaniyla()
    {
        var (service, db, _) = Kur();
        using var context = db;
        var transaction = Islem(deleted: false);
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync();
        var before = DateTime.UtcNow;

        await service.DeleteTransactionAsync(transaction.Id);

        var after = DateTime.UtcNow;
        db.ChangeTracker.Clear();
        var controller = new TransactionController(service);
        var response = Assert.IsType<OkObjectResult>(await controller.GetDeletedTransactionsAsync());
        var row = Assert.Single(Assert.IsAssignableFrom<IEnumerable<DeletedTransactionDto>>(response.Value));
        Assert.Equal(transaction.Id, row.Id);
        Assert.Equal(transaction.Amount, row.Amount);
        Assert.Equal(transaction.UserId, row.UserId);
        Assert.Equal(transaction.Description, row.Description);
        Assert.Equal(transaction.MerchantName, row.MerchantName);
        Assert.Equal(transaction.TransactionDate, row.TransactionDate);
        Assert.Equal(transaction.CreatedDate, row.CreatedDate);
        Assert.Equal(transaction.Type, row.Type);
        Assert.Null(row.CategoryId);
        Assert.InRange(row.DeletedAt, before, after);
        Assert.Equal(DateTimeKind.Utc, row.DeletedAt.Kind);
        Assert.Empty(await service.GetAllTransactionsAsync());
    }

    // NEDEN: IgnoreQueryFilters kullanıcı ayrımını kaldırmamalı.
    [Fact]
    public async Task BaskaKullanicininSilinenleri_Gorunmez()
    {
        var (service, db, _) = Kur();
        using var context = db;
        var own = Islem();
        db.Transactions.AddRange(own, Islem(user: 2), Islem(deleted: false));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.Equal(own.Id, Assert.Single(await service.GetDeletedTransactionsAsync()).Id);
    }

    // NEDEN: Liste ile geri alma aynı otuz günlük saklama penceresini kullanmalı.
    [Fact]
    public async Task OtuzGundenEski_Listelenmez_VeGeriAlinamaz()
    {
        var (service, db, _) = Kur();
        using var context = db;
        var old = Islem(deletedAt: DateTime.UtcNow.AddDays(-30).AddMinutes(-1));
        var recent = Islem(deletedAt: DateTime.UtcNow.AddDays(-30).AddMinutes(1));
        db.Transactions.AddRange(old, recent);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.Equal(recent.Id, Assert.Single(await service.GetDeletedTransactionsAsync()).Id);
        var controller = new TransactionController(service);
        Assert.IsType<NotFoundResult>(await controller.RestoreTransactionAsync(old.Id));
        Assert.True((await db.Transactions.IgnoreQueryFilters().SingleAsync(t => t.Id == old.Id)).IsDeleted);
        Assert.IsType<OkObjectResult>(await controller.RestoreTransactionAsync(recent.Id));
    }

    // NEDEN: Geri alınan gider tekrar görünmeli ve aylık toplama tam bir kez eklenmeli.
    [Fact]
    public async Task GeriAl_IslemiAktifYapar_VeAylikOzeteGeriEkler()
    {
        var (service, db, _) = Kur();
        using var context = db;
        var transaction = Islem();
        db.Categories.Add(new Category { Id = 5, UserId = 1, Name = "Deneme Kategori", Type = TransactionType.Expense });
        transaction.CategoryId = 5;
        var active = Islem(deleted: false);
        active.Amount = 20.50m;
        db.Transactions.AddRange(transaction, active);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.Equal(20.50m, (await service.GetMonthlySummaryAsync(10, 2026)).TotalExpense);

        var response = Assert.IsType<OkObjectResult>(await new TransactionController(service).RestoreTransactionAsync(transaction.Id));
        var restored = Assert.IsType<TransactionDto>(response.Value);
        Assert.Equal(transaction.Id, restored.Id);
        Assert.Equal(transaction.CreatedDate, restored.CreatedDate);
        Assert.Equal(transaction.MerchantName, restored.MerchantName);
        Assert.Equal(5, restored.CategoryId);
        db.ChangeTracker.Clear();
        Assert.False((await db.Transactions.SingleAsync(t => t.Id == transaction.Id)).IsDeleted);
        Assert.Equal(2, (await service.GetAllTransactionsAsync()).Count());
        Assert.Empty(await service.GetDeletedTransactionsAsync());
        var summary = await service.GetMonthlySummaryAsync(10, 2026);
        // 12,25 + 20,50 = 32,75; gelir yok, bakiye -32,75.
        Assert.Equal(32.75m, summary.TotalExpense);
        Assert.Equal(0m, summary.TotalIncome);
        Assert.Equal(-32.75m, summary.Balance);
        Assert.Equal(2, summary.TransactionCount);
        Assert.IsType<NotFoundResult>(await new TransactionController(service).RestoreTransactionAsync(transaction.Id));
    }

    // NEDEN: Başkasının işlemine 404 dönülmeli ve kaydın varlığı sızmamalı.
    [Fact]
    public async Task BaskasininIslemi_GeriAl404()
    {
        var (service, db, _) = Kur();
        using var context = db;
        var foreign = Islem(user: 2);
        db.Transactions.Add(foreign);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.IsType<NotFoundResult>(await new TransactionController(service).RestoreTransactionAsync(foreign.Id));
        Assert.True((await db.Transactions.IgnoreQueryFilters().SingleAsync(t => t.Id == foreign.Id)).IsDeleted);
    }

    // NEDEN: Aktif kayda geri alma uygulamak silinme penceresini veya veriyi değiştirmemeli.
    [Fact]
    public async Task SilinmemisIslem_GeriAl404()
    {
        var (service, db, _) = Kur();
        using var context = db;
        var active = Islem(deleted: false);
        db.Transactions.Add(active);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.IsType<NotFoundResult>(await new TransactionController(service).RestoreTransactionAsync(active.Id));
        Assert.Equal(active.UpdatedDate, (await db.Transactions.SingleAsync()).UpdatedDate);
    }

    // NEDEN: Silinmiş ya da fiziksel olarak kaldırılmış kategori yeniden etkinleştirilmemeli.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task KategorisiSilinmisIslem_KategorisizGeriGelir(bool softDeleted)
    {
        var (service, db, _) = Kur();
        using var context = db;
        if (softDeleted)
            db.Categories.Add(new Category { Id = 5, UserId = 1, Name = "Silinmiş Deneme", IsDeleted = true });
        var transaction = Islem();
        transaction.CategoryId = 5;
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.Equal(transaction.Id, Assert.Single(await service.GetDeletedTransactionsAsync()).Id);
        var response = Assert.IsType<OkObjectResult>(await new TransactionController(service).RestoreTransactionAsync(transaction.Id));
        Assert.Null(Assert.IsType<TransactionDto>(response.Value).CategoryId);
        db.ChangeTracker.Clear();
        Assert.Null((await db.Transactions.SingleAsync()).CategoryId);
        if (softDeleted)
            Assert.True((await db.Categories.IgnoreQueryFilters().SingleAsync()).IsDeleted);
    }

    // NEDEN: Geri alma yeni gider ekleme değildir; bütçe kontrolü ve bildirim çağrısı olmamalı.
    [Fact]
    public async Task GeriAlma_BildirimUretmez()
    {
        var (service, db, notifications) = Kur();
        using var context = db;
        var transaction = Islem();
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.NotNull(await service.RestoreTransactionAsync(transaction.Id));
        notifications.VerifyNoOtherCalls();
        Assert.Empty(await db.Notifications.ToListAsync());
    }

    // NEDEN: Mobil en yeni iki yüz silineni almalı; işlem tarihi sıralamayı etkilememeli.
    [Fact]
    public async Task CopKutusu_EnYeniIkiYuzKaydi_SilinmeZamaninaGoreSiralar()
    {
        var (service, db, _) = Kur();
        using var context = db;
        var now = DateTime.UtcNow;
        var transactions = Enumerable.Range(0, 205)
            .Select(i => Islem(deletedAt: now.AddMinutes(-i))).ToArray();
        for (var i = 0; i < transactions.Length; i++)
            transactions[i].TransactionDate = transactions[i].TransactionDate.AddDays(i);
        db.Transactions.AddRange(transactions.Reverse());
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var rows = (await service.GetDeletedTransactionsAsync()).ToArray();
        Assert.Equal(200, rows.Length);
        Assert.Equal(transactions.Take(200).Select(t => t.Id), rows.Select(t => t.Id));
    }

    // NEDEN: SQL datetime UTC Kind bilgisini saklamaz; deletedAt yine Z ile serileşmeli.
    [Fact]
    public async Task CopKutusu_JsonAlanlariMevcutDtoVeUtcDeletedAt()
    {
        var (service, db, _) = Kur();
        using var context = db;
        db.Transactions.Add(Islem(deletedAt: DateTime.SpecifyKind(DateTime.UtcNow.AddHours(-1), DateTimeKind.Unspecified)));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var row = Assert.Single(await service.GetDeletedTransactionsAsync());
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(row, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        // accountId 19.10'da çoklu hesap özelliğiyle eklendi.
        Assert.Equal(new[] { "accountId", "amount", "categoryId", "createdDate", "deletedAt", "description", "id", "merchantName", "transactionDate", "type", "userId" },
            json.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.EndsWith("Z", json.RootElement.GetProperty("deletedAt").GetString());
    }

    // NEDEN: Silinme zamanı bilinmeyen eski satırlar yanlışlıkla geri alınabilir sayılmamalı.
    [Fact]
    public async Task SilinmeZamaniYok_Listelenmez_VeGeriAlinamaz()
    {
        var (service, db, _) = Kur();
        using var context = db;
        var transaction = Islem();
        transaction.UpdatedDate = null;
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.Empty(await service.GetDeletedTransactionsAsync());
        Assert.IsType<NotFoundResult>(await new TransactionController(service).RestoreTransactionAsync(transaction.Id));
    }

    // NEDEN: Olmayan kimlik ile başka kullanıcıya ait kimlik aynı HTTP sonucunu vermeli.
    [Fact]
    public async Task OlmayanIslem_GeriAl404()
    {
        var (service, db, _) = Kur();
        using var context = db;
        Assert.IsType<NotFoundResult>(await new TransactionController(service).RestoreTransactionAsync(12345));
    }
}
