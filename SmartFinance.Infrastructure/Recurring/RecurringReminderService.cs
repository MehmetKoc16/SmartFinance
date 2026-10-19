using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SmartFinance.Infrastructure.Recurring;

public class RecurringReminderService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RecurringReminderService> _logger;

    public RecurringReminderService(IServiceScopeFactory scopeFactory, ILogger<RecurringReminderService> logger)
    {
        _scopeFactory = scopeFactory; _logger = logger;
    }

    public static bool ShouldRunOnStartup(DateTime utcNow)
    {
        var tr = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), RecurrenceSchedule.TrTimeZone);
        return tr.TimeOfDay >= TimeSpan.FromHours(9) && tr.TimeOfDay < TimeSpan.FromHours(21);
    }

    public static DateTime NextRunUtc(DateTime utcNow)
    {
        var utc = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
        var today = RecurrenceSchedule.TodayTr(utc);
        for (var offset = 0; offset <= 1; offset++)
        {
            var candidateTr = DateTime.SpecifyKind(today.AddDays(offset).ToDateTime(new TimeOnly(9, 0)), DateTimeKind.Unspecified);
            var candidateUtc = TimeZoneInfo.ConvertTimeToUtc(candidateTr, RecurrenceSchedule.TrTimeZone);
            if (candidateUtc > utc) return candidateUtc;
        }
        throw new InvalidOperationException("Sonraki planlı ödeme çalışma saati hesaplanamadı.");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (ShouldRunOnStartup(DateTime.UtcNow)) await RunSafelyAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            try { await Task.Delay(NextRunUtc(now) - now, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            await RunSafelyAsync(stoppingToken);
        }
    }

    private async Task RunSafelyAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            await scope.ServiceProvider.GetRequiredService<RecurringReminderRunner>().RunOnceAsync(DateTime.UtcNow, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { _logger.LogError(ex, "Planlı ödeme hatırlatma işi başarısız oldu."); }
    }
}
