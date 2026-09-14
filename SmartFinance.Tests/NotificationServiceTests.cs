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

public class NotificationServiceTests
{
    private static (TransactionService txService, NotificationService notificationService,
        SmartFinanceDbContext context, int userId) CreateServices()
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
        var currentUserService = new CurrentUserService(accessor);
        var notificationService = new NotificationService(context, currentUserService);
        var txService = new TransactionService(
            new GenericRepository<Transaction>(context), context, currentUserService, notificationService);

        return (txService, notificationService, context, user.Id);
    }

    private static Category NewExpenseCategory(int userId) => new()
    {
        Name = "Market",
        Type = TransactionType.Expense,
        UserId = userId,
    };

    [Fact]
    public async Task ButceAsilinca_BirBildirimOlusur()
    {
        var (txService, notificationService, context, userId) = CreateServices();
        var kategori = NewExpenseCategory(userId);
        context.Categories.Add(kategori);
        await context.SaveChangesAsync();
        context.Budgets.Add(new Budget { UserId = userId, CategoryId = kategori.Id, MonthlyLimit = 1000m });
        await context.SaveChangesAsync();

        await txService.CreateTransactionAsync(new CreateTransactionDto
        {
            Amount = 1200m,
            Description = "Market alışverişi",
            TransactionDate = DateTime.UtcNow,
            Type = TransactionType.Expense,
            CategoryId = kategori.Id,
        });

        var notifications = await notificationService.GetAllAsync();
        var notification = Assert.Single(notifications);
        Assert.Equal(NotificationType.BudgetExceeded, notification.Type);
        Assert.False(notification.IsRead);
    }

    [Fact]
    public async Task ButceAsilmadiginda_BildirimOlusmaz()
    {
        var (txService, notificationService, context, userId) = CreateServices();
        var kategori = NewExpenseCategory(userId);
        context.Categories.Add(kategori);
        await context.SaveChangesAsync();
        context.Budgets.Add(new Budget { UserId = userId, CategoryId = kategori.Id, MonthlyLimit = 1000m });
        await context.SaveChangesAsync();

        await txService.CreateTransactionAsync(new CreateTransactionDto
        {
            Amount = 500m,
            Description = "Market alışverişi",
            TransactionDate = DateTime.UtcNow,
            Type = TransactionType.Expense,
            CategoryId = kategori.Id,
        });

        Assert.Empty(await notificationService.GetAllAsync());
    }

    [Fact]
    public async Task AyniAyIcindeTekrarAsilinca_IkinciBildirimUretilmez()
    {
        var (txService, notificationService, context, userId) = CreateServices();
        var kategori = NewExpenseCategory(userId);
        context.Categories.Add(kategori);
        await context.SaveChangesAsync();
        context.Budgets.Add(new Budget { UserId = userId, CategoryId = kategori.Id, MonthlyLimit = 1000m });
        await context.SaveChangesAsync();

        var tarih = DateTime.UtcNow;
        await txService.CreateTransactionAsync(new CreateTransactionDto
        {
            Amount = 1200m, Description = "İlk aşım", TransactionDate = tarih,
            Type = TransactionType.Expense, CategoryId = kategori.Id,
        });
        // Limit zaten asilmisken eklenen ikinci gider — dedupe olmasaydi
        // her islemde yeni bir bildirim spam'ine donerdi.
        await txService.CreateTransactionAsync(new CreateTransactionDto
        {
            Amount = 100m, Description = "İkinci harcama", TransactionDate = tarih,
            Type = TransactionType.Expense, CategoryId = kategori.Id,
        });

        Assert.Single(await notificationService.GetAllAsync());
    }

    [Fact]
    public async Task FarkliAydaTekrarAsilinca_YeniBildirimOlusur()
    {
        var (txService, notificationService, context, userId) = CreateServices();
        var kategori = NewExpenseCategory(userId);
        context.Categories.Add(kategori);
        await context.SaveChangesAsync();
        context.Budgets.Add(new Budget { UserId = userId, CategoryId = kategori.Id, MonthlyLimit = 1000m });
        await context.SaveChangesAsync();

        var buAy = new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc);
        var gecenAy = new DateTime(2026, 2, 10, 0, 0, 0, DateTimeKind.Utc);
        await txService.CreateTransactionAsync(new CreateTransactionDto
        {
            Amount = 1200m, Description = "Şubat aşımı", TransactionDate = gecenAy,
            Type = TransactionType.Expense, CategoryId = kategori.Id,
        });
        await txService.CreateTransactionAsync(new CreateTransactionDto
        {
            Amount = 1200m, Description = "Mart aşımı", TransactionDate = buAy,
            Type = TransactionType.Expense, CategoryId = kategori.Id,
        });

        Assert.Equal(2, (await notificationService.GetAllAsync()).Count());
    }

    [Fact]
    public async Task ButceTanimliDegilse_BildirimOlusmaz()
    {
        var (txService, notificationService, context, userId) = CreateServices();
        var kategori = NewExpenseCategory(userId);
        context.Categories.Add(kategori);
        await context.SaveChangesAsync();
        // Kasten butce eklenmedi.

        await txService.CreateTransactionAsync(new CreateTransactionDto
        {
            Amount = 5000m, Description = "Büyük harcama", TransactionDate = DateTime.UtcNow,
            Type = TransactionType.Expense, CategoryId = kategori.Id,
        });

        Assert.Empty(await notificationService.GetAllAsync());
    }

    [Fact]
    public async Task MarkAsRead_OkunmamisSayisiniAzaltir()
    {
        var (txService, notificationService, context, userId) = CreateServices();
        var kategori = NewExpenseCategory(userId);
        context.Categories.Add(kategori);
        await context.SaveChangesAsync();
        context.Budgets.Add(new Budget { UserId = userId, CategoryId = kategori.Id, MonthlyLimit = 100m });
        await context.SaveChangesAsync();
        await txService.CreateTransactionAsync(new CreateTransactionDto
        {
            Amount = 200m, Description = "Aşım", TransactionDate = DateTime.UtcNow,
            Type = TransactionType.Expense, CategoryId = kategori.Id,
        });

        Assert.Equal(1, await notificationService.GetUnreadCountAsync());

        var notification = Assert.Single(await notificationService.GetAllAsync());
        await notificationService.MarkAsReadAsync(notification.Id);

        Assert.Equal(0, await notificationService.GetUnreadCountAsync());
    }

    [Fact]
    public async Task MarkAsRead_BaskaKullaniciyaAitBildirim_NotFoundFirlatir()
    {
        var (_, notificationService, context, _) = CreateServices();
        var baskaKullanici = new User { FullName = "Başka", Email = $"{Guid.NewGuid()}@test.com", PasswordHash = "x" };
        context.Users.Add(baskaKullanici);
        await context.SaveChangesAsync();
        context.Notifications.Add(new Notification
        {
            UserId = baskaKullanici.Id, Type = NotificationType.Info, Title = "x", Message = "x",
        });
        await context.SaveChangesAsync();
        var baskasininBildirimi = context.Notifications.First();

        await Assert.ThrowsAsync<NotFoundException>(() => notificationService.MarkAsReadAsync(baskasininBildirimi.Id));
    }
}
