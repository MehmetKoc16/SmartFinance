using SmartFinance.Domain.Common;
using SmartFinance.Domain.Enums;

namespace SmartFinance.Domain.Entities;

public class Transaction : BaseEntity
{
    public int? AccountId { get; set; }
    public Account? Account { get; set; }
    public decimal Amount {get;set;}
    public string Description {get;set;}=string.Empty;
    public string? MerchantName {get;set;}
    public DateTime TransactionDate {get;set;}
    public TransactionType Type{get;set;}

    public int UserId {get;set;}
    public int? CategoryId {get;set;}

    public User User{get;set;}=null!;
    public Category Category{get;set;}=null!;
}
