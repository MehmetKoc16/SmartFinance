using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using SmartFinance.Application.DTOs.Transaction;
using SmartFinance.Application.Exceptions;
using SmartFinance.Domain.Entities;
using SmartFinance.Domain.Enums;
using SmartFinance.Infrastructure.Context;
using SmartFinance.Infrastructure.Repositories;
using SmartFinance.Infrastructure.Services;

namespace SmartFinance.Tests;

public class TransactionServiceTests
{
    private (TransactionService service,SmartFinanceDbContext context,int userId) CreateService()
    {
        var options=new DbContextOptionsBuilder<SmartFinanceDbContext>().UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString()).Options;
        var context = new SmartFinanceDbContext(options);

        var user = new User { FullName = "Test Kullanıcı", Email = $"{Guid.NewGuid()}@test.com", PasswordHash = "x" };
        context.Users.Add(user);
        context.SaveChanges();

        var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()) });
        var httpContextAccessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };

        var repository = new GenericRepository<Transaction>(context);
        var service = new TransactionService(repository, context, httpContextAccessor);
        return (service, context, user.Id);
    }

    private static Category NewCategory(int userId, TransactionType type) => new Category
    {
        Name = "Test Kategori",
        Type = type,
        UserId = userId
    };

    [Fact]
    public async Task CreateTransaction_GecerliKategoriIleBasarili_TransactionDoner()
    {
        var (service, context, userId) = CreateService();
        var kategori = NewCategory(userId, TransactionType.Expense);
        context.Categories.Add(kategori);
        await context.SaveChangesAsync();

        var result = await service.CreateTransactionAsync(new CreateTransactionDto
        {
            Amount = 150.50m,
            Description = "Market alışverişi",
            TransactionDate = DateTime.UtcNow,
            Type = TransactionType.Expense,
            CategoryId = kategori.Id
        });

        Assert.True(result.Id > 0);
        Assert.Equal(150.50m, result.Amount);
    }

    [Fact]
    public async Task CreateTransaction_BaskaKullaniciyaAitKategori_BadRequestFireder()
    {
        var (service, context, _) = CreateService();
        var baskaKullanici = new User { FullName = "Başka", Email = $"{Guid.NewGuid()}@test.com", PasswordHash = "x" };
        context.Users.Add(baskaKullanici);
        await context.SaveChangesAsync();
        var baskasininKategorisi = NewCategory(baskaKullanici.Id, TransactionType.Expense);
        context.Categories.Add(baskasininKategorisi);
        await context.SaveChangesAsync();

        await Assert.ThrowsAsync<BadRequestException>(() => service.CreateTransactionAsync(new CreateTransactionDto
        {
            Amount = 100,
            Description = "Test",
            TransactionDate = DateTime.UtcNow,
            Type = TransactionType.Expense,
            CategoryId = baskasininKategorisi.Id
        }));
    }

    [Fact]
    public async Task CreateTransaction_KategoriTipiIslemTipiyleUyusmuyor_BadRequestFireder()
    {
        // Gelir kategorisiyle gider islemi eklemeye calisma — bu oturumda eklenen dogrulama.
        var (service, context, userId) = CreateService();
        var gelirKategorisi = NewCategory(userId, TransactionType.Income);
        context.Categories.Add(gelirKategorisi);
        await context.SaveChangesAsync();

        await Assert.ThrowsAsync<BadRequestException>(() => service.CreateTransactionAsync(new CreateTransactionDto
        {
            Amount = 100,
            Description = "Test",
            TransactionDate = DateTime.UtcNow,
            Type = TransactionType.Expense,
            CategoryId = gelirKategorisi.Id
        }));
    }

    [Fact]
    public async Task CreateTransaction_KategorisizIslem_BasariliOlusturulur()
    {
        var (service, _, _) = CreateService();

        var result = await service.CreateTransactionAsync(new CreateTransactionDto
        {
            Amount = 50,
            Description = "Kategorisiz",
            TransactionDate = DateTime.UtcNow,
            Type = TransactionType.Expense,
            CategoryId = null
        });

        Assert.Null(result.CategoryId);
    }

    [Fact]
    public async Task UpdateTransaction_BaskaKullaniciyaAitIslem_NotFoundFireder()
    {
        var (service, context, _) = CreateService();
        var baskaKullanici = new User { FullName = "Başka", Email = $"{Guid.NewGuid()}@test.com", PasswordHash = "x" };
        context.Users.Add(baskaKullanici);
        await context.SaveChangesAsync();
        var baskasininIslemi = new Transaction
        {
            Amount = 10,
            Description = "Başkasının işlemi",
            TransactionDate = DateTime.UtcNow,
            Type = TransactionType.Expense,
            UserId = baskaKullanici.Id
        };
        context.Transactions.Add(baskasininIslemi);
        await context.SaveChangesAsync();

        await Assert.ThrowsAsync<NotFoundException>(() => service.UpdateTransactionAsync(baskasininIslemi.Id, new CreateTransactionDto
        {
            Amount = 20,
            Description = "Değişiklik",
            TransactionDate = DateTime.UtcNow,
            Type = TransactionType.Expense,
            CategoryId = null
        }));
    }

    [Fact]
    public async Task DeleteTransaction_GecerliIslem_ArtikListelenmez()
    {
        var (service, context, userId) = CreateService();
        var islem = new Transaction
        {
            Amount = 30,
            Description = "Silinecek",
            TransactionDate = DateTime.UtcNow,
            Type = TransactionType.Expense,
            UserId = userId
        };
        context.Transactions.Add(islem);
        await context.SaveChangesAsync();

        await service.DeleteTransactionAsync(islem.Id);

        var tumIslemler = await service.GetAllTransactionsAsync();
        Assert.Empty(tumIslemler);
    }

    [Fact]
    public async Task GetMonthlySummary_DogruToplamlariHesaplar()
    {
        var (service, context, userId) = CreateService();
        var buAy = new DateTime(2026, 7, 15);
        context.Transactions.AddRange(
            new Transaction { Amount = 1000, Description = "Maaş", TransactionDate = buAy, Type = TransactionType.Income, UserId = userId },
            new Transaction { Amount = 200, Description = "Market", TransactionDate = buAy, Type = TransactionType.Expense, UserId = userId },
            new Transaction { Amount = 9999, Description = "Başka ay", TransactionDate = new DateTime(2026, 6, 1), Type = TransactionType.Expense, UserId = userId }
        );
        await context.SaveChangesAsync();

        var result = await service.GetMonthlySummaryAsync(7, 2026);

        Assert.Equal(1000, result.TotalIncome);
        Assert.Equal(200, result.TotalExpense);
        Assert.Equal(800, result.Balance);
        Assert.Equal(2, result.TransactionCount);
    }
}
