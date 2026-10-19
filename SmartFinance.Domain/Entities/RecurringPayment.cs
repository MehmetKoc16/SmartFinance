using SmartFinance.Domain.Common;
using SmartFinance.Domain.Enums;

namespace SmartFinance.Domain.Entities;

public class RecurringPayment : BaseEntity
{
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    public string Name { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public TransactionType Type { get; set; }
    public int? CategoryId { get; set; }
    public Category? Category { get; set; }
    public int? AccountId { get; set; }
    public Account? Account { get; set; }
    public RecurrenceFrequency Frequency { get; set; }
    public DateOnly StartDate { get; set; }
    public int AnchorDay { get; set; }
    public DateOnly NextDueDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public int RemindDaysBefore { get; set; } = 1;
    public bool IsActive { get; set; } = true;
}
