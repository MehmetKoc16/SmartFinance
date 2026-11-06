using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using SmartFinance.Domain.Entities;
using SmartFinance.Infrastructure.Context;
using SmartFinance.Infrastructure.Recurring;

namespace SmartFinance.Infrastructure.Retention;

/// <summary>
/// Gecelik kalıcı silme işi (KVKK md. 7). Her gün 03:30 TSİ'de bir kez DataRetentionRunner'ı çalıştırır.
/// Saat, diğer zamanlanmış işlerle çakışmasın diye seçildi: KAP senkronu 05:00 ve 13:00 TSİ,
/// planlı ödeme hatırlatması 09:00 TSİ. Fiyat geçmişi (24 saatte bir, açılıştan 1 dk sonra),
/// fiyat yenileme (5 dk) ve abonelik yenileme (15 dk) sabit saate bağlı değil.
/// Açılışta çalışmaz: kaçan bir gece zararsızdır, sonraki gece aynı kayıtlar silinir.
/// </summary>
public class DataRetentionService : BackgroundService
{
    public static readonly TimeOnly RunTimeTr = new(3, 30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DataRetentionService> _logger;

    public DataRetentionService(IServiceScopeFactory scopeFactory, ILogger<DataRetentionService> logger)
    {
        _scopeFactory = scopeFactory; _logger = logger;
    }

    public static DateTime NextRunUtc(DateTime utcNow)
    {
        var utc = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
        var today = RecurrenceSchedule.TodayTr(utc);
        for (var offset = 0; offset <= 1; offset++)
        {
            var candidateTr = DateTime.SpecifyKind(today.AddDays(offset).ToDateTime(RunTimeTr), DateTimeKind.Unspecified);
            var candidateUtc = TimeZoneInfo.ConvertTimeToUtc(candidateTr, RecurrenceSchedule.TrTimeZone);
            if (candidateUtc > utc) return candidateUtc;
        }
        throw new InvalidOperationException("Sonraki veri saklama temizliği saati hesaplanamadı.");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
            // Program.cs'e ayrı AddScoped satırı gerekmesin diye kapsamdan oluşturulur
            // (DbContext ve ILogger kapsamdan çözülür).
            var runner = ActivatorUtilities.CreateInstance<DataRetentionRunner>(scope.ServiceProvider);
            await runner.RunOnceAsync(DateTime.UtcNow, ct);
            await CleanupPurchaseConsentsAsync(scope.ServiceProvider.GetRequiredService<SmartFinanceDbContext>(),
                DateTime.UtcNow, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { _logger.LogError(ex, "Veri saklama temizliği başarısız oldu."); }
    }

    /// <summary>Kimliksiz kanıtları 3 yıl, satın almaya dönüşmeyen denemeleri 30 gün saklar.</summary>
    public async Task<int> CleanupPurchaseConsentsAsync(SmartFinanceDbContext context, DateTime utcNow, CancellationToken ct)
    {
        var evidenceCutoff = utcNow.AddYears(-3);
        var attemptCutoff = utcNow.AddDays(-30);
        var deleted = 0;
        try
        {
            var lastId = 0;
            while (true)
            {
                var ids = await context.PurchaseConsents.IgnoreQueryFilters()
                    .Where(x => x.Id > lastId &&
                        ((x.UserId == null && x.ConsentedAt < evidenceCutoff) ||
                         (x.UserId != null && x.PurchaseToken == null && x.ConsentedAt < attemptCutoff)))
                    .OrderBy(x => x.Id).Select(x => x.Id).Take(DataRetentionRunner.BatchSize).ToListAsync(ct);
                if (ids.Count == 0) break;
                context.ChangeTracker.Clear();
                context.PurchaseConsents.RemoveRange(ids.Select(id => new PurchaseConsent { Id = id }));
                await context.SaveChangesAsync(ct);
                context.ChangeTracker.Clear();
                deleted += ids.Count;
                lastId = ids[^1];
                if (ids.Count < DataRetentionRunner.BatchSize) break;
            }
            _logger.LogInformation("Veri saklama: PurchaseConsents temizliği tamamlandı ({Deleted} kayıt silindi).", deleted);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Veri saklama: PurchaseConsents temizliği yarıda kaldı ({Deleted} kayıt silinmişti); sonraki turda tekrar denenecek.", deleted);
        }
        finally { context.ChangeTracker.Clear(); }
        return deleted;
    }
}
