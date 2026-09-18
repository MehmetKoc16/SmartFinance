using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartFinance.Domain.Entities;
using SmartFinance.Infrastructure.Context;

namespace SmartFinance.Infrastructure.Fundamentals;

/// <summary>
/// Portfoylerde tutulan hisselerin donemlik net kar ve ozkaynagini KAP'tan ceker.
/// Acilistan kisa sure sonra ve 6 saatte bir calisir; yeni bir ceyrek raporu
/// en gec birkac saat icinde yansir.
///
/// Hisse basina TEK sirketlik istek: Excel satirlarinda sirket kimligi yok, yalnizca
/// rapor tarihindeki unvan var. Toplu istekte unvan degistirmis bir sirketin
/// satirlari yanlis hisseye baglanabilirdi.
/// </summary>
public class KapFundamentalsSyncService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<KapFundamentalsSyncService> _logger;

    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    // KAP ~8 ardisik indirmeden sonra 429 donuyor (otomatik-al-sat olcumu);
    // istekler arasi bekleme o projedekiyle ayni.
    private static readonly TimeSpan RequestDelay = TimeSpan.FromSeconds(6);

    public KapFundamentalsSyncService(IServiceScopeFactory scopeFactory, ILogger<KapFundamentalsSyncService> logger)
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
                await SyncAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                // Tur basarisiz olursa servis olmemeli; F/K son kaydedilen veriyle gosterilmeye devam eder.
                _logger.LogError(ex, "KAP temel veri senkronu basarisiz oldu.");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task SyncAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<SmartFinanceDbContext>();
        var kap = scope.ServiceProvider.GetRequiredService<IKapClient>();

        var semboller = await context.Investments
            .AsNoTracking()
            .Where(i => i.InvestmentType == "stock")
            .Select(i => i.Name.Trim().ToUpper())
            .Distinct()
            .ToListAsync(ct);
        if (semboller.Count == 0) return;

        var sirketler = await kap.GetCompaniesAsync(ct);
        var yilGruplari = YearGroups(DateTime.UtcNow);
        var kaydedilen = 0;

        foreach (var sembol in semboller)
        {
            if (!sirketler.TryGetValue(sembol, out var sirket))
            {
                _logger.LogDebug("{Symbol} KAP sirket listesinde yok, atlandi.", sembol);
                continue;
            }

            foreach (var yillar in yilGruplari)
            {
                await Task.Delay(RequestDelay, ct);
                try
                {
                    var satirlar = await kap.GetFinancialsAsync(sirket, yillar, ct);
                    kaydedilen += await SaveAsync(context, sembol, satirlar, ct);
                }
                catch (KapRateLimitException)
                {
                    _logger.LogWarning("KAP hiz sinirina takildi; tur {Symbol} hissesinde birakildi.", sembol);
                    return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "{Symbol} icin KAP verisi alinamadi.", sembol);
                }
            }
        }

        _logger.LogInformation("KAP temel veri senkronu: {Count} hisse, {Rows} donem kaydi guncellendi.",
            semboller.Count, kaydedilen);
    }

    /// <summary>
    /// Istenecek yil gruplari (KAP istek basina en fazla 2 yil kabul ediyor).
    /// Ocak-Nisan arasinda gecen yilin yillik raporu henuz yayimlanmamis
    /// olabilir; en son donem gecen yilin 3. ceyregi olur ve TTM icin iki yil
    /// oncesi de gerekir.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<int>> YearGroups(DateTime utcNow)
    {
        var y = utcNow.Year;
        var gruplar = new List<IReadOnlyList<int>> { new[] { y, y - 1 } };
        if (utcNow.Month <= 4) gruplar.Add(new[] { y - 2 });
        return gruplar;
    }

    /// Ayni donem icin konsolide tablo solo'ya tercih edilir. Mevcut kayit guncellenir.
    public static async Task<int> SaveAsync(SmartFinanceDbContext context, string symbol,
        IEnumerable<KapFinancialRow> rows, CancellationToken ct = default)
    {
        var enIyiler = rows
            .GroupBy(r => (r.Year, r.Period))
            .Select(g => g.OrderByDescending(r => r.Consolidated).ThenByDescending(r => r.PublishedAt).First())
            .ToList();
        if (enIyiler.Count == 0) return 0;

        var mevcut = await context.CompanyFinancials
            .Where(c => c.Symbol == symbol)
            .ToDictionaryAsync(c => (c.Year, c.Period), ct);

        foreach (var r in enIyiler)
        {
            if (!mevcut.TryGetValue((r.Year, r.Period), out var kayit))
            {
                kayit = new CompanyFinancial { Symbol = symbol, Year = r.Year, Period = r.Period };
                context.CompanyFinancials.Add(kayit);
            }
            else
            {
                kayit.UpdatedDate = DateTime.UtcNow;
            }
            kayit.NetProfitParent = r.NetProfitParent;
            kayit.EquityParent = r.EquityParent;
            kayit.Consolidated = r.Consolidated;
            kayit.DisclosureId = r.DisclosureId;
            kayit.PublishedAt = r.PublishedAt;
        }
        await context.SaveChangesAsync(ct);
        return enIyiler.Count;
    }
}
