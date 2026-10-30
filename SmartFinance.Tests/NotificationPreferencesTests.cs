using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using SmartFinance.API.Cli;
using SmartFinance.API.Controllers;
using SmartFinance.Application.DTOs.Notification;
using SmartFinance.Application.DTOs.Transaction;
using SmartFinance.Application.Interfaces;
using SmartFinance.Domain.Entities;
using SmartFinance.Domain.Enums;
using SmartFinance.Infrastructure.Context;
using SmartFinance.Infrastructure.Recurring;
using SmartFinance.Infrastructure.Repositories;
using SmartFinance.Infrastructure.Services;

namespace SmartFinance.Tests;

/// Bildirim turune gore tercihler (30.10.2026). KVKK 14.01.2026 duyurusu: operasyonel
/// bildirimler ile kampanya/duyuru bildirimleri tek onayda birlestirilemez. Tanitim
/// duyurulari 6563 sayili Kanun kapsaminda on onay (opt-in) ve onay tarihi ister.
public class NotificationPreferencesTests
{
    private static readonly DateTime PlanliSaat = new(2026, 10, 19, 6, 0, 0, DateTimeKind.Utc);

    /// Gonderilenleri kaydeden sahte FCM (PushNotificationTests kalibi).
    private sealed class SahteGonderici : IPushSender
    {
        public List<(IReadOnlyList<string> Tokens, PushMessage Mesaj)> Gonderilen { get; } = new();

        public Task<IReadOnlyList<string>> SendAsync(IReadOnlyList<string> tokens, PushMessage message, CancellationToken ct = default)
        {
            Gonderilen.Add((tokens, message));
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }
    }

    private sealed class Ortam : IDisposable
    {
        public string DbAdi { get; } = Guid.NewGuid().ToString();
        public SmartFinanceDbContext Db { get; }
        public HttpContextAccessor Accessor { get; } = new();
        public SahteGonderici Push { get; } = new();
        public NotificationService Bildirimler { get; }

        public Ortam()
        {
            Db = YeniDb(DbAdi);
            Bildirimler = new NotificationService(Db, new CurrentUserService(Accessor), Push);
        }

        public static SmartFinanceDbContext YeniDb(string ad) => new(new DbContextOptionsBuilder<SmartFinanceDbContext>()
            .UseInMemoryDatabase(ad).Options);

