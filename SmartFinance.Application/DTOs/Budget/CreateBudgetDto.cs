using System.ComponentModel.DataAnnotations;

namespace SmartFinance.Application.DTOs.Budget;

public class CreateBudgetDto
{
    [Required(ErrorMessage = "Kategori seçimi zorunludur!")]
    [Range(1, int.MaxValue, ErrorMessage = "Geçersiz kategori!")]
    public int CategoryId { get; set; }

    [Required(ErrorMessage = "Aylık limit zorunludur!")]
    [Range(0.01, double.MaxValue, ErrorMessage = "Aylık limit 0'dan büyük olmalıdır!")]
    public decimal MonthlyLimit { get; set; }
}
