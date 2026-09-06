using Microsoft.Extensions.Configuration;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SmartFinance.Application.Common;
using SmartFinance.Application.DTOs.Budget;
using SmartFinance.Application.DTOs.Investment;
using SmartFinance.Application.DTOs.MarketData;
using SmartFinance.Application.DTOs.PdfImport;
using SmartFinance.Application.Exceptions;
using SmartFinance.Application.Interfaces;
using SmartFinance.Domain.Entities;
using SmartFinance.Domain.Enums;
using SmartFinance.Infrastructure.Context;
using SmartFinance.Infrastructure.Repositories;
using SmartFinance.Infrastructure.Services;

namespace SmartFinance.Tests;

/// Ucretsiz katman sinirlari. Sinirlarin uygulandigi TEK yer sunucudur —
/// uygulama degistirilebilir, istekler elle atilabilir. Bu yuzden sinirlarin
/// servis katmaninda tuttugunu dogrulamak kritik.
public class FreeTierLimitTests
{
    private sealed class Ortam
    {
        public SmartFinanceDbContext Context = null!;
        public int UserId;
        public InvestmentService Investments = null!;
        public BudgetService Budgets = null!;
        public PdfImportService Import = null!;
        public SubscriptionService Subscription = null!;
        public Mock<IMarketDataService> MarketData = null!;
    }

    private static Ortam Kur()
    {
        var options = new DbContextOptionsBuilder<SmartFinanceDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString()).Options;
        var context = new SmartFinanceDbContext(options);

        var user = new User { FullName = "Test", Email = $"{Guid.NewGuid()}@test.com", PasswordHash = "x" };
        context.Users.Add(user);
        context.SaveChanges();

