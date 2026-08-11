using SmartFinance.Application.DTOs.Budget;

namespace SmartFinance.Application.Interfaces;

public interface IBudgetService
{
    Task<IEnumerable<BudgetDto>> GetAllAsync();
    Task<BudgetDto> UpsertAsync(CreateBudgetDto dto);
    Task DeleteAsync(int id);
    Task<IEnumerable<BudgetStatusDto>> GetStatusAsync(int year, int month);
}
