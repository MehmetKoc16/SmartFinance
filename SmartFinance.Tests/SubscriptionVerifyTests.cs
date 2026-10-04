using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SmartFinance.Application.DTOs.Subscription;
using SmartFinance.Application.Exceptions;
using SmartFinance.Application.Interfaces;
using SmartFinance.Domain.Entities;
using SmartFinance.Infrastructure.Billing;
using SmartFinance.Infrastructure.Context;
using SmartFinance.Infrastructure.Services;

namespace SmartFinance.Tests;

internal sealed class SahtePlayBillingClient : IPlayBillingClient
{
    public bool IsConfigured { get; set; } = true;
    public PlaySubscriptionInfo? Result { get; set; }
    public Func<string, PlaySubscriptionInfo?>? GetResult { get; set; }
    public bool AcknowledgeFails { get; set; }
    public List<string> QueriedTokens { get; } = new();
    public List<(string ProductId, string Token)> Acknowledgements { get; } = new();

    public Task<PlaySubscriptionInfo?> GetSubscriptionAsync(string purchaseToken, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        QueriedTokens.Add(purchaseToken);
        return Task.FromResult(GetResult is null ? Result : GetResult(purchaseToken));
    }

    public Task AcknowledgeAsync(string productId, string purchaseToken, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Acknowledgements.Add((productId, purchaseToken));
        if (AcknowledgeFails) throw new HttpRequestException("Örnek onay hatası.");
        return Task.CompletedTask;
    }
}

