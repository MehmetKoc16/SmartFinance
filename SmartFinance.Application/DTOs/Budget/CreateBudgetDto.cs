namespace SmartFinance.Application.DTOs.Budget;

public class CreateBudgetDto
{
    public int CategoryId { get; set; }
    public decimal MonthlyLimit { get; set; }
}
