using SmartFinance.Domain.Common;
using SmartFinance.Domain.Enums;

namespace SmartFinance.Domain.Entities;

public class Category : BaseEntity
{
    public string Name {get;set;}=string.Empty;
    public TransactionType Type {get;set;} = TransactionType.Expense;
    public string? Icon {get;set;}
    public string? Color {get;set;}
    public int UserId {get;set;}
    public User User {get;set;}=null!;
    public ICollection<Transaction>Transactions{get;set;}=new List<Transaction>();
}