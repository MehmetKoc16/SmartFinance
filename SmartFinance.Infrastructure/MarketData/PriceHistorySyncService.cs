using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartFinance.Application.Interfaces;

namespace SmartFinance.Infrastructure.MarketData;

/// <summary>
/// Günlük fiyat geçmişini dış kaynaklardan (TEFAS, Yahoo) çekip kendi
/// veritabanımıza yazan arka plan işi. Fon ve hisse için ortak çalışır.
///
/// Dış kaynağa giden TEK yer burasıdır — kullanıcı istekleri depodan okur.
/// Bunun nedeni hız sınırlarının IP başına olması: sınır tüm kullanıcılar
/// arasında paylaşıldığı için, istek anında çekmek kullanıcı sayısı arttıkça
/// tıkanmaya yol açardı (TEFAS'ta ölçülen sınır dakikada ~6 istek).
///
/// Yalnızca EKSİK günler çekilir: deponun son tarihinden bugüne kadar olan
/// aralık istenir. Günlük çalışmada bu sembol başına tek istek demektir.
/// Son saklanan gün ARALIĞA DAHİL edilir — o günün barı seans sürerken kısmi
/// kaydedilmiş olabilir; yeniden çekilip kesin kapanışla düzeltilir.
/// </summary>
public class PriceHistorySyncService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PriceHistorySyncService> _logger;

    // Yeni eklenen bir sembol için ilk seferde ne kadar geçmiş çekilecek.
    private const int InitialBackfillDays = 365;

    // Senkron turları arası bekleme. Günlük bar günde bir kez kesinleşir,
    // günde bir tur yeterli.
    private static readonly TimeSpan SyncInterval = TimeSpan.FromHours(24);

    // Uygulama açılışında hemen başlamaz: önce API'nin ayağa kalkması beklenir,
    // aksi halde açılış istekleriyle yarışır.
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);

    public PriceHistorySyncService(IServiceScopeFactory scopeFactory, ILogger<PriceHistorySyncService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SyncAllAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Bir tur başarısız olursa servis ölmemeli — bir sonraki turda
                // yeniden denenir, kullanıcı bu arada depodaki veriyi görmeye devam eder.
                _logger.LogError(ex, "Fiyat geçmişi senkronizasyonu başarısız oldu, bir sonraki turda tekrar denenecek.");
            }

            try
            {
                await Task.Delay(SyncInterval, stoppingToken);
            }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task SyncAllAsync(CancellationToken ct)
    {
        // BackgroundService singleton'dır; DbContext scoped olduğu için her
        // turda kendi kapsamı açılır.
        using var scope = _scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IPriceHistoryStore>();

        // Geçmişi dış kaynaktan çekebilen sağlayıcılar kendilerini IHistorySource
        // ile bildiriyor; yeni bir sağlayıcı eklendiğinde burası değişmiyor.
        var sources = scope.ServiceProvider.GetServices<IPriceProvider>()
            .OfType<IHistorySource>()
            .ToList();

        if (sources.Count == 0)
        {
            _logger.LogWarning("Geçmiş verisi çekebilen sağlayıcı bulunamadı, senkronizasyon atlandı.");
            return;
        }

        foreach (var source in sources)
        {
            if (ct.IsCancellationRequested) return;

            var provider = (IPriceProvider)source;
            foreach (var investmentType in provider.SupportedInvestmentTypes)
            {
                if (ct.IsCancellationRequested) return;
                await SyncTypeAsync(store, source, investmentType, ct);
            }
        }
    }

    private async Task SyncTypeAsync(
        IPriceHistoryStore store, IHistorySource source, string investmentType, CancellationToken ct)
    {
        var symbols = await store.GetTrackedSymbolsAsync(investmentType, ct);
        if (symbols.Count == 0)
        {
            _logger.LogInformation("Takip edilen {Type} yok, senkronizasyon atlandı.", investmentType);
            return;
        }

        _logger.LogInformation("{Type} senkronizasyonu başladı: {Count} sembol.", investmentType, symbols.Count);
        var today = DateTime.Today;
        var totalAdded = 0;
        var isFirst = true;

        foreach (var symbol in symbols)
        {
            if (ct.IsCancellationRequested) return;

            try
            {
                var latest = await store.GetLatestDateAsync(symbol, investmentType, ct);

                // Son saklanan gün dahil ediliyor (AddDays(1) değil): o günün barı
                // seans sürerken kısmi yazılmış olabilir, kesin değerle düzeltilsin.
                var from = latest ?? today.AddDays(-InitialBackfillDays);
                if (from > today) continue;

                // Kaynağın hız sınırına saygı — semboller arası bekleme.
                if (!isFirst) await Task.Delay(source.InterSymbolDelay, ct);
                isFirst = false;

                var bars = await source.FetchDailyBarsAsync(symbol, from, today, ct);
                var added = await store.UpsertAsync(symbol, investmentType, bars, ct);
                totalAdded += added;

                _logger.LogInformation("{Symbol} ({Type}): {Added} yeni gün eklendi ({From:yyyy-MM-dd} .. {To:yyyy-MM-dd}).",
                    symbol, investmentType, added, from, today);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Tek bir sembolün hatası (kod değişmiş, kaynakta yok vb.)
                // diğerlerini engellememelidir.
                _logger.LogWarning(ex, "{Symbol} ({Type}) senkronize edilemedi, diğerlerine devam ediliyor.",
                    symbol, investmentType);
            }
        }

        _logger.LogInformation("{Type} senkronizasyonu bitti: toplam {Total} yeni gün.", investmentType, totalAdded);
    }
}
