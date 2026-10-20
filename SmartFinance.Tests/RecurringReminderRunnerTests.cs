using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SmartFinance.Application.Interfaces;
using SmartFinance.Domain.Entities;
using SmartFinance.Domain.Enums;
using SmartFinance.Infrastructure.Context;
using SmartFinance.Infrastructure.Recurring;

namespace SmartFinance.Tests;

public class RecurringReminderRunnerTests
{
    private static readonly DateTime Now = new(2026, 10, 19, 6, 0, 0, DateTimeKind.Utc);
    private sealed class SahtePush : IPushSender
    {
        public bool Hata { get; init; }
        public List<PushMessage> Messages { get; } = new();
        public Task<IReadOnlyList<string>> SendAsync(IReadOnlyList<string> tokens, PushMessage message, CancellationToken ct = default)
        {
            Messages.Add(message);
            if (Hata) throw new InvalidOperationException("Deneme push hatası");
            return Task.FromResult<IReadOnlyList<string>>(tokens);
        }
    }
    private static async Task Hazirla(RecurringPaymentServiceTests.Ortam o)
    {
        o.Db.Users.Add(new User { Id = 1, FullName = "Deneme", Email = "dummy@example.invalid", PasswordHash = "deneme" });
        await o.Db.SaveChangesAsync();
    }

    // NEDEN: Bir gün önceki mesaj doğal Türkçe ve sabit para biçimi kullanmalı.
    [Fact]
    public async Task Hatirlatma_BirGunOnce_Yarin()
    {
        using var o = new RecurringPaymentServiceTests.Ortam(); await Hazirla(o); var p = await o.Kayit(1);
        p.Amount = 1234.50m; await o.Db.SaveChangesAsync(); await new RecurringReminderRunner(o.Db).RunOnceAsync(Now, default);
        var n = Assert.Single(o.Db.Notifications); Assert.Equal("Yaklaşan ödeme", n.Title);
        Assert.Equal("Deneme planı: ₺1.234,50 — yarın (20.10)", n.Message);
        Assert.Equal($"planli:{p.Id}:2026-10-20:once", n.DedupeKey); Assert.Equal(NotificationType.RecurringDue, n.Type);
    }

    // NEDEN: Hatırlatma aralığı sıfır olsa da vade günü bildirimi gitmeli.
    [Theory]
    [InlineData(false, "Bugün ödenecek")]
    [InlineData(true, "Bugün beklenen gelir")]
    public async Task Hatirlatma_OdemeGunu_Bugun(bool income, string title)
    {
        using var o = new RecurringPaymentServiceTests.Ortam(); await Hazirla(o); var p = await o.Kayit();
        p.RemindDaysBefore = 0; p.Type = income ? TransactionType.Income : TransactionType.Expense; await o.Db.SaveChangesAsync();
        await new RecurringReminderRunner(o.Db).RunOnceAsync(Now, default);
        var n = Assert.Single(o.Db.Notifications); Assert.Equal(title, n.Title);
        Assert.Equal("Deneme planı: ₺17,25", n.Message); Assert.EndsWith(":gun", n.DedupeKey!);
    }

    // NEDEN: Yeniden başlama ve silinmiş bildirim aynı anahtarı tekrar üretmemeli.
    [Fact]
    public async Task IkiKezCalisma_CiftBildirimUretmez()
    {
        using var o = new RecurringPaymentServiceTests.Ortam(); await Hazirla(o); await o.Kayit(1);
        var runner = new RecurringReminderRunner(o.Db); await runner.RunOnceAsync(Now, default);
        o.Db.Notifications.Single().IsDeleted = true; await o.Db.SaveChangesAsync();
        await runner.RunOnceAsync(Now.AddHours(1), default); Assert.Single(o.Db.Notifications.IgnoreQueryFilters());
    }

    // NEDEN: Gecikmiş vade her gün kullanıcıyı tekrar rahatsız etmemeli.
    [Fact]
    public async Task Gecikmis_BildirimUretmez()
    {
        using var o = new RecurringPaymentServiceTests.Ortam(); await Hazirla(o); await o.Kayit(-1);
        await new RecurringReminderRunner(o.Db).RunOnceAsync(Now, default); Assert.Empty(o.Db.Notifications);
    }

    // NEDEN: Pasif, silinmiş plan ve silinmiş kullanıcı bildirime dahil olmamalı.
    [Fact]
    public async Task Pasif_VeSilinmis_BildirimUretmez()
    {
        using var o = new RecurringPaymentServiceTests.Ortam(); await Hazirla(o); await o.Kayit(0, false);
        var p = await o.Kayit(); p.IsDeleted = true;
        var other = await o.Kayit(); other.UserId = 2;
        o.Db.Users.Add(new User { Id = 2, IsDeleted = true, FullName = "Deneme", Email = "other@example.invalid", PasswordHash = "deneme" });
        await o.Db.SaveChangesAsync(); await new RecurringReminderRunner(o.Db).RunOnceAsync(Now, default);
        Assert.Empty(o.Db.Notifications);
    }

