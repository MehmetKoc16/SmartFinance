namespace SmartFinance.Application.DTOs.Notification;

/// GET/PUT /api/Notification/preferences yaniti. JSON alan adlari (camelCase)
/// mobil istemciyle sozlesme: budgetAlerts, recurringReminders, announcements.
public class NotificationPreferencesDto
{
    public bool BudgetAlerts { get; set; }
    public bool RecurringReminders { get; set; }
    public bool Announcements { get; set; }
}

/// PUT govdesi. Her alan istege bagli: null (ya da gonderilmemis) = degistirme.
public class UpdateNotificationPreferencesDto
{
    public bool? BudgetAlerts { get; set; }
    public bool? RecurringReminders { get; set; }
    public bool? Announcements { get; set; }
}
