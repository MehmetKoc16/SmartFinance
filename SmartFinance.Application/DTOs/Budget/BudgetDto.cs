namespace SmartFinance.Application.DTOs.Budget;

public class BudgetDto
{
    public int Id { get; set; }
    public int CategoryId { get; set; }
    public string CategoryName { get; set; } = string.Empty;
    public string? Icon { get; set; }
    public string? Color { get; set; }
    public decimal MonthlyLimit { get; set; }
}
