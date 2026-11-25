using InvoiceEye.Core.FixedExpenses;
using InvoiceEye.Core.Lottery;
using InvoiceEye.Core.Models;
using InvoiceEye.Core.Services;

namespace InvoiceEye.Api;

/// <summary>把內部資料轉成給 App 用的 JSON 格式。</summary>
public static class Dto
{
    public static string Month(DateOnly d) => $"{d.Year:0000}-{d.Month:00}";

    public static object Invoice(InvoiceData d, string? storeName) => new
    {
        type = d.Type.ToString(),
        typeName = d.Type switch
        {
            InvoiceType.EInvoice => "電子發票",
            InvoiceType.CashRegister => "收銀機發票",
            InvoiceType.Handwritten => "手開發票",
            _ => "未知",
        },
        number = d.Number,
        numberDisplay = d.Number is null ? null : InvoiceNumber.Format(d.Number),
        period = d.Period is { } p ? new { key = p.Key, display = p.Display } : null,
        date = d.Date,
        randomCode = d.RandomCode,
        total = d.TotalAmount,
        sellerTaxId = d.SellerTaxId,
        storeName,
        items = d.Items.Select(i => new { i.Name, i.Quantity, i.UnitPrice, i.Amount }),
        numberSource = d.NumberSource.ToString(),
        amountSource = d.AmountSource.ToString(),
        warnings = d.Warnings,
    };

    public static object SaveResult(SaveInvoiceResult r) => new
    {
        saved = r.Saved,
        invoiceId = r.InvoiceId,
        error = r.Error,
        warnings = r.Warnings,
        lottery = r.Lottery is { } l ? new { status = l.Status.ToString(), l.Message } : null,
    };

    public static object Tx(TransactionRecord t) => new
    {
        t.Id, t.Date, t.Kind, t.Amount, t.Category, t.Note, t.Source, t.InvoiceId,
        invoiceNumber = t.InvoiceNumber is null ? null : InvoiceNumber.Format(t.InvoiceNumber),
        t.FixedExpenseId,
    };

    public static object Fixed(FixedExpense e, DateOnly today) => new
    {
        e.Id, e.Name, e.Amount, e.Category, e.DayOfMonth, e.StartDate, e.EndDate, e.Active,
        nextDueDate = FixedExpenseSchedule.NextDueDate(e, today),
    };

    public static object LotteryRow(InvoiceLotteryRow r) => new
    {
        r.InvoiceId,
        number = InvoiceNumber.Format(r.Number),
        period = r.PeriodKey is not null && InvoicePeriod.TryParseKey(r.PeriodKey, out var p) ? p.Display : "未知期別",
        r.Date,
        r.Total,
        status = r.Result.Status.ToString(),
        prize = LotteryChecker.PrizeName(r.Result.Prize),
        r.Result.PrizeAmount,
        r.Result.Message,
        claimEnd = r.Result.ClaimEnd,
    };

    public static object Winning(WinningNumbers w) => new
    {
        period = w.Period.Key, periodDisplay = w.Period.Display, w.Special, w.Grand, w.First,
        additionalSixth = w.AdditionalSixth ?? Array.Empty<string>(),
    };

    public static object Report(MonthReport r) => new
    {
        r.Month, r.TotalExpense, r.TotalIncome,
        balance = r.TotalIncome - r.TotalExpense,
        byCategory = r.ByCategory,
        daily = r.Daily.Select(d => new { date = d.Date, amount = d.Amount }),
        r.BudgetAlerts,
    };
}
