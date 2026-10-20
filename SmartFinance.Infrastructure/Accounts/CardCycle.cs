using System.Globalization;
using SmartFinance.Application.DTOs.Account;

namespace SmartFinance.Infrastructure.Accounts;

// Saf hesap: zaman ve bütün hareketler çağıran tarafından verilir; DB/sistem saati yoktur.
// ZAMAN KURALI: harcama/iade (Transaction) tarihi TSİ duvar saatidir → gün dönüştürülmeden alınır.
// Ödeme (Transfer) tarihi UTC'dir → gün UTC→TSİ dönüşümüyle alınır. 'Bugün' TSİ'dir.
public static class CardCycle
{
    public readonly record struct Entry(DateTime Date, decimal Amount);

    // Transaction.TransactionDate için: duvar saati, Kind'dan bağımsız olarak dönüştürülmez.
    public static DateOnly WallClockDay(DateTime wallClock) => DateOnly.FromDateTime(wallClock);

    public static DateOnly IstanbulDay(DateTime utc)
    {
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul"); }
        catch (TimeZoneNotFoundException) { zone = TimeZoneInfo.FindSystemTimeZoneById("Turkey Standard Time"); }
        // SQL datetime Kind saklamaz; kaynağın UTC olduğu sözleşmesi korunur.
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone));
    }

    public static DateOnly StatementDate(int year, int month, int statementDay)
    {
        if (statementDay is < 1 or > 28) throw new ArgumentOutOfRangeException(nameof(statementDay));
        return new DateOnly(year, month, Math.Min(statementDay, DateTime.DaysInMonth(year, month)));
    }

    public static DateOnly DueDate(DateOnly statement, int dueDay)
    {
        if (dueDay is < 1 or > 28) throw new ArgumentOutOfRangeException(nameof(dueDay));
        var month = new DateOnly(statement.Year, statement.Month, 1);
        if (dueDay <= statement.Day) month = month.AddMonths(1);
        return new DateOnly(month.Year, month.Month, Math.Min(dueDay, DateTime.DaysInMonth(month.Year, month.Month)));
    }

    public static StatementsDto Calculate(int statementDay, int dueDay, decimal openingBalance,
        IEnumerable<Entry> charges, IEnumerable<Entry> payments, DateTime todayUtc, int count = 3)
    {
        if (count is < 1 or > 12) throw new ArgumentOutOfRangeException(nameof(count));
        var today = IstanbulDay(todayUtc);
        var month = new DateOnly(today.Year, today.Month, 1);
        var lastClosed = StatementDate(month.Year, month.Month, statementDay);
        if (today < lastClosed)
        {
            month = month.AddMonths(-1);
            lastClosed = StatementDate(month.Year, month.Month, statementDay);
        }
        var nextMonth = month.AddMonths(1);
        var openEnd = StatementDate(nextMonth.Year, nextMonth.Month, statementDay);
        var datedCharges = charges.Select(x => (Day: WallClockDay(x.Date), x.Amount))
            .Where(x => x.Day <= today).ToArray();
        var amounts = new Dictionary<DateOnly, decimal>();
        foreach (var charge in datedCharges)
        {
            var end = StatementDate(charge.Day.Year, charge.Day.Month, statementDay);
            if (charge.Day > end)
            {
                var next = new DateOnly(charge.Day.Year, charge.Day.Month, 1).AddMonths(1);
                end = StatementDate(next.Year, next.Month, statementDay);
            }
            amounts[end] = amounts.GetValueOrDefault(end) + charge.Amount;
        }

        // Tüm tarihçe FIFO'ya katılır. İade fazlası kredi gibi sonraki borçlara taşınır.
        // Açılış borcu ilk kalemdir; API'de ekstre gibi gösterilmez.
        var credit = payments.Where(x => IstanbulDay(x.Date) <= today).Sum(x => x.Amount)
            + amounts.Values.Where(x => x < 0).Sum(x => -x);
        credit = Math.Max(0, credit - openingBalance);
        var paidByEnd = new Dictionary<DateOnly, decimal>();
        foreach (var end in amounts.Keys.OrderBy(x => x))
        {
            var paid = Math.Min(credit, Math.Max(0, amounts[end]));
            paidByEnd[end] = paid;
            credit -= paid;
        }

        StatementDto Build(DateOnly end, bool open)
        {
            var previousMonth = new DateOnly(end.Year, end.Month, 1).AddMonths(-1);
            var start = StatementDate(previousMonth.Year, previousMonth.Month, statementDay).AddDays(1);
            var due = DueDate(end, dueDay);
            var amount = amounts.GetValueOrDefault(end);
            var paid = paidByEnd.GetValueOrDefault(end);
            var remaining = Math.Max(0, amount - paid);
            return new StatementDto
            {
                PeriodStart = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                PeriodEnd = end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                DueDate = due.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Amount = amount, Paid = paid, Remaining = remaining,
                Status = open ? "open" : amount == 0 && paid == 0 ? "empty"
                    : remaining == 0 ? "paid" : today > due ? "overdue" : paid > 0 ? "partial" : "unpaid"
            };
        }

        var past = new List<StatementDto>();
        for (var i = 0; i < count; i++)
        {
            var m = month.AddMonths(-i);
            past.Add(Build(StatementDate(m.Year, m.Month, statementDay), false));
        }
        return new StatementsDto { Current = Build(openEnd, true), Past = past };
    }
}
