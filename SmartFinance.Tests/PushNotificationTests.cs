using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using SmartFinance.Application.DTOs.Notification;
using SmartFinance.Application.DTOs.Transaction;
using SmartFinance.Application.Interfaces;
using SmartFinance.Domain.Entities;
using SmartFinance.Domain.Enums;
using SmartFinance.Infrastructure.Context;
using SmartFinance.Infrastructure.Repositories;
using SmartFinance.Infrastructure.Services;

namespace SmartFinance.Tests;

/// Butce asimi gibi bildirimler uygulama icinde olusuyordu ama kullanici
/// uygulamayi acmadikca gormuyordu (test kullanicisi geri bildirimi,
/// 24.09.2026: "bildirim atsin"). FCM ile telefona da gonderiliyor.
public class PushNotificationTests
{
    /// Gonderilenleri kaydeden sahte FCM.
    private class SahteGonderici : IPushSender
    {
        public List<(IReadOnlyList<string> Tokens, PushMessage Mesaj)> Gonderilen { get; } = new();
        public HashSet<string> Gecersiz { get; } = new();
        public bool Patla { get; set; }

        public Task<IReadOnlyList<string>> SendAsync(IReadOnlyList<string> tokens, PushMessage message, CancellationToken ct = default)
        {
            if (Patla) throw new HttpRequestException("FCM ulasilamiyor");
            Gonderilen.Add((tokens, message));
            return Task.FromResult<IReadOnlyList<string>>(tokens.Where(Gecersiz.Contains).ToList());
        }
    }

    private class Ortam
    {
        public SmartFinanceDbContext Context = null!;
        public HttpContextAccessor Accessor = new();
        public SahteGonderici Gonderici = new();
        public DeviceTokenService Tokenlar = null!;
        public TransactionService Islemler = null!;
        public NotificationService Bildirimler = null!;