    // NEDEN: Push kaydı olmayan ortam uygulama içi kanalı kullanabilmeli.
    [Fact]
    public async Task PushYoksa_YalnizUygulamaIciBildirim()
    {
        using var o = new RecurringPaymentServiceTests.Ortam(); await Hazirla(o); await o.Kayit();
        await new RecurringReminderRunner(o.Db).RunOnceAsync(Now, default); Assert.Single(o.Db.Notifications);
    }

    // NEDEN: Push hatası önceden kaydedilen bildirimi geri almamalı.
    [Fact]
    public async Task PushHatasi_BildirimKaydiKalir()
    {
        using var o = new RecurringPaymentServiceTests.Ortam(); await Hazirla(o); await o.Kayit();
        o.Db.DeviceTokens.Add(new DeviceToken { UserId = 1, Token = "deneme-token" }); await o.Db.SaveChangesAsync();
        var push = new SahtePush { Hata = true }; await new RecurringReminderRunner(o.Db, push).RunOnceAsync(Now, default);
        var n = Assert.Single(o.Db.Notifications); var m = Assert.Single(push.Messages);
        Assert.Equal(n.Id.ToString(), m.Data["notificationId"]); Assert.Equal("notification", m.Data["type"]);
        Assert.Single(o.Db.DeviceTokens);
    }

    // NEDEN: Başarılı push dönüşündeki geçersiz cihazlar temizlenmeli.
    [Fact]
    public async Task Push_GecersizTokenSilinir()
    {
        using var o = new RecurringPaymentServiceTests.Ortam(); await Hazirla(o); await o.Kayit();
        o.Db.DeviceTokens.Add(new DeviceToken { UserId = 1, Token = "deneme-token" }); await o.Db.SaveChangesAsync();
        await new RecurringReminderRunner(o.Db, new SahtePush()).RunOnceAsync(Now, default);
        Assert.Empty(o.Db.DeviceTokens); Assert.Single(o.Db.Notifications);
    }

    // NEDEN: Sonraki çalışma işletim sistemi saat diliminden bağımsız 09:00 TSİ olmalı;
    // açılış telafi penceresi 09:00 dahil–23:00 hariç TSİ (madde 9d, 20.10.2026).
    [Fact]
    public void SonrakiCalisma_09Tsi_VeAcilisPenceresi()
    {
        Assert.Equal(Now, RecurringReminderService.NextRunUtc(Now.AddMinutes(-1)));
        Assert.Equal(Now.AddDays(1), RecurringReminderService.NextRunUtc(Now));
        Assert.True(RecurringReminderService.ShouldRunOnStartup(Now));
        Assert.True(RecurringReminderService.ShouldRunOnStartup(Now.AddHours(11)));
        Assert.True(RecurringReminderService.ShouldRunOnStartup(Now.AddHours(13).AddMinutes(59)));
        Assert.False(RecurringReminderService.ShouldRunOnStartup(Now.AddHours(14)));
        Assert.False(RecurringReminderService.ShouldRunOnStartup(Now.AddMinutes(-1)));
    }

    // NEDEN: Ön hatırlatma vade günü bildirimini engellememeli; gelir başlığı farklı olmalı.
    [Fact]
    public async Task Hatirlatma_GelirYediGunOnce_VeVadeGunuAyriAnahtar()
    {
        using var o = new RecurringPaymentServiceTests.Ortam(); await Hazirla(o); var p = await o.Kayit(7);
        p.Type = TransactionType.Income; p.RemindDaysBefore = 7; await o.Db.SaveChangesAsync();
        var runner = new RecurringReminderRunner(o.Db); await runner.RunOnceAsync(Now, default);
        var first = Assert.Single(o.Db.Notifications);
        Assert.Equal("Yaklaşan gelir", first.Title); Assert.Contains("7 gün sonra (26.10)", first.Message);
        await runner.RunOnceAsync(Now.AddDays(7), default);
        Assert.Equal(2, o.Db.Notifications.Count());
    }

    // ---- v16 yayın öncesi düzeltmeler (20.10.2026) ----

