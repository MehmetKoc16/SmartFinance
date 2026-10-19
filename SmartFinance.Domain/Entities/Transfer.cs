using SmartFinance.Domain.Common;

namespace SmartFinance.Domain.Entities;

public class Transfer : BaseEntity
{
    public int UserId { get; set; }
    public int FromAccountId { get; set; }
    public int ToAccountId { get; set; }
    public decimal Amount { get; set; }
    public DateTime TransferDate { get; set; }
    public string? Description { get; set; }
    public Account FromAccount { get; set; } = null!;
    public Account ToAccount { get; set; } = null!;
}
