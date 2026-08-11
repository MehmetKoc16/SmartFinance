using SmartFinance.Domain.Common;

namespace SmartFinance.Domain.Entities;

public class Budget : BaseEntity
{
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    public int CategoryId { get; set; }
    public Category Category { get; set; } = null!;
    public decimal MonthlyLimit { get; set; }
}
