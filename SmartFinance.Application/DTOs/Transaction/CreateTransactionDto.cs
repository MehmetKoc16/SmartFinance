using SmartFinance.Domain.Enums;
using System.ComponentModel.DataAnnotations;

namespace SmartFinance.Application.DTOs.Transaction;

public class CreateTransactionDto
{
    [Required(ErrorMessage="Tutar zorunludur!")]
    [Range(0.01,double.MaxValue,ErrorMessage="Tutar 0'dan büyük olmalıdır!")]
    public decimal Amount{get;set;}
    [MaxLength(500, ErrorMessage = "Açıklama en fazla 500 karakter olabilir!")]
    public string Description{get;set;}=string.Empty;
    [Required(ErrorMessage = "İşlem tarihi zorunludur!")]
    public DateTime TransactionDate{get;set;}
    [Required(ErrorMessage = "İşlem tipi zorunludur!")]
    public TransactionType Type{get;set;}
    [Required(ErrorMessage = "Kategori seçimi zorunludur!")]
    public int CategoryId{get;set;}
}