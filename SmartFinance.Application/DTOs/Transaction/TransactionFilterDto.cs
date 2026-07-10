using System.ComponentModel.DataAnnotations;

namespace SmartFinance.Application.DTOs.Transaction;

public class TransactionFilterDto
{
    [Range(1, int.MaxValue, ErrorMessage = "Sayfa numarası 1'den küçük olamaz!")]
    public int Page{get;set;}=1;

    [Range(1, 100, ErrorMessage = "Sayfa boyutu 1 ile 100 arasında olmalıdır!")]
    public int PageSize{get;set;}=10;

    public DateTime? StartDate{get;set;}
    public DateTime? EndDate{get;set;}

    public int? Type{get;set;}
    public int? CategoryId{get;set;}
}