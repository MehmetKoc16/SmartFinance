using System.ComponentModel.DataAnnotations;

namespace SmartFinance.Application.DTOs.Category;

public class CreateCategoryDto
{
    [Required(ErrorMessage = "Kategori adı zorunludur!")]
    [MaxLength(100,ErrorMessage="Kategori adı en fazla 100 karakter olabilir!")]
    public string Name{get;set;}=string.Empty;
}