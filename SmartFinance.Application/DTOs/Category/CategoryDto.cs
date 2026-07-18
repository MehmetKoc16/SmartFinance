using SmartFinance.Domain.Enums;

namespace SmartFinance.Application.DTOs.Category;

public class CategoryDto
{
    public int Id {get;set;}
    public string Name {get;set;}=string.Empty;
    public TransactionType Type {get;set;}
    public string? Icon {get;set;}
    public string? Color {get;set;}
    public DateTime CreatedDate{get;set;}
}