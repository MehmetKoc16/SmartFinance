using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SmartFinance.Domain.Common;
using SmartFinance.Domain.Entities;
using SmartFinance.Infrastructure.Context;

namespace SmartFinance.Infrastructure.Retention;

/// Bir temizlik turunun sonucu. Yalnız sayılar: loglara ve testlere kişisel veri taşımaz.
public sealed record DataRetentionResult(
    int DeletedTransactions,
    int SkippedTransactionsWithoutDeletedAt,
    int DeletedRefreshTokens,
    int DeletedPasswordResetTokens,
    IReadOnlyList<string> FailedKinds);

/// <summary>
/// KVKK md. 7 (29.10.2026 denetimi, onay 30.10.2026): amacı ortadan kalkan kayıtları kalıcı siler.
/// - Çöp kutusundaki işlemler: geri alma süresi (30 gün) dolunca.
/// - Süresi dolmuş / iptal edilmiş refresh token'lar ve süresi dolmuş / kullanılmış şifre
///   sıfırlama token'ları: 7 gün sonra.
/// Kategori, bütçe, yatırım, hesap ve transfer bu işin kapsamı DIŞINDADIR (FK bağları, bkz. rapor).
/// </summary>
public class DataRetentionRunner
{
    /// Çöp kutusu süresi. TransactionService.GetDeletedTransactionsAsync ve RestoreTransactionAsync
    /// içindeki `now.AddDays(-30)` ile AYNI değer olmalı: kullanıcıya gösterilen pencere ile kalıcı
    /// silme sınırı birbirini tam tamamlar (gösterim: UpdatedDate >= now-30, silme: UpdatedDate < now-30).
    /// Biri değişirse diğeri de değişmeli; DataRetentionRunnerTests.CopKutusuPenceresi_TransactionServiceIleAyni
    /// bu bağı test eder.
    public const int TrashRetentionDays = 30;

    /// Süresi dolmuş / iptal edilmiş / kullanılmış token'ların saklanma süresi.
    public const int TokenRetentionDays = 7;

    /// Tek SaveChanges'te silinen en fazla kayıt (uzun kilit ve büyük işlem günlüğünü önler).
    public const int BatchSize = 500;

    private readonly SmartFinanceDbContext _context;
    private readonly ILogger<DataRetentionRunner> _logger;

    public DataRetentionRunner(SmartFinanceDbContext context, ILogger<DataRetentionRunner>? logger = null)
    {
        _context = context;
        _logger = logger ?? NullLogger<DataRetentionRunner>.Instance;
    }

    public async Task<DataRetentionResult> RunOnceAsync(DateTime utcNow, CancellationToken ct)
    {
        var now = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
        var trashCutoff = now.AddDays(-TrashRetentionDays);
        var tokenCutoff = now.AddDays(-TokenRetentionDays);
        var failed = new List<string>();

        // Her tür ayrı try/catch: birinde veritabanı hatası olsa da diğerleri temizlenir.
        // İşlemlere FK ile bağlı tablo yok (Transfer, RecurringPayment, ImportLog, Notification
        // TransactionId tutmaz); işlem silmek yalnız kendi satırını kaldırır.
        var transactions = await RunKindAsync("Transactions", failed, ct, counter =>
            DeleteInBatchesAsync(_context.Transactions.IgnoreQueryFilters()
                .Where(t => t.IsDeleted && t.UpdatedDate != null && t.UpdatedDate < trashCutoff), counter, ct));

        // Silinme anı bilinmeyen kayıtlar (UpdatedDate null) CreatedDate'e göre SİLİNMEZ.
        var skipped = 0;
        try
        {
            skipped = await _context.Transactions.IgnoreQueryFilters()
                .CountAsync(t => t.IsDeleted && t.UpdatedDate == null, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Veri saklama: silinme anı bilinmeyen işlemler sayılamadı.");
        }

        var refreshTokens = await RunKindAsync("RefreshTokens", failed, ct, counter =>
            DeleteInBatchesAsync(_context.RefreshTokens.IgnoreQueryFilters()
                .Where(t => t.ExpiresAt < tokenCutoff || (t.RevokedAt != null && t.RevokedAt < tokenCutoff)),
                counter, ct));

        var resetTokens = await RunKindAsync("PasswordResetTokens", failed, ct, counter =>
            DeleteInBatchesAsync(_context.PasswordResetTokens.IgnoreQueryFilters()
                .Where(t => t.ExpiresAt < tokenCutoff || (t.UsedAt != null && t.UsedAt < tokenCutoff)),
                counter, ct));

        _logger.LogInformation(
            "Veri saklama temizliği tamamlandı. Silinen işlem: {DeletedTransactions}, " +
            "silinme anı bilinmediği için atlanan işlem: {SkippedTransactions}, " +
            "silinen refresh token: {DeletedRefreshTokens}, silinen şifre sıfırlama token'ı: {DeletedPasswordResetTokens}, " +
            "hatalı tür sayısı: {FailedKindCount}.",
            transactions, skipped, refreshTokens, resetTokens, failed.Count);

        return new DataRetentionResult(transactions, skipped, refreshTokens, resetTokens, failed);
    }

    private sealed class Counter { public int Value; }

    private async Task<int> RunKindAsync(string kind, List<string> failed, CancellationToken ct,
        Func<Counter, Task> work)
    {
        var counter = new Counter();
        try
        {
            await work(counter);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // EF, hassas veri loglaması kapalıyken istisna metnine kolon değeri koymaz.
            _logger.LogError(ex, "Veri saklama: {Kind} temizliği yarıda kaldı ({Deleted} kayıt silinmişti); sonraki turda tekrar denenecek.",
                kind, counter.Value);
            failed.Add(kind);
        }
        finally
        {
            // Düşen partinin 'Deleted' kayıtları izleyicide kalırsa sonraki türün SaveChanges'i de düşer.
            _context.ChangeTracker.Clear();
        }
        return counter.Value;
    }

    // ExecuteDeleteAsync yerine RemoveRange + SaveChanges: InMemory sağlayıcı toplu silmeyi desteklemiyor
    // ve sağlayıcıya göre iki ayrı yol tutmak testlerin gerçek yolu sınamamasına yol açardı.
    // Yalnız Id okunur (kişisel alanlar belleğe alınmaz), anahtardan saplama varlık silinir.
    // Id üzerinden ilerleyen sayfalama: bir parti beklenmedik şekilde silinmese bile döngü sonsuza girmez.
    private async Task DeleteInBatchesAsync<T>(IQueryable<T> source, Counter counter, CancellationToken ct)
        where T : BaseEntity, new()
    {
        var lastId = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var ids = await source.Where(x => x.Id > lastId).OrderBy(x => x.Id)
                .Select(x => x.Id).Take(BatchSize).ToListAsync(ct);
            if (ids.Count == 0) return;

            _context.ChangeTracker.Clear();
            _context.Set<T>().RemoveRange(ids.Select(id => new T { Id = id }));
            await _context.SaveChangesAsync(ct);
            _context.ChangeTracker.Clear();

            counter.Value += ids.Count;
            lastId = ids[^1];
            if (ids.Count < BatchSize) return;
        }
    }
}
