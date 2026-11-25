using InvoiceEye.Core.Classification;
using InvoiceEye.Core.FixedExpenses;
using InvoiceEye.Core.Models;

namespace InvoiceEye.Tests;

public class ClassificationAndFixedTests
{
    private sealed class Rules : IUserRuleStore, IBusinessRegistry
    {
        public Dictionary<string, string> Stores { get; } = new();
        public Dictionary<string, string> Items { get; } = new();
        public Dictionary<string, string> Industries { get; } = new();
        public string? CategoryForStore(string id) => Stores.GetValueOrDefault(id);
        public string? CategoryForItem(string name) => Items.GetValueOrDefault(name);
        public string? IndustryOf(string id) => Industries.GetValueOrDefault(id);
    }

    private static InvoiceData Invoice(decimal total, string? seller, params (string Name, decimal Price)[] items) => new()
    {
        Type = InvoiceType.EInvoice,
        Number = "AB12345678",
        TotalAmount = total,
        SellerTaxId = seller,
        Items = items.Select(i => new InvoiceItem(i.Name, 1, i.Price)).ToList(),
    };

    [Fact]
    public void Splits_mixed_invoice_by_keyword()
    {
        var r = new CategoryClassifier(new Rules()).Classify(Invoice(184, null, ("排骨便當", 85), ("洗髮精", 99)));
        Assert.False(r.NeedsUserChoice);
        Assert.Equal(85m, r.TotalsByCategory[Categories.Food]);
        Assert.Equal(99m, r.TotalsByCategory[Categories.Daily]);
    }

    [Fact]
    public void User_rules_win_over_keywords()
    {
        var rules = new Rules();
        rules.Items["排骨便當"] = "聚餐";
        var r = new CategoryClassifier(rules).Classify(Invoice(85, null, ("排骨便當", 85)));
        Assert.Equal("聚餐", r.Lines.Single().Category);
        Assert.Equal(ClassificationSource.UserItemRule, r.Lines.Single().Source);
    }

    [Fact]
    public void Falls_back_to_industry_then_asks_user()
    {
        var rules = new Rules();
        rules.Industries["11112222"] = "汽車燃料零售業（加油站）";
        var r = new CategoryClassifier(rules, rules).Classify(Invoice(500, "11112222", ("95無鉛", 500)));
        Assert.Equal(Categories.Transport, r.Lines.Single().Category);
        Assert.Equal(ClassificationSource.Industry, r.Lines.Single().Source);

        var unknown = new CategoryClassifier(rules, rules).Classify(Invoice(280, "99998888", ("寵物飼料", 280)));
        Assert.True(unknown.NeedsUserChoice);
    }

    [Fact]
    public void Traditional_invoice_without_items_uses_store_rule()
    {
        var rules = new Rules();
        rules.Stores["12345678"] = Categories.Food;
        var inv = new InvoiceData { Type = InvoiceType.CashRegister, TotalAmount = 135, SellerTaxId = "12345678" };
        var r = new CategoryClassifier(rules).Classify(inv);
        Assert.Equal(Categories.Food, r.Lines.Single().Category);
        Assert.Equal(135m, r.Lines.Single().Amount);
    }

    [Fact]
    public void Discount_difference_goes_to_largest_line()
    {
        var r = new CategoryClassifier(new Rules()).Classify(Invoice(170, null, ("便當", 85), ("洗髮精", 99)));
        Assert.Equal(85m, r.TotalsByCategory[Categories.Food]);
        Assert.Equal(85m, r.TotalsByCategory[Categories.Daily]); // 99 - 14 折扣
        Assert.Equal(170m, r.Lines.Sum(l => l.Amount));
    }

    [Fact]
    public void Fixed_expense_due_dates_clamp_to_month_end_and_skip_recorded()
    {
        var e = new FixedExpense(1, "房租", 8000, Categories.Housing, 31, new DateOnly(2026, 1, 15));
        var dates = FixedExpenseSchedule.DueDatesUntil(e, new DateOnly(2026, 3, 30), new HashSet<string> { "2026-01" }).ToList();
        Assert.Equal(new[] { new DateOnly(2026, 2, 28) }, dates); // 1 月已記、3/31 還沒到
    }

    [Fact]
    public void Fixed_expense_respects_start_date()
    {
        var e = new FixedExpense(1, "月票", 1200, Categories.Transport, 1, new DateOnly(2026, 11, 20));
        Assert.Empty(FixedExpenseSchedule.DueDatesUntil(e, new DateOnly(2026, 11, 30), new HashSet<string>()));
        Assert.Equal(new DateOnly(2026, 12, 1), FixedExpenseSchedule.NextDueDate(e, new DateOnly(2026, 11, 25)));
    }
}
