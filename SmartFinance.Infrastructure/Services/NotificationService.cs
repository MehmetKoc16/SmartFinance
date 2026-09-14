using Microsoft.EntityFrameworkCore;
using SmartFinance.Application.DTOs.Notification;
using SmartFinance.Application.Exceptions;
using SmartFinance.Application.Interfaces;
using SmartFinance.Domain.Entities;
using SmartFinance.Domain.Enums;
using SmartFinance.Infrastructure.Context;

namespace SmartFinance.Infrastructure.Services;

public class NotificationService : INotificationService
{
    private readonly SmartFinanceDbContext _context;
    private readonly ICurrentUserService _currentUserService;

    public NotificationService(SmartFinanceDbContext context, ICurrentUserService currentUserService)
    {
        _context = context;
        _currentUserService = currentUserService;
    }

    private int GetUserId() => _currentUserService.UserId;

    public async Task<IEnumerable<NotificationDto>> GetAllAsync()
    {
        var userId = GetUserId();
        return await _context.Notifications
            .Where(n => n.UserId == userId)
            .OrderByDescending(n => n.CreatedDate)
            .Select(n => new NotificationDto
            {
                Id = n.Id,
                Type = n.Type,
                Title = n.Title,
                Message = n.Message,
                IsRead = n.IsRead,
                CreatedDate = n.CreatedDate,
            })
            .ToListAsync();
    }

    public async Task<int> GetUnreadCountAsync()
    {
        var userId = GetUserId();
        return await _context.Notifications.CountAsync(n => n.UserId == userId && !n.IsRead);
    }

    public async Task MarkAsReadAsync(int id)
    {
        var userId = GetUserId();
        var notification = await _context.Notifications.FirstOrDefaultAsync(n => n.Id == id && n.UserId == userId);
        if (notification == null)
            throw new NotFoundException("Bildirim bulunamadı!");

        if (notification.IsRead) return;
        notification.IsRead = true;
        notification.UpdatedDate = DateTime.UtcNow;
        await _context.SaveChangesAsync();
    }

    public async Task MarkAllAsReadAsync()
    {
        var userId = GetUserId();
        var unread = await _context.Notifications.Where(n => n.UserId == userId && !n.IsRead).ToListAsync();
        if (unread.Count == 0) return;

        foreach (var n in unread)
        {
            n.IsRead = true;
            n.UpdatedDate = DateTime.UtcNow;
        }
        await _context.SaveChangesAsync();
    }

    public async Task EvaluateBudgetForTransactionAsync(int userId, int? categoryId, DateTime transactionDate)
    {
        if (categoryId == null) return;

        var budget = await _context.Budgets
            .Include(b => b.Category)
            .FirstOrDefaultAsync(b => b.UserId == userId && b.CategoryId == categoryId.Value);
        if (budget == null) return;

        var spent = await _context.Transactions
            .Where(t => t.UserId == userId
                && t.CategoryId == categoryId.Value
                && t.Type == Domain.Enums.TransactionType.Expense
                && t.TransactionDate.Year == transactionDate.Year
                && t.TransactionDate.Month == transactionDate.Month)
            .SumAsync(t => t.Amount);

        if (spent <= budget.MonthlyLimit) return;

        var dedupeKey = $"budget:{categoryId.Value}:{transactionDate:yyyy-MM}";
        var alreadyNotified = await _context.Notifications
            .AnyAsync(n => n.UserId == userId && n.DedupeKey == dedupeKey);
        if (alreadyNotified) return;

        _context.Notifications.Add(new Notification
        {
            UserId = userId,
            Type = NotificationType.BudgetExceeded,
            Title = "Bütçe limiti aşıldı",
            Message = $"{budget.Category.Name} kategorisinde bu ayki {budget.MonthlyLimit:N0}₺ bütçe limitinizi aştınız.",
            DedupeKey = dedupeKey,
        });
        await _context.SaveChangesAsync();
    }
}