        public void Kullanici(int userId)
        {
            var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) });
            Accessor.HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
        }
    }

    private static Ortam Kur()
    {
        var o = new Ortam();
        o.Context = new SmartFinanceDbContext(new DbContextOptionsBuilder<SmartFinanceDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var currentUser = new CurrentUserService(o.Accessor);
        o.Tokenlar = new DeviceTokenService(o.Context, currentUser);
        o.Bildirimler = new NotificationService(o.Context, currentUser, o.Gonderici);
        o.Islemler = new TransactionService(new GenericRepository<Transaction>(o.Context), o.Context, currentUser, o.Bildirimler);
        return o;
    }

    private static int YeniKullanici(Ortam o)
    {
        var u = new User { FullName = "Test", Email = $"{Guid.NewGuid()}@test.com", PasswordHash = "x" };
        o.Context.Users.Add(u);
        o.Context.SaveChanges();
        return u.Id;
    }

    /// Kullanicinin 1000 TL butceli Market kategorisi; donen: kategori id.
    private static int ButceliKategori(Ortam o, int userId)
    {
        var k = new Category { Name = "Market", Type = TransactionType.Expense, UserId = userId };
        o.Context.Categories.Add(k);
        o.Context.SaveChanges();
        o.Context.Budgets.Add(new Budget { UserId = userId, CategoryId = k.Id, MonthlyLimit = 1000m });
        o.Context.SaveChanges();
        return k.Id;
    }

    private static Task Harca(Ortam o, int kategoriId, decimal tutar) =>
        o.Islemler.CreateTransactionAsync(new CreateTransactionDto
        {
            Amount = tutar, Description = "Market alışverişi", TransactionDate = DateTime.UtcNow,
            Type = TransactionType.Expense, CategoryId = kategoriId,
        });

    private static List<string> TokenlarDb(Ortam o, int userId) =>
        o.Context.DeviceTokens.Where(t => t.UserId == userId).Select(t => t.Token).OrderBy(t => t).ToList();

    // ── Cihaz kaydi ──

    [Fact]
    public async Task TokenKaydi_KullaniciyaEklenir_TekrarKayitCiftUretmez()
    {
        var o = Kur();
        var ali = YeniKullanici(o);
        o.Kullanici(ali);

        await o.Tokenlar.RegisterAsync("tel-1");
        await o.Tokenlar.RegisterAsync("tel-1"); // her acilista yeniden bildiriliyor

        Assert.Equal(["tel-1"], TokenlarDb(o, ali));
    }

    /// Ayni telefonda hesap degistirildi: eski hesabin bildirimleri yeni
    /// kullanicinin ekranina dusmemeli.
    [Fact]
    public async Task AyniCihazBaskaHesapla_TokenYeniHesabaTasinir()
    {
        var o = Kur();
        var ali = YeniKullanici(o);
        var ayse = YeniKullanici(o);

        o.Kullanici(ali);
        await o.Tokenlar.RegisterAsync("ortak-tel");
        o.Kullanici(ayse);
        await o.Tokenlar.RegisterAsync("ortak-tel");

        Assert.Empty(TokenlarDb(o, ali));
        Assert.Equal(["ortak-tel"], TokenlarDb(o, ayse));
    }

    [Fact]
    public async Task CihazSiniriAsilinca_EnUzunSuredirGorulmeyenSilinir()
    {
        var o = Kur();
        var ali = YeniKullanici(o);
        o.Kullanici(ali);
        for (var i = 0; i < DeviceTokenLimits.MaxTokensPerUser; i++)
        {
            await o.Tokenlar.RegisterAsync($"tel-{i:D2}");
            var kayit = o.Context.DeviceTokens.Single(t => t.Token == $"tel-{i:D2}");
            kayit.LastSeenAt = new DateTime(2026, 1, 1).AddDays(i); // tel-00 en eski
        }
        o.Context.SaveChanges();

        await o.Tokenlar.RegisterAsync("yeni-tel");

        var kalan = TokenlarDb(o, ali);
        Assert.Equal(DeviceTokenLimits.MaxTokensPerUser, kalan.Count);
        Assert.DoesNotContain("tel-00", kalan);
        Assert.Contains("yeni-tel", kalan);
    }

    /// Cikis yapan kullanici o cihaza bildirim almamali. Baskasinin token'ini
    /// silmek ise mumkun olmamali.
    [Fact]
    public async Task KayitSilme_YalnizcaKendiTokeniniSiler()
    {
        var o = Kur();
        var ali = YeniKullanici(o);
        var ayse = YeniKullanici(o);
        o.Kullanici(ali);
        await o.Tokenlar.RegisterAsync("ali-tel");
        o.Kullanici(ayse);
        await o.Tokenlar.RegisterAsync("ayse-tel");

        await o.Tokenlar.UnregisterAsync("ali-tel"); // ayse, ali'nin token'ini silmeye calisiyor
        await o.Tokenlar.UnregisterAsync("ayse-tel");

        Assert.Equal(["ali-tel"], TokenlarDb(o, ali));
        Assert.Empty(TokenlarDb(o, ayse));
    }

    // ── Gonderim ──

    [Fact]
    public async Task ButceAsilinca_KullanicininTumCihazlarinaPushGider()
    {
        var o = Kur();
        var ali = YeniKullanici(o);
        var ayse = YeniKullanici(o);
        o.Kullanici(ayse);
        await o.Tokenlar.RegisterAsync("ayse-tel");
        o.Kullanici(ali);
        await o.Tokenlar.RegisterAsync("ali-tel");
        await o.Tokenlar.RegisterAsync("ali-tablet");
        var kategori = ButceliKategori(o, ali);

        await Harca(o, kategori, 1200m);

        var (tokens, mesaj) = Assert.Single(o.Gonderici.Gonderilen);
        Assert.Equal(["ali-tablet", "ali-tel"], tokens.OrderBy(t => t));
        Assert.Equal("Bütçe limiti aşıldı", mesaj.Title);
        Assert.Contains("Market", mesaj.Body);
        // Uygulama bildirime dokununca bildirimler ekranini acabilsin.
        Assert.Equal("notification", mesaj.Data["type"]);
    }

    [Fact]
    public async Task AyniAyTekrarAsim_IkinciPushGitmez()
    {
        var o = Kur();
        var ali = YeniKullanici(o);
        o.Kullanici(ali);
        await o.Tokenlar.RegisterAsync("ali-tel");
        var kategori = ButceliKategori(o, ali);

        await Harca(o, kategori, 1200m);
        await Harca(o, kategori, 50m);

        Assert.Single(o.Gonderici.Gonderilen);
    }

    [Fact]
    public async Task CihaziOlmayanKullanici_PushDenenmez()
    {
        var o = Kur();
        var ali = YeniKullanici(o);
        o.Kullanici(ali);
        var kategori = ButceliKategori(o, ali);

        await Harca(o, kategori, 1200m);

        Assert.Empty(o.Gonderici.Gonderilen);
        Assert.Single(await o.Bildirimler.GetAllAsync()); // uygulama ici bildirim yine olusur
    }

    [Fact]
    public async Task FcmGecersizDediginToken_Silinir()
    {
        var o = Kur();
        var ali = YeniKullanici(o);
        o.Kullanici(ali);
        await o.Tokenlar.RegisterAsync("eski-tel");
        await o.Tokenlar.RegisterAsync("yeni-tel");
        o.Gonderici.Gecersiz.Add("eski-tel"); // uygulama kaldirilmis
        var kategori = ButceliKategori(o, ali);

        await Harca(o, kategori, 1200m);

        Assert.Equal(["yeni-tel"], TokenlarDb(o, ali));
    }

    /// Push yalnizca ek kanal: FCM'ye ulasilamazsa islem kaydi ve uygulama
    /// ici bildirim yine basarili olmali.
    [Fact]
    public async Task FcmHataVerirse_IslemVeBildirimYineKaydedilir()
    {
        var o = Kur();
        var ali = YeniKullanici(o);
        o.Kullanici(ali);
        await o.Tokenlar.RegisterAsync("ali-tel");
        o.Gonderici.Patla = true;
        var kategori = ButceliKategori(o, ali);

        await Harca(o, kategori, 1200m);

        Assert.Single(o.Context.Transactions.Where(t => t.UserId == ali));
        Assert.Single(await o.Bildirimler.GetAllAsync());
        Assert.Equal(["ali-tel"], TokenlarDb(o, ali)); // gecici hata token sildirmez
    }

    // ── Istek dogrulama ──

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void BosToken_Reddedilir(string? token)
    {
        var dto = new DeviceTokenDto { Token = token! };
        Assert.False(Validator.TryValidateObject(dto, new ValidationContext(dto), new List<ValidationResult>(), true));
    }

    [Fact]
    public void CokUzunToken_Reddedilir()
    {
        var dto = new DeviceTokenDto { Token = new string('a', DeviceTokenLimits.MaxTokenLength + 1) };
        Assert.False(Validator.TryValidateObject(dto, new ValidationContext(dto), new List<ValidationResult>(), true));
    }
}