        public void OturumAc(int userId)
        {
            var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) });
            Accessor.HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
        }

        public User Kullanici(Action<User>? ayar = null, string? token = null)
        {
            var u = new User { FullName = "Deneme", Email = $"{Guid.NewGuid()}@example.invalid", PasswordHash = "x" };
            ayar?.Invoke(u);
            Db.Users.Add(u);
            Db.SaveChanges();
            if (token != null)
            {
                Db.DeviceTokens.Add(new DeviceToken { UserId = u.Id, Token = token });
                Db.SaveChanges();
            }
            return u;
        }

        public void Dispose() => Db.Dispose();
    }

    // ── GET / PUT /api/Notification/preferences ──

    // NEDEN: Mobil sozlesme: varsayilan butce ve planli acik, duyuru KAPALI (opt-in);
    // alan adlari camelCase budgetAlerts/recurringReminders/announcements.
    [Fact]
    public async Task Tercihler_Varsayilan_ButceVePlanliAcik_DuyuruKapali()
    {
        using var o = new Ortam();
        var u = o.Kullanici();
        o.OturumAc(u.Id);

        var ok = Assert.IsType<OkObjectResult>(await new NotificationController(o.Bildirimler).GetPreferences());
        var dto = Assert.IsType<NotificationPreferencesDto>(ok.Value);

        Assert.True(dto.BudgetAlerts);
        Assert.True(dto.RecurringReminders);
        Assert.False(dto.Announcements);
        Assert.Equal("{\"budgetAlerts\":true,\"recurringReminders\":true,\"announcements\":false}",
            JsonSerializer.Serialize(dto, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    // NEDEN: Mobil ajan yolu "api/Notification/preferences" olarak cagiriyor; route degisirse istemci 404 alir.
    [Fact]
    public void Tercihler_Yolu_MevcutNotificationKokunde()
    {
        var kok = typeof(NotificationController).GetCustomAttribute<RouteAttribute>()!;
        Assert.Equal("api/[controller]", kok.Template);
        Assert.Equal("preferences", typeof(NotificationController)
            .GetMethod(nameof(NotificationController.GetPreferences))!.GetCustomAttribute<HttpGetAttribute>()!.Template);
        Assert.Equal("preferences", typeof(NotificationController)
            .GetMethod(nameof(NotificationController.UpdatePreferences))!.GetCustomAttribute<HttpPutAttribute>()!.Template);
        Assert.NotNull(typeof(NotificationController).GetCustomAttribute<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>());
    }

    // NEDEN: PUT'ta null/gonderilmeyen alan "degistirme" demek; eski istemci bos govde
    // gonderse bile kullanicinin kapattigi tercih acilmamali.
    [Fact]
    public async Task Tercih_KismiGuncelleme_YalnizGonderilenAlanDegisir()
    {
        using var o = new Ortam();
        var u = o.Kullanici();
        o.OturumAc(u.Id);
        var controller = new NotificationController(o.Bildirimler);

        var ok = Assert.IsType<OkObjectResult>(await controller.UpdatePreferences(
            new UpdateNotificationPreferencesDto { BudgetAlerts = false }));
        var ilk = Assert.IsType<NotificationPreferencesDto>(ok.Value);
        Assert.False(ilk.BudgetAlerts);
        Assert.True(ilk.RecurringReminders);
        Assert.False(ilk.Announcements);

        var ikinci = await o.Bildirimler.UpdatePreferencesAsync(new() { RecurringReminders = false });
        Assert.False(ikinci.BudgetAlerts);
        Assert.False(ikinci.RecurringReminders);

        var bos = await o.Bildirimler.UpdatePreferencesAsync(new());
        Assert.False(bos.BudgetAlerts);
        Assert.False(bos.RecurringReminders);
        Assert.False(bos.Announcements);

        using var okuma = Ortam.YeniDb(o.DbAdi);
        var kayit = okuma.Users.Single(x => x.Id == u.Id);
        Assert.False(kayit.NotifyBudgetAlerts);
        Assert.False(kayit.NotifyRecurringReminders);
        Assert.False(kayit.NotifyAnnouncements);
    }

    // NEDEN: 6563 sayili Kanun onayin ve geri cekmenin tarihini ister. Ayni degerle
    // tekrar gelen istek ya da baska alan guncellemesi tarihleri kaydirmamali.
    [Fact]
    public async Task DuyuruOnayi_AcilincaOnayTarihi_KapatilincaGeriCekmeTarihiYazilir()
    {
        using var o = new Ortam();
        var u = o.Kullanici();
        o.OturumAc(u.Id);

        var once = DateTime.UtcNow;
        var acik = await o.Bildirimler.UpdatePreferencesAsync(new() { Announcements = true });
        Assert.True(acik.Announcements);
        var onay = u.AnnouncementsConsentAt;
        Assert.NotNull(onay);
        Assert.InRange(onay!.Value, once, DateTime.UtcNow);
        Assert.Null(u.AnnouncementsOptOutAt);

        await o.Bildirimler.UpdatePreferencesAsync(new() { Announcements = true });
        await o.Bildirimler.UpdatePreferencesAsync(new() { BudgetAlerts = false });
        Assert.Equal(onay, u.AnnouncementsConsentAt);
        Assert.Null(u.AnnouncementsOptOutAt);

        var onceKapat = DateTime.UtcNow;
        var kapali = await o.Bildirimler.UpdatePreferencesAsync(new() { Announcements = false });
        Assert.False(kapali.Announcements);
        var geriCekme = u.AnnouncementsOptOutAt;
        Assert.NotNull(geriCekme);
        Assert.InRange(geriCekme!.Value, onceKapat, DateTime.UtcNow);
        Assert.Equal(onay, u.AnnouncementsConsentAt);

        await o.Bildirimler.UpdatePreferencesAsync(new() { Announcements = false });
        Assert.Equal(geriCekme, u.AnnouncementsOptOutAt);

        using var okuma = Ortam.YeniDb(o.DbAdi);
        var kayit = okuma.Users.Single(x => x.Id == u.Id);
        Assert.Equal(onay, kayit.AnnouncementsConsentAt);
        Assert.Equal(geriCekme, kayit.AnnouncementsOptOutAt);
    }

    // ── Gonderim kurallari ──

    // NEDEN: Butce uyarisi kullanicinin kendi verisi; tercih yalnizca push kanalini kapatir,
    // uygulama ici kayit her durumda olusur.
    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public async Task ButceTercihiKapaliyken_PushGitmez_UygulamaIciBildirimOlusur(bool tercih, int beklenenPush)
    {
        using var o = new Ortam();
        var u = o.Kullanici(x => x.NotifyBudgetAlerts = tercih, "ali-tel");
        o.OturumAc(u.Id);
        var kategori = new Category { Name = "Market", Type = TransactionType.Expense, UserId = u.Id };
        o.Db.Categories.Add(kategori);
        o.Db.SaveChanges();
        o.Db.Budgets.Add(new Budget { UserId = u.Id, CategoryId = kategori.Id, MonthlyLimit = 1000m });
        o.Db.SaveChanges();
        var islemler = new TransactionService(new GenericRepository<Transaction>(o.Db), o.Db,
            new CurrentUserService(o.Accessor), o.Bildirimler);

        await islemler.CreateTransactionAsync(new CreateTransactionDto
        {
            Amount = 1200m, Description = "Market alışverişi", TransactionDate = DateTime.UtcNow,
            Type = TransactionType.Expense, CategoryId = kategori.Id,
        });

        Assert.Equal(beklenenPush, o.Push.Gonderilen.Count);
        var n = Assert.Single(o.Db.Notifications.Where(x => x.UserId == u.Id));
        Assert.Equal(NotificationType.BudgetExceeded, n.Type);
    }

    // NEDEN: Planli odeme hatirlatmasi icin ayni kural: tercih kapaliysa push yok, kayit var.
    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public async Task PlanliOdemeTercihiKapaliyken_PushGitmez_UygulamaIciBildirimOlusur(bool tercih, int beklenenPush)
    {
        using var o = new RecurringPaymentServiceTests.Ortam();
        o.Db.Users.Add(new User
        {
            Id = 1, FullName = "Deneme", Email = "dummy@example.invalid", PasswordHash = "deneme",
            NotifyRecurringReminders = tercih,
        });
        o.Db.DeviceTokens.Add(new DeviceToken { UserId = 1, Token = "deneme-token" });
        await o.Db.SaveChangesAsync();
        await o.Kayit(); // vade bugun
        var push = new SahteGonderici();

        await new RecurringReminderRunner(o.Db, push).RunOnceAsync(PlanliSaat, default);

        Assert.Equal(beklenenPush, push.Gonderilen.Count);
        Assert.Equal(NotificationType.RecurringDue, Assert.Single(o.Db.Notifications).Type);
    }

    // NEDEN: Tanitim duyurusu (ornegin "Wallet Mark yayında!") on onay vermeyene ne
    // uygulama ici ne push olarak gitmemeli; onay verene ikisi de gitmeli.
    [Fact]
    public async Task Duyuru_OnaysizKullaniciyaGitmez_OnayVerenGider()
    {
        using var o = new Ortam();
        var onayli = o.Kullanici(x => x.NotifyAnnouncements = true, "onayli-tel");
        o.Kullanici(token: "onaysiz-tel");

        var sonuc = await o.Bildirimler.BroadcastAsync("yayin-1", "Wallet Mark yayında!", "Hemen deneyin.");

        Assert.Equal(1, sonuc.NotifiedUsers);
        Assert.Equal(1, sonuc.NotOptedInUsers);
        Assert.False(sonuc.Mandatory);
        Assert.Equal([onayli.Id], o.Db.Notifications.Select(n => n.UserId));
        var (tokens, _) = Assert.Single(o.Push.Gonderilen);
        Assert.Equal(["onayli-tel"], tokens);
        // Bayrak yokken komut sonucu kac onayli kullaniciya gittigini soyler.
        Assert.Contains("onay veren 1 kullanıcıya",
            DuyuruKomutu.SonucMetni(new DuyuruKomutu.Istek("yayin-1", "b", "m", null), sonuc));
    }

    // NEDEN: Guvenlik/veri ihlali/hukuki bildirim tercihten bagimsiz herkese ulasmali.
    [Fact]
    public async Task ZorunluDuyuru_TercihYokSayilir_HerkeseGider()
    {
        using var o = new Ortam();
        o.Kullanici(x => x.NotifyAnnouncements = true, "onayli-tel");
        o.Kullanici(token: "onaysiz-tel");

        var sonuc = await o.Bildirimler.BroadcastAsync("guvenlik-1", "Güvenlik bildirimi", "Şifrenizi yenileyin.", zorunlu: true);

        Assert.Equal(2, sonuc.NotifiedUsers);
        Assert.Equal(0, sonuc.NotOptedInUsers);
        Assert.True(sonuc.Mandatory);
        Assert.Equal(2, o.Db.Notifications.Count());
        Assert.Equal(["onayli-tel", "onaysiz-tel"], o.Push.Gonderilen.SelectMany(g => g.Tokens).OrderBy(t => t));
    }

    // NEDEN: --yalnizca ile deneme gonderimi de onay kuralina uymali; gonderilmediyse
    // yonetici bunu sonuc satirindan anlamali.
    [Fact]
    public async Task YalnizcaEposta_OnaysizKullanici_GonderilmezVeSonuctaBelirtilir()
    {
        using var o = new Ortam();
        var u = o.Kullanici(token: "tel");

        var sonuc = await o.Bildirimler.BroadcastAsync("deneme-1", "Başlık", "Metin", onlyEmail: u.Email);

        Assert.Equal(0, sonuc.NotifiedUsers);
        Assert.Equal(1, sonuc.NotOptedInUsers);
        Assert.Empty(o.Db.Notifications);
        Assert.Empty(o.Push.Gonderilen);
        Assert.Contains("onay vermemiş, gönderilmedi",
            DuyuruKomutu.SonucMetni(new DuyuruKomutu.Istek("deneme-1", "Başlık", "Metin", u.Email), sonuc));

        var zorunlu = await o.Bildirimler.BroadcastAsync("deneme-2", "Başlık", "Metin", onlyEmail: u.Email, zorunlu: true);
        Assert.Equal(1, zorunlu.NotifiedUsers);
        Assert.Single(o.Db.Notifications);
    }

    // ── EF sentinel tuzagi ──

    // NEDEN: HasDefaultValue(true) olsaydi EF false'u "deger yok" sayip INSERT'ten cikarir,
    // SQL Server varsayilani (true) yazilirdi (v16 RemindDaysBefore=0 -> 1 hatasi).
    // InMemory bu davranisi gostermez; bu yuzden SQL Server saglayicisiyla kurulan
    // modelde varsayilan deger/uretim olmadigi da dogrulaniyor (baglanti acilmaz).
    [Fact]
    public void YeniKullanici_TercihKolonlarindaVeritabaniVarsayilaniYok()
    {
        using var db = new SmartFinanceDbContext(new DbContextOptionsBuilder<SmartFinanceDbContext>()
            .UseSqlServer().Options);
        var tip = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(User))!;
        foreach (var ad in new[] { nameof(User.NotifyBudgetAlerts), nameof(User.NotifyRecurringReminders), nameof(User.NotifyAnnouncements) })
        {
            var p = tip.FindProperty(ad)!;
            Assert.Null(p.FindAnnotation(RelationalAnnotationNames.DefaultValue));
            Assert.Equal(ValueGenerated.Never, p.ValueGenerated);
        }
    }

    // NEDEN: false ile kaydedilen tercih false kalmali, true yapilinca true kalmali.
    [Fact]
    public void YeniKullanici_FalseKaydedilenTercihFalseKalir_TrueYapilincaTrueKalir()
    {
        var ad = Guid.NewGuid().ToString();
        int id;
        using (var db = Ortam.YeniDb(ad))
        {
            var u = new User
            {
                FullName = "Deneme", Email = "sentinel@example.invalid", PasswordHash = "x",
                NotifyBudgetAlerts = false, NotifyRecurringReminders = false, NotifyAnnouncements = false,
            };
            db.Users.Add(u);
            db.SaveChanges();
            id = u.Id;
        }
        using (var db = Ortam.YeniDb(ad))
        {
            var u = db.Users.Single(x => x.Id == id);
            Assert.False(u.NotifyBudgetAlerts);
            Assert.False(u.NotifyRecurringReminders);
            Assert.False(u.NotifyAnnouncements);
            u.NotifyAnnouncements = true;
            db.SaveChanges();
        }
        using (var db = Ortam.YeniDb(ad))
        {
            Assert.True(db.Users.Single(x => x.Id == id).NotifyAnnouncements);
        }
    }
}
