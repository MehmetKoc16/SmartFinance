using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Moq;
using SmartFinance.Application.DTOs.Investment;
using SmartFinance.Application.DTOs.MarketData;
using SmartFinance.Application.Exceptions;
using SmartFinance.Application.Interfaces;
using SmartFinance.Domain.Entities;
using SmartFinance.Infrastructure.Context;
using SmartFinance.Infrastructure.Repositories;
using SmartFinance.Infrastructure.Services;

namespace SmartFinance.Tests;

public class InvestmentServiceTests
{
    private (InvestmentService service,SmartFinanceDbContext context,int userId,Mock<IMarketDataService> marketData) CreateService()
    {
        var options=new DbContextOptionsBuilder<SmartFinanceDbContext>().UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString()).Options;
        var context = new SmartFinanceDbContext(options);

        var user = new User { FullName = "Test Kullanıcı", Email = $"{Guid.NewGuid()}@test.com", PasswordHash = "x" };
        context.Users.Add(user);
        context.SaveChanges();

        var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()) });
        var httpContextAccessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };

        var marketData = new Mock<IMarketDataService>();
        var repository = new GenericRepository<Investment>(context);
        var service = new InvestmentService(repository, context, new CurrentUserService(httpContextAccessor), marketData.Object);
        return (service, context, user.Id, marketData);
    }

    [Fact]
    public async Task CreateInvestment_GecerliSembol_FiyatSaglayicidanAlinipKaydedilir()
    {
        var (service, _, _, marketData) = CreateService();
        marketData.Setup(m => m.GetCurrentPriceAsync("THYAO", "stock", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PriceQuoteDto { Symbol = "THYAO", Price = 275.50m, AsOf = DateTime.UtcNow });

        var result = await service.CreateInvestmentAsync(new CreateInvestmentDto
        {
            Name = "THYAO",
            FullName = "Türk Hava Yolları",
            PurchasePrice = 250,
            Quantity = 10,
            InvestmentType = "stock"
        });

        Assert.Equal(275.50m, result.CurrentPrice);
    }

    [Fact]
    public async Task CreateInvestment_SaglayiciHataFirlatirsa_KayitOlusturulmaz()
    {
        // Yanlis/bulunamayan sembol icin saglayici hata firlatirsa kayit
        // hic olusturulmamali — kullanici once sembolu duzeltmeli.
        var (service, context, _, marketData) = CreateService();
        marketData.Setup(m => m.GetCurrentPriceAsync("YOKSEMBOL", "stock", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ExternalServiceException("Sembol bulunamadı!"));

        await Assert.ThrowsAsync<ExternalServiceException>(() => service.CreateInvestmentAsync(new CreateInvestmentDto
        {
            Name = "YOKSEMBOL",
            FullName = "Yok",
            PurchasePrice = 10,
            Quantity = 1,
            InvestmentType = "stock"
        }));

        Assert.Empty(context.Investments);
    }

    /// Ayni semboldan tekrar alim yeni satir acmamali: portfoyde ayni hisse
    /// birden cok kez gorunurse "bu hissede ne kadar kardayim" sorusunun tek
    /// bir cevabi olmaz.
    [Fact]
    public async Task CreateInvestment_AyniSembolTekrarAlinirsa_YeniKayitAcilmaz()
    {
        var (service, context, _, marketData) = CreateService();
        marketData.Setup(m => m.GetCurrentPriceAsync("THYAO", "stock", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PriceQuoteDto { Symbol = "THYAO", Price = 300m, AsOf = DateTime.UtcNow });

        await service.CreateInvestmentAsync(new CreateInvestmentDto
        { Name = "THYAO", PurchasePrice = 100, Quantity = 1, InvestmentType = "stock" });
        var second = await service.CreateInvestmentAsync(new CreateInvestmentDto
        { Name = "THYAO", PurchasePrice = 200, Quantity = 9, InvestmentType = "stock" });

        Assert.Single(context.Investments);
        Assert.True(second.Merged);
        Assert.Equal(10, second.Quantity);
    }

    /// Agirlikli ortalama: 1 adet 100'den + 9 adet 200'den = 190.
    /// Iki fiyatin basit ortalamasi (150) yanlis olurdu.
    [Fact]
    public async Task CreateInvestment_TekrarAlim_MaliyetiAgirlikliOrtalamayaCeker()
    {
        var (service, _, _, marketData) = CreateService();
        marketData.Setup(m => m.GetCurrentPriceAsync("THYAO", "stock", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PriceQuoteDto { Symbol = "THYAO", Price = 300m, AsOf = DateTime.UtcNow });

        await service.CreateInvestmentAsync(new CreateInvestmentDto
        { Name = "THYAO", PurchasePrice = 100, Quantity = 1, InvestmentType = "stock" });
        var result = await service.CreateInvestmentAsync(new CreateInvestmentDto
        { Name = "THYAO", PurchasePrice = 200, Quantity = 9, InvestmentType = "stock" });

        Assert.Equal(190m, result.PurchasePrice);
        // Toplam maliyet korunmali: 1x100 + 9x200 = 1900.
        Assert.Equal(1900m, result.TotalPurchaseValue);
    }

    [Fact]
    public async Task CreateInvestment_SembolKucukHarfGirilse_AyniPozisyonaEklenir()
    {
        var (service, context, _, marketData) = CreateService();
        marketData.Setup(m => m.GetCurrentPriceAsync("THYAO", "stock", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PriceQuoteDto { Symbol = "THYAO", Price = 300m, AsOf = DateTime.UtcNow });

        await service.CreateInvestmentAsync(new CreateInvestmentDto
        { Name = "THYAO", PurchasePrice = 100, Quantity = 2, InvestmentType = "stock" });
        var result = await service.CreateInvestmentAsync(new CreateInvestmentDto
        { Name = " thyao ", PurchasePrice = 100, Quantity = 3, InvestmentType = "stock" });

        Assert.Single(context.Investments);
        Assert.Equal("THYAO", Assert.Single(context.Investments).Name);
        Assert.Equal(5, result.Quantity);
    }

    /// Ayni kod farkli piyasalarda bulunabilir; tip farkliysa birlestirilmemeli.
    [Fact]
    public async Task CreateInvestment_AyniKodFarkliTip_AyriKayitKalir()
    {
        var (service, context, _, marketData) = CreateService();
        marketData.Setup(m => m.GetCurrentPriceAsync("AFA", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PriceQuoteDto { Symbol = "AFA", Price = 1.28m, AsOf = DateTime.UtcNow });

        await service.CreateInvestmentAsync(new CreateInvestmentDto
        { Name = "AFA", PurchasePrice = 1, Quantity = 1000, InvestmentType = "fund" });
        var result = await service.CreateInvestmentAsync(new CreateInvestmentDto
        { Name = "AFA", PurchasePrice = 40, Quantity = 5, InvestmentType = "stock" });

        Assert.Equal(2, context.Investments.Count());
        Assert.False(result.Merged);
    }

    /// Kullanici artik tam adi elle yazmiyor — saglayicinin yanitindan geliyor.
    [Fact]
    public async Task CreateInvestment_TamAdSaglayicidanDoldurulur()
    {
        var (service, _, _, marketData) = CreateService();
        marketData.Setup(m => m.GetCurrentPriceAsync("THYAO", "stock", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PriceQuoteDto
            {
                Symbol = "THYAO", Price = 300m, AsOf = DateTime.UtcNow,
                LongName = "Türk Hava Yollari Anonim Ortakligi",
            });

        var result = await service.CreateInvestmentAsync(new CreateInvestmentDto
        { Name = "THYAO", PurchasePrice = 100, Quantity = 1, InvestmentType = "stock" });

        Assert.Equal("Türk Hava Yollari Anonim Ortakligi", result.FullName);
    }

    /// Saglayici ad dondurmezse kayit yine olusmali; arayuz o durumda yalnizca
    /// sembolu gosteriyor.
    [Fact]
    public async Task CreateInvestment_SaglayiciAdDondurmezse_TamAdBosKalir()
    {
        var (service, _, _, marketData) = CreateService();
        marketData.Setup(m => m.GetCurrentPriceAsync("XYZ", "crypto", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PriceQuoteDto { Symbol = "XYZ", Price = 5m, AsOf = DateTime.UtcNow });

        var result = await service.CreateInvestmentAsync(new CreateInvestmentDto
        { Name = "XYZ", PurchasePrice = 5, Quantity = 1, InvestmentType = "crypto" });

        Assert.Equal(string.Empty, result.FullName);
    }

    [Fact]
    public async Task RefreshPrices_BirYatirimBasarisizOlursa_DigerleriEtkilenmez()
    {
        var (service, context, userId, marketData) = CreateService();
        context.Investments.AddRange(
            new Investment { Name = "THYAO", InvestmentType = "stock", PurchasePrice = 100, CurrentPrice = 100, Quantity = 1, UserId = userId },
            new Investment { Name = "BOZUK", InvestmentType = "stock", PurchasePrice = 50, CurrentPrice = 50, Quantity = 1, UserId = userId }
        );
        await context.SaveChangesAsync();

        marketData.Setup(m => m.GetCurrentPriceAsync("THYAO", "stock", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PriceQuoteDto { Symbol = "THYAO", Price = 300m, AsOf = DateTime.UtcNow });
        marketData.Setup(m => m.GetCurrentPriceAsync("BOZUK", "stock", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ExternalServiceException("Sembol bulunamadı!"));

        var result = await service.RefreshPricesAsync();

        Assert.Equal(1, result.UpdatedCount);
        Assert.Equal(1, result.FailedCount);
        var thyao = await context.Investments.FirstAsync(i => i.Name == "THYAO");
        Assert.Equal(300m, thyao.CurrentPrice);
        var bozuk = await context.Investments.FirstAsync(i => i.Name == "BOZUK");
        Assert.Equal(50m, bozuk.CurrentPrice);
    }

    [Fact]
    public async Task GetPortfolioSummary_DogruToplamVeKarZararHesaplar()
    {
        var (service, context, userId, _) = CreateService();
        context.Investments.AddRange(
            new Investment { Name = "THYAO", InvestmentType = "stock", PurchasePrice = 100, CurrentPrice = 150, Quantity = 2, UserId = userId },
            new Investment { Name = "ALTIN", InvestmentType = "gold", PurchasePrice = 1000, CurrentPrice = 900, Quantity = 1, UserId = userId }
        );
        await context.SaveChangesAsync();

        var result = await service.GetPortfolioSummaryAsync();

        // Alis: 100*2 + 1000*1 = 1200 ; Guncel: 150*2 + 900*1 = 1200 ; K/Z: 0
        Assert.Equal(1200m, result.TotalPurchaseValue);
        Assert.Equal(1200m, result.TotalCurrentValue);
        Assert.Equal(0m, result.TotalProfitLoss);
        Assert.Equal(2, result.TotalInvestmentCount);
    }

    [Fact]
    public async Task DeleteInvestment_BaskaKullaniciyaAitYatirim_NotFoundFireder()
    {
        var (service, context, _, _) = CreateService();
        var baskaKullanici = new User { FullName = "Başka", Email = $"{Guid.NewGuid()}@test.com", PasswordHash = "x" };
        context.Users.Add(baskaKullanici);
        await context.SaveChangesAsync();
        var baskasininYatirimi = new Investment { Name = "XYZ", InvestmentType = "stock", PurchasePrice = 1, CurrentPrice = 1, Quantity = 1, UserId = baskaKullanici.Id };
        context.Investments.Add(baskasininYatirimi);
        await context.SaveChangesAsync();

        await Assert.ThrowsAsync<NotFoundException>(() => service.DeleteInvestmentAsync(baskasininYatirimi.Id));
    }
}
