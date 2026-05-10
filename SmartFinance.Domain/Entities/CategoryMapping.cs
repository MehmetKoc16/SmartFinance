using SmartFinance.Domain.Common;

namespace SmartFinance.Domain.Entities;

public class CategoryMapping : BaseEntity
{
    public string MerchantKeyword { get; set; } = string.Empty;
    public int CategoryId { get; set; }
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    public Category Category { get; set; } = null!;
}
