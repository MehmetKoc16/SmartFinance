using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using SmartFinance.Application.DTOs.Category;
using SmartFinance.Application.Exceptions;
using SmartFinance.Domain.Entities;
using SmartFinance.Domain.Enums;
using SmartFinance.Infrastructure.Context;
using SmartFinance.Infrastructure.Repositories;
using SmartFinance.Infrastructure.Services;

namespace SmartFinance.Tests;

public class CategoryServiceTests
{
    private (CategoryService service,SmartFinanceDbContext context,int userId) CreateService()
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

        var repository = new GenericRepository<Category>(context);
        var service = new CategoryService(repository, context, new CurrentUserService(httpContextAccessor));
        return (service, context, user.Id);
    }

    [Fact]
    public async Task CreateCategory_GecerliBilgi_KategoriDoner()
    {
        var (service, _, _) = CreateService();

        var result = await service.CreateCategoryAsync(new CreateCategoryDto
        {
            Name = "Eğlence",
            Type = TransactionType.Expense,
            Icon = "film",
            Color = "#FF00FF"
        });

        Assert.True(result.Id > 0);
        Assert.Equal("Eğlence", result.Name);
    }

    [Fact]
    public async Task GetAllCategories_SadeceKendiKategorileriniDoner()
    {
        var (service, context, userId) = CreateService();
        var baskaKullanici = new User { FullName = "Başka", Email = $"{Guid.NewGuid()}@test.com", PasswordHash = "x" };
        context.Users.Add(baskaKullanici);
        await context.SaveChangesAsync();
        context.Categories.AddRange(
            new Category { Name = "Benim Kategorim", Type = TransactionType.Expense, UserId = userId },
            new Category { Name = "Başkasının Kategorisi", Type = TransactionType.Expense, UserId = baskaKullanici.Id }
        );
        await context.SaveChangesAsync();

        var result = await service.GetAllCategoriesAsync();

        Assert.Single(result);
        Assert.Equal("Benim Kategorim", result.First().Name);
    }

    [Fact]
    public async Task UpdateCategory_BaskaKullaniciyaAitKategori_NotFoundFireder()
    {
        var (service, context, _) = CreateService();
        var baskaKullanici = new User { FullName = "Başka", Email = $"{Guid.NewGuid()}@test.com", PasswordHash = "x" };
        context.Users.Add(baskaKullanici);
        await context.SaveChangesAsync();
        var baskasininKategorisi = new Category { Name = "Başkasının", Type = TransactionType.Expense, UserId = baskaKullanici.Id };
        context.Categories.Add(baskasininKategorisi);
        await context.SaveChangesAsync();

        await Assert.ThrowsAsync<NotFoundException>(() => service.UpdateCategoryAsync(baskasininKategorisi.Id, new CreateCategoryDto
        {
            Name = "Değiştirilmeye Çalışıldı",
            Type = TransactionType.Expense
        }));
    }

    [Fact]
    public async Task DeleteCategory_GecerliKategori_ArtikListelenmez()
    {
        var (service, context, userId) = CreateService();
        var kategori = new Category { Name = "Silinecek", Type = TransactionType.Expense, UserId = userId };
        context.Categories.Add(kategori);
        await context.SaveChangesAsync();

        await service.DeleteCategoryAsync(kategori.Id);

        var tumKategoriler = await service.GetAllCategoriesAsync();
        Assert.Empty(tumKategoriler);
    }
}
