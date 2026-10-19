using SmartFinance.Application.DTOs.Transaction;
using SmartFinance.Domain.Enums;

namespace SmartFinance.Application.DTOs.RecurringPayment;

public class CreateRecurringPaymentDto
{
    public string Name { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public TransactionType Type { get; set; }
    public int? CategoryId { get; set; }
    public int? AccountId { get; set; }
    public RecurrenceFrequency Frequency { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public int RemindDaysBefore { get; set; } = 1;
}

public class RecurringPaymentDto : CreateRecurringPaymentDto
{
    public int Id { get; set; }
    public int AnchorDay { get; set; }
    public DateOnly NextDueDate { get; set; }
    public bool IsActive { get; set; }
    public int DaysUntil { get; set; }
    public string Status { get; set; } = string.Empty;
}

public class ProcessRecurringPaymentDto
{
    // Nullable: gönderilmeyen tarih default DateOnly ile karışmasın; servis Türkçe 400 döndürür.
    public DateOnly? DueDate { get; set; }
}

public class PayRecurringPaymentDto : ProcessRecurringPaymentDto
{
    public decimal? Amount { get; set; }
    public DateTime? TransactionDate { get; set; }
    public int? AccountId { get; set; }
    public string? Description { get; set; }
}

public class PayRecurringPaymentResultDto
{
    public TransactionDto Transaction { get; set; } = null!;
    public RecurringPaymentDto RecurringPayment { get; set; } = null!;
}

public class UpcomingRecurringPaymentDto
{
    public int RecurringPaymentId { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public TransactionType Type { get; set; }
    public int? CategoryId { get; set; }
    public int? AccountId { get; set; }
    public DateOnly DueDate { get; set; }
    public string Status { get; set; } = string.Empty;
}

public class UpcomingRecurringPaymentsDto
{
    public IReadOnlyList<UpcomingRecurringPaymentDto> Items { get; set; } = Array.Empty<UpcomingRecurringPaymentDto>();
    public decimal ExpenseTotal { get; set; }
    public decimal IncomeTotal { get; set; }
}
