using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using SmartFinance.Infrastructure.Billing;
using SmartFinance.Infrastructure.Retention;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SmartFinance.API.Controllers;
using SmartFinance.Application.DTOs.Auth;
using SmartFinance.Application.DTOs.Subscription;
using SmartFinance.Application.Exceptions;
using SmartFinance.Domain.Entities;
using SmartFinance.Infrastructure.Context;
using SmartFinance.Infrastructure.Services;

namespace SmartFinance.Tests;

public class PurchaseConsentTests
{
    private sealed class Ortam : IDisposable
    {
        public SmartFinanceDbContext Context { get; }
        public SubscriptionService Service { get; }
        public AuthService Auth { get; }
        public int UserId { get; }
        public SahtePlayBillingClient Client { get; } = new();
        public FakeEmailSender Email { get; } = new();

        public Ortam()
        {
            Context = new SmartFinanceDbContext(new DbContextOptionsBuilder<SmartFinanceDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options);
            var user = new User
            {
                FullName = "Test Kullanıcı", Email = "onay@example.invalid",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword("Sifre123!"),
            };
            Context.Users.Add(user);
            Context.SaveChanges();
            UserId = user.Id;
            // AsyncLocal kurulumu async yardımcıda yapılırsa çağıranın oturumuna taşınmaz.
            var accessor = new HttpContextAccessor
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim(ClaimTypes.NameIdentifier, UserId.ToString()),
                    }, "Test")),
                },
            };
            var currentUser = new CurrentUserService(accessor);
            var configuration = new ConfigurationBuilder().Build();
            Service = new SubscriptionService(Context, currentUser,
                new EntitlementService(Context, currentUser, configuration), Client, configuration,
                emailSender: Email);
            Client.Result = SubscriptionVerifyTests.Bilgi(DateTime.UtcNow);
            Auth = new AuthService(Context, configuration, currentUser,
                new FakeEmailSender(), NullLogger<AuthService>.Instance);
        }

        public void Dispose() => Context.Dispose();
    }

    // NEDEN: Kanıt istemcinin kullanıcı/saat beyanına değil oturuma ve sunucu saatine dayanmalı.
    [Theory]
    [InlineData("premium_monthly")]
    [InlineData("premium_yearly")]
    public async Task GecerliSurum_Kabul(string productId)
    {
        using var o = new Ortam();
        var before = DateTime.UtcNow;
        await o.Service.RecordPurchaseConsentAsync(productId, "2026-11-06", default);
        var after = DateTime.UtcNow;

        var row = Assert.Single(await o.Context.PurchaseConsents.AsNoTracking().ToListAsync());
        Assert.Equal(o.UserId, row.UserId);
        Assert.Equal(productId, row.ProductId);
        Assert.Equal("2026-11-06", row.TextVersion);
        Assert.Equal(DateTimeKind.Utc, row.ConsentedAt.Kind);
        Assert.InRange(row.ConsentedAt, before, after);
    }

    // NEDEN: Satılmayan ürün için kanıt kaydı oluşturulmamalı; BadRequestException HTTP 400'e çevrilir.
    [Fact]
    public async Task BilinmeyenUrun_Reddedilir()
    {
        using var o = new Ortam();
        await Assert.ThrowsAsync<BadRequestException>(() =>
            o.Service.RecordPurchaseConsentAsync("premium_lifetime", "2026-11-06", default));
        Assert.Empty(o.Context.PurchaseConsents.IgnoreQueryFilters());
    }

    // NEDEN: Boş veya yalnız boşluk içeren sürüm, hangi metne onay verildiğini ispatlamaz.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    public async Task BosSurum_Reddedilir(string? version)
    {
        using var o = new Ortam();
        await Assert.ThrowsAsync<BadRequestException>(() =>
            o.Service.RecordPurchaseConsentAsync("premium_monthly", version!, default));
        Assert.Empty(o.Context.PurchaseConsents.IgnoreQueryFilters());
    }

    // NEDEN: DTO atlanarak doğrudan servis çağrılsa bile veritabanı uzunluk sınırı korunmalı.
    [Fact]
    public async Task UzunSurum_Reddedilir()
    {
        using var o = new Ortam();
        await Assert.ThrowsAsync<BadRequestException>(() =>
            o.Service.RecordPurchaseConsentAsync("premium_monthly", new string('x', 33), default));
        Assert.Empty(o.Context.PurchaseConsents.IgnoreQueryFilters());
    }

    // NEDEN: Her satın alma denemesinin ayrı kanıtı kalmalı; önceki onay güncellenmemeli.
    [Fact]
    public async Task AyniKullaniciIkiKezOnaylarsa_IkiKayitOlur()
    {
        using var o = new Ortam();
        await o.Service.RecordPurchaseConsentAsync("premium_monthly", "2026-11-06", default);
        var first = await o.Context.PurchaseConsents.AsNoTracking().SingleAsync();
        await o.Service.RecordPurchaseConsentAsync("premium_monthly", "2026-11-06", default);

        var rows = await o.Context.PurchaseConsents.AsNoTracking().OrderBy(x => x.Id).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.NotEqual(rows[0].Id, rows[1].Id);
        Assert.Equal(first.ConsentedAt, rows[0].ConsentedAt);
        Assert.All(rows, x => Assert.Equal(o.UserId, x.UserId));
    }

    // NEDEN: Bağlı kanıt kimliksiz korunmalı; bağsız ve soft-delete denemeler açıkça silinmeli.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HesapSilinince_BagliOnayKimliksizKalir_BagsizOnaySilinir(bool softDeleted)
    {
        using var o = new Ortam();
        await o.Service.RecordPurchaseConsentAsync("premium_monthly", "2026-11-06", default);
        await o.Service.VerifyPurchaseAsync("premium_monthly", "token", default);
        var linkedId = o.Context.PurchaseConsents.Single().Id;
        o.Context.PurchaseConsents.Single().IsDeleted = softDeleted;
        o.Context.PurchaseConsents.Add(new PurchaseConsent
        {
            UserId = o.UserId, ProductId = "premium_yearly", TextVersion = "eski",
            ConsentedAt = DateTime.UtcNow, IsDeleted = true,
        });
        var other = new User { FullName = "Diğer Kullanıcı", Email = "diger@example.invalid", PasswordHash = "x" };
        o.Context.Users.Add(other);
        o.Context.PurchaseConsents.Add(new PurchaseConsent
        {
            User = other, ProductId = "premium_monthly", TextVersion = "2026-11-06",
            ConsentedAt = DateTime.UtcNow,
        });
        await o.Context.SaveChangesAsync();

        // NEDEN: Takip edilmeyen satırlar da servis tarafından işlenmeli; yalnız FK yeterli değil.
        o.Context.ChangeTracker.Clear();
        await o.Auth.DeleteAccountAsync(new DeleteAccountDto { Password = "Sifre123!" });

        Assert.False(await o.Context.Users.IgnoreQueryFilters().AnyAsync(x => x.Id == o.UserId));
        var remaining = await o.Context.PurchaseConsents.IgnoreQueryFilters().ToListAsync();
        Assert.Equal(2, remaining.Count);
        var linked = Assert.Single(remaining, x => x.Id == linkedId);
        Assert.Null(linked.UserId);
        Assert.Equal("token", linked.PurchaseToken);
        Assert.Equal("ornek-siparis", linked.OrderId);
        Assert.Contains(remaining, x => x.UserId == other.Id);
    }

    // NEDEN: Mobil uygulama kayıt kalıcılaştıktan sonra boş gövdeli 204 alarak satın almayı başlatmalı.
    [Fact]
    public async Task OnayUcu_Kaydedip204Doner()
    {
        using var o = new Ortam();
        var result = await new SubscriptionController(o.Service).Consent(new PurchaseConsentDto
        {
            ProductId = "premium_monthly", TextVersion = "2026-11-06",
        }, default);
        Assert.IsType<NoContentResult>(result);
        Assert.Single(o.Context.PurchaseConsents);
    }

    // NEDEN: HTTP gövdesinin zorunlu alanları ve uzunluk sınırları model bağlama sırasında korunmalı.
    [Theory]
    [InlineData("", "2026-11-06")]
    [InlineData("premium_monthly", " ")]
    [InlineData("xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx", "2026-11-06")]
    [InlineData("premium_monthly", "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx")]
    public void OnayDto_GecersizAlanlariReddeder(string productId, string version)
    {
        var dto = new PurchaseConsentDto { ProductId = productId, TextVersion = version };
        Assert.False(Validator.TryValidateObject(dto, new ValidationContext(dto), new List<ValidationResult>(), true));
    }

    // NEDEN: İstemci rastgele bir tarihi geçerli sözleşme sürümü gibi kaydedememeli.
    [Fact]
    public async Task BilinmeyenSurum_Reddedilir()
    {
        using var o = new Ortam();
        await Assert.ThrowsAsync<BadRequestException>(() =>
            o.Service.RecordPurchaseConsentAsync("premium_monthly", "2025-01-01", default));
        Assert.Empty(o.Context.PurchaseConsents);
        Assert.True(PurchaseConsentTexts.IsAllowed(PurchaseConsentTexts.Current));
    }

    // NEDEN: Yalnız aynı kullanıcı/ürünün son 24 saatteki en son bağsız onayı satın almaya bağlanmalı.
    [Fact]
    public async Task Verify_SonOnayiSatinAlmayaBaglar()
    {
        using var o = new Ortam();
        await o.Service.RecordPurchaseConsentAsync("premium_monthly", PurchaseConsentTexts.Current, default);
        o.Context.PurchaseConsents.Single().ConsentedAt = DateTime.UtcNow.AddHours(-2);
        await o.Service.RecordPurchaseConsentAsync("premium_monthly", PurchaseConsentTexts.Current, default);
        var latest = o.Context.PurchaseConsents.OrderByDescending(x => x.Id).First();
        await o.Service.VerifyPurchaseAsync("premium_monthly", "token", default);
        Assert.Equal("token", latest.PurchaseToken);
        Assert.Equal("ornek-siparis", latest.OrderId);
        Assert.Single(o.Context.PurchaseConsents.Where(x => x.PurchaseToken == null));
        Assert.Single(o.Email.Kutu);
    }

    // NEDEN: Eski deneme, farklı ürün veya başka kullanıcının onayı bu satın alma için kanıt olamaz.
    [Fact]
    public async Task Verify_BaskaKullanicininOnayiniBaglamaz()
    {
        using var o = new Ortam();
        await o.Service.RecordPurchaseConsentAsync("premium_monthly", PurchaseConsentTexts.Current, default);
        var consent = o.Context.PurchaseConsents.Single();
        consent.UserId = o.UserId + 1;
        consent.User = null;
        await o.Context.SaveChangesAsync();
        Assert.True((await o.Service.VerifyPurchaseAsync("premium_monthly", "token", default)).IsPremium);
        Assert.Null(consent.PurchaseToken);
        Assert.Empty(o.Email.Kutu);
    }

    // NEDEN: 24 saatten eski onay yeni satın alma isteğini ispatlamaz.
    [Fact]
    public async Task Verify_24SaattenEskiOnayiBaglamaz()
    {
        using var o = new Ortam();
        await o.Service.RecordPurchaseConsentAsync("premium_monthly", PurchaseConsentTexts.Current, default);
        o.Context.PurchaseConsents.Single().ConsentedAt = DateTime.UtcNow.AddHours(-25);
        await o.Context.SaveChangesAsync();
        Assert.True((await o.Service.VerifyPurchaseAsync("premium_monthly", "token", default)).IsPremium);
        Assert.Null(o.Context.PurchaseConsents.Single().PurchaseToken);
        Assert.Empty(o.Email.Kutu);
    }

    // NEDEN: Yıllık plan onayı aylık planın satın alınmasına bağlanamaz.
    [Fact]
    public async Task Verify_BaskaUrununOnayiniBaglamaz()
    {
        using var o = new Ortam();
        await o.Service.RecordPurchaseConsentAsync("premium_yearly", PurchaseConsentTexts.Current, default);
        Assert.True((await o.Service.VerifyPurchaseAsync("premium_monthly", "token", default)).IsPremium);
        Assert.Null(o.Context.PurchaseConsents.Single().PurchaseToken);
        Assert.Empty(o.Email.Kutu);
    }

    // NEDEN: v15 istemcisi onay kaydı göndermese de doğrulanmış satın alması çalışmalı.
    [Fact]
    public async Task Verify_OnaysizSatinAlma_YineBasarili()
    {
        using var o = new Ortam();
        Assert.True((await o.Service.VerifyPurchaseAsync("premium_monthly", "token", default)).IsPremium);
        Assert.Empty(o.Context.PurchaseConsents);
        Assert.Empty(o.Email.Kutu);
    }

    // NEDEN: Restore/tekrar verify başka bir bağsız onayı tüketmemeli ve teyit e-postasını yinelememeli.
    [Fact]
    public async Task Verify_TekrarCagrilinca_IkinciKezBaglamazVeEpostaTekrarGitmez()
    {
        using var o = new Ortam();
        await o.Service.RecordPurchaseConsentAsync("premium_monthly", PurchaseConsentTexts.Current, default);
        await o.Service.VerifyPurchaseAsync("premium_monthly", "token", default);
        await o.Service.RecordPurchaseConsentAsync("premium_monthly", PurchaseConsentTexts.Current, default);
        await o.Service.VerifyPurchaseAsync("premium_monthly", "token", default);
        Assert.Single(o.Context.PurchaseConsents.Where(x => x.PurchaseToken == "token"));
        Assert.Single(o.Context.PurchaseConsents.Where(x => x.PurchaseToken == null));
        Assert.Single(o.Email.Kutu);
    }

    // NEDEN: Reddedilen veya bekleyen ödeme için satın alma kanıtı ve başlangıç teyidi oluşmamalı.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Verify_DogrulamaBasarisizsa_BaglamaYokEpostaYok(bool pending)
    {
        using var o = new Ortam();
        await o.Service.RecordPurchaseConsentAsync("premium_monthly", PurchaseConsentTexts.Current, default);
        if (pending)
        {
            o.Client.Result = o.Client.Result! with { State = "SUBSCRIPTION_STATE_PENDING" };
            Assert.True((await o.Service.VerifyPurchaseAsync("premium_monthly", "token", default)).PurchasePending);
        }
        else
        {
            o.Client.Result = null;
            await Assert.ThrowsAsync<BadRequestException>(() =>
                o.Service.VerifyPurchaseAsync("premium_monthly", "token", default));
        }
        Assert.Null(o.Context.PurchaseConsents.Single().PurchaseToken);
        Assert.Empty(o.Email.Kutu);
    }

    // NEDEN: Kalıcı teyit planı, TSİ saatini, sürümü ve iptal/koşul bilgilerini taşımalı, HTML güvenli olmalı.
    [Theory]
    [InlineData("premium_monthly", "Aylık Premium")]
    [InlineData("premium_yearly", "Yıllık Premium")]
    public async Task TeyitEpostasi_IcerikDogru(string product, string plan)
    {
        using var o = new Ortam();
        var start = new DateTime(2026, 11, 5, 22, 0, 0, DateTimeKind.Utc);
        o.Client.Result = o.Client.Result! with
        {
            ProductId = product, StartedAtUtc = start, LatestOrderId = "<siparis>&",
        };
        await o.Service.RecordPurchaseConsentAsync(product, PurchaseConsentTexts.Current, default);
        var consentTime = o.Context.PurchaseConsents.Single().ConsentedAt;
        await o.Service.VerifyPurchaseAsync(product, "token", default);
        var mail = Assert.Single(o.Email.Kutu);
        var decoded = WebUtility.HtmlDecode(mail.HtmlBody);
        Assert.Equal("onay@example.invalid", mail.To);
        Assert.Equal("Wallet Mark Premium aboneliğiniz başladı", mail.Subject);
        Assert.Contains(plan, decoded);
        Assert.Contains("06.11.2026", decoded);
        Assert.Contains(consentTime.AddHours(3).ToString("dd.MM.yyyy HH:mm"), decoded);
        Assert.Contains("sürüm " + PurchaseConsentTexts.Current, decoded);
        Assert.Contains("Onayınızla ifasına başlanan hizmette cayma hakkı kullanılamaz (Mesafeli Sözleşmeler Yönetmeliği md.15/1-h).", decoded);
        Assert.Contains("https://walletmark.com.tr/kosullar", mail.HtmlBody);
        Assert.Contains("https://walletmark.com.tr/gizlilik", mail.HtmlBody);
        Assert.Contains("https://play.google.com/store/account/subscriptions?package=com.walletmark.app", mail.HtmlBody);
        Assert.Contains("her dönem sonunda otomatik yenilenir", decoded);
        Assert.Contains("Mehmet Koç (Wallet Mark) · destek@walletmark.com.tr", decoded);
        Assert.Contains("&lt;siparis&gt;&amp;", mail.HtmlBody);
        Assert.DoesNotContain("<siparis>", mail.HtmlBody);
    }

    // NEDEN: Gönderim kesintisi kazanılmış premium hakkını veya başarılı verify sonucunu bozamaz.
    [Fact]
    public async Task TeyitEpostasiHatasi_VerifyiBozmaz()
    {
        using var o = new Ortam();
        o.Email.Hata = new InvalidOperationException("test");
        await o.Service.RecordPurchaseConsentAsync("premium_monthly", PurchaseConsentTexts.Current, default);
        Assert.True((await o.Service.VerifyPurchaseAsync("premium_monthly", "token", default)).IsPremium);
        Assert.Equal("token", o.Context.PurchaseConsents.Single().PurchaseToken);
    }

    // NEDEN: Kimliksiz kanıt tam 3 yıl korunmalı; aktif kullanıcıya bağlı satın alma kanıtı silinmemeli.
    [Fact]
    public async Task Saklama_3YildanEskiKimliksizOnaySilinir_YenisiKalir()
    {
        using var o = new Ortam();
        var now = DateTime.UtcNow;
        var old = new PurchaseConsent { ConsentedAt = now.AddYears(-3).AddTicks(-1), PurchaseToken = "eski", IsDeleted = true };
        var boundary = new PurchaseConsent { ConsentedAt = now.AddYears(-3), PurchaseToken = "sinir" };
        var recent = new PurchaseConsent { ConsentedAt = now.AddYears(-2), PurchaseToken = "yeni" };
        var owned = new PurchaseConsent { UserId = o.UserId, ConsentedAt = now.AddYears(-4), PurchaseToken = "bagli" };
        o.Context.PurchaseConsents.AddRange(old, boundary, recent, owned);
        await o.Context.SaveChangesAsync();
        using var provider = new ServiceCollection().BuildServiceProvider();
        var retention = new DataRetentionService(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<DataRetentionService>.Instance);
        Assert.Equal(1, await retention.CleanupPurchaseConsentsAsync(o.Context, now, default));
        var rows = await o.Context.PurchaseConsents.IgnoreQueryFilters().ToListAsync();
        Assert.DoesNotContain(rows, x => x.Id == old.Id);
        Assert.Equal(3, rows.Count);
    }

    // NEDEN: Bağsız deneme 30 gün sonra silinir; sınırdaki/yeni deneme ve satın alma kanıtı korunur.
    [Fact]
    public async Task Saklama_30GundenEskiBagsizDenemeSilinir()
    {
        using var o = new Ortam();
        var now = DateTime.UtcNow;
        o.Context.PurchaseConsents.AddRange(
            new PurchaseConsent { UserId = o.UserId, ConsentedAt = now.AddDays(-31), IsDeleted = true },
            new PurchaseConsent { UserId = o.UserId, ConsentedAt = now.AddDays(-30) },
            new PurchaseConsent { UserId = o.UserId, ConsentedAt = now.AddDays(-29) },
            new PurchaseConsent { UserId = o.UserId, ConsentedAt = now.AddDays(-31), PurchaseToken = "bagli" });
        await o.Context.SaveChangesAsync();
        using var provider = new ServiceCollection().BuildServiceProvider();
        var retention = new DataRetentionService(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<DataRetentionService>.Instance);
        Assert.Equal(1, await retention.CleanupPurchaseConsentsAsync(o.Context, now, default));
        Assert.Equal(3, await o.Context.PurchaseConsents.IgnoreQueryFilters().CountAsync());
    }

    // NEDEN: Entegrasyon hostu olmadan da uçların kimlik doğrulama zorunluluğu korunmalı.
    [Fact]
    public void GirissizIstek_401()
    {
        Assert.NotEmpty(typeof(SubscriptionController).GetCustomAttributes(typeof(AuthorizeAttribute), true));
        Assert.Empty(typeof(SubscriptionController).GetCustomAttributes(typeof(AllowAnonymousAttribute), true));
        Assert.Empty(typeof(SubscriptionController).GetMethod(nameof(SubscriptionController.Consent))!
            .GetCustomAttributes(typeof(AllowAnonymousAttribute), true));
    }

    // NEDEN: Bu kontrol yalnız modeli karşılaştırır; SQL Server bağlantısı açmaz.
    [Fact]
    public void MigrationSnapshot_ModelIleUyumludur()
    {
        using var db = new SmartFinanceDbContext(new DbContextOptionsBuilder<SmartFinanceDbContext>()
            .UseSqlServer().Options);
        Assert.False(db.Database.HasPendingModelChanges());
        var consent = db.Model.FindEntityType(typeof(PurchaseConsent))!;
        Assert.True(consent.FindProperty(nameof(PurchaseConsent.UserId))!.IsNullable);
        Assert.Equal(450, consent.FindProperty(nameof(PurchaseConsent.PurchaseToken))!.GetMaxLength());
        Assert.Equal(128, consent.FindProperty(nameof(PurchaseConsent.OrderId))!.GetMaxLength());
        Assert.Equal(DeleteBehavior.SetNull, Assert.Single(consent.GetForeignKeys()).DeleteBehavior);
        Assert.False(Assert.Single(consent.GetIndexes(), i =>
            i.Properties.Single().Name == nameof(PurchaseConsent.PurchaseToken)).IsUnique);
    }
}