        var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()) });
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
        var currentUser = new CurrentUserService(accessor);
        var entitlement = new EntitlementService(context, currentUser, BosYapilandirma());

        var marketData = new Mock<IMarketDataService>();
        marketData.Setup(m => m.GetCurrentPriceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PriceQuoteDto { Symbol = "X", Price = 100m, AsOf = DateTime.UtcNow });

        return new Ortam
        {
            Context = context,
            UserId = user.Id,
            MarketData = marketData,
            Investments = new InvestmentService(
                new GenericRepository<Investment>(context), context, currentUser, marketData.Object, entitlement),
            Budgets = new BudgetService(
                new GenericRepository<Budget>(context), context, currentUser, entitlement),
            Import = new PdfImportService(
                context, currentUser, NullLogger<PdfImportService>.Instance, entitlement),
            Subscription = new SubscriptionService(context, currentUser, entitlement),
        };
    }

    private static void PremiumYap(Ortam o, DateTime? bitis = null)
    {
        o.Context.Subscriptions.Add(new Subscription
        {
            UserId = o.UserId,
            ProductId = "premium_monthly",
            PurchaseToken = Guid.NewGuid().ToString(),
            StartsAt = DateTime.UtcNow.AddDays(-1),
            ExpiresAt = bitis ?? DateTime.UtcNow.AddDays(30),
            AutoRenewing = true,
        });
        o.Context.SaveChanges();
    }

    private static CreateInvestmentDto Yatirim(string ad) => new()
    { Name = ad, PurchasePrice = 100, Quantity = 1, InvestmentType = "stock" };

    private static ConfirmImportDto Ekstre(string aciklama) => new()
    {
        Transactions = new List<ConfirmTransactionItemDto>
        {
            new() { Amount = 10, Description = aciklama, TransactionDate = DateTime.UtcNow.Date, Type = 2 },
        }
    };

    // ─── Yatirim siniri ──────────────────────────────────────────

    [Fact]
    public async Task Ucretsiz_SinirdakiSonYatirimEklenebilir()
    {
        var o = Kur();
        for (var i = 0; i < FreeTierLimits.Investments; i++)
            await o.Investments.CreateInvestmentAsync(Yatirim($"HISSE{i}"));

        Assert.Equal(FreeTierLimits.Investments, o.Context.Investments.Count());
    }

    [Fact]
    public async Task Ucretsiz_SiniriAsanYatirim_PremiumRequired()
    {
        var o = Kur();
        for (var i = 0; i < FreeTierLimits.Investments; i++)
            await o.Investments.CreateInvestmentAsync(Yatirim($"HISSE{i}"));

        var hata = await Assert.ThrowsAsync<PremiumRequiredException>(
            () => o.Investments.CreateInvestmentAsync(Yatirim("FAZLA")));

        Assert.Contains("Premium", hata.Message);
        Assert.Equal(FreeTierLimits.Investments, o.Context.Investments.Count());
    }

    [Fact]
    public async Task Premium_YatirimSiniriYok()
    {
        var o = Kur();
        PremiumYap(o);

        for (var i = 0; i < FreeTierLimits.Investments + 3; i++)
            await o.Investments.CreateInvestmentAsync(Yatirim($"HISSE{i}"));

        Assert.Equal(FreeTierLimits.Investments + 3, o.Context.Investments.Count());
    }

    /// Sinirdaki kullanici MEVCUT pozisyonuna ekleme yapabilmeli — sahip
    /// oldugu veriyi guncelleyememek kabul edilemez.
    [Fact]
    public async Task Ucretsiz_Sinirdayken_MevcutPozisyonaEklemeSerbest()
    {
        var o = Kur();
        for (var i = 0; i < FreeTierLimits.Investments; i++)
            await o.Investments.CreateInvestmentAsync(Yatirim($"HISSE{i}"));

        var sonuc = await o.Investments.CreateInvestmentAsync(Yatirim("HISSE0"));

        Assert.True(sonuc.Merged);
        Assert.Equal(2, sonuc.Quantity);
        Assert.Equal(FreeTierLimits.Investments, o.Context.Investments.Count());
    }

    /// Suresi gecmis abonelik premium saymamali.
    [Fact]
    public async Task SuresiGecmisAbonelik_PremiumSaymaz()
    {
        var o = Kur();
        PremiumYap(o, bitis: DateTime.UtcNow.AddDays(-1));

        for (var i = 0; i < FreeTierLimits.Investments; i++)
            await o.Investments.CreateInvestmentAsync(Yatirim($"HISSE{i}"));

        await Assert.ThrowsAsync<PremiumRequiredException>(
            () => o.Investments.CreateInvestmentAsync(Yatirim("FAZLA")));
    }

    /// Iptal edilmis ama suresi dolmamis abonelik gecerli kalmali: kullanici
    /// parasini odedigi donemin hakkini kaybetmemeli.
    [Fact]
    public async Task IptalEdilmisAmaSuresiDolmamisAbonelik_PremiumSayilir()
    {
        var o = Kur();
        o.Context.Subscriptions.Add(new Subscription
        {
            UserId = o.UserId,
            ProductId = "premium_monthly",
            PurchaseToken = Guid.NewGuid().ToString(),
            StartsAt = DateTime.UtcNow.AddDays(-20),
            ExpiresAt = DateTime.UtcNow.AddDays(10),
            AutoRenewing = false,   // iptal edilmis
        });
        o.Context.SaveChanges();

        for (var i = 0; i < FreeTierLimits.Investments + 2; i++)
            await o.Investments.CreateInvestmentAsync(Yatirim($"HISSE{i}"));

        Assert.Equal(FreeTierLimits.Investments + 2, o.Context.Investments.Count());
    }

    // ─── Butce siniri ────────────────────────────────────────────

    private async Task<int> KategoriEkle(Ortam o, string ad)
    {
        var k = new Category { Name = ad, Type = TransactionType.Expense, UserId = o.UserId };
        o.Context.Categories.Add(k);
        await o.Context.SaveChangesAsync();
        return k.Id;
    }

    [Fact]
    public async Task Ucretsiz_SiniriAsanButce_PremiumRequired()
    {
        var o = Kur();
        for (var i = 0; i < FreeTierLimits.Budgets; i++)
        {
            var kid = await KategoriEkle(o, $"Kategori{i}");
            await o.Budgets.UpsertAsync(new CreateBudgetDto { CategoryId = kid, MonthlyLimit = 100 });
        }

        var fazlaKid = await KategoriEkle(o, "Fazla");
        await Assert.ThrowsAsync<PremiumRequiredException>(
            () => o.Budgets.UpsertAsync(new CreateBudgetDto { CategoryId = fazlaKid, MonthlyLimit = 100 }));
    }

    /// Sinirdaki kullanici MEVCUT butcesinin limitini degistirebilmeli.
    [Fact]
    public async Task Ucretsiz_SinirdaykenMevcutButceGuncellenebilir()
    {
        var o = Kur();
        var ilkKid = 0;
        for (var i = 0; i < FreeTierLimits.Budgets; i++)
        {
            var kid = await KategoriEkle(o, $"Kategori{i}");
            if (i == 0) ilkKid = kid;
            await o.Budgets.UpsertAsync(new CreateBudgetDto { CategoryId = kid, MonthlyLimit = 100 });
        }

        var sonuc = await o.Budgets.UpsertAsync(new CreateBudgetDto { CategoryId = ilkKid, MonthlyLimit = 500 });

        Assert.Equal(500, sonuc.MonthlyLimit);
        Assert.Equal(FreeTierLimits.Budgets, o.Context.Budgets.Count());
    }

    // ─── Ice aktarma siniri ──────────────────────────────────────

    [Fact]
    public async Task Ucretsiz_AylikIceAktarmaSiniriAsilinca_PremiumRequired()
    {
        var o = Kur();
        for (var i = 0; i < FreeTierLimits.ImportsPerMonth; i++)
            await o.Import.ConfirmImportAsync(Ekstre($"islem {i}"));

        await Assert.ThrowsAsync<PremiumRequiredException>(
            () => o.Import.ConfirmImportAsync(Ekstre("fazla")));
    }

    /// Tamami mukerrer oldugu icin hicbir sey eklenmeyen yukleme hak yakmamali.
    [Fact]
    public async Task HicbirSeyEklenmeyenIceAktarma_HakYakmaz()
    {
        var o = Kur();
        await o.Import.ConfirmImportAsync(Ekstre("ayni islem"));
        // Ayni dosya tekrar: hepsi atlanir, kayit olusmaz.
        var ikinci = await o.Import.ConfirmImportAsync(Ekstre("ayni islem"));
        Assert.Equal(0, ikinci.SavedCount);

        // Hak yakilmadiysa bir tane daha yapilabilmeli.
        var ucuncu = await o.Import.ConfirmImportAsync(Ekstre("yeni islem"));
        Assert.Equal(1, ucuncu.SavedCount);
    }

    [Fact]
    public async Task Premium_IceAktarmaSiniriYok()
    {
        var o = Kur();
        PremiumYap(o);

        for (var i = 0; i < FreeTierLimits.ImportsPerMonth + 3; i++)
            await o.Import.ConfirmImportAsync(Ekstre($"islem {i}"));

        Assert.Equal(FreeTierLimits.ImportsPerMonth + 3, o.Context.ImportLogs.Count());
    }

    // ─── Durum ucu ───────────────────────────────────────────────

    [Fact]
    public async Task Durum_UcretsizKullanicidaSinirlariBildirir()
    {
        var o = Kur();
        await o.Investments.CreateInvestmentAsync(Yatirim("THYAO"));

        var durum = await o.Subscription.GetStatusAsync();

        Assert.False(durum.IsPremium);
        Assert.False(durum.IndicatorsIncluded);
        Assert.Equal(1, durum.Investments.Used);
        Assert.Equal(FreeTierLimits.Investments, durum.Investments.Limit);
        Assert.Null(durum.ExpiresAt);
    }

    /// Premium'da sinir null donmeli — uygulama "5/5" yerine hicbir sayac
    /// gostermiyor.
    [Fact]
    public async Task Durum_PremiumKullanicidaSinirNullDoner()
    {
        var o = Kur();
        PremiumYap(o);

        var durum = await o.Subscription.GetStatusAsync();

        Assert.True(durum.IsPremium);
        Assert.True(durum.IndicatorsIncluded);
        Assert.Null(durum.Investments.Limit);
        Assert.Null(durum.Budgets.Limit);
        Assert.Null(durum.MonthlyImports.Limit);
        Assert.NotNull(durum.ExpiresAt);
        Assert.True(durum.AutoRenewing);
    }

    /// EntitlementService yapilandirmadan "odeme olmadan premium" listesini
    /// okuyor. Testlerde bu liste BOS olmali: dolu olsaydi limit testleri
    /// sessizce premium yolundan gecip hicbir sey dogrulamaz hale gelirdi.
    private static IConfiguration BosYapilandirma() =>
        new ConfigurationBuilder().Build();
}
