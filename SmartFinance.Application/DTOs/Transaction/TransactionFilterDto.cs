namespace SmartFinance.Application.DTOs.Transaction;

public class TransactionFilterDto
{
    public int Page{get;set;}=1;
    public int PageSize{get;set;}=10;

    public DateTime? StartDate{get;set;}
    public DateTime? EndDate{get;set;}

    public int? Type{get;set;}
    public int? CategoryId{get;set;}
}