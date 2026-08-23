using SmartFinance.Application.DTOs.Category;
using SmartFinance.Application.Interfaces;
using SmartFinance.Domain.Entities;
using SmartFinance.Infrastructure.Context;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using SmartFinance.Application.Exceptions;

namespace SmartFinance.Infrastructure.Services;

public class CategoryService : ICategoryService
{
    private readonly IGenericRepository<Category> _repository;
    private readonly SmartFinanceDbContext _context;
    private readonly ICurrentUserService _currentUserService;

    public CategoryService(IGenericRepository<Category> repository, SmartFinanceDbContext context, ICurrentUserService currentUserService)
    {
        _repository = repository;
        _context = context;
        _currentUserService = currentUserService;
    }
    
    public async Task<IEnumerable<CategoryDto>> GetAllCategoriesAsync()
    {
        var userId=_currentUserService.UserId;
        return await _repository.Query().Where(c=>c.UserId==userId).Select(c=>new CategoryDto{
            Id=c.Id,
            Name=c.Name,
            Type=c.Type,
            Icon=c.Icon,
            Color=c.Color,
            CreatedDate=c.CreatedDate,
        }).ToListAsync();
    }
    public async Task<CategoryDto?> GetCategoryByIdAsync(int id)
    {
        var userId=_currentUserService.UserId;
        var category = await _repository.GetByIdAsync(id);
        if (category == null || category.UserId != userId) return null;
        return new CategoryDto{
            Id=category.Id,
            Name=category.Name,
            Type=category.Type,
            Icon=category.Icon,
            Color=category.Color,
            CreatedDate=category.CreatedDate,
        };
    }

    public async Task<CategoryDto> CreateCategoryAsync(CreateCategoryDto dto)
    {
        // Token'dan UserId al
        var userId = _currentUserService.UserId;

        var category= new Category
        {
            Name =dto.Name,
            Type = dto.Type,
            Icon = dto.Icon,
            Color = dto.Color,
            UserId = userId
        };

        await _repository.AddAsync(category);
        await _context.SaveChangesAsync();

        return new CategoryDto{
            Id=category.Id,
            Name=category.Name,
            Type=category.Type,
            Icon=category.Icon,
            Color=category.Color,
            CreatedDate=category.CreatedDate,
        };
    }

    public async Task UpdateCategoryAsync(int id, CreateCategoryDto dto)
    {
        var userId=_currentUserService.UserId;
        var category= await _repository.GetByIdAsync(id);
        if(category==null || category.UserId!=userId)
            throw new NotFoundException("Kategori bulunamadı!");

        category.Name=dto.Name;
        category.Type=dto.Type;
        category.Icon=dto.Icon;
        category.Color=dto.Color;
        category.UpdatedDate=DateTime.UtcNow;
        _repository.Update(category);
        await _context.SaveChangesAsync();
    }
    
    public async Task DeleteCategoryAsync(int id)
    {
        var userId=_currentUserService.UserId;
        var category=await _repository.GetByIdAsync(id);
        if(category==null || category.UserId!=userId)
            throw new NotFoundException("Kategori bulunamadı!");
            
        category.IsDeleted=true;
        category.UpdatedDate=DateTime.UtcNow;
        _repository.Update(category);
        await _context.SaveChangesAsync();
    }
}