public class SubscriptionVerifyTests
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
            Client.Result = Bilgi(DateTime.UtcNow);
        }

        public void Dispose() => Context.Dispose();
    }

    internal static PlaySubscriptionInfo Bilgi(DateTime nowUtc) => new(
        "premium_monthly", "SUBSCRIPTION_STATE_ACTIVE", nowUtc.AddDays(30), nowUtc.AddDays(-1),
        true, false, null, "ornek-siparis", null);

    /// NEDEN: Premium istemcinin beyanıyla değil Google'ın sonucu ve kalıcı kayıtla açılmalı.
    [Fact]
    public async Task AktifAlim_PremiumOlur_Kaydedilir_VeOnaylanir()
    {
        using var o = new Ortam();
        o.Client.Result = o.Client.Result! with { ObfuscatedAccountId = o.Service.AccountToken(o.UserId) };
        var before = DateTime.UtcNow;
        var status = await o.Service.VerifyPurchaseAsync("premium_monthly", "ornek-token", default);

        Assert.True(status.IsPremium);
        Assert.False(status.PurchasePending);
        Assert.Equal(o.Service.AccountToken(o.UserId), status.AccountToken);
        var row = Assert.Single(await o.Context.Subscriptions.AsNoTracking().ToListAsync());
        Assert.Equal(o.UserId, row.UserId);
        Assert.Equal("premium_monthly", row.ProductId);
        Assert.Equal("ornek-token", row.PurchaseToken);
        Assert.Equal(o.Client.Result.LatestOrderId, row.OrderId);
        Assert.Equal(o.Client.Result.StartedAtUtc, row.StartsAt);
        Assert.Equal(o.Client.Result.ExpiresAtUtc, row.ExpiresAt);
        Assert.True(row.AutoRenewing);
        Assert.NotNull(row.LastVerifiedAt);
        Assert.InRange(row.LastVerifiedAt!.Value, before, DateTime.UtcNow);
        Assert.Equal(("premium_monthly", "ornek-token"), Assert.Single(o.Client.Acknowledgements));
    }

    /// NEDEN: Geri yükleme ve yinelenen istekler aynı satın alımı çoğaltmamalı; durum tazelenmeli.
    [Fact]
    public async Task AyniToken_TekSatirKalir_VeGuncellenir()
    {
        using var o = new Ortam();
        await o.Service.VerifyPurchaseAsync("premium_monthly", "ornek-token", default);
        var id = o.Context.Subscriptions.Single().Id;
        o.Client.Result = o.Client.Result! with
        {
            ExpiresAtUtc = DateTime.UtcNow.AddDays(60), AutoRenewing = false,
            LatestOrderId = "ornek-yeni-siparis", Acknowledged = true,
        };
        var status = await o.Service.VerifyPurchaseAsync("premium_monthly", "ornek-token", default);
        var row = Assert.Single(await o.Context.Subscriptions.AsNoTracking().ToListAsync());
        Assert.Equal(id, row.Id);
        Assert.Equal(o.Client.Result.ExpiresAtUtc, row.ExpiresAt);
        Assert.Equal("ornek-yeni-siparis", row.OrderId);
        Assert.False(row.AutoRenewing);
        Assert.True(status.IsPremium);
        Assert.Single(o.Client.Acknowledgements);
    }

    /// NEDEN: Yıllık plan da aylık planla aynı güvenli doğrulama yolunu kullanmalı.
    [Fact]
    public async Task YillikUrun_Dogrulanir()
    {
        using var o = new Ortam();
        o.Client.Result = o.Client.Result! with { ProductId = "premium_yearly" };
        Assert.True((await o.Service.VerifyPurchaseAsync("premium_yearly", "ornek-token", default)).IsPremium);
        Assert.Equal("premium_yearly", o.Context.Subscriptions.Single().ProductId);
    }

    /// NEDEN: Desteklenmeyen ürün Google sorgusundan önce reddedilmeli.
    [Theory]
    [InlineData("")]
    [InlineData("ornek-urun")]
    public async Task GecersizUrun_Reddedilir(string productId)
    {
        using var o = new Ortam();
        var ex = await Assert.ThrowsAsync<BadRequestException>(() =>
            o.Service.VerifyPurchaseAsync(productId, "ornek-token", default));
        Assert.Equal("Geçersiz ürün.", ex.Message);
        Assert.Empty(o.Client.QueriedTokens);
        Assert.Empty(o.Context.Subscriptions);
    }

    /// NEDEN: Boş veya indeks sınırını aşan jeton dış servise gönderilmemeli.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task BosToken_Reddedilir(string? token)
    {
        using var o = new Ortam();
        await Assert.ThrowsAsync<BadRequestException>(() =>
            o.Service.VerifyPurchaseAsync("premium_monthly", token!, default));
        Assert.Empty(o.Client.QueriedTokens);
    }

    /// NEDEN: Servis doğrudan çağrılsa da DTO'nun jeton uzunluğu sınırı korunmalı.
    [Fact]
    public async Task UzunToken_Reddedilir()
    {
        using var o = new Ortam();
        await Assert.ThrowsAsync<BadRequestException>(() =>
            o.Service.VerifyPurchaseAsync("premium_monthly", new string('x', 451), default));
        Assert.Empty(o.Client.QueriedTokens);
    }

    /// NEDEN: İstemcinin ürün kimliği Google'ın doğruladığı ürün yerine kullanılamaz.
    [Fact]
    public async Task GoogleUrunuFarkliysa_Reddedilir()
    {
        using var o = new Ortam();
        o.Client.Result = o.Client.Result! with { ProductId = "premium_yearly" };
        await Assert.ThrowsAsync<BadRequestException>(() =>
            o.Service.VerifyPurchaseAsync("premium_monthly", "ornek-token", default));
        Assert.Empty(o.Context.Subscriptions);
        Assert.Empty(o.Client.Acknowledgements);
    }

    /// NEDEN: Google'da bulunmayan satın alım premium sağlamamalı.
    [Fact]
    public async Task GoogleNullDonerse_Reddedilir()
    {
        using var o = new Ortam();
        o.Client.Result = null;
        var ex = await Assert.ThrowsAsync<BadRequestException>(() =>
            o.Service.VerifyPurchaseAsync("premium_monthly", "ornek-token", default));
        Assert.Equal("Satın alma bulunamadı.", ex.Message);
        Assert.Empty(o.Context.Subscriptions);
    }

    /// NEDEN: Play hesabı bağlanmış jeton başka uygulama hesabına aktarılamaz.
    [Fact]
    public async Task HesapKimligiBaskasinaAitse_Reddedilir()
    {
        using var o = new Ortam();
        o.Client.Result = o.Client.Result! with { ObfuscatedAccountId = o.Service.AccountToken(o.UserId + 1) };
        var ex = await Assert.ThrowsAsync<BadRequestException>(() =>
            o.Service.VerifyPurchaseAsync("premium_monthly", "ornek-token", default));
        Assert.Equal("Bu satın alma başka bir hesaba ait.", ex.Message);
        Assert.Empty(o.Context.Subscriptions);
    }

    /// NEDEN: Eski veya geri yüklenen, hesap kimliği taşımayan satın alımlar kabul edilmeli.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task HesapKimligiBossa_KabulEdilir(string? accountId)
    {
        using var o = new Ortam();
        o.Client.Result = o.Client.Result! with { ObfuscatedAccountId = accountId };
        Assert.True((await o.Service.VerifyPurchaseAsync("premium_monthly", "ornek-token", default)).IsPremium);
    }

    /// NEDEN: Soft delete filtresi başka kullanıcıya bağlı jetonun sahipliğini gizleyememeli.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TokenBaskaKullaniciyaKayitliysa_Reddedilir(bool deleted)
    {
        using var o = new Ortam();
        o.Context.Subscriptions.Add(new Subscription
        {
            UserId = o.UserId + 1, ProductId = "premium_monthly", PurchaseToken = "ornek-token",
            ExpiresAt = DateTime.UtcNow.AddDays(30), IsDeleted = deleted,
        });
        await o.Context.SaveChangesAsync();
        var ex = await Assert.ThrowsAsync<BadRequestException>(() =>
            o.Service.VerifyPurchaseAsync("premium_monthly", "ornek-token", default));
        Assert.Equal("Bu satın alma başka bir hesaba ait.", ex.Message);
        Assert.Equal(o.UserId + 1, o.Context.Subscriptions.IgnoreQueryFilters().Single().UserId);
        Assert.Empty(o.Client.Acknowledgements);
    }

    /// NEDEN: Bekleyen ödeme tamamlanmış abonelik gibi kaydedilmemeli veya onaylanmamalı.
    [Fact]
    public async Task BekleyenOdeme_KayitYazmaz_PremiumVermez()
    {
        using var o = new Ortam();
        o.Client.Result = o.Client.Result! with { State = "SUBSCRIPTION_STATE_PENDING" };
        var status = await o.Service.VerifyPurchaseAsync("premium_monthly", "ornek-token", default);
        Assert.True(status.PurchasePending);
        Assert.False(status.IsPremium);
        Assert.Empty(o.Context.Subscriptions);
        Assert.Empty(o.Client.Acknowledgements);
    }

    /// NEDEN: Aynı scope'ta önce ücretsiz durum okunması, satın alma sonrası eski sonucu döndürmemeli.
    [Fact]
    public async Task BekleyenSonraAktifOdeme_GuncelPremiumDoner()
    {
        using var o = new Ortam();
        o.Client.Result = o.Client.Result! with { State = "SUBSCRIPTION_STATE_PENDING" };
        Assert.False((await o.Service.VerifyPurchaseAsync("premium_monthly", "ornek-token", default)).IsPremium);
        o.Client.Result = o.Client.Result with { State = "SUBSCRIPTION_STATE_ACTIVE" };
        var status = await o.Service.VerifyPurchaseAsync("premium_monthly", "ornek-token", default);
        Assert.True(status.IsPremium);
        Assert.False(status.PurchasePending);
    }

    /// NEDEN: İptal ve ödeme tolerans dönemi mevcut dönem hakkını sona erdirmemeli.
    [Theory]
    [InlineData("SUBSCRIPTION_STATE_CANCELED")]
    [InlineData("SUBSCRIPTION_STATE_IN_GRACE_PERIOD")]
    public async Task IptalVeToleransDonemi_BitiseKadarPremiumSurer(string state)
    {
        using var o = new Ortam();
        o.Client.Result = o.Client.Result! with { State = state, AutoRenewing = false };
        var status = await o.Service.VerifyPurchaseAsync("premium_monthly", "ornek-token", default);
        Assert.True(status.IsPremium);
        Assert.Equal(o.Client.Result.ExpiresAtUtc, status.ExpiresAt);
        Assert.False(status.AutoRenewing);
        Assert.Single(o.Client.Acknowledgements);
    }

    /// NEDEN: Bitiş tarihi gelecekte görünse bile sona ermiş/askıdaki/duraklatılmış durum hak vermemeli.
    [Theory]
    [InlineData("SUBSCRIPTION_STATE_EXPIRED")]
    [InlineData("SUBSCRIPTION_STATE_ON_HOLD")]
    [InlineData("SUBSCRIPTION_STATE_PAUSED")]
    [InlineData("SUBSCRIPTION_STATE_PENDING_PURCHASE_CANCELED")]
    public async Task GecersizDurumlar_PremiumVermez(string state)
    {
        using var o = new Ortam();
        o.Client.Result = o.Client.Result! with { State = state };
        Assert.False((await o.Service.VerifyPurchaseAsync("premium_monthly", "ornek-token", default)).IsPremium);
        Assert.True(o.Context.Subscriptions.Single().ExpiresAt <= DateTime.UtcNow);
        Assert.Empty(o.Client.Acknowledgements);
    }

    /// NEDEN: Gerçek geçmiş bitiş tarihi şimdiye ileri taşınmamalı.
    [Fact]
    public async Task SonaErmisAlim_GecmisBitisiKorur()
    {
        using var o = new Ortam();
        o.Client.Result = o.Client.Result! with
        {
            State = "SUBSCRIPTION_STATE_EXPIRED", ExpiresAtUtc = DateTime.UtcNow.AddDays(-2),
        };
        Assert.False((await o.Service.VerifyPurchaseAsync("premium_monthly", "ornek-token", default)).IsPremium);
        Assert.Equal(o.Client.Result.ExpiresAtUtc, o.Context.Subscriptions.Single().ExpiresAt);
    }

    /// NEDEN: Plan değişiminde eski jetonun hakkı sonlanmalı ve yeni jeton geçerli kalmalı.
    [Fact]
    public async Task BagliToken_EskiAboneligiSonlandirir()
    {
        using var o = new Ortam();
        await o.Service.VerifyPurchaseAsync("premium_monthly", "ornek-eski-token", default);
        o.Client.Result = o.Client.Result! with { LinkedPurchaseToken = "ornek-eski-token" };
        var status = await o.Service.VerifyPurchaseAsync("premium_monthly", "ornek-yeni-token", default);
        Assert.True(status.IsPremium);
        Assert.Equal(2, await o.Context.Subscriptions.CountAsync());
        Assert.True(o.Context.Subscriptions.Single(s => s.PurchaseToken == "ornek-eski-token").ExpiresAt <= DateTime.UtcNow);
        Assert.Equal(o.Client.Result.ExpiresAtUtc,
            o.Context.Subscriptions.Single(s => s.PurchaseToken == "ornek-yeni-token").ExpiresAt);
    }

    /// NEDEN: Bağlı jeton alanı üzerinden başka hesabın aboneliği sonlandırılamamalı.
    [Fact]
    public async Task BagliTokenBaskaHesabaAitse_Reddedilir()
    {
        using var o = new Ortam();
        var expiry = DateTime.UtcNow.AddDays(30);
        o.Context.Subscriptions.Add(new Subscription
        {
            UserId = o.UserId + 1, ProductId = "premium_monthly", PurchaseToken = "ornek-eski-token", ExpiresAt = expiry,
        });
        await o.Context.SaveChangesAsync();
        o.Client.Result = o.Client.Result! with { LinkedPurchaseToken = "ornek-eski-token" };
        await Assert.ThrowsAsync<BadRequestException>(() =>
            o.Service.VerifyPurchaseAsync("premium_monthly", "ornek-yeni-token", default));
        Assert.Equal(expiry, Assert.Single(o.Context.Subscriptions).ExpiresAt);
        Assert.Empty(o.Client.Acknowledgements);
    }

    /// NEDEN: Onay çağrısı geçici hata verirse doğrulanmış satın alma kaybedilmemeli.
    [Fact]
    public async Task OnayHatasi_KayitYazilmasiniEngellemez()
    {
        using var o = new Ortam();
        o.Client.AcknowledgeFails = true;
        Assert.True((await o.Service.VerifyPurchaseAsync("premium_monthly", "ornek-token", default)).IsPremium);
        Assert.Single(await o.Context.Subscriptions.AsNoTracking().ToListAsync());
        Assert.Single(o.Client.Acknowledgements);
    }

    /// NEDEN: Eksik sunucu yapılandırması kullanıcıya güvenli, tekrar deneme mesajı vermeli.
    [Fact]
    public async Task YapilandirmaYoksa_DisServisHatasiDoner()
    {
        using var o = new Ortam();
        o.Client.IsConfigured = false;
        var ex = await Assert.ThrowsAsync<ExternalServiceException>(() =>
            o.Service.VerifyPurchaseAsync("premium_monthly", "ornek-token", default));
        Assert.Equal("Satın alma şu an doğrulanamıyor. Lütfen biraz sonra tekrar deneyin.", ex.UserMessage);
        Assert.Empty(o.Client.QueriedTokens);
        Assert.Empty(o.Context.Subscriptions);
    }

    /// NEDEN: Sağlayıcı hatası kullanıcıya Google URL'si veya satın alma jetonu sızdırmamalı.
    [Fact]
    public async Task GoogleHatasi_GuvenliDisServisHatasinaDonusur()
    {
        using var o = new Ortam();
        o.Client.GetResult = _ => throw new HttpRequestException("ornek-token");
        var ex = await Assert.ThrowsAsync<ExternalServiceException>(() =>
            o.Service.VerifyPurchaseAsync("premium_monthly", "ornek-token", default));
        Assert.DoesNotContain("ornek-token", ex.Message);
        Assert.Empty(o.Context.Subscriptions);
    }

    /// NEDEN: Play hesap kimliği aynı hesapta sabit, hesaplar arasında farklı ve tam SHA-256 hex olmalı.
    [Fact]
    public async Task HesapTokeni_Sabit_Farkli_VeAltmisDortKarakterdir()
    {
        using var o = new Ortam();
        var token = o.Service.AccountToken(o.UserId);
        Assert.Equal(token, o.Service.AccountToken(o.UserId));
        Assert.NotEqual(token, o.Service.AccountToken(o.UserId + 1));
        Assert.Equal(64, token.Length);
        Assert.Matches("^[0-9a-f]{64}$", token);
        Assert.Equal(token, (await o.Service.GetStatusAsync()).AccountToken);
    }

    /// NEDEN: Kimlik dosyası yapılandırılmadan uygulama açılabilmeli, gerçek çağrılar açıkça reddedilmeli.
    [Fact]
    public async Task GoogleIstemcisi_YapilandirmaYokkenGuvenleOlusur()
    {
        using var client = new GooglePlayBillingClient(new ConfigurationBuilder().Build());
        Assert.False(client.IsConfigured);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetSubscriptionAsync("ornek-token", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.AcknowledgeAsync("premium_monthly", "ornek-token", default));
    }

    /// NEDEN: HTTP model doğrulaması boş ürün/jeton ve aşırı uzun jetonu kabul etmemeli.
    [Theory]
    [InlineData("", "ornek-token")]
    [InlineData("premium_monthly", "")]
    public void DogrulamaDto_GerekliAlanlariZorunluTutar(string productId, string token)
    {
        var dto = new VerifyPurchaseDto { ProductId = productId, PurchaseToken = token };
        Assert.False(Validator.TryValidateObject(dto, new ValidationContext(dto), new List<ValidationResult>(), true));
    }

    /// NEDEN: HTTP isteği veritabanı jeton sınırını aşmamalı.
    [Fact]
    public void DogrulamaDto_UzunTokeniReddeder()
    {
        var dto = new VerifyPurchaseDto { ProductId = "premium_monthly", PurchaseToken = new string('x', 451) };
        Assert.False(Validator.TryValidateObject(dto, new ValidationContext(dto), new List<ValidationResult>(), true));
    }
}
