using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using SmartFinance.Application.DTOs.Transaction;
using SmartFinance.Domain.Entities;
using SmartFinance.Domain.Enums;
using SmartFinance.Infrastructure.Context;
using SmartFinance.Infrastructure.Repositories;
using SmartFinance.Infrastructure.Services;

namespace SmartFinance.Tests;

/// Regresyon: ekstre yuklendikten sonra yeni islemler "Son Islemler"de
/// gorunmuyordu. Sebep sayfalama sorgusunda OrderBy bulunmamasiydi —
/// siralama olmadan veritabani satirlari ekleme sirasinda donduruyor, yani
/// ana ekran en yeni degil EN ESKI bes kaydi gosteriyordu.
public class TransactionFilterOrderingTests
{
    private static (TransactionService service, SmartFinanceDbContext context, int userId) CreateService()
    {
        var options = new DbContextOptionsBuilder<SmartFinanceDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString()).Options;
        var context = new SmartFinanceDbContext(options);

        var user = new User { FullName = "Test Kullanıcı", Email = $"{Guid.NewGuid()}@test.com", PasswordHash = "x" };
        context.Users.Add(user);
        context.SaveChanges();

        var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()) });
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };

        var service = new TransactionService(
            new GenericRepository<Transaction>(context), context, new CurrentUserService(accessor));
        return (service, context, user.Id);
    }

    private static Transaction Tx(int userId, DateTime date, decimal amount) => new()
    {
        UserId = userId,
        Amount = amount,
        Description = $"islem {date:yyyy-MM-dd} {amount}",
        TransactionDate = date,
        Type = TransactionType.Expense,
    };

    private static List<TransactionDto> Items(object result)
    {
        var items = result.GetType().GetProperty("items")!.GetValue(result)!;
        return (List<TransactionDto>)items;
    }

    private static int Total(object result)
        => (int)result.GetType().GetProperty("totalCount")!.GetValue(result)!;

    /// Ekleme sirasi kasten tarih sirasinin TERSI: siralama yoksa test duser.
    ///
    /// Not: bu yardimci SENKRON olmali. CurrentUserService, HttpContext'i
    /// AsyncLocal uzerinden okuyor; kurulum async bir metotta yapilirsa
    /// atanan deger cagirana geri akmiyor ve test "oturum gecersiz" hatasi
    /// aliyor.
    private static (TransactionService, SmartFinanceDbContext, int) Seed()
    {
        var (service, context, userId) = CreateService();
        context.Transactions.AddRange(
            Tx(userId, new DateTime(2026, 8, 1), 10),
            Tx(userId, new DateTime(2026, 8, 25), 20),
            Tx(userId, new DateTime(2026, 8, 26), 30),
            Tx(userId, new DateTime(2026, 8, 27), 40),
            Tx(userId, new DateTime(2026, 8, 28), 50));
        context.SaveChanges();
        return (service, context, userId);
    }

    [Fact]
    public async Task Filtre_EnYeniIslemiIlkSiradaDoner()
    {
        var (service, _, _) = Seed();

        var result = await service.GetFilteredTransactionsAsync(
            new TransactionFilterDto { Page = 1, PageSize = 5 });

        var dates = Items(result).Select(i => i.TransactionDate.Date).ToList();
        Assert.Equal(new[]
        {
            new DateTime(2026, 8, 28),
            new DateTime(2026, 8, 27),
            new DateTime(2026, 8, 26),
            new DateTime(2026, 8, 25),
            new DateTime(2026, 8, 1),
        }, dates);
    }

    /// Ana ekran yalnizca ilk 5 kaydi istiyor; yeni yuklenen ekstre burada
    /// gorunmeliydi.
    [Fact]
    public async Task Filtre_KucukSayfaBoyutundaEnYeniKayitlariDoner()
    {
        var (service, _, _) = Seed();

        var result = await service.GetFilteredTransactionsAsync(
            new TransactionFilterDto { Page = 1, PageSize = 2 });

        var dates = Items(result).Select(i => i.TransactionDate.Date).ToList();
        Assert.Equal(new[] { new DateTime(2026, 8, 28), new DateTime(2026, 8, 27) }, dates);
    }

    /// Ayni gune ait kayitlarda esitlik Id ile deterministik bozulmali; aksi
    /// halde OFFSET/FETCH sayfalari kararsiz kalir.
    [Fact]
    public async Task Filtre_AyniTarihliKayitlar_IdyeGoreAzalanSirada()
    {
        var (service, context, userId) = CreateService();
        var ayniGun = new DateTime(2026, 8, 27);
        context.Transactions.AddRange(
            Tx(userId, ayniGun, 1), Tx(userId, ayniGun, 2), Tx(userId, ayniGun, 3));
        context.SaveChanges();

        var result = await service.GetFilteredTransactionsAsync(
            new TransactionFilterDto { Page = 1, PageSize = 3 });

        var ids = Items(result).Select(i => i.Id).ToList();
        Assert.Equal(ids.OrderByDescending(x => x).ToList(), ids);
    }

    /// Sayfalama tutarli olmali: sayfa 1 + sayfa 2, tekrar veya atlama olmadan
    /// tum kayitlari vermeli.
    [Fact]
    public async Task Filtre_SayfalarArasindaKayitTekrarlanmaz_Atlanmaz()
    {
        var (service, _, _) = Seed();

        var page1 = await service.GetFilteredTransactionsAsync(
            new TransactionFilterDto { Page = 1, PageSize = 3 });
        var page2 = await service.GetFilteredTransactionsAsync(
            new TransactionFilterDto { Page = 2, PageSize = 3 });

        var ids = Items(page1).Select(i => i.Id).Concat(Items(page2).Select(i => i.Id)).ToList();

        Assert.Equal(5, Total(page1));
        Assert.Equal(5, ids.Count);
        Assert.Equal(5, ids.Distinct().Count());
    }
}
