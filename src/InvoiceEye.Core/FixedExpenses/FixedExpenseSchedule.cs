namespace InvoiceEye.Core.FixedExpenses;

/// <summary>每月固定支出，例如房租、通勤月票、訂閱服務。</summary>
public sealed record FixedExpense(
    long Id,
    string Name,
    decimal Amount,
    string Category,
    int DayOfMonth,
    DateOnly StartDate,
    bool Active = true,
    DateOnly? EndDate = null);

public static class FixedExpenseSchedule
{
    /// <summary>某個月的扣款日；29～31 號遇到小月時改成該月最後一天。</summary>
    public static DateOnly DueDate(int year, int month, int dayOfMonth) =>
        new(year, month, Math.Min(Math.Max(dayOfMonth, 1), DateTime.DaysInMonth(year, month)));

    /// <summary>
    /// 從開始日到今天（含）之間所有已到扣款日的日期，用來自動補記。
    /// alreadyRecorded 是已經記過的月份（yyyy-MM），避免重複。
    /// </summary>
    public static IEnumerable<DateOnly> DueDatesUntil(FixedExpense e, DateOnly today, ISet<string> alreadyRecorded)
    {
        if (!e.Active) yield break;
        var cursor = new DateOnly(e.StartDate.Year, e.StartDate.Month, 1);
        while (cursor <= today)
        {
            var due = DueDate(cursor.Year, cursor.Month, e.DayOfMonth);
            var inRange = due >= e.StartDate && due <= today && (e.EndDate is null || due <= e.EndDate);
            if (inRange && !alreadyRecorded.Contains(MonthKey(due))) yield return due;
            cursor = cursor.AddMonths(1);
        }
    }

    /// <summary>下一次扣款日（今天還沒扣也算今天）。</summary>
    public static DateOnly? NextDueDate(FixedExpense e, DateOnly today)
    {
        if (!e.Active) return null;
        var from = today < e.StartDate ? e.StartDate : today;
        for (var i = 0; i < 2; i++)
        {
            var m = new DateOnly(from.Year, from.Month, 1).AddMonths(i);
            var due = DueDate(m.Year, m.Month, e.DayOfMonth);
            if (due >= from && due >= e.StartDate)
                return e.EndDate is { } end && due > end ? null : due;
        }
        return null;
    }

    public static string MonthKey(DateOnly d) => $"{d.Year:0000}-{d.Month:00}";
}
