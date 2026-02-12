using SmartFinance.Domain.Common;

namespace SmartFinance.Domain.Entities;

public class Category : BaseEntity
{
    public string Name {get;set;}=string.Empty;
    public int UserId {get;set;}
    public User User {get;set;}=null!;
    public ICollection<Transaction>Transactions{get;set;}=new List<Transaction>();
}