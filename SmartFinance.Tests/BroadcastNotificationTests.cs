using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using SmartFinance.Application.Interfaces;
using SmartFinance.Domain.Entities;
using SmartFinance.Domain.Enums;
using SmartFinance.Infrastructure.Context;
using SmartFinance.Infrastructure.Services;

namespace SmartFinance.Tests;

/// Tum kullanicilara duyuru (yeni surum, gizlilik politikasi guncellemesi).
/// Gizlilik politikasi "onemli degisiklikler uygulama icinde duyurulur" diyor;
/// bildirimleri yalnizca butce asimi olusturuyordu (28.09.2026).
public class BroadcastNotificationTests
{
    private class SahteGonderici : IPushSender
    {
        public List<IReadOnlyList<string>> Gruplar { get; } = new();
        public HashSet<string> Gecersiz { get; } = new();
        public bool Patla { get; set; }

        public Task<IReadOnlyList<string>> SendAsync(IReadOnlyList<string> tokens, PushMessage message, CancellationToken ct = default)
        {
            if (Patla) throw new HttpRequestException("FCM ulasilamiyor");
            Gruplar.Add(tokens);
            return Task.FromResult<IReadOnlyList<string>>(tokens.Where(Gecersiz.Contains).ToList());
        }
    }

    private static (NotificationService servis, SmartFinanceDbContext db, SahteGonderici fcm) Kur()
    {
        var db = new SmartFinanceDbContext(new DbContextOptionsBuilder<SmartFinanceDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var fcm = new SahteGonderici();
        // Komut satirindan calisiyor: oturum acmis kullanici yok.
        var servis = new NotificationService(db, new CurrentUserService(new HttpContextAccessor()), fcm);
        return (servis, db, fcm);
    }

    private static User Kullanici(SmartFinanceDbContext db, params string[] tokenlar)
    {
        var u = new User { FullName = "Test", Email = $"{Guid.NewGuid()}@test.com", PasswordHash = "x" };
        db.Users.Add(u);
        db.SaveChanges();
        foreach (var t in tokenlar) db.DeviceTokens.Add(new DeviceToken { UserId = u.Id, Token = t });
        db.SaveChanges();
        return u;
    }

    [Fact]
    public async Task Duyuru_HerKullaniciyaBirBilgilendirmeBildirimiOlusturur()
    {
        var (servis, db, _) = Kur();
        var ali = Kullanici(db);
        var ayse = Kullanici(db);

        var sonuc = await servis.BroadcastAsync("surum11", "Yeni sürüm yayında", "Play Store'dan güncelleyin.");

        Assert.Equal(2, sonuc.NotifiedUsers);
        foreach (var u in new[] { ali, ayse })
        {
            var n = Assert.Single(db.Notifications.Where(x => x.UserId == u.Id));
            Assert.Equal(NotificationType.Info, n.Type);
            Assert.Equal("Yeni sürüm yayında", n.Title);
            Assert.Equal("Play Store'dan güncelleyin.", n.Message);
            Assert.False(n.IsRead);
        }
    }

    /// Komut yanlislikla iki kez calistirilirsa kimse ikinci bildirimi almamali.
    [Fact]
    public async Task AyniAnahtarlaTekrar_KimseyeIkinciKezGitmez()
    {
        var (servis, db, fcm) = Kur();
        Kullanici(db, "tel-1");

        await servis.BroadcastAsync("surum11", "Başlık", "Metin");
        var ikinci = await servis.BroadcastAsync("surum11", "Başlık", "Metin");

        Assert.Equal(0, ikinci.NotifiedUsers);
        Assert.Equal(1, ikinci.AlreadyNotifiedUsers);
        Assert.Single(db.Notifications);
        Assert.Single(fcm.Gruplar); // ikinci calistirmada push da yok
    }

    [Fact]
    public async Task FarkliAnahtar_YeniDuyuruSayilir()
    {
        var (servis, db, _) = Kur();
        Kullanici(db);

        await servis.BroadcastAsync("surum11", "Başlık", "Metin");
        await servis.BroadcastAsync("surum12", "Başlık", "Metin");

        Assert.Equal(2, db.Notifications.Count());
    }

    [Fact]
    public async Task Push_TumCihazlaraBesYuzerlikGruplarlaGider()
    {
        var (servis, db, fcm) = Kur();
        // 501 cihaz: FCM tek istekte en fazla 500 kabul ediyor.
        for (var i = 0; i < 51; i++)
            Kullanici(db, Enumerable.Range(0, i < 50 ? 10 : 1).Select(j => $"tel-{i}-{j}").ToArray());

        var sonuc = await servis.BroadcastAsync("surum11", "Başlık", "Metin");

        Assert.Equal(501, sonuc.PushTargets);
        Assert.Equal([NotificationService.PushBatchSize, 1], fcm.Gruplar.Select(g => g.Count));
        Assert.Equal(501, fcm.Gruplar.SelectMany(g => g).Distinct().Count());
    }

    [Fact]
    public async Task FcmGecersizDedigiCihaz_Silinir()
    {
        var (servis, db, fcm) = Kur();
        Kullanici(db, "eski-tel", "yeni-tel");
        fcm.Gecersiz.Add("eski-tel");

        var sonuc = await servis.BroadcastAsync("surum11", "Başlık", "Metin");

        Assert.Equal(1, sonuc.InvalidTokensRemoved);
        Assert.Equal(["yeni-tel"], db.DeviceTokens.Select(t => t.Token));
    }

    /// Push ek kanal: FCM'ye ulasilamazsa uygulama ici bildirimler yine kalir.
    [Fact]
    public async Task FcmHataVerirse_UygulamaIciBildirimlerYineKaydedilir()
    {
        var (servis, db, fcm) = Kur();
        Kullanici(db, "tel-1");
        Kullanici(db);
        fcm.Patla = true;

        var sonuc = await servis.BroadcastAsync("surum11", "Başlık", "Metin");

        Assert.Equal(2, sonuc.NotifiedUsers);
        Assert.Equal(2, db.Notifications.Count());
    }

    /// Herkese gondermeden once tek hesapta denemek icin.
    [Fact]
    public async Task YalnizcaVerilenEposta_SadeceOKullaniciyaGider()
    {
        var (servis, db, fcm) = Kur();
        var deneme = Kullanici(db, "deneme-tel");
        Kullanici(db, "baska-tel");

        var sonuc = await servis.BroadcastAsync("surum11-deneme", "Başlık", "Metin", onlyEmail: deneme.Email.ToUpperInvariant());

        Assert.Equal(1, sonuc.NotifiedUsers);
        Assert.Equal([deneme.Id], db.Notifications.Select(n => n.UserId));
        Assert.Equal(["deneme-tel"], fcm.Gruplar.Single());
    }

    [Fact]
    public async Task YalnizcaVerilenEpostaBulunamazsa_HataVerir()
    {
        var (servis, db, _) = Kur();
        Kullanici(db);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            servis.BroadcastAsync("surum11", "Başlık", "Metin", onlyEmail: "yok@test.com"));
        Assert.Empty(db.Notifications);
    }

    [Theory]
    [InlineData("", "Başlık", "Metin")]
    [InlineData("surum11", "", "Metin")]
    [InlineData("surum11", "Başlık", " ")]
    public async Task BosAlan_Reddedilir(string anahtar, string baslik, string metin)
    {
        var (servis, db, _) = Kur();
        Kullanici(db);

        await Assert.ThrowsAsync<ArgumentException>(() => servis.BroadcastAsync(anahtar, baslik, metin));
        Assert.Empty(db.Notifications);
    }

    [Fact]
    public async Task VeritabaniSinirindanUzun_Reddedilir()
    {
        var (servis, db, _) = Kur();
        Kullanici(db);

        await Assert.ThrowsAsync<ArgumentException>(() => servis.BroadcastAsync("k", new string('a', 201), "Metin"));
        await Assert.ThrowsAsync<ArgumentException>(() => servis.BroadcastAsync("k", "Başlık", new string('a', 1001)));
        await Assert.ThrowsAsync<ArgumentException>(() => servis.BroadcastAsync(new string('a', 90), "Başlık", "Metin"));
    }
}
