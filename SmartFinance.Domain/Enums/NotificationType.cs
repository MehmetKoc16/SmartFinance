namespace SmartFinance.Domain.Enums;

public enum NotificationType
{
    BudgetExceeded = 1, // kategori aylik butce limiti asildi
    Info = 2, // genel bilgilendirme
    RecurringDue = 3, // planlı ödeme / gelir vadesi
}
