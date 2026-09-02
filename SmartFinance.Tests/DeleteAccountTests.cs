using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.InMemory.Infrastructure.Internal;
using Microsoft.Extensions.Configuration;
using SmartFinance.Application.DTOs.Auth;
using SmartFinance.Application.Exceptions;
using SmartFinance.Domain.Entities;
using SmartFinance.Domain.Enums;
using SmartFinance.Infrastructure.Context;
using SmartFinance.Infrastructure.Services;

namespace SmartFinance.Tests;

/// Hesap silme geri alinamaz ve Google Play tarafindan zorunlu tutuluyor.
/// Yanlis calismasinin iki yonu de agir: eksik silme KVKK ihlali, fazla
/// silme baska kullanicinin verisini goturur.
public class DeleteAccountTests
{
    private static (AuthService service, SmartFinanceDbContext context, HttpContextAccessor accessor) CreateService()
    {
        var options = new DbContextOptionsBuilder<SmartFinanceDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            // InMemory saglayicisi islem (transaction) desteklemiyor; uyariyi
            // susturuyoruz ki silme mantigi yine de test edilebilsin.
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        var context = new SmartFinanceDbContext(options);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            {"Jwt:Key","TestGizliAnahtar123456789012345678"},
            {"Jwt:Issuer","TestIssuer"},
            {"Jwt:Audience","TestAudience"},
            {"Jwt:ExpireMinutes","60"},
            {"Jwt:RefreshTokenExpireDays","30"}
        }).Build();

        var accessor = new HttpContextAccessor();
        return (new AuthService(context, config, new CurrentUserService(accessor),
            new FakeEmailSender(), NullLogger<AuthService>.Instance), context, accessor);
    }

    private static void SetCurrentUser(HttpContextAccessor accessor, int userId)
    {
        var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) });
        accessor.HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
    }

    /// Verisi dolu bir kullanici olusturur: kategori, islem, yatirim, butce,
    /// kategori eslesmesi ve yenileme token'i.
    private static User SeedUser(SmartFinanceDbContext context, string password = "Sifre123!")
    {
        var user = new User
        {
            FullName = "Test Kullanıcı",
            Email = $"{Guid.NewGuid()}@test.com",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
        };
        context.Users.Add(user);
        context.SaveChanges();

        var category = new Category { Name = "Yeme-İçme", Type = TransactionType.Expense, UserId = user.Id };
        context.Categories.Add(category);
        context.SaveChanges();

        context.Transactions.Add(new Transaction
        {
            UserId = user.Id, CategoryId = category.Id, Amount = 100,
            Description = "test", TransactionDate = new DateTime(2026, 8, 1), Type = TransactionType.Expense,
        });
        context.Investments.Add(new Investment
        {
            UserId = user.Id, Name = "THYAO", InvestmentType = "stock", Quantity = 1, PurchasePrice = 300,
        });
        context.Budgets.Add(new Budget { UserId = user.Id, CategoryId = category.Id, MonthlyLimit = 1000 });
        context.CategoryMappings.Add(new CategoryMapping
        {
            UserId = user.Id, CategoryId = category.Id, MerchantKeyword = "TEST MARKET",
        });
        context.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id, Token = Guid.NewGuid().ToString(), ExpiresAt = DateTime.UtcNow.AddDays(30),
        });
        context.SaveChanges();
        return user;
    }

    private static int UserRowCount(SmartFinanceDbContext c, int userId) =>
        c.Transactions.IgnoreQueryFilters().Count(x => x.UserId == userId)
        + c.Investments.IgnoreQueryFilters().Count(x => x.UserId == userId)
        + c.Budgets.IgnoreQueryFilters().Count(x => x.UserId == userId)
        + c.CategoryMappings.IgnoreQueryFilters().Count(x => x.UserId == userId)
        + c.RefreshTokens.IgnoreQueryFilters().Count(x => x.UserId == userId)
        + c.Categories.IgnoreQueryFilters().Count(x => x.UserId == userId)
        + c.Users.IgnoreQueryFilters().Count(x => x.Id == userId);

    [Fact]
    public async Task DogruSifre_KullaniciVeTumVerisiSilinir()
    {
        var (service, context, accessor) = CreateService();
        var user = SeedUser(context);
        SetCurrentUser(accessor, user.Id);

        await service.DeleteAccountAsync(new DeleteAccountDto { Password = "Sifre123!" });

        Assert.Equal(0, UserRowCount(context, user.Id));
    }

    /// KVKK "silme" hakki gercek silme istiyor; IsDeleted isaretlemek yeterli
    /// degil, satirlar tablodan kalkmali.
    [Fact]
    public async Task Silme_IsaretlemeDegil_SatirlariKaldirir()
    {
        var (service, context, accessor) = CreateService();
        var user = SeedUser(context);
        SetCurrentUser(accessor, user.Id);

        await service.DeleteAccountAsync(new DeleteAccountDto { Password = "Sifre123!" });

        // Global filtre kapatildiginda bile hicbir satir kalmamali.
        Assert.Empty(context.Users.IgnoreQueryFilters().Where(x => x.Id == user.Id));
        Assert.Empty(context.Transactions.IgnoreQueryFilters().Where(x => x.UserId == user.Id));
    }

    /// Asil tuzak: daha once soft-delete edilmis kayitlar global filtre
    /// yuzunden gorunmuyor ama veritabaninda duruyor ve kategoriye yabanci
    /// anahtarla bagli. Filtre kapatilmasaydi bunlar geride kalir, Categories
    /// silinirken FK hatasi verirdi.
    [Fact]
    public async Task SoftDeleteEdilmisKayitlar_DaSilinir()
    {
        var (service, context, accessor) = CreateService();
        var user = SeedUser(context);
        var kategoriId = context.Categories.First(c => c.UserId == user.Id).Id;

        context.Transactions.Add(new Transaction
        {
            UserId = user.Id, CategoryId = kategoriId, Amount = 50, Description = "silinmis",
            TransactionDate = new DateTime(2026, 7, 1), Type = TransactionType.Expense, IsDeleted = true,
        });
        context.Investments.Add(new Investment
        {
            UserId = user.Id, Name = "ESKI", InvestmentType = "stock",
            Quantity = 1, PurchasePrice = 1, IsDeleted = true,
        });
        context.SaveChanges();

        SetCurrentUser(accessor, user.Id);
        await service.DeleteAccountAsync(new DeleteAccountDto { Password = "Sifre123!" });

        Assert.Equal(0, UserRowCount(context, user.Id));
    }

    [Fact]
    public async Task YanlisSifre_HicbirSeySilinmez()
    {
        var (service, context, accessor) = CreateService();
        var user = SeedUser(context);
        var oncekiSayi = UserRowCount(context, user.Id);
        SetCurrentUser(accessor, user.Id);

        await Assert.ThrowsAsync<BadRequestException>(
            () => service.DeleteAccountAsync(new DeleteAccountDto { Password = "YanlisSifre" }));

        Assert.Equal(oncekiSayi, UserRowCount(context, user.Id));
    }

    /// Fazla silmek eksik silmek kadar agir: baska kullanicinin verisi
    /// kesinlikle etkilenmemeli.
    [Fact]
    public async Task BaskaKullanicininVerisi_Etkilenmez()
    {
        var (service, context, accessor) = CreateService();
        var silinecek = SeedUser(context);
        var kalacak = SeedUser(context);
        var kalacakSayi = UserRowCount(context, kalacak.Id);

        SetCurrentUser(accessor, silinecek.Id);
        await service.DeleteAccountAsync(new DeleteAccountDto { Password = "Sifre123!" });

        Assert.Equal(0, UserRowCount(context, silinecek.Id));
        Assert.Equal(kalacakSayi, UserRowCount(context, kalacak.Id));
    }

    /// PriceHistories kullaniciya degil piyasaya ait paylasilan veri.
    /// Silinmesi diger kullanicilarin grafiklerini bozardi.
    [Fact]
    public async Task PiyasaVerisi_Silinmez()
    {
        var (service, context, accessor) = CreateService();
        var user = SeedUser(context);
        context.PriceHistories.Add(new PriceHistory
        {
            Symbol = "THYAO", InvestmentType = "stock", Date = new DateTime(2026, 8, 28),
            Open = 300, High = 310, Low = 295, Close = 305, Volume = 1000,
        });
        context.SaveChanges();

        SetCurrentUser(accessor, user.Id);
        await service.DeleteAccountAsync(new DeleteAccountDto { Password = "Sifre123!" });

        Assert.Single(context.PriceHistories);
    }

    /// JWT durumsuz ve 60 dakika gecerli; hesap silindikten sonra token teknik
    /// olarak hala imzali kaliyor. Bu uc yalnizca token icerigini yansitsaydi
    /// uygulama silinmis hesapla "giris yapilmis" gorunmeye devam ederdi.
    [Fact]
    public async Task SilinenHesabinTokeni_MeUcundaReddedilir()
    {
        var (service, context, accessor) = CreateService();
        var user = SeedUser(context);
        SetCurrentUser(accessor, user.Id);

        // Silmeden once calisiyor.
        Assert.NotNull(await service.GetMeAsync());

        await service.DeleteAccountAsync(new DeleteAccountDto { Password = "Sifre123!" });

        // Token hala "gecerli" ama kullanici yok.
        await Assert.ThrowsAsync<UnauthorizedException>(() => service.GetMeAsync());
    }

    [Fact]
    public async Task SilinenKullanici_AyniEmailIleYenidenKayitOlabilir()
    {
        var (service, context, accessor) = CreateService();
        var user = SeedUser(context);
        var email = user.Email;
        SetCurrentUser(accessor, user.Id);

        await service.DeleteAccountAsync(new DeleteAccountDto { Password = "Sifre123!" });

        // Email benzersizlik kisiti geride kalan bir satira takilmamali.
        var token = await service.RegisterAsync(new RegisterDto
        {
            FullName = "Yeni Kayıt", Email = email, Password = "YeniSifre123!",
        });
        Assert.False(string.IsNullOrWhiteSpace(token.Token));
    }
}
