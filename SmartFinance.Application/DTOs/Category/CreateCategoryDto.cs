using System.ComponentModel.DataAnnotations;
using SmartFinance.Domain.Enums;

namespace SmartFinance.Application.DTOs.Category;

public class CreateCategoryDto
{
    [Required(ErrorMessage = "Kategori adı zorunludur!")]
    [MaxLength(100,ErrorMessage="Kategori adı en fazla 100 karakter olabilir!")]
    public string Name{get;set;}=string.Empty;

    [Required(ErrorMessage = "Kategori tipi zorunludur!")]
    public TransactionType Type{get;set;}

    public string? Icon{get;set;}
    public string? Color{get;set;}
}