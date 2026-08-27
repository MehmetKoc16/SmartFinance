using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartFinance.Application.DTOs.MarketData;
using SmartFinance.Application.Interfaces;
using SmartFinance.Infrastructure.Context;

namespace SmartFinance.Infrastructure.MarketData;

/// <summary>
/// Takip edilen tüm sembollerin güncel fiyatını arka planda, TOPLU isteklerle
/// yenileyip önbelleğe yazar.
///
/// Çözdüğü problem: fiyat isteğe bağlı çekildiğinde dış servise giden istek
/// sayısı kullanıcı sayısıyla ve kullanıcıların uygulamaya bakma sıklığıyla
/// birlikte büyüyor. Yahoo'nun hız sınırı belgelenmemiş ve IP bazlı olduğu için
/// bu, ölçeklendikçe kontrolsüz bir risk.
///
/// Bu servisle dış istek sayısı yalnızca FARKLI SEMBOL sayısına bağlı kalıyor:
/// 100 sembol / 50'lik toplu istek = 2 istek, 5 dakikada bir → günde ~192 istek.
/// 1.000 kullanıcı da 100.000 kullanıcı da olsa bu sayı değişmiyor.
///
/// Önbellek TTL'i yenileme aralığından uzun tutuluyor: dış servis geçici olarak
/// cevap veremezse kullanıcı hata görmek yerine son bilinen fiyatı görür.
/// </summary>
public class PriceRefreshService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PriceRefreshService> _logger;

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);

    // Yenileme aralığından belirgin şekilde uzun: bir tur atlansa bile
    // kullanıcı fiyatsız kalmaz (zarif bozulma).
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(20);

    // Farklı sağlayıcılara ard arda gitmemek için küçük bir ara.
    private static readonly TimeSpan InterBatchDelay = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(20);

    public PriceRefreshService(IServiceScopeFactory scopeFactory, ILogger<PriceRefreshService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshAllAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                // Bir tur başarısız olursa servis ölmemeli; kullanıcılar bu arada
                // önbellekteki son fiyatı görmeye devam eder.
                _logger.LogError(ex, "Fiyat yenileme turu başarısız oldu.");
            }

            try { await Task.Delay(RefreshInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task RefreshAllAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var context = sp.GetRequiredService<SmartFinanceDbContext>();
        var cache = sp.GetRequiredService<IPriceCache>();
        var providers = sp.GetServices<IPriceProvider>().ToList();

        // Portföylerde gerçekten tutulan semboller — TEFAS fonları hariç
        // (onların fiyatı FundHistorySyncService tarafından güncelleniyor).
        var tracked = await context.Investments
            .AsNoTracking()
            .Where(i => i.InvestmentType != "fund")
            .Select(i => new { i.Name, i.InvestmentType })
            .Distinct()
            .ToListAsync(ct);

        if (tracked.Count == 0) return;

        var totalRefreshed = 0;
        var totalRequests = 0;

        foreach (var group in tracked.GroupBy(t => t.InvestmentType))
        {
            if (ct.IsCancellationRequested) return;

            var investmentType = group.Key;
            var symbols = group.Select(g => g.Name.Trim().ToUpperInvariant()).Distinct().ToList();

            var provider = providers.FirstOrDefault(p =>
                p.SupportedInvestmentTypes.Contains(investmentType, StringComparer.OrdinalIgnoreCase));

            if (provider is not IBatchPriceProvider batchProvider)
            {
                // Toplu istek desteklemeyen sağlayıcılar atlanır; onların fiyatı
                // eskisi gibi istek anında çekilir.
                _logger.LogDebug("{Type} için toplu fiyat desteği yok, atlandı.", investmentType);
                continue;
            }

            foreach (var chunk in Chunk(symbols, batchProvider.MaxBatchSize))
            {
                if (ct.IsCancellationRequested) return;

                try
                {
                    if (totalRequests > 0) await Task.Delay(InterBatchDelay, ct);

                    var prices = await batchProvider.GetCurrentPricesAsync(chunk, ct);
                    totalRequests++;

                    var asOf = DateTime.UtcNow;
                    foreach (var (symbol, price) in prices)
                    {
                        cache.Set(symbol, investmentType,
                            new PriceQuoteDto { Symbol = symbol, Price = price, AsOf = asOf },
                            CacheTtl);
                        totalRefreshed++;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    // Tek bir grubun hatası diğerlerini engellememeli.
                    _logger.LogWarning(ex, "{Type} için toplu fiyat çekilemedi ({Count} sembol).",
                        investmentType, chunk.Count);
                }
            }
        }

        _logger.LogInformation(
            "Fiyat yenileme: {Refreshed} sembol, {Requests} dış istek ({Tracked} takip edilen sembol).",
            totalRefreshed, totalRequests, tracked.Count);
    }

    private static IEnumerable<IReadOnlyCollection<string>> Chunk(List<string> items, int size)
    {
        for (var i = 0; i < items.Count; i += size)
            yield return items.GetRange(i, Math.Min(size, items.Count - i));
    }
}
