using System.Text;
using InvoiceEye.Core.Data;
using InvoiceEye.Core.FixedExpenses;
using InvoiceEye.Core.Lottery;
using InvoiceEye.Core.Models;
using InvoiceEye.Core.Services;
using InvoiceEye.Core.Vision;

namespace InvoiceEye.Tests;

public class PipelineAndLedgerTests
{
    /// <summary>可以指定每個基本功能回傳什麼的假影像引擎。</summary>
    private sealed class FakeEngine : IVisionEngine
    {
        public List<string> Qr { get; } = new();
        public List<string> Barcodes { get; } = new();
        public Rect? Paper { get; set; }
        public Rect? Red { get; set; }
        public Rect? TotalLabel { get; set; }
        public Dictionary<OcrField, string> Ocr { get; } = new();

        private sealed class Img : IVisionImage { public int Width => 600; public int Height => 2000; public void Dispose() { } }
        public string Name => "fake";
        public IVisionImage Load(Stream s) => new Img();
        public IReadOnlyList<byte[]> ReadQrCodes(IVisionImage i) => Qr.Select(Encoding.UTF8.GetBytes).ToList();
        public IReadOnlyList<string> ReadBarcodes(IVisionImage i) => Barcodes;
        public Rect? FindPaper(IVisionImage i) => Paper;
        public Rect? FindRedInk(IVisionImage i, Rect a) => Red;
        public Rect? FindTemplate(IVisionImage i, TemplateKind k, Rect a) => k == TemplateKind.TotalLabel ? TotalLabel : null;
        public string? ReadText(IVisionImage i, Rect a, OcrField f) => Ocr.GetValueOrDefault(f);
    }

    private const string Left = "AB12345678115112212340000005100000055000000001234567801234567890123456789ABCD:**********:1:1:1:便當:1:85";

    private static InvoiceData Run(FakeEngine e) => new RecognitionPipeline(e).Recognize(new MemoryStream(new byte[] { 1 }));

    [Fact]
    public void QR_code_wins_and_barcode_cross_checks()
    {
        var e = new FakeEngine();
        e.Qr.Add(Left);
        e.Barcodes.Add("11512AB123456791234"); // 號碼不同
        var d = Run(e);
        Assert.Equal(DataSource.QrCode, d.NumberSource);
        Assert.Equal(85m, d.TotalAmount);
        Assert.Contains(d.Warnings, w => w.Contains("不一致"));
    }

    [Fact]
    public void Barcode_is_fallback_when_no_qr()
    {
        var e = new FakeEngine();
        e.Barcodes.Add("11512AB123456781234");
        var d = Run(e);
        Assert.Equal(InvoiceType.EInvoice, d.Type);
        Assert.Equal(DataSource.Barcode, d.NumberSource);
        Assert.Null(d.TotalAmount);
    }

    [Fact]
    public void Long_paper_is_cash_register_and_reads_red_number_and_total()
    {
        var e = new FakeEngine
        {
            Paper = new Rect(100, 50, 400, 1800),
            Red = new Rect(150, 200, 300, 60),
            TotalLabel = new Rect(120, 1400, 80, 40),
        };
        e.Ocr[OcrField.InvoiceNumber] = "AB 1234567B";   // B→8 校正
        e.Ocr[OcrField.Period] = "115 11-12";
        e.Ocr[OcrField.Amount] = "$1,35O";              // O→0
        var d = Run(e);
        Assert.Equal(InvoiceType.CashRegister, d.Type);
        Assert.Equal("AB12345678", d.Number);
        Assert.Equal("11512", d.Period!.Value.Key);
        Assert.Equal(1350m, d.TotalAmount);
        Assert.Contains(d.Warnings, w => w.Contains("校正"));
    }

    [Fact]
    public void Wide_paper_is_handwritten_and_asks_for_amount()
    {
        var e = new FakeEngine { Paper = new Rect(0, 0, 600, 420), Red = new Rect(400, 20, 180, 40) };
        e.Ocr[OcrField.InvoiceNumber] = "CD87654321";
        var d = Run(e);
        Assert.Equal(InvoiceType.Handwritten, d.Type);
        Assert.Equal("CD87654321", d.Number);
        Assert.Null(d.TotalAmount);
        Assert.Contains(d.Warnings, w => w.Contains("手寫"));
        Assert.Contains(d.Warnings, w => w.Contains("期別"));
    }

    [Fact]
    public void Mock_engine_produces_parseable_invoice()
    {
        var d = new RecognitionPipeline(new MockVisionEngine(() => new DateOnly(2026, 11, 25)))
            .Recognize(new MemoryStream(Encoding.UTF8.GetBytes("photo")));
        Assert.True(d.HasValidNumber);
        Assert.Equal("11512", d.Period!.Value.Key);
        Assert.Equal(85m, d.TotalAmount);
        Assert.Equal(3, d.Items.Count);
    }

    // ───────────── 帳本 ─────────────

    private static (LedgerService svc, Action<DateOnly> setToday) NewLedger(DateOnly start)
    {
        var today = start;
        var svc = new LedgerService(Db.InMemory(Guid.NewGuid().ToString("N")), () => today);
        return (svc, d => today = d);
    }

