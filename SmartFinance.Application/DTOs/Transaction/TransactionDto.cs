using SmartFinance.Domain.Enums;

namespace SmartFinance.Application.DTOs.Transaction;

public class TransactionDto{
    public int Id{get;set;}
    public int UserId{get;set;}
    public decimal Amount{get;set;}
    public string Description{get;set;}=string.Empty;
    public DateTime TransactionDate{get;set;}
    public TransactionType Type{get;set;}
    public int CategoryId{get;set;}
    public DateTime CreatedDate{get;set;}
}