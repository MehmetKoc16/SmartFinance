using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SmartFinance.Application.Interfaces;
using SmartFinance.Domain.Entities;
using SmartFinance.Domain.Enums;
using SmartFinance.Infrastructure.Context;
using SmartFinance.Infrastructure.Services;

namespace SmartFinance.Infrastructure.Recurring;

public class RecurringReminderRunner
{
    private readonly SmartFinanceDbContext _context;
    private readonly IPushSender? _pushSender;
    private readonly ILogger<RecurringReminderRunner> _logger;
    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    public RecurringReminderRunner(SmartFinanceDbContext context, IPushSender? pushSender = null,
        ILogger<RecurringReminderRunner>? logger = null)
    {
        _context = context; _pushSender = pushSender;
        _logger = logger ?? NullLogger<RecurringReminderRunner>.Instance;
    }

    public async Task RunOnceAsync(DateTime utcNow, CancellationToken ct)
    {
        var today = RecurrenceSchedule.TodayTr(utcNow);
        var last = today.AddDays(7);
        var plans = await _context.RecurringPayments.AsNoTracking()
            .Where(x => x.IsActive && x.NextDueDate >= today && x.NextDueDate <= last
                && (!x.EndDate.HasValue || x.NextDueDate <= x.EndDate.Value)
                && _context.Users.Any(u => u.Id == x.UserId && !u.IsDeleted))
            .OrderBy(x => x.Id).ToListAsync(ct);
        foreach (var p in plans)
        {
            ct.ThrowIfCancellationRequested();
            // Her plan ayrı: bir planın hatası loglanır, sıradaki plana geçilir.
            try { await ProcessAsync(p, today, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Planlı ödeme hatırlatması işlenemedi (plan {PlanId}, kullanıcı {UserId})", p.Id, p.UserId);
                // Kaydedilemeyen kayıtlar izleyicide kalırsa sonraki planların SaveChanges'i de düşer.
                _context.ChangeTracker.Clear();
            }
        }
    }

    private async Task ProcessAsync(RecurringPayment p, DateOnly today, CancellationToken ct)
    {
        var days = p.NextDueDate.DayNumber - today.DayNumber;
        if (days != 0 && (p.RemindDaysBefore <= 0 || days != p.RemindDaysBefore)) return;
        var key = $"planli:{p.Id}:{p.NextDueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}:{(days == 0 ? "gun" : "once")}";
        if (await _context.Notifications.IgnoreQueryFilters().AnyAsync(x => x.UserId == p.UserId && x.DedupeKey == key, ct))
            return;
        var income = p.Type == TransactionType.Income;
        var amount = "₺" + p.Amount.ToString("N2", Tr);
        var when = days == 0 ? null : $"{(days == 1 ? "yarın" : $"{days} gün sonra")} ({p.NextDueDate.ToString("dd.MM", Tr)})";
        var n = new Notification
        {
            UserId = p.UserId, Type = NotificationType.RecurringDue, DedupeKey = key,
            Title = days == 0 ? (income ? "Bugün beklenen gelir" : "Bugün ödenecek")
                : (income ? "Yaklaşan gelir" : "Yaklaşan ödeme"),
            // Uygulama içi mesaj tutarı içerir; push gövdesi içermez (kilit ekranı / 'Tutarları gizle').
            Message = when == null ? $"{p.Name}: {amount}" : $"{p.Name}: {amount} — {when}"
        };
        var pushBody = $"{p.Name} — {when ?? "bugün"}";
        _context.Notifications.Add(n);
        try { await _context.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            // Birden fazla sunucu aynı anda çalışırsa mevcut UNIQUE(UserId,DedupeKey)
            // son savunmadır. Yalnız gerçekten aynı anahtar yazıldıysa yarış kabul edilir.
            _context.Entry(n).State = EntityState.Detached;
            if (await _context.Notifications.IgnoreQueryFilters().AnyAsync(x => x.UserId == p.UserId && x.DedupeKey == key, ct))
                return;
            throw;
        }
        await PushAsync(n, pushBody, ct);
    }

    private async Task PushAsync(Notification n, string body, CancellationToken ct)
    {
        if (_pushSender == null) return;
        try
        {
            var tokens = await _context.DeviceTokens.Where(x => x.UserId == n.UserId).Select(x => x.Token).ToListAsync(ct);
            var invalid = new List<string>();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            foreach (var batch in tokens.Chunk(NotificationService.PushBatchSize))
            {
                invalid.AddRange(await _pushSender.SendAsync(batch, new PushMessage(n.Title, body,
                    new Dictionary<string, string>
                    {
                        ["type"] = "notification",
                        ["notificationId"] = n.Id.ToString(CultureInfo.InvariantCulture)
                    }), timeout.Token));
            }
            if (invalid.Count > 0)
            {
                _context.DeviceTokens.RemoveRange(await _context.DeviceTokens
                    .Where(x => x.UserId == n.UserId && invalid.Contains(x.Token)).ToListAsync(ct));
                await _context.SaveChangesAsync(ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Planlı ödeme push bildirimi gönderilemedi (kullanıcı {UserId}, bildirim {Id})", n.UserId, n.Id);
            // Geçersiz token silme kaydı düştüyse izleyicide 'Deleted' kalır; sonraki planlar etkilenmesin.
            _context.ChangeTracker.Clear();
        }
    }
}
