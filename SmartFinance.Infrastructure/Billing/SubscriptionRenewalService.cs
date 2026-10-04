using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartFinance.Application.Interfaces;
using SmartFinance.Infrastructure.Context;

namespace SmartFinance.Infrastructure.Billing;

public sealed class SubscriptionRenewalService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IPlayBillingClient _client;
    private readonly ILogger<SubscriptionRenewalService> _logger;

    public SubscriptionRenewalService(IServiceScopeFactory scopeFactory, IPlayBillingClient client,
        ILogger<SubscriptionRenewalService> logger)
    {
        _scopeFactory = scopeFactory;
        _client = client;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(DateTime.UtcNow, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _logger.LogError("Abonelik yenileme turu başarısız. Hata türü: {ErrorType}.", ex.GetType().Name);
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }

    public async Task RunOnceAsync(DateTime nowUtc, CancellationToken ct)
    {
        if (!_client.IsConfigured) return;
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<SmartFinanceDbContext>();
        var from = nowUtc.AddDays(-7);
        var to = nowUtc.AddDays(2);
        var stale = nowUtc.Subtract(Interval);
        var ids = await context.Subscriptions.AsNoTracking()
            .Where(s => s.ExpiresAt >= from && s.ExpiresAt <= to
                && (s.LastVerifiedAt == null || s.LastVerifiedAt < stale))
            .OrderBy(s => s.LastVerifiedAt).ThenBy(s => s.Id)
            .Select(s => s.Id).Take(200).ToListAsync(ct);

        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // Her kaydı yeniden yükle; bağlı jeton önceki adımda değişmiş olabilir.
                var row = await context.Subscriptions.SingleOrDefaultAsync(s => s.Id == id, ct);
                if (row is null) continue;
                var info = await _client.GetSubscriptionAsync(row.PurchaseToken, ct);
                if (info is null)
                {
                    row.ExpiresAt = nowUtc;
                    row.LastVerifiedAt = nowUtc;
                }
                else
                {
                    await SubscriptionUpdater.ApplyAsync(context, row, info, _client, _logger, nowUtc, ct);
                }
                await context.SaveChangesAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning("Abonelik yeniden doğrulanamadı. Kayıt: {SubscriptionId}, hata türü: {ErrorType}.",
                    id, ex.GetType().Name);
            }
            finally
            {
                // Başarısız kaydın değişiklikleri sonraki SaveChanges'e taşınmasın.
                context.ChangeTracker.Clear();
            }
        }
    }
}
