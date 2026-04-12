using SmartFinance.Application.DTOs.Category;
using SmartFinance.Application.Interfaces;
using SmartFinance.Domain.Entities;
using SmartFinance.Infrastructure.Context;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using SmartFinance.Application.Exceptions;

namespace SmartFinance.Infrastructure.Services;

public class CategoryService : ICategoryService
{
    private readonly IGenericRepository<Category> _repository;
    private readonly SmartFinanceDbContext _context;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CategoryService(IGenericRepository<Category> repository, SmartFinanceDbContext context, IHttpContextAccessor httpContextAccessor)
    {
        _repository = repository;
        _context = context;
        _httpContextAccessor = httpContextAccessor;
    }
    
    public async Task<IEnumerable<CategoryDto>> GetAllCategoriesAsync()
    {
        var userId=int.Parse(_httpContextAccessor.HttpContext!.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
        var categories = await _repository.GetAllAsync();
        return categories.Where(c=>c.UserId==userId).Select(c=>new CategoryDto{
            Id=c.Id,
            Name=c.Name,
            CreatedDate=c.CreatedDate,
        });
    }
    public async Task<CategoryDto?> GetCategoryByIdAsync(int id)
    {
        var userId=int.Parse(_httpContextAccessor.HttpContext!.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
        var category = await _repository.GetByIdAsync(id);
        if (category == null || category.UserId != userId) return null;
        return new CategoryDto{
            Id=category.Id,
            Name=category.Name,
            CreatedDate=category.CreatedDate,
        };
    } 

    public async Task<CategoryDto> CreateCategoryAsync(CreateCategoryDto dto)
    {
        // Token'dan UserId al
        var userId = int.Parse(_httpContextAccessor.HttpContext!.User
            .FindFirst(ClaimTypes.NameIdentifier)!.Value);

        var category= new Category
        {
            Name =dto.Name,
            UserId = userId
        };

        await _repository.AddAsync(category);
        await _context.SaveChangesAsync();

        return new CategoryDto{
            Id=category.Id,
            Name=category.Name,
            CreatedDate=category.CreatedDate,
        };
    }

    public async Task UpdateCategoryAsync(int id, CreateCategoryDto dto)
    {
        var userId=int.Parse(_httpContextAccessor.HttpContext!.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
        var category= await _repository.GetByIdAsync(id);
        if(category==null || category.UserId!=userId)
            throw new NotFoundException("Kategori bulunamadı!");

        category.Name=dto.Name;
        category.UpdatedDate=DateTime.UtcNow;
        _repository.Update(category);
        await _context.SaveChangesAsync();
    }
    
    public async Task DeleteCategoryAsync(int id)
    {
        var userId=int.Parse(_httpContextAccessor.HttpContext!.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
        var category=await _repository.GetByIdAsync(id);
        if(category==null || category.UserId!=userId)
            throw new NotFoundException("Kategori bulunamadı!");
            
        category.IsDeleted=true;
        category.UpdatedDate=DateTime.UtcNow;
        _repository.Update(category);
        await _context.SaveChangesAsync();
    }
}