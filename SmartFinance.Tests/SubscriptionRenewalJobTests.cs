using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SmartFinance.Application.Interfaces;
using SmartFinance.Domain.Entities;
using SmartFinance.Infrastructure.Billing;
using SmartFinance.Infrastructure.Context;

namespace SmartFinance.Tests;

public class SubscriptionRenewalJobTests
{
    private static readonly DateTime Simdi = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);

    private sealed class Ortam : IDisposable
    {
        private readonly ServiceProvider _provider;
        public SahtePlayBillingClient Client { get; } = new();
        public SubscriptionRenewalService Job { get; }

        public Ortam()
        {
            var databaseName = Guid.NewGuid().ToString();
            var services = new ServiceCollection();
            services.AddDbContext<SmartFinanceDbContext>(options => options.UseInMemoryDatabase(databaseName));
            services.AddSingleton<IPlayBillingClient>(Client);
            _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            Job = new SubscriptionRenewalService(_provider.GetRequiredService<IServiceScopeFactory>(),
                Client, NullLogger<SubscriptionRenewalService>.Instance);
            Client.Result = SubscriptionVerifyTests.Bilgi(Simdi);
        }

        public async Task Ekle(params Subscription[] rows)
        {
            using var scope = _provider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<SmartFinanceDbContext>();
            context.Subscriptions.AddRange(rows);
            await context.SaveChangesAsync();
        }

        public async Task<List<Subscription>> Oku()
        {
            using var scope = _provider.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<SmartFinanceDbContext>()
                .Subscriptions.AsNoTracking().OrderBy(s => s.Id).ToListAsync();
        }

        public void Dispose()
        {
            Job.Dispose();
            _provider.Dispose();
        }
    }

    private static Subscription Kayit(string token, DateTime? expiry = null, DateTime? verified = null,
        bool autoRenewing = false) => new()
    {
        UserId = 1, ProductId = "premium_monthly", PurchaseToken = token,
        StartsAt = Simdi.AddDays(-10), ExpiresAt = expiry ?? Simdi.AddDays(1), LastVerifiedAt = verified,
        AutoRenewing = autoRenewing,
    };

    /// NEDEN: Play'de yenilenmiş süre ve sipariş bilgisi yeni scope'ta kalıcı olarak okunabilmeli.
    [Fact]
    public async Task UzayanAbonelik_SuresiVeDogrulamaZamaniGuncellenir()
    {
        using var o = new Ortam();
        await o.Ekle(Kayit("ornek-token"));
        await o.Job.RunOnceAsync(Simdi, default);
        var row = Assert.Single(await o.Oku());
        Assert.Equal(o.Client.Result!.ExpiresAtUtc, row.ExpiresAt);
        Assert.Equal(o.Client.Result.StartedAtUtc, row.StartsAt);
        Assert.Equal(o.Client.Result.LatestOrderId, row.OrderId);
        Assert.Equal(Simdi, row.LastVerifiedAt);
        Assert.True(row.AutoRenewing);
        Assert.Single(o.Client.Acknowledgements);
    }

    /// NEDEN: Google artık jetonu tanımıyorsa önceki premium hakkı sürmemeli.
    [Fact]
    public async Task GoogleNullDonerse_AbonelikSonlanir()
    {
        using var o = new Ortam();
        await o.Ekle(Kayit("ornek-token", autoRenewing: true));
        o.Client.Result = null;
        await o.Job.RunOnceAsync(Simdi, default);
        var row = Assert.Single(await o.Oku());
        Assert.Equal(Simdi, row.ExpiresAt);
        Assert.Equal(Simdi, row.LastVerifiedAt);
        // Sonlanan satır artık otomatik yenilenen sayılmamalı.
        Assert.False(row.AutoRenewing);
        Assert.Empty(o.Client.Acknowledgements);
    }

    /// NEDEN: Google parayı almışken (deneme bitti / aylık yenileme) tablo eski kalırsa kullanıcı ücretsiz sayılıyordu.
    [Theory]
    [InlineData(null)]
    [InlineData(11)]
    public async Task SuresiDolmusOtomatikYenilenen_IsteYenidenSorulur(int? dogrulamaDakikaOnce)
    {
        using var o = new Ortam();
        DateTime? verified = dogrulamaDakikaOnce is null ? null : Simdi.AddMinutes(-dogrulamaDakikaOnce.Value);
        await o.Ekle(Kayit("ornek-token", Simdi.AddMinutes(-30), verified, autoRenewing: true));
        await o.Job.RunOnceAsync(Simdi, default);
        Assert.Equal("ornek-token", Assert.Single(o.Client.QueriedTokens));
        var row = Assert.Single(await o.Oku());
        Assert.Equal(o.Client.Result!.ExpiresAtUtc, row.ExpiresAt);
        Assert.True(row.ExpiresAt > Simdi);
        Assert.Equal(Simdi, row.LastVerifiedAt);
        Assert.True(row.AutoRenewing);
    }

    /// NEDEN: Yeni dolmuş satır her turda değil, en fazla 10 dakikada bir sorulmalı (Google kotası).
    [Fact]
    public async Task SuresiDolmusOtomatikYenilenen_OnDakikaIcindeTekrarSorulmaz()
    {
        using var o = new Ortam();
        var expiry = Simdi.AddMinutes(-30);
        // 10 dakika tam sınır: bayatlık "10 dakikadan eski" olduğu için bu da sorulmaz.
        await o.Ekle(Kayit("ornek-dokuz-dakika", expiry, Simdi.AddMinutes(-9), autoRenewing: true),
            Kayit("ornek-tam-on-dakika", expiry, Simdi.AddMinutes(-10), autoRenewing: true));
        await o.Job.RunOnceAsync(Simdi, default);
        Assert.Empty(o.Client.QueriedTokens);
        foreach (var row in await o.Oku())
            Assert.Equal(expiry, row.ExpiresAt);
    }

    /// NEDEN: Kullanıcı iptal ettiyse (AutoRenewing=false) süresi dolan satır için Google'a boşuna sorulmamalı.
    [Fact]
    public async Task SuresiDolmusIptalEdilmis_GoogleaSorulmaz()
    {
        using var o = new Ortam();
        var expiry = Simdi.AddHours(-1);
        await o.Ekle(Kayit("ornek-iptal", expiry, autoRenewing: false));
        await o.Job.RunOnceAsync(Simdi, default);
        Assert.Empty(o.Client.QueriedTokens);
        var row = Assert.Single(await o.Oku());
        Assert.Equal(expiry, row.ExpiresAt);
        Assert.Null(row.LastVerifiedAt);
    }

    /// NEDEN: Otuz günden eski dolmuş satırlar terk edilmiş sayılır; sınırın tam üstü hâlâ sorulur.
    [Fact]
    public async Task OtuzGundenEskiDolmus_GoogleaSorulmaz()
    {
        using var o = new Ortam();
        await o.Ekle(Kayit("ornek-otuz-gunden-eski", Simdi.AddDays(-30).AddTicks(-1), autoRenewing: true),
            Kayit("ornek-tam-otuz-gun", Simdi.AddDays(-30), autoRenewing: true));
        await o.Job.RunOnceAsync(Simdi, default);
        Assert.Equal("ornek-tam-otuz-gun", Assert.Single(o.Client.QueriedTokens));
        var eski = (await o.Oku()).Single(s => s.PurchaseToken == "ornek-otuz-gunden-eski");
        Assert.Equal(Simdi.AddDays(-30).AddTicks(-1), eski.ExpiresAt);
        Assert.Null(eski.LastVerifiedAt);
    }

    /// NEDEN: Google jetonu tanımıyorsa satır kapanmalı; yoksa dolmuş satır her 15 dakikada yeniden sorulurdu.
    [Fact]
    public async Task GoogleNullDonerse_OtomatikYenilemeKapanir()
    {
        using var o = new Ortam();
        var expiry = Simdi.AddHours(-1);
        await o.Ekle(Kayit("ornek-token", expiry, autoRenewing: true));
        o.Client.Result = null;
        await o.Job.RunOnceAsync(Simdi, default);
        var row = Assert.Single(await o.Oku());
        Assert.False(row.AutoRenewing);
        // Geçmişte bitmiş tarih şimdiye ileri taşınmaz.
        Assert.Equal(expiry, row.ExpiresAt);
        Assert.Equal(Simdi, row.LastVerifiedAt);
        Assert.Single(o.Client.QueriedTokens);

        await o.Job.RunOnceAsync(Simdi.AddMinutes(30), default);
        Assert.Single(o.Client.QueriedTokens);
    }

    /// NEDEN: Tek jetondaki dış servis hatası diğer kullanıcıların yenilenmesini durdurmamalı.
    [Fact]
    public async Task BirSatirinHatasi_DigerleriniDurdurmaz()
    {
        using var o = new Ortam();
        await o.Ekle(Kayit("ornek-hatali-token"), Kayit("ornek-gecerli-token"));
        o.Client.GetResult = token => token == "ornek-hatali-token"
            ? throw new HttpRequestException("Örnek sorgu hatası.") : o.Client.Result;
        await o.Job.RunOnceAsync(Simdi, default);
        var rows = await o.Oku();
        var failed = rows.Single(s => s.PurchaseToken == "ornek-hatali-token");
        Assert.Equal(Simdi.AddDays(1), failed.ExpiresAt);
        Assert.Null(failed.LastVerifiedAt);
        var renewed = rows.Single(s => s.PurchaseToken == "ornek-gecerli-token");
        Assert.Equal(o.Client.Result!.ExpiresAtUtc, renewed.ExpiresAt);
        Assert.Equal(Simdi, renewed.LastVerifiedAt);
        Assert.Equal(2, o.Client.QueriedTokens.Count);
    }

    /// NEDEN: Pencere dışındaki veya yakın zamanda doğrulanan kayıtlar API kotasını tüketmemeli.
    /// Beklenti değişikliği: eski 7 günlük "dolmuş" penceresi kalktı; dolmuş satırlar artık yalnızca
    /// AutoRenewing == true ise sorulur. Bu testteki dolmuş satırlar AutoRenewing=false (varsayılan)
    /// olduğundan "ornek-alt-sinir" artık sorulmaz ve değişmeden kalır (Take(4) -> Take(5)).
    [Fact]
    public async Task PencereDisindakilereVeYeniDogrulananlara_Dokunulmaz()
    {
        using var o = new Ortam();
        var rows = new[]
        {
            Kayit("ornek-cok-eski", Simdi.AddDays(-7).AddTicks(-1)),
            Kayit("ornek-cok-ileri", Simdi.AddDays(2).AddTicks(1)),
            Kayit("ornek-yeni-dogrulama", verified: Simdi.AddHours(-1)),
            Kayit("ornek-alti-saat", verified: Simdi.AddHours(-6)),
            Kayit("ornek-alt-sinir", Simdi.AddDays(-7)),
            Kayit("ornek-ust-sinir", Simdi.AddDays(2), Simdi.AddHours(-6).AddTicks(-1)),
        };
        await o.Ekle(rows);
        await o.Job.RunOnceAsync(Simdi, default);
        Assert.Equal(new[] { "ornek-ust-sinir" }, o.Client.QueriedTokens.OrderBy(t => t));
        var saved = await o.Oku();
        foreach (var original in rows.Take(5))
        {
            var current = saved.Single(s => s.PurchaseToken == original.PurchaseToken);
            Assert.Equal(original.ExpiresAt, current.ExpiresAt);
            Assert.Equal(original.LastVerifiedAt, current.LastVerifiedAt);
        }
    }

    /// NEDEN: Eksik Google yapılandırması işi sessizce atlamalı ve abonelikleri değiştirmemeli.
    [Fact]
    public async Task YapilandirmaYoksa_HicbirKaydaDokunulmaz()
    {
        using var o = new Ortam();
        await o.Ekle(Kayit("ornek-token"));
        o.Client.IsConfigured = false;
        await o.Job.RunOnceAsync(Simdi, default);
        var row = Assert.Single(await o.Oku());
        Assert.Equal(Simdi.AddDays(1), row.ExpiresAt);
        Assert.Null(row.LastVerifiedAt);
        Assert.Empty(o.Client.QueriedTokens);
    }

    /// NEDEN: Büyük tabloda tek tur en fazla iki yüz Google sorgusu yapmalı.
    [Fact]
    public async Task TekTur_EnFazlaIkiYuzKaydiIsler()
    {
        using var o = new Ortam();
        await o.Ekle(Enumerable.Range(0, 201).Select(i => Kayit($"ornek-token-{i}")).ToArray());
        await o.Job.RunOnceAsync(Simdi, default);
        Assert.Equal(200, o.Client.QueriedTokens.Count);
        var rows = await o.Oku();
        Assert.Equal(200, rows.Count(s => s.LastVerifiedAt == Simdi));
        Assert.Single(rows.Where(s => s.LastVerifiedAt is null));
    }

    /// NEDEN: Yenileme işi de doğrulama ucuyla aynı askıya alma ve iptal kurallarını uygulamalı.
    [Theory]
    [InlineData("SUBSCRIPTION_STATE_EXPIRED", false)]
    [InlineData("SUBSCRIPTION_STATE_ON_HOLD", false)]
    [InlineData("SUBSCRIPTION_STATE_PAUSED", false)]
    [InlineData("SUBSCRIPTION_STATE_CANCELED", true)]
    [InlineData("SUBSCRIPTION_STATE_IN_GRACE_PERIOD", true)]
    public async Task DurumKurallari_DogrulamaUcuylaAynidir(string state, bool entitled)
    {
        using var o = new Ortam();
        await o.Ekle(Kayit("ornek-token"));
        o.Client.Result = o.Client.Result! with { State = state };
        await o.Job.RunOnceAsync(Simdi, default);
        var row = Assert.Single(await o.Oku());
        Assert.Equal(entitled ? o.Client.Result.ExpiresAtUtc : Simdi, row.ExpiresAt);
        Assert.Equal(Simdi, row.LastVerifiedAt);
        Assert.Equal(entitled ? 1 : 0, o.Client.Acknowledgements.Count);
    }

    /// NEDEN: Yenileme sırasında gelen bağlı jeton eski aboneliği pencere dışında olsa da sonlandırmalı.
    [Fact]
    public async Task BagliToken_YenilemedeEskiAboneligiSonlandirir()
    {
        using var o = new Ortam();
        await o.Ekle(Kayit("ornek-eski-token", Simdi.AddDays(10)), Kayit("ornek-yeni-token"));
        o.Client.Result = o.Client.Result! with { LinkedPurchaseToken = "ornek-eski-token" };
        await o.Job.RunOnceAsync(Simdi, default);
        var rows = await o.Oku();
        Assert.Equal(Simdi, rows.Single(s => s.PurchaseToken == "ornek-eski-token").ExpiresAt);
        Assert.Equal(o.Client.Result.ExpiresAtUtc, rows.Single(s => s.PurchaseToken == "ornek-yeni-token").ExpiresAt);
        Assert.Equal("ornek-yeni-token", Assert.Single(o.Client.QueriedTokens));
    }

    /// NEDEN: Google onayı hata verse de yenilenen süre kaydedilmeli ve sonraki uygun tur tekrar denemeli.
    [Fact]
    public async Task OnayHatasi_KaydiGeriAlmaz_SonrakiTurTekrarDener()
    {
        using var o = new Ortam();
        await o.Ekle(Kayit("ornek-token"));
        o.Client.Result = o.Client.Result! with { ExpiresAtUtc = Simdi.AddDays(1) };
        o.Client.AcknowledgeFails = true;
        await o.Job.RunOnceAsync(Simdi, default);
        Assert.Equal(Simdi.AddDays(1), Assert.Single(await o.Oku()).ExpiresAt);
        Assert.Single(o.Client.Acknowledgements);
        o.Client.AcknowledgeFails = false;
        var later = Simdi.AddHours(7);
        await o.Job.RunOnceAsync(later, default);
        Assert.Equal(2, o.Client.Acknowledgements.Count);
        Assert.Equal(later, Assert.Single(await o.Oku()).LastVerifiedAt);
    }

    /// NEDEN: Ürün uyuşmazlığı olan kaydın hatası sonraki kayda veya kalıcı verilere taşınmamalı.
    [Fact]
    public async Task UrunUyusmazligi_HataliKaydiDegistirmez_DigerKaydiYeniler()
    {
        using var o = new Ortam();
        await o.Ekle(Kayit("ornek-farkli-urun"), Kayit("ornek-gecerli-token"));
        o.Client.GetResult = token => token == "ornek-farkli-urun"
            ? o.Client.Result! with { ProductId = "premium_yearly" } : o.Client.Result;
        await o.Job.RunOnceAsync(Simdi, default);
        var rows = await o.Oku();
        var invalid = rows.Single(s => s.PurchaseToken == "ornek-farkli-urun");
        Assert.Equal("premium_monthly", invalid.ProductId);
        Assert.Equal(Simdi.AddDays(1), invalid.ExpiresAt);
        Assert.Null(invalid.LastVerifiedAt);
        Assert.Equal(Simdi, rows.Single(s => s.PurchaseToken == "ornek-gecerli-token").LastVerifiedAt);
    }
}
