using SmartFinance.Domain.Common;

namespace SmartFinance.Domain.Entities;

public class User : BaseEntity
{
    public string FullName {get;set;}=string.Empty;
    public string Email {get;set;}=string.Empty;
    public string PasswordHash{get;set;}=string.Empty;
    public ICollection<Transaction> Transactions {get;set;}= new List<Transaction>();
    public ICollection<Category> Categories {get;set;}=new List<Category>();
}