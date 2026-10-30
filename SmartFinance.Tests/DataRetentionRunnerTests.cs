using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Moq;
using SmartFinance.Application.Interfaces;
using SmartFinance.Domain.Entities;
using SmartFinance.Domain.Enums;
using SmartFinance.Infrastructure.Context;
using SmartFinance.Infrastructure.Repositories;
using SmartFinance.Infrastructure.Retention;
using SmartFinance.Infrastructure.Services;

namespace SmartFinance.Tests;

/// <summary>
/// KVKK md. 7 gecelik kalıcı silme işi (29.10.2026 denetimi, onay 30.10.2026).
/// Tüm veriler sentetiktir.
/// </summary>
public class DataRetentionRunnerTests
{
    private static readonly DateTime Now = new(2026, 10, 30, 0, 30, 0, DateTimeKind.Utc);

    private static DbContextOptions<SmartFinanceDbContext> Secenekler() =>
        new DbContextOptionsBuilder<SmartFinanceDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

    private static SmartFinanceDbContext Baglam() => new(Secenekler());

    /// RefreshToken kaydını sildirmeye çalışınca hata veren bağlam: bir türdeki
    /// veritabanı hatasının diğer türleri durdurmadığını sınamak için.
    private sealed class RefreshTokenSilmedeHataVerenBaglam(DbContextOptions<SmartFinanceDbContext> o)
        : SmartFinanceDbContext(o)
    {
        public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
        {
            if (ChangeTracker.Entries<RefreshToken>().Any(e => e.State == EntityState.Deleted))
                throw new DbUpdateException("Deneme: FK ihlali benzetimi");
            return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }
    }

    private static Transaction Islem(bool silinmis, DateTime? silinmeAni, int user = 1) => new()
    {
        UserId = user, Amount = 10m, Description = "Sentetik işlem", Type = TransactionType.Expense,
        TransactionDate = new DateTime(2026, 1, 1), CreatedDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        IsDeleted = silinmis, UpdatedDate = silinmeAni
    };

    private static RefreshToken Rt(DateTime expiresAt, DateTime? revokedAt = null) => new()
    {
        UserId = 1, Token = Guid.NewGuid().ToString("N"), ExpiresAt = expiresAt, RevokedAt = revokedAt
    };

    private static PasswordResetToken Prt(DateTime expiresAt, DateTime? usedAt = null) => new()
    {
        UserId = 1, TokenHash = Guid.NewGuid().ToString("N"), ExpiresAt = expiresAt, UsedAt = usedAt
    };

    // ─── İşlemler ──────────────────────────────────────────────────────────

    // NEDEN: Çöp kutusu süresi (30 gün) dolan işlem KVKK md. 7 gereği kalıcı silinmeli;
    // süresi dolmayan, hiç silinmemiş ve silinme anı bilinmeyen işlemler kalmalı.
    [Fact]
    public async Task Islemler_31GunOnceSilinenSilinir_DigerleriKalir()
    {
        using var db = Baglam();
        var eski = Islem(true, Now.AddDays(-31));
        var yeni = Islem(true, Now.AddDays(-29));
        var silinmemis = Islem(false, Now.AddDays(-400));
        var anSiz = Islem(true, null);
        db.Transactions.AddRange(eski, yeni, silinmemis, anSiz);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var sonuc = await new DataRetentionRunner(db).RunOnceAsync(Now, default);

        var kalan = await db.Transactions.IgnoreQueryFilters().Select(t => t.Id).ToListAsync();
        Assert.DoesNotContain(eski.Id, kalan);
        Assert.Contains(yeni.Id, kalan);
        Assert.Contains(silinmemis.Id, kalan);
        Assert.Contains(anSiz.Id, kalan);
        Assert.Equal(1, sonuc.DeletedTransactions);
        Assert.Equal(1, sonuc.SkippedTransactionsWithoutDeletedAt);
        Assert.Empty(sonuc.FailedKinds);
    }