    private static SaveInvoiceRequest Req(string number, decimal total = 85, string period = "11512", bool force = false) =>
        new(InvoiceType.EInvoice, number, period, new DateOnly(2026, 11, 22), total, "12345678", "1234",
            new[] { new InvoiceItem("便當", 1, total) }, new[] { new CategoryLine("餐飲", total) }, RememberStore: true, Force: force);

    [Fact]
    public void Saves_invoice_rejects_duplicate_and_remembers_store()
    {
        var (svc, _) = NewLedger(new DateOnly(2026, 11, 25));
        var r = svc.SaveInvoice(Req("AB12345678"));
        Assert.True(r.Saved, r.Error);
        Assert.Equal(LotteryStatus.NotDrawn, r.Lottery!.Status);

        var dup = svc.SaveInvoice(Req("AB12345678"));
        Assert.False(dup.Saved);
        Assert.Contains("已經記過", dup.Error);

        Assert.Equal("餐飲", svc.CategoryForStore("12345678"));
        Assert.Single(svc.GetTransactions("2026-11"));
    }

    [Fact]
    public void Fixed_expenses_auto_record_and_warn_on_matching_invoice()
    {
        var (svc, setToday) = NewLedger(new DateOnly(2026, 11, 25));
        svc.SaveFixedExpense(new FixedExpense(0, "通勤月票", 1200, "交通", 1, new DateOnly(2026, 10, 1)));

        var tx = svc.GetTransactions("2026-11");
        Assert.Single(tx, t => t.Source == "fixed" && t.Amount == 1200);
        Assert.Single(svc.GetTransactions("2026-10"), t => t.Source == "fixed");

        // 再拍到同金額的月票發票 → 提醒可能重複
        var r = svc.SaveInvoice(Req("CD11112222", 1200));
        Assert.False(r.Saved);
        Assert.Contains("固定支出", r.Error);
        Assert.True(svc.SaveInvoice(Req("CD11112222", 1200, force: true)).Saved);

        // 不會重複補記
        Assert.Equal(0, svc.GenerateDueFixedExpenses());

        // 扣款前提醒：11/30 時，12/1 的月票在 3 天內
        setToday(new DateOnly(2026, 11, 30));
        var upcoming = svc.UpcomingFixed();
        Assert.Single(upcoming);
        Assert.Equal(1, upcoming[0].DaysLeft);
    }

    [Fact]
    public void Winning_invoice_becomes_income_once()
    {
        var (svc, setToday) = NewLedger(new DateOnly(2026, 11, 25));
        Assert.True(svc.SaveInvoice(Req("AB00000678")).Saved);

        setToday(new DateOnly(2027, 1, 26));
        var errors = svc.SaveWinningNumbers(new WinningNumbers(new InvoicePeriod(115, 12), "11111111", "22222222", new[] { "12345678" }));
        Assert.Empty(errors);

        var row = svc.LotteryOverview().Single();
        Assert.Equal(Prize.Sixth, row.Result.Prize);

        var income = svc.GetTransactions("2027-01").Where(t => t.Kind == "income").ToList();
        Assert.Single(income);
        Assert.Equal(200m, income[0].Amount);
        Assert.Equal(0, svc.SyncLotteryIncome());
    }

    [Fact]
    public void Report_sums_categories_and_flags_budget()
    {
        var (svc, _) = NewLedger(new DateOnly(2026, 11, 25));
        svc.SetBudget("餐飲", 100);
        svc.SaveInvoice(Req("AB12345678", 85));
        svc.AddManual(new ManualTransactionRequest(new DateOnly(2026, 11, 23), "expense", 50, "寵物", "飼料"));
        svc.AddManual(new ManualTransactionRequest(new DateOnly(2026, 11, 24), "income", 500, "零用錢", null));

        var rep = svc.Report("2026-11");
        Assert.Equal(135m, rep.TotalExpense);
        Assert.Equal(500m, rep.TotalIncome);
        Assert.Equal(85m, rep.ByCategory.Single(c => c.Category == "餐飲").Amount);
        Assert.Single(rep.BudgetAlerts);
        Assert.Contains(svc.GetCategories(), c => c.Name == "寵物");
    }

    [Fact]
    public void Deleting_one_line_removes_whole_invoice()
    {
        var (svc, _) = NewLedger(new DateOnly(2026, 11, 25));
        var req = Req("AB12345678", 184) with
        {
            Lines = new[] { new CategoryLine("餐飲", 85), new CategoryLine("日用品", 99) },
        };
        Assert.True(svc.SaveInvoice(req).Saved);
        var tx = svc.GetTransactions("2026-11");
        Assert.Equal(2, tx.Count);
        Assert.True(svc.DeleteTransaction(tx[0].Id));
        Assert.Empty(svc.GetTransactions("2026-11"));
        Assert.False(svc.InvoiceExists("AB12345678", "11512"));
    }

    [Fact]
    public void Imports_business_registry_csv()
    {
        var (svc, _) = NewLedger(new DateOnly(2026, 11, 25));
        var csv = "﻿營業地址,統一編號,總機構統一編號,營業人名稱,資本額,行業代號,名稱\n" +
                  "台北市,11112222,,好吃餐館,100000,561111,餐館\n" +
                  "台北市,bad,,x,1,1,y\n";
        Assert.Equal(1, svc.ImportBusinessRegistry(new StringReader(csv)));
        Assert.Equal("餐館", svc.IndustryOf("11112222"));
        Assert.Equal("好吃餐館", svc.StoreNameOf("11112222"));
    }
}
