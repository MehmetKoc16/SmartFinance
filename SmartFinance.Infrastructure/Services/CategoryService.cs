using SmartFinance.Application.DTOs.Category;
using SmartFinance.Application.Interfaces;
using SmartFinance.Domain.Entities;
using SmartFinance.Infrastructure.Context;

namespace SmartFinance.Infrastructure.Services;

public class CategoryService : ICategoryService
{
    private readonly IGenericRepository<Category> _repository;
    private readonly SmartFinanceDbContext _context;

    public CategoryService(IGenericRepository<Category> repository, SmartFinanceDbContext context)
    {
        _repository = repository;
        _context = context;
    }
    
    public async Task<IEnumerable<CategoryDto>> GetAllCategoriesAsync()
    {
        var categories = await _repository.GetAllAsync();
        return categories.Select(c=> new CategoryDto {
            Id=c.Id,
            Name=c.Name,
            CreatedDate=c.CreatedDate,
        });
    }
    public async Task<CategoryDto> GetCategoryByIdAsync(int id)
    {
        var category = await _repository.GetByIdAsync(id);
        return new CategoryDto{
            Id=category.Id,
            Name=category.Name,
            CreatedDate=category.CreatedDate,
        };
    } 

    public async Task<CategoryDto> CreateCategoryAsync(CreateCategoryDto dto)
    {
        var category= new Category
        {
            Name =dto.Name
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
        var category= await _repository.GetByIdAsync(id);
        category.Name=dto.Name;
        category.UpdatedDate=DateTime.UtcNow;
        _repository.Update(category);
        await _context.SaveChangesAsync();
    }
    
    public async Task DeleteCategoryAsync(int id)
    {
        var category=await _repository.GetByIdAsync(id);
        category.IsDeleted=true;
        category.UpdatedDate=DateTime.UtcNow;
        _repository.Update(category);
        await _context.SaveChangesAsync();
    }
}