namespace SmartFinance.Application.DTOs.Budget;

public class BudgetStatusDto
{
    public int BudgetId { get; set; }
    public int CategoryId { get; set; }
    public string CategoryName { get; set; } = string.Empty;
    public string? Icon { get; set; }
    public string? Color { get; set; }
    public decimal MonthlyLimit { get; set; }
    public decimal Spent { get; set; }
    public decimal Ratio { get; set; }
    public bool IsOverLimit { get; set; }
}
