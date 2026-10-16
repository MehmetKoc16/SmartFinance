namespace SmartFinance.Application.DTOs.Transaction;

public class CategorySummaryDto
{
    public int? CategoryId { get; set; }
    public string CategoryName { get; set; } = string.Empty;
    public decimal Total { get; set; }
    public int Count { get; set; }
}