    // Seçilen kayıt anında SaveChanges'i düşürür; InMemory'de gerçek DB hatasının yerine geçer.
    private sealed class BozukKayit : SaveChangesInterceptor
    {
        public Func<DbContext, bool>? Kosul { get; set; }
        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            Kontrol(eventData.Context); return base.SavingChanges(eventData, result);
        }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Kontrol(eventData.Context); return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
        private void Kontrol(DbContext? context)
        {
            if (context != null && Kosul != null && Kosul(context)) throw new DbUpdateException("Deneme kayıt hatası");
        }
    }

    private static SmartFinanceDbContext BozulabilirDb(BozukKayit interceptor) => new(new DbContextOptionsBuilder<SmartFinanceDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
        .AddInterceptors(interceptor).Options);

    private static RecurringPayment YarinkiPlan() => new()
    {
        UserId = 1, Name = "Deneme planı", Amount = 17.25m, Type = TransactionType.Expense,
        Frequency = RecurrenceFrequency.Weekly, StartDate = new DateOnly(2026, 10, 20),
        NextDueDate = new DateOnly(2026, 10, 20), AnchorDay = 20, IsActive = true, RemindDaysBefore = 1
    };

    // NEDEN: Tek bir planın kayıt hatası o turdaki diğer kullanıcıların hatırlatmasını düşürmemeli.
    [Fact]
    public async Task Hatirlatma_BirPlanHataVerirse_DigerleriDevamEder()
    {
        var interceptor = new BozukKayit();
        using var db = BozulabilirDb(interceptor);
        db.Users.Add(new User { Id = 1, FullName = "Deneme", Email = "dummy@example.invalid", PasswordHash = "deneme" });
        var bozuk = YarinkiPlan(); var saglam = YarinkiPlan();
        db.RecurringPayments.Add(bozuk); await db.SaveChangesAsync();
        db.RecurringPayments.Add(saglam); await db.SaveChangesAsync();
        Assert.True(bozuk.Id < saglam.Id); // Runner Id sırasıyla işler; hatalı plan önce gelir.
        var prefix = $"planli:{bozuk.Id}:";
        interceptor.Kosul = c => c.ChangeTracker.Entries<Notification>()
            .Any(e => e.State == EntityState.Added && e.Entity.DedupeKey != null && e.Entity.DedupeKey.StartsWith(prefix));

        await new RecurringReminderRunner(db).RunOnceAsync(Now, default);

        var n = Assert.Single(db.Notifications.AsNoTracking());
        Assert.Equal($"planli:{saglam.Id}:2026-10-20:once", n.DedupeKey);
    }

    // NEDEN: Geçersiz token temizliği düşerse izleyicide kalan silme kaydı sonraki planların kaydını bozmamalı.
    [Fact]
    public async Task Push_TokenTemizligiHataVerirse_SonrakiPlanlarKaydedilir()
    {
        var interceptor = new BozukKayit();
        using var db = BozulabilirDb(interceptor);
        db.Users.Add(new User { Id = 1, FullName = "Deneme", Email = "dummy@example.invalid", PasswordHash = "deneme" });
        db.RecurringPayments.AddRange(YarinkiPlan(), YarinkiPlan());
        db.DeviceTokens.Add(new DeviceToken { UserId = 1, Token = "deneme-token" });
        await db.SaveChangesAsync();
        interceptor.Kosul = c => c.ChangeTracker.Entries<DeviceToken>().Any(e => e.State == EntityState.Deleted);

        var push = new SahtePush();
        await new RecurringReminderRunner(db, push).RunOnceAsync(Now, default);

        Assert.Equal(2, db.Notifications.AsNoTracking().Count());
        Assert.Equal(2, push.Messages.Count);
        Assert.Single(db.DeviceTokens.AsNoTracking());
    }

    // NEDEN: Push kilit ekranında görünür ve kullanıcı 'Tutarları gizle' açmış olabilir;
    // tutar yalnız uygulama içi bildirimde kalmalı.
    [Fact]
    public async Task Push_MetnindeTutarYok_UygulamaIciMesajdaVar()
    {
        using var o = new RecurringPaymentServiceTests.Ortam(); await Hazirla(o); var p = await o.Kayit(1);
        p.Amount = 1234.50m; o.Db.DeviceTokens.Add(new DeviceToken { UserId = 1, Token = "deneme-token" });
        await o.Db.SaveChangesAsync();
        var push = new SahtePush();
        await new RecurringReminderRunner(o.Db, push).RunOnceAsync(Now, default);

        var m = Assert.Single(push.Messages);
        Assert.Equal("Yaklaşan ödeme", m.Title);
        Assert.Equal("Deneme planı — yarın (20.10)", m.Body);
        Assert.DoesNotContain("₺", m.Body); Assert.DoesNotContain("1.234", m.Body);
        Assert.Equal("Deneme planı: ₺1.234,50 — yarın (20.10)", Assert.Single(o.Db.Notifications).Message);
    }

    // NEDEN: Vade günü push'u da tutarsız olmalı.
    [Fact]
    public async Task Push_VadeGunu_TutarYok()
    {
        using var o = new RecurringPaymentServiceTests.Ortam(); await Hazirla(o); await o.Kayit();
        o.Db.DeviceTokens.Add(new DeviceToken { UserId = 1, Token = "deneme-token" }); await o.Db.SaveChangesAsync();
        var push = new SahtePush();
        await new RecurringReminderRunner(o.Db, push).RunOnceAsync(Now, default);
        Assert.Equal("Deneme planı — bugün", Assert.Single(push.Messages).Body);
        Assert.Equal("Deneme planı: ₺17,25", Assert.Single(o.Db.Notifications).Message);
    }
}
