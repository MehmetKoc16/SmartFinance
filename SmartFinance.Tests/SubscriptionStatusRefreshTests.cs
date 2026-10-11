using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SmartFinance.Domain.Entities;
using SmartFinance.Infrastructure.Context;
using SmartFinance.Infrastructure.Services;

namespace SmartFinance.Tests;

/// Durum ucu (GetStatusAsync) süresi yeni dolmuş, otomatik yenilenen aboneliği Google'a sorup
/// anında düzeltmeli; Google'ın yenilemeyi bildirmemesi kullanıcıyı saatlerce ücretsiz göstermemeli.
public class SubscriptionStatusRefreshTests
{
    private sealed class Ortam : IDisposable
    {
        public SmartFinanceDbContext Context { get; }
        public SahtePlayBillingClient Client { get; } = new();
        public SubscriptionService Service { get; }
        public int UserId { get; }

        public Ortam()
        {
            Context = new SmartFinanceDbContext(new DbContextOptionsBuilder<SmartFinanceDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            var user = new User { FullName = "Test Kullanıcı", Email = "ornek@test.com", PasswordHash = "ornek" };
            Context.Users.Add(user);
            Context.SaveChanges();
            UserId = user.Id;
            var accessor = new HttpContextAccessor
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim(ClaimTypes.NameIdentifier, UserId.ToString()),
                    })),
                },
            };
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["Jwt:Key"] = "ornek-test-anahtari-yalnizca-testler-icin" }).Build();
            var currentUser = new CurrentUserService(accessor);
            Service = new SubscriptionService(Context, currentUser,
                new EntitlementService(Context, currentUser, configuration), Client, configuration,
                NullLogger<SubscriptionService>.Instance);
            Client.Result = SubscriptionVerifyTests.Bilgi(DateTime.UtcNow);
        }

        public async Task Ekle(params Subscription[] rows)
        {
            Context.Subscriptions.AddRange(rows);
            await Context.SaveChangesAsync();
        }

        public async Task<List<Subscription>> Oku()
            => await Context.Subscriptions.AsNoTracking().OrderBy(s => s.Id).ToListAsync();

        public void Dispose() => Context.Dispose();
    }

    // Süresi beş dakika önce dolmuş, otomatik yenilenen ve üç saat önce doğrulanmış kayıt (varsayılan).
    private static Subscription Kayit(Ortam o, string token = "ornek-token", DateTime? expiry = null,
        DateTime? verified = null, bool autoRenewing = true) => new()
    {
        UserId = o.UserId, ProductId = "premium_monthly", PurchaseToken = token,
        StartsAt = DateTime.UtcNow.AddDays(-30),
        ExpiresAt = expiry ?? DateTime.UtcNow.AddMinutes(-5),
        LastVerifiedAt = verified ?? DateTime.UtcNow.AddHours(-3),
        AutoRenewing = autoRenewing,
    };

    /// NEDEN: Google yenilemeyi bildirmiyor; kullanıcı uygulamayı açtığı anda premium'a dönmeli, 6-12 saat beklememeli.
    [Fact]
    public async Task DurumSorgusu_YenilenmisAboneligiAnindaPremiumYapar()
    {
        using var o = new Ortam();
        await o.Ekle(Kayit(o));
        var yeniBitis = DateTime.UtcNow.AddDays(30);
        o.Client.Result = o.Client.Result! with { ExpiresAtUtc = yeniBitis };

        var status = await o.Service.GetStatusAsync();

        Assert.True(status.IsPremium);
        Assert.Equal(yeniBitis, status.ExpiresAt);
        Assert.True(status.AutoRenewing);
        Assert.Equal("ornek-token", Assert.Single(o.Client.QueriedTokens));
        var row = Assert.Single(await o.Oku());
        Assert.Equal(yeniBitis, row.ExpiresAt);
        Assert.NotNull(row.LastVerifiedAt);
        Assert.True(row.LastVerifiedAt!.Value > DateTime.UtcNow.AddMinutes(-1));
    }

    /// NEDEN: Uygulama durum ucunu sık çağırır; aynı satır için Google'a en fazla iki dakikada bir sorulmalı.
    [Fact]
    public async Task DurumSorgusu_IkiDakikaIcindeTekrarGoogleaSormaz()
    {
        using var o = new Ortam();
        // Google "süresi dolmuş" diyor ama bayrak hâlâ true: ikinci çağrı yalnızca LastVerifiedAt sayesinde sorulmaz.
        await o.Ekle(Kayit(o));
        o.Client.Result = o.Client.Result! with
        {
            State = "SUBSCRIPTION_STATE_EXPIRED", ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-5), AutoRenewing = true,
        };

        Assert.False((await o.Service.GetStatusAsync()).IsPremium);
        Assert.Single(o.Client.QueriedTokens);
        Assert.False((await o.Service.GetStatusAsync()).IsPremium);
        Assert.Single(o.Client.QueriedTokens);

        // Doğrudan kurulan durum: 30 saniye önce doğrulanmış satır da sorulmaz.
        using var p = new Ortam();
        await p.Ekle(Kayit(p, verified: DateTime.UtcNow.AddSeconds(-30)));
        Assert.False((await p.Service.GetStatusAsync()).IsPremium);
        Assert.Empty(p.Client.QueriedTokens);
    }

    /// NEDEN: Google'ın geçici hatası durum ucunu (paywall/ana ekran) bozmamalı; eski veriyle yanıt dönmeli.
    [Fact]
    public async Task DurumSorgusu_GoogleHatasindaBozulmaz()
    {
        using var o = new Ortam();
        var expiry = DateTime.UtcNow.AddMinutes(-5);
        var verified = DateTime.UtcNow.AddHours(-3);
        await o.Ekle(Kayit(o, expiry: expiry, verified: verified));
        o.Client.GetResult = _ => throw new HttpRequestException("Örnek sorgu hatası.");

        var status = await o.Service.GetStatusAsync();

        Assert.False(status.IsPremium);
        Assert.Single(o.Client.QueriedTokens);
        var row = Assert.Single(await o.Oku());
        Assert.Equal(expiry, row.ExpiresAt);
        Assert.Equal(verified, row.LastVerifiedAt);
        Assert.True(row.AutoRenewing);
    }

    /// NEDEN: Aktif aboneliği olan kullanıcıda yenileme gereksiz; her durum çağrısı Google kotasını tüketmemeli.
    [Fact]
    public async Task DurumSorgusu_AktifAbonelikVarsaGoogleaSormaz()
    {
        using var o = new Ortam();
        // Aktif bir satır + eski, bayat ve otomatik yenilenen bir satır: yine de sorulmamalı.
        await o.Ekle(
            Kayit(o, "ornek-aktif-token", DateTime.UtcNow.AddDays(10)),
            Kayit(o, "ornek-eski-token", DateTime.UtcNow.AddDays(-3)));

        var status = await o.Service.GetStatusAsync();

        Assert.True(status.IsPremium);
        Assert.Empty(o.Client.QueriedTokens);
    }

    /// NEDEN: Google yapılandırılmamışsa (geliştirme/eksik anahtar) durum ucu hiç dış çağrı denememeli.
    [Fact]
    public async Task DurumSorgusu_YapilandirmaYoksaGoogleaSormaz()
    {
        using var o = new Ortam();
        var expiry = DateTime.UtcNow.AddMinutes(-5);
        await o.Ekle(Kayit(o, expiry: expiry));
        o.Client.IsConfigured = false;

        var status = await o.Service.GetStatusAsync();

        Assert.False(status.IsPremium);
        Assert.Empty(o.Client.QueriedTokens);
        Assert.Equal(expiry, Assert.Single(await o.Oku()).ExpiresAt);
    }

    /// NEDEN: Google jetonu tanımıyorsa satır kapanmalı; yoksa her durum çağrısı iki dakikada bir boşuna sorardı.
    [Fact]
    public async Task DurumSorgusu_GoogleNullDonerse_OtomatikYenilemeKapanir()
    {
        using var o = new Ortam();
        var expiry = DateTime.UtcNow.AddMinutes(-5);
        await o.Ekle(Kayit(o, expiry: expiry));
        o.Client.Result = null;

        Assert.False((await o.Service.GetStatusAsync()).IsPremium);
        var row = Assert.Single(await o.Oku());
        Assert.False(row.AutoRenewing);
        Assert.Equal(expiry, row.ExpiresAt);
        Assert.Single(o.Client.QueriedTokens);

        // Bayat hâle getirilse bile artık AutoRenewing=false olduğundan sorulmaz.
        row = await o.Context.Subscriptions.SingleAsync();
        row.LastVerifiedAt = DateTime.UtcNow.AddHours(-1);
        await o.Context.SaveChangesAsync();
        Assert.False((await o.Service.GetStatusAsync()).IsPremium);
        Assert.Single(o.Client.QueriedTokens);
    }
}
