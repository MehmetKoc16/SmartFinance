using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { _logger.LogError(ex, "Veri saklama temizliği başarısız oldu."); }
    }
}
