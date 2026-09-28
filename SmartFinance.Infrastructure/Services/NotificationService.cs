using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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

    private readonly IPushSender? _pushSender;
    private readonly ILogger<NotificationService> _logger;

    public NotificationService(SmartFinanceDbContext context, ICurrentUserService currentUserService,
        IPushSender? pushSender = null, ILogger<NotificationService>? logger = null)
    {
        _pushSender = pushSender;
        _logger = logger ?? NullLogger<NotificationService>.Instance;
        _context = context;
        _currentUserService = currentUserService;
    }

    private int GetUserId() => _currentUserService.UserId;

    // FCM tek istekte en fazla 500 cihaz kabul ediyor.
    public const int PushBatchSize = 500;

    public async Task<BroadcastResult> BroadcastAsync(string key, string title, string message,
        string? onlyEmail = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(message))
            throw new ArgumentException("Anahtar, başlık ve metin boş olamaz.");
        title = title.Trim();
        message = message.Trim();
        // Sinirlar NotificationConfiguration ile ayni (Title 200, Message 1000, DedupeKey 100).
        if (key.Trim().Length > 50 || title.Length > 200 || message.Length > 1000)
            throw new ArgumentException("Anahtar en fazla 50, başlık 200, metin 1000 karakter olabilir.");
        var dedupeKey = $"duyuru:{key.Trim()}";

        var kullanicilar = _context.Users.AsQueryable();
        if (onlyEmail != null)
        {
            var eposta = onlyEmail.Trim().ToLower();
            kullanicilar = kullanicilar.Where(u => u.Email.ToLower() == eposta);
        }
        var hedef = await kullanicilar.Select(u => u.Id).ToListAsync(ct);
        if (onlyEmail != null && hedef.Count == 0)
            throw new ArgumentException($"{onlyEmail} ile kayıtlı kullanıcı yok.");

        // Ayni anahtarla daha once bildirim almis olanlar atlanir (tekrar calistirma
        // guvenli). Silinmis isaretliler de sayilir: (UserId, DedupeKey) tekil.
        var zaten = (await _context.Notifications.IgnoreQueryFilters()
            .Where(n => n.DedupeKey == dedupeKey && hedef.Contains(n.UserId))
            .Select(n => n.UserId)
            .ToListAsync(ct)).ToHashSet();
        var yeni = hedef.Where(id => !zaten.Contains(id)).ToList();

        foreach (var userId in yeni)
        {
            _context.Notifications.Add(new Notification
            {
                UserId = userId,
                Type = NotificationType.Info,
                Title = title,
                Message = message,
                DedupeKey = dedupeKey,
            });
        }
        await _context.SaveChangesAsync(ct);

        // Push yalnizca bu calistirmada bildirim alanlarin cihazlarina. Surum
        // 9 oncesi uygulamalar cihaz kaydi yapmiyor; onlar zilde gorur.
        var tokenlar = await _context.DeviceTokens
            .Where(t => yeni.Contains(t.UserId))
            .Select(t => t.Token)
            .ToListAsync(ct);

        var silinen = 0;
        if (_pushSender != null && tokenlar.Count > 0)
        {
            var push = new PushMessage(title, message, new Dictionary<string, string> { ["type"] = "notification" });
            var gecersiz = new List<string>();
            foreach (var grup in tokenlar.Chunk(PushBatchSize))
            {
                try
                {
                    gecersiz.AddRange(await _pushSender.SendAsync(grup, push, ct));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Duyuru push grubu gonderilemedi ({Count} cihaz)", grup.Length);
                }
            }
            if (gecersiz.Count > 0)
            {
                var kayitlar = await _context.DeviceTokens.Where(t => gecersiz.Contains(t.Token)).ToListAsync(ct);
                _context.DeviceTokens.RemoveRange(kayitlar);
                await _context.SaveChangesAsync(ct);
                silinen = kayitlar.Count;
            }
        }

        _logger.LogInformation("Duyuru {Key}: {New} kullaniciya bildirim ({Already} zaten almisti), {Push} cihaza push, {Invalid} gecersiz cihaz silindi",
            dedupeKey, yeni.Count, zaten.Count, tokenlar.Count, silinen);
        return new BroadcastResult(yeni.Count, zaten.Count, tokenlar.Count, silinen);
    }

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
                // UTC saklaniyor ama SQL Server turu (Kind) tutmuyor; isaretlenmezse
                // JSON'a "Z" olmadan gider ve telefon yerel saat sanir (3 saat kayar).
                CreatedDate = DateTime.SpecifyKind(n.CreatedDate, DateTimeKind.Utc),
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

        var bildirim = new Notification
        {
            UserId = userId,
            Type = NotificationType.BudgetExceeded,
            Title = "Bütçe limiti aşıldı",
            Message = $"{budget.Category.Name} kategorisinde bu ayki {budget.MonthlyLimit:N0}₺ bütçe limitinizi aştınız.",
            DedupeKey = dedupeKey,
        };
        _context.Notifications.Add(bildirim);
        await _context.SaveChangesAsync();

        await PushGonderAsync(userId, bildirim);
    }

    /// Uygulama ici bildirimi kullanicinin telefonlarina da gonderir. Push ek
    /// bir kanal: hata olursa loglanir, islem kaydini veya bildirimi bozmaz.
    private async Task PushGonderAsync(int userId, Notification bildirim)
    {
        if (_pushSender == null) return;

        var tokenlar = await _context.DeviceTokens
            .Where(t => t.UserId == userId)
            .Select(t => t.Token)
            .ToListAsync();
        if (tokenlar.Count == 0) return;

        try
        {
            // Kullanici islem kaydederken bekliyor; FCM yavaslarsa kaydi bekletme.
            using var zamanAsimi = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var gecersiz = await _pushSender.SendAsync(tokenlar, new PushMessage(
                bildirim.Title,
                bildirim.Message,
                new Dictionary<string, string>
                {
                    ["type"] = "notification",
                    ["notificationId"] = bildirim.Id.ToString(),
                }), zamanAsimi.Token);

            if (gecersiz.Count > 0)
            {
                _context.DeviceTokens.RemoveRange(
                    await _context.DeviceTokens.Where(t => gecersiz.Contains(t.Token)).ToListAsync());
                await _context.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Push bildirimi gonderilemedi (kullanici {UserId}, {Count} cihaz)", userId, tokenlar.Count);
        }
    }
}
