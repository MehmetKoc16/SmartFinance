using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SmartFinance.Domain.Entities;
using SmartFinance.Infrastructure.Context;
using SmartFinance.Infrastructure.Services;

namespace SmartFinance.Tests;

/// <summary>
/// Ödeme olmadan premium tanımlama, yetkilendirmeye açılan bir kapı: yanlış
/// yazılırsa ya herkes premium olur ya da Google Play inceleme hesabı içeriği
/// göremeyip uygulama reddedilir. Bu yüzden hem çalıştığı hem de YALNIZCA
/// listedeki hesap için çalıştığı ayrı ayrı doğrulanıyor.
/// </summary>
public class ComplimentaryPremiumTests
{
    private static SmartFinanceDbContext Baglam() =>
        new(new DbContextOptionsBuilder<SmartFinanceDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static IConfiguration Yapilandirma(params string[] epostalar) =>
        new ConfigurationBuilder().AddInMemoryCollection(
            epostalar.Select((e, i) =>
                new KeyValuePair<string, string?>($"Entitlement:ComplimentaryEmails:{i}", e))
        ).Build();

    private static User KullaniciEkle(SmartFinanceDbContext c, string eposta)
    {
        var u = new User { FullName = "Test", Email = eposta, PasswordHash = "x" };
        c.Users.Add(u);
        c.SaveChanges();
        return u;
    }

    [Fact]
    public async Task ListedekiHesap_AboneligiOlmadanPremiumSayilir()
    {
        var c = Baglam();
        var u = KullaniciEkle(c, "play-inceleme@walletmark.com.tr");
        var servis = new EntitlementService(c, new SabitKullanici(u.Id),
            Yapilandirma("play-inceleme@walletmark.com.tr"));

        Assert.True(await servis.IsPremiumAsync(u.Id));
    }

    [Fact]
    public async Task ListedeOlmayanHesap_PremiumSayilmaz()
    {
        var c = Baglam();
        KullaniciEkle(c, "play-inceleme@walletmark.com.tr");
        var baskasi = KullaniciEkle(c, "normal@ornek.com");
        var servis = new EntitlementService(c, new SabitKullanici(baskasi.Id),
            Yapilandirma("play-inceleme@walletmark.com.tr"));

        Assert.False(await servis.IsPremiumAsync(baskasi.Id));
    }

    /// Üretimde liste boş olacak. Boş listenin herkesi premium yapmadığından
    /// emin olmak gerekiyor — bu hata sessizce tüm ücretli katmanı kapatırdı.
    [Fact]
    public async Task ListeBossa_KimsePremiumSayilmaz()
    {
        var c = Baglam();
        var u = KullaniciEkle(c, "play-inceleme@walletmark.com.tr");
        var servis = new EntitlementService(c, new SabitKullanici(u.Id),
            new ConfigurationBuilder().Build());

        Assert.False(await servis.IsPremiumAsync(u.Id));
    }

    /// Türkçe büyük/küçük harf kuralı (I/İ) yüzünden kültüre duyarlı
    /// karşılaştırma yanlış sonuç verebiliyor; OrdinalIgnoreCase kullanıldığı
    /// doğrulanıyor.
    [Fact]
    public async Task EslesmeBuyukKucukHarfDuyarsizdir()
    {
        var c = Baglam();
        var u = KullaniciEkle(c, "Play-Inceleme@WalletMark.com.tr");
        var servis = new EntitlementService(c, new SabitKullanici(u.Id),
            Yapilandirma("play-inceleme@walletmark.com.tr"));

        Assert.True(await servis.IsPremiumAsync(u.Id));
    }

    private sealed class SabitKullanici : Application.Interfaces.ICurrentUserService
    {
        public SabitKullanici(int id) => UserId = id;
        public int UserId { get; }
    }
}
