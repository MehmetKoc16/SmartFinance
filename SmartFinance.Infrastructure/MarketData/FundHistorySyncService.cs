using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartFinance.Application.Interfaces;

namespace SmartFinance.Infrastructure.MarketData;

/// <summary>
/// Fon NAV geçmişini TEFAS'tan çekip kendi veritabanımıza yazan arka plan işi.
///
/// TEFAS'a giden TEK yer burasıdır — kullanıcı istekleri depodan okur. Bunun
/// nedeni TEFAS'ın IP başına dakikada ~6 istekle sınırlaması: sınır tüm
/// kullanıcılar arasında paylaşıldığı için, kullanıcı sayısı arttıkça istek
/// anında çekmek tıkanmaya yol açardı.
///
/// Yalnızca EKSİK günler çekilir: deponun son tarihinden bugüne kadar olan
/// aralık istenir. Günlük çalışmada bu fon başına tek istek demektir
/// (7 istek yerine), böylece 200 fon ~35 dakikada güncellenir.
/// </summary>
public class FundHistorySyncService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<FundHistorySyncService> _logger;

    // Yeni eklenen bir fon için ilk seferde ne kadar geçmiş çekilecek.
    private const int InitialBackfillDays = 365;

    // Senkron turları arası bekleme. TEFAS NAV'ı günde bir yayınlıyor,
    // günde bir tur yeterli.
    private static readonly TimeSpan SyncInterval = TimeSpan.FromHours(24);

    // Fonlar arası bekleme — TEFAS hız sınırına (dakikada ~6) saygı.
    private static readonly TimeSpan InterFundDelay = TimeSpan.FromSeconds(11);

    // Uygulama açılışında hemen başlamaz: önce API'nin ayağa kalkması beklenir,
    // aksi halde açılış istekleriyle yarışır.
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);

    public FundHistorySyncService(IServiceScopeFactory scopeFactory, ILogger<FundHistorySyncService> logger)
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
                _logger.LogError(ex, "Fon senkronizasyonu başarısız oldu, bir sonraki turda tekrar denenecek.");
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
        var store = scope.ServiceProvider.GetRequiredService<IFundHistoryStore>();
        var tefas = scope.ServiceProvider.GetServices<IPriceProvider>()
            .OfType<TefasPriceProvider>()
            .FirstOrDefault();

        if (tefas == null)
        {
            _logger.LogWarning("TefasPriceProvider bulunamadı, fon senkronizasyonu atlandı.");
            return;
        }

        var fundCodes = await store.GetTrackedFundCodesAsync(ct);
        if (fundCodes.Count == 0)
        {
            _logger.LogInformation("Takip edilen fon yok, senkronizasyon atlandı.");
            return;
        }

        _logger.LogInformation("Fon senkronizasyonu başladı: {Count} fon.", fundCodes.Count);
        var today = DateTime.Today;
        var totalAdded = 0;
        var isFirst = true;

        foreach (var code in fundCodes)
        {
            if (ct.IsCancellationRequested) return;

            try
            {
                var latest = await store.GetLatestDateAsync(code, ct);

                // Sadece eksik aralık istenir. Hiç veri yoksa ilk dolum yapılır.
                var from = latest.HasValue ? latest.Value.AddDays(1) : today.AddDays(-InitialBackfillDays);
                if (from > today)
                {
                    _logger.LogDebug("{Fund} zaten güncel, atlandı.", code);
                    continue;
                }

                if (!isFirst) await Task.Delay(InterFundDelay, ct);
                isFirst = false;

                var bars = await tefas.FetchFromTefasAsync(code, from, today, ct);
                var added = await store.UpsertAsync(code, bars, ct);
                totalAdded += added;

                _logger.LogInformation("{Fund}: {Added} yeni gün eklendi ({From:yyyy-MM-dd} .. {To:yyyy-MM-dd}).",
                    code, added, from, today);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Tek bir fonun hatası (kod değişmiş, TEFAS'ta yok vb.) diğerlerini
                // engellememelidir.
                _logger.LogWarning(ex, "{Fund} senkronize edilemedi, diğer fonlara devam ediliyor.", code);
            }
        }

        _logger.LogInformation("Fon senkronizasyonu bitti: toplam {Total} yeni gün.", totalAdded);
    }
}
