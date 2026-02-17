using SmartFinance.Application.DTOs.Category;

namespace SmartFinance.Application.Interfaces;

public interface ICategoryService{
    Task<IEnumerable<CategoryDto>> GetAllCategoriesAsync();
    Task<CategoryDto> GetCategoryByIdAsync(int id);
    Task<CategoryDto> CreateCategoryAsync(CreateCategoryDto dto);
    Task UpdateCategoryAsync(int id, CreateCategoryDto dto);
    Task DeleteCategoryAsync(int id);
}