    // NEDEN: Silinmemiş eski işlem kullanıcının aktif verisidir; yaşı ne olursa olsun silinmemeli
    // (UpdatedDate eski bir düzenleme anı olabilir).
    [Fact]
    public async Task Islemler_SilinmemisEskiIslem_DuzenlemeAniEskiOlsaDaKalir()
    {
        using var db = Baglam();
        var islem = Islem(false, Now.AddDays(-90));
        db.Transactions.Add(islem);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var sonuc = await new DataRetentionRunner(db).RunOnceAsync(Now, default);

        Assert.Single(await db.Transactions.IgnoreQueryFilters().ToListAsync());
        Assert.Equal(0, sonuc.DeletedTransactions);
    }

    // NEDEN: Silinme anı bilinmeyen kayıt CreatedDate'e göre silinmemeli; çok eski oluşturulmuş
    // olsa bile dün silinmiş olabilir (kullanıcının 30 günlük geri alma hakkı).
    [Fact]
    public async Task Islemler_UpdatedDateNullSilinmis_CokEskiOlsaDaKalirVeSayilir()
    {
        using var db = Baglam();
        var islem = Islem(true, null);
        islem.CreatedDate = Now.AddYears(-3);
        db.Transactions.Add(islem);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var sonuc = await new DataRetentionRunner(db).RunOnceAsync(Now, default);

        Assert.Single(await db.Transactions.IgnoreQueryFilters().ToListAsync());
        Assert.Equal(0, sonuc.DeletedTransactions);
        Assert.Equal(1, sonuc.SkippedTransactionsWithoutDeletedAt);
    }

    // NEDEN: Kalıcı silme sınırı çöp kutusu penceresiyle birebir aynı olmalı: kullanıcının hâlâ
    // görüp geri alabildiği işlem silinmemeli, artık göremediği işlem kalmamalı.
    // TransactionService'teki 30 gün ile DataRetentionRunner.TrashRetentionDays bu test ile bağlı.
    [Fact]
    public async Task CopKutusuPenceresi_TransactionServiceIleAyni()
    {
        using var db = Baglam();
        var current = new Mock<ICurrentUserService>();
        current.SetupGet(c => c.UserId).Returns(1);
        var service = new TransactionService(new GenericRepository<Transaction>(db), db, current.Object,
            new Mock<INotificationService>(MockBehavior.Strict).Object);
        var simdi = DateTime.UtcNow;
        var gorunen = Islem(true, simdi.AddDays(-(DataRetentionRunner.TrashRetentionDays - 0.5)));
        var gorunmeyen = Islem(true, simdi.AddDays(-(DataRetentionRunner.TrashRetentionDays + 0.5)));
        db.Transactions.AddRange(gorunen, gorunmeyen);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var cop = (await service.GetDeletedTransactionsAsync()).Select(x => x.Id).ToList();
        Assert.Equal(new[] { gorunen.Id }, cop);

        await new DataRetentionRunner(db).RunOnceAsync(DateTime.UtcNow, default);

        var kalan = await db.Transactions.IgnoreQueryFilters().Select(t => t.Id).ToListAsync();
        Assert.Equal(new[] { gorunen.Id }, kalan);
    }

    // ─── Refresh token ─────────────────────────────────────────────────────

    // NEDEN: Süresi 7 günden önce dolan token artık oturum açmaz, saklama amacı kalmamıştır.
    // Geçerli token silinirse kullanıcı oturumdan atılır (401 → mobilde oturum kapatma).
    [Fact]
    public async Task RefreshToken_8GunOnceDolanSilinir_GecerliKalir()
    {
        using var db = Baglam();
        var dolmus = Rt(Now.AddDays(-8));
        var gecerli = Rt(Now.AddDays(20));
        db.RefreshTokens.AddRange(dolmus, gecerli);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var sonuc = await new DataRetentionRunner(db).RunOnceAsync(Now, default);

        var kalan = await db.RefreshTokens.IgnoreQueryFilters().Select(t => t.Id).ToListAsync();
        Assert.Equal(new[] { gecerli.Id }, kalan);
        Assert.Equal(1, sonuc.DeletedRefreshTokens);
    }

    // NEDEN: İptal anından 7 gün geçmeden token silinmemeli (yakın tarihli oturum kaydı);
    // 7 günden eski iptal edilmiş token, süresi dolmamış olsa da silinmeli.
    [Fact]
    public async Task RefreshToken_3GunOnceIptalEdilenKalir_8GunOnceIptalEdilenSilinir()
    {
        using var db = Baglam();
        var yeniIptal = Rt(Now.AddDays(20), Now.AddDays(-3));
        var eskiIptal = Rt(Now.AddDays(20), Now.AddDays(-8));
        db.RefreshTokens.AddRange(yeniIptal, eskiIptal);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await new DataRetentionRunner(db).RunOnceAsync(Now, default);

        var kalan = await db.RefreshTokens.IgnoreQueryFilters().Select(t => t.Id).ToListAsync();
        Assert.Equal(new[] { yeniIptal.Id }, kalan);
    }

    // NEDEN: Parti boyutunu (500) aşan kayıtlar da tek turda silinmeli; döngü ilk partide durmamalı.
    [Fact]
    public async Task RefreshToken_PartiBoyutunuAsanKayitlarTekTurdaSilinir()
    {
        using var db = Baglam();
        var adet = DataRetentionRunner.BatchSize + 1;
        for (var i = 0; i < adet; i++) db.RefreshTokens.Add(Rt(Now.AddDays(-10)));
        db.RefreshTokens.Add(Rt(Now.AddDays(5)));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var sonuc = await new DataRetentionRunner(db).RunOnceAsync(Now, default);

        Assert.Equal(adet, sonuc.DeletedRefreshTokens);
        Assert.Single(await db.RefreshTokens.IgnoreQueryFilters().ToListAsync());
    }

    // ─── Şifre sıfırlama token'ı ───────────────────────────────────────────

    // NEDEN: Süresi 7 günden önce dolan sıfırlama bağlantısı işe yaramaz; bekleyen (geçerli)
    // bağlantı silinirse kullanıcı e-postadaki bağlantıyı kullanamaz.
    [Fact]
    public async Task SifreSifirlama_8GunOnceDolanSilinir_GecerliKalir()
    {
        using var db = Baglam();
        var dolmus = Prt(Now.AddDays(-8));
        var gecerli = Prt(Now.AddMinutes(30));
        db.PasswordResetTokens.AddRange(dolmus, gecerli);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var sonuc = await new DataRetentionRunner(db).RunOnceAsync(Now, default);

        var kalan = await db.PasswordResetTokens.IgnoreQueryFilters().Select(t => t.Id).ToListAsync();
        Assert.Equal(new[] { gecerli.Id }, kalan);
        Assert.Equal(1, sonuc.DeletedPasswordResetTokens);
    }

    // NEDEN: Kullanılmış bağlantı 7 gün sonra silinmeli; 3 gün önce kullanılan ve süresi
    // 7 günü geçmemiş olan kalmalı.
    [Fact]
    public async Task SifreSifirlama_8GunOnceKullanilanSilinir_3GunOnceKullanilanKalir()
    {
        using var db = Baglam();
        var eski = Prt(Now.AddDays(-8).AddHours(1), Now.AddDays(-8));
        var yeni = Prt(Now.AddDays(-3).AddHours(1), Now.AddDays(-3));
        db.PasswordResetTokens.AddRange(eski, yeni);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await new DataRetentionRunner(db).RunOnceAsync(Now, default);

        var kalan = await db.PasswordResetTokens.IgnoreQueryFilters().Select(t => t.Id).ToListAsync();
        Assert.Equal(new[] { yeni.Id }, kalan);
    }

    // ─── Hata yalıtımı ve log ──────────────────────────────────────────────

    // NEDEN: Bir türdeki veritabanı hatası (ör. FK ihlali) işi durdurmamalı; önceki ve sonraki
    // türler yine temizlenmeli, hata loglanmalı.
    [Fact]
    public async Task BirTurdeHata_DigerTurlerYineTemizlenir()
    {
        var loglar = new YakalayiciLoggerProvider();
        using var db = new RefreshTokenSilmedeHataVerenBaglam(Secenekler());
        db.Transactions.Add(Islem(true, Now.AddDays(-31)));
        db.RefreshTokens.Add(Rt(Now.AddDays(-8)));
        db.PasswordResetTokens.Add(Prt(Now.AddDays(-8)));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var sonuc = await new DataRetentionRunner(db, loglar.CreateLogger<DataRetentionRunner>())
            .RunOnceAsync(Now, default);

        Assert.Empty(await db.Transactions.IgnoreQueryFilters().ToListAsync());
        Assert.Single(await db.RefreshTokens.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await db.PasswordResetTokens.IgnoreQueryFilters().ToListAsync());
        Assert.Equal(new[] { "RefreshTokens" }, sonuc.FailedKinds);
        Assert.Equal(1, sonuc.DeletedTransactions);
        Assert.Equal(0, sonuc.DeletedRefreshTokens);
        Assert.Equal(1, sonuc.DeletedPasswordResetTokens);
        Assert.Contains(loglar.Kayitlar, k => k.Level == LogLevel.Error && k.Exception is DbUpdateException);
        Assert.Contains(loglar.Kayitlar, k => k.Level == LogLevel.Information);
    }

    // NEDEN: Her çalışma tür başına silinen sayıyı Information düzeyinde loglamalı;
    // log kişisel veri (açıklama, token özeti, kullanıcı kimliği) içermemeli.
    [Fact]
    public async Task Log_YalnizSayilarIcerir()
    {
        var loglar = new YakalayiciLoggerProvider();
        using var db = Baglam();
        var islem = Islem(true, Now.AddDays(-31), user: 4242);
        islem.Description = "Gizli açıklama";
        var rt = Rt(Now.AddDays(-8));
        db.Transactions.Add(islem);
        db.RefreshTokens.Add(rt);
        db.PasswordResetTokens.Add(Prt(Now.AddDays(-9)));
        db.PasswordResetTokens.Add(Prt(Now.AddDays(-10)));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await new DataRetentionRunner(db, loglar.CreateLogger<DataRetentionRunner>()).RunOnceAsync(Now, default);

        var bilgi = Assert.Single(loglar.Kayitlar, k => k.Level == LogLevel.Information);
        var alanlar = bilgi.Alanlar.ToDictionary(a => a.Key, a => a.Value);
        Assert.Equal(1, alanlar["DeletedTransactions"]);
        Assert.Equal(0, alanlar["SkippedTransactions"]);
        Assert.Equal(1, alanlar["DeletedRefreshTokens"]);
        Assert.Equal(2, alanlar["DeletedPasswordResetTokens"]);
        Assert.Equal(0, alanlar["FailedKindCount"]);
        foreach (var k in loglar.Kayitlar)
        {
            Assert.DoesNotContain("Gizli açıklama", k.TumMetin);
            Assert.DoesNotContain(rt.Token, k.TumMetin);
            Assert.DoesNotContain("4242", k.TumMetin);
        }
    }

    // ─── Zamanlama ─────────────────────────────────────────────────────────

    // NEDEN: İş sunucu saat diliminden bağımsız her gün 03:30 TSİ'de (00:30 UTC) çalışmalı;
    // KAP (05:00, 13:00) ve planlı ödeme hatırlatması (09:00) ile çakışmamalı.
    [Fact]
    public void SonrakiCalisma_0330Tsi()
    {
        Assert.Equal(new DateTime(2026, 10, 30, 0, 30, 0, DateTimeKind.Utc),
            DataRetentionService.NextRunUtc(new DateTime(2026, 10, 30, 0, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(new DateTime(2026, 10, 31, 0, 30, 0, DateTimeKind.Utc),
            DataRetentionService.NextRunUtc(new DateTime(2026, 10, 30, 0, 30, 0, DateTimeKind.Utc)));
        Assert.Equal(new DateTime(2026, 10, 31, 0, 30, 0, DateTimeKind.Utc),
            DataRetentionService.NextRunUtc(new DateTime(2026, 10, 30, 16, 56, 0, DateTimeKind.Utc)));
        // 21:30 UTC = ertesi gün 00:30 TSİ: TSİ takvim günü esas alınır.
        Assert.Equal(new DateTime(2026, 10, 31, 0, 30, 0, DateTimeKind.Utc),
            DataRetentionService.NextRunUtc(new DateTime(2026, 10, 30, 21, 30, 0, DateTimeKind.Utc)));
    }
}
