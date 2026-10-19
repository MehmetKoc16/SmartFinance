using SmartFinance.Domain.Enums;

namespace SmartFinance.Infrastructure.Recurring;

public static class RecurrenceSchedule
{
    // KapFundamentalsSyncService ile aynı Windows/Linux çözüm kalıbı.
    internal static readonly TimeZoneInfo TrTimeZone = ResolveTrTimeZone();

    private static TimeZoneInfo ResolveTrTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Turkey Standard Time"); }
    }

    public static DateOnly TodayTr(DateTime utcNow) => DateOnly.FromDateTime(
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), TrTimeZone));

    public static DateOnly Next(DateOnly current, RecurrenceFrequency frequency, int anchorDay)
    {
        if (anchorDay is < 1 or > 31) throw new ArgumentOutOfRangeException(nameof(anchorDay));
        if (frequency == RecurrenceFrequency.Weekly) return current.AddDays(7);
        var target = frequency switch
        {
            RecurrenceFrequency.Monthly => new DateOnly(current.Year, current.Month, 1).AddMonths(1),
            RecurrenceFrequency.Yearly => new DateOnly(current.Year + 1, current.Month, 1),
            _ => throw new ArgumentOutOfRangeException(nameof(frequency))
        };
        return new DateOnly(target.Year, target.Month, Math.Min(anchorDay, DateTime.DaysInMonth(target.Year, target.Month)));
    }

    public static IEnumerable<DateOnly> Occurrences(DateOnly nextDueDate, RecurrenceFrequency frequency,
        int anchorDay, DateOnly from, DateOnly through, DateOnly? endDate = null)
    {
        var last = endDate.HasValue && endDate.Value < through ? endDate.Value : through;
        var due = nextDueDate;
        while (due <= last)
        {
            if (due >= from) yield return due;
            if (!CanAdvance(due, frequency)) yield break;
            due = Next(due, frequency, anchorDay);
        }
    }

    // DateOnly son yılına denk gelen geçerli istekler taşma ile 500 üretmesin.
    internal static bool CanAdvance(DateOnly due, RecurrenceFrequency frequency) => frequency switch
    {
        RecurrenceFrequency.Weekly => due.DayNumber <= DateOnly.MaxValue.DayNumber - 7,
        RecurrenceFrequency.Monthly => due.Year < 9999 || due.Month < 12,
        RecurrenceFrequency.Yearly => due.Year < 9999,
        _ => false
    };
}
