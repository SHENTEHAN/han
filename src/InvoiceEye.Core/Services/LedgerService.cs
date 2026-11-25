using System.Globalization;
using System.Text.Json;
using InvoiceEye.Core.Classification;
using InvoiceEye.Core.Data;
using InvoiceEye.Core.FixedExpenses;
using InvoiceEye.Core.Lottery;
using InvoiceEye.Core.Models;
using Microsoft.Data.Sqlite;

namespace InvoiceEye.Core.Services;

public sealed record CategoryInfo(string Name, decimal? Budget, bool IsIncome);

public sealed record TransactionRecord(
    long Id, DateOnly Date, string Kind, decimal Amount, string Category, string? Note, string Source,
    long? InvoiceId, string? InvoiceNumber, long? FixedExpenseId);

public sealed record CategoryLine(string Category, decimal Amount, string? Note = null);

public sealed record SaveInvoiceRequest(
    InvoiceType Type,
    string Number,
    string? PeriodKey,
    DateOnly Date,
    decimal Total,
    string? SellerTaxId,
    string? RandomCode,
    IReadOnlyList<InvoiceItem>? Items,
    IReadOnlyList<CategoryLine> Lines,
    bool RememberStore = false,
    IReadOnlyDictionary<string, string>? LearnItemCategories = null,
    bool Force = false);

public sealed record SaveInvoiceResult(bool Saved, long? InvoiceId, string? Error, IReadOnlyList<string> Warnings, LotteryResult? Lottery);

public sealed record ManualTransactionRequest(DateOnly Date, string Kind, decimal Amount, string Category, string? Note);

public sealed record InvoiceLotteryRow(long InvoiceId, string Number, string? PeriodKey, DateOnly Date, decimal Total, LotteryResult Result);

public sealed record CategoryTotal(string Category, decimal Amount, decimal? Budget);

public sealed record MonthReport(
    string Month, decimal TotalExpense, decimal TotalIncome,
    IReadOnlyList<CategoryTotal> ByCategory,
    IReadOnlyList<(DateOnly Date, decimal Amount)> Daily,
    IReadOnlyList<string> BudgetAlerts);

public sealed record UpcomingFixed(FixedExpense Expense, DateOnly DueDate, int DaysLeft);

/// <summary>帳本、固定支出、對獎、分類規則的資料存取與商業邏輯。</summary>
public sealed class LedgerService : IUserRuleStore, IBusinessRegistry
{
    private readonly Db _db;
    private readonly Func<DateOnly> _today;

    public LedgerService(Db db, Func<DateOnly>? today = null)
    {
        _db = db;
        _today = today ?? (() => DateOnly.FromDateTime(DateTime.Now));
        SeedCategories();
    }

    public DateOnly Today => _today();

    private static string Now() => DateTime.Now.ToString("s", CultureInfo.InvariantCulture);

    // ───────────── 分類 ─────────────

    private void SeedCategories()
    {
        using var c = _db.Open();
        var i = 0;
        foreach (var name in Categories.DefaultExpense)
            Db.Exec(c, "INSERT OR IGNORE INTO categories(name, sort, is_income) VALUES ($n, $s, 0)", ("$n", name), ("$s", i++));
        Db.Exec(c, "INSERT OR IGNORE INTO categories(name, sort, is_income) VALUES ($n, 1000, 1)", ("$n", Categories.LotteryIncome));
        Db.Exec(c, "INSERT OR IGNORE INTO categories(name, sort, is_income) VALUES ('薪水', 1001, 1)");
        Db.Exec(c, "INSERT OR IGNORE INTO categories(name, sort, is_income) VALUES ('零用錢', 1002, 1)");
    }

    public List<CategoryInfo> GetCategories()
    {
        using var c = _db.Open();
        return Db.Query(c, "SELECT * FROM categories ORDER BY is_income, sort, name",
            r => new CategoryInfo((string)r["name"], Db.DecN(r, "budget"), Db.Long(r, "is_income") == 1));
    }

    /// <summary>新增分類（使用者在選單「自行輸入」），已存在就不動。</summary>
    public void EnsureCategory(string name, bool isIncome = false)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        using var c = _db.Open();
        Db.Exec(c, "INSERT OR IGNORE INTO categories(name, sort, is_income) VALUES ($n, 500, $i)", ("$n", name.Trim()), ("$i", isIncome));
    }

    public void SetBudget(string name, decimal? budget)
    {
        EnsureCategory(name);
        using var c = _db.Open();
        Db.Exec(c, "UPDATE categories SET budget = $b WHERE name = $n", ("$b", budget), ("$n", name));
    }

    // ───────────── 分類規則（IUserRuleStore / IBusinessRegistry）─────────────

    public string? CategoryForStore(string sellerTaxId)
    {
        using var c = _db.Open();
        return (string?)Db.Scalar(c, "SELECT category FROM store_rules WHERE seller_tax_id = $t", ("$t", sellerTaxId));
    }

    public string? CategoryForItem(string itemName)
    {
        using var c = _db.Open();
        return (string?)Db.Scalar(c, "SELECT category FROM item_rules WHERE item_name = $n", ("$n", itemName.Trim()));
    }

    public string? IndustryOf(string sellerTaxId)
    {
        using var c = _db.Open();
        return (string?)Db.Scalar(c, "SELECT industry FROM business_registry WHERE tax_id = $t", ("$t", sellerTaxId));
    }

    public string? StoreNameOf(string sellerTaxId)
    {
        using var c = _db.Open();
        return (string?)Db.Scalar(c, "SELECT name FROM business_registry WHERE tax_id = $t", ("$t", sellerTaxId));
    }

    public void RememberStore(string sellerTaxId, string category)
    {
        using var c = _db.Open();
        Db.Exec(c, "INSERT INTO store_rules(seller_tax_id, category) VALUES ($t, $c) ON CONFLICT(seller_tax_id) DO UPDATE SET category = $c",
            ("$t", sellerTaxId), ("$c", category));
    }

    public void RememberItem(string itemName, string category)
    {
        using var c = _db.Open();
        Db.Exec(c, "INSERT INTO item_rules(item_name, category) VALUES ($n, $c) ON CONFLICT(item_name) DO UPDATE SET category = $c",
            ("$n", itemName.Trim()), ("$c", category));
    }

    /// <summary>
    /// 匯入財政部「全國營業(稅籍)登記資料集」CSV。
    /// 欄位名稱各版可能不同，所以用表頭關鍵字找：含「統一編號」的欄位、含「營業人名稱」的欄位、
    /// 以及第一個名稱含「行業」且不含「代號」的欄位（例如「行業名稱」）或緊接在「行業代號」後面的「名稱」欄。
    /// </summary>
    public int ImportBusinessRegistry(TextReader csv)
    {
        var header = ParseCsvLine(csv.ReadLine() ?? "");
        int Find(Func<string, bool> pred) => header.FindIndex(h => pred(h.Trim().Trim('﻿')));
        var taxIdx = Find(h => h.Contains("統一編號") && !h.Contains("總機構"));
        var nameIdx = Find(h => h.Contains("營業人名稱"));
        var industryIdx = Find(h => h.Contains("行業") && !h.Contains("代號"));
        if (industryIdx < 0)
        {
            var codeIdx = Find(h => h.Contains("行業代號"));
            if (codeIdx >= 0 && codeIdx + 1 < header.Count) industryIdx = codeIdx + 1;
        }
        if (taxIdx < 0 || industryIdx < 0)
            throw new InvalidDataException("CSV 表頭找不到「統一編號」或「行業名稱」欄位");

        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        using var cmd = Db.Command(c, "INSERT INTO business_registry(tax_id, name, industry) VALUES ($t, $n, $i) " +
                                      "ON CONFLICT(tax_id) DO UPDATE SET name = $n, industry = $i");
        cmd.Transaction = tx;
        var pt = cmd.Parameters.Add("$t", SqliteType.Text);
        var pn = cmd.Parameters.Add("$n", SqliteType.Text);
        var pi = cmd.Parameters.Add("$i", SqliteType.Text);
        var count = 0;
        string? line;
        while ((line = csv.ReadLine()) is not null)
        {
            var f = ParseCsvLine(line);
            if (f.Count <= Math.Max(taxIdx, industryIdx)) continue;
            var tax = f[taxIdx].Trim();
            if (tax.Length != 8 || !tax.All(char.IsAsciiDigit)) continue;
            pt.Value = tax;
            pn.Value = nameIdx >= 0 && nameIdx < f.Count ? f[nameIdx].Trim() : DBNull.Value;
            pi.Value = f[industryIdx].Trim();
            cmd.ExecuteNonQuery();
            count++;
        }
        tx.Commit();
        return count;
    }

    private static List<string> ParseCsvLine(string line)
    {
        var result = new List<string>();
        var sb = new System.Text.StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (inQuotes)
            {
                if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (ch == '"') inQuotes = false;
                else sb.Append(ch);
            }
            else if (ch == '"') inQuotes = true;
            else if (ch == ',') { result.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        result.Add(sb.ToString());
        return result;
    }

    // ───────────── 發票記帳 ─────────────

    public bool InvoiceExists(string number, string? periodKey)
    {
        using var c = _db.Open();
        return Db.Scalar(c, "SELECT 1 FROM invoices WHERE number = $n AND period IS $p", ("$n", number), ("$p", periodKey)) is not null;
    }

    /// <summary>同月份是否已有金額相同的固定支出（例如月票發票和固定支出重複）。</summary>
    public string? FindMatchingFixed(DateOnly date, decimal amount)
    {
        using var c = _db.Open();
        return (string?)Db.Scalar(c,
            "SELECT f.name FROM transactions t JOIN fixed_expenses f ON f.id = t.fixed_expense_id " +
            "WHERE t.source = 'fixed' AND t.fixed_month = $m AND ABS(t.amount - $a) < 0.5 LIMIT 1",
            ("$m", FixedExpenseSchedule.MonthKey(date)), ("$a", amount));
    }

    /// <summary>辨識完、使用者確認後，存成發票＋各分類的支出紀錄。</summary>
    public SaveInvoiceResult SaveInvoice(SaveInvoiceRequest req)
    {
        var warnings = new List<string>();
        if (!InvoiceNumber.IsValid(req.Number))
            return new SaveInvoiceResult(false, null, "發票號碼格式不正確（應為 2 個英文字＋8 位數字）", warnings, null);
        if (req.PeriodKey is not null && !InvoicePeriod.TryParseKey(req.PeriodKey, out _))
            return new SaveInvoiceResult(false, null, "期別格式不正確（例如 11512）", warnings, null);
        if (req.Lines.Count == 0 || req.Lines.Any(l => string.IsNullOrWhiteSpace(l.Category)))
            return new SaveInvoiceResult(false, null, "每一筆都要選擇分類", warnings, null);
        if (req.Total <= 0)
            return new SaveInvoiceResult(false, null, "金額必須大於 0", warnings, null);

        if (InvoiceExists(req.Number, req.PeriodKey))
            return new SaveInvoiceResult(false, null, $"這張發票（{InvoiceNumber.Format(req.Number)}）已經記過了", warnings, null);

        if (!req.Force && FindMatchingFixed(req.Date, req.Total) is { } fixedName)
            return new SaveInvoiceResult(false, null, $"這筆可能已在固定支出「{fixedName}」記過。確定要再記一次嗎？", warnings, null);

        var lineSum = req.Lines.Sum(l => l.Amount);
        if (lineSum != req.Total)
            warnings.Add($"各分類加總 {lineSum:#,0} 元和發票總計 {req.Total:#,0} 元不同");

        long invoiceId;
        using (var c = _db.Open())
        using (var tx = c.BeginTransaction())
        {
            Db.Exec(c, """
                INSERT INTO invoices(number, period, type, date, total, seller_tax_id, random_code, items_json, created_at)
                VALUES ($n, $p, $t, $d, $total, $s, $r, $items, $now)
                """,
                ("$n", req.Number), ("$p", req.PeriodKey), ("$t", (int)req.Type), ("$d", req.Date), ("$total", req.Total),
                ("$s", req.SellerTaxId), ("$r", req.RandomCode),
                ("$items", req.Items is { Count: > 0 } ? JsonSerializer.Serialize(req.Items) : null), ("$now", Now()));
            invoiceId = (long)Db.Scalar(c, "SELECT last_insert_rowid()")!;

            foreach (var line in req.Lines)
            {
                Db.Exec(c, "INSERT OR IGNORE INTO categories(name, sort, is_income) VALUES ($n, 500, 0)", ("$n", line.Category.Trim()));
                Db.Exec(c, """
                    INSERT INTO transactions(date, kind, amount, category, note, source, invoice_id, created_at)
                    VALUES ($d, 'expense', $a, $c, $note, 'invoice', $i, $now)
                    """,
                    ("$d", req.Date), ("$a", line.Amount), ("$c", line.Category.Trim()),
                    ("$note", line.Note), ("$i", invoiceId), ("$now", Now()));
            }
            tx.Commit();
        }

        // 記住使用者的選擇，下次自動分類
        if (req.RememberStore && req.SellerTaxId is { Length: 8 } && req.Lines.Select(l => l.Category).Distinct().Count() == 1)
            RememberStore(req.SellerTaxId, req.Lines[0].Category.Trim());
        foreach (var (item, cat) in req.LearnItemCategories ?? new Dictionary<string, string>())
            RememberItem(item, cat);

        SyncLotteryIncome();
        var period = req.PeriodKey is not null && InvoicePeriod.TryParseKey(req.PeriodKey, out var p) ? p : (InvoicePeriod?)null;
        var lottery = LotteryChecker.Check(req.Number[2..], period, GetWinningNumbers(req.PeriodKey), Today);
        return new SaveInvoiceResult(true, invoiceId, null, warnings, lottery);
    }

    public long AddManual(ManualTransactionRequest req)
    {
        if (req.Kind is not ("expense" or "income")) throw new ArgumentException("kind 必須是 expense 或 income");
        if (req.Amount <= 0) throw new ArgumentException("金額必須大於 0");
        if (string.IsNullOrWhiteSpace(req.Category)) throw new ArgumentException("請選擇分類");
        EnsureCategory(req.Category, req.Kind == "income");
        using var c = _db.Open();
        Db.Exec(c, """
            INSERT INTO transactions(date, kind, amount, category, note, source, created_at)
            VALUES ($d, $k, $a, $c, $n, 'manual', $now)
            """, ("$d", req.Date), ("$k", req.Kind), ("$a", req.Amount), ("$c", req.Category.Trim()), ("$n", req.Note), ("$now", Now()));
        return (long)Db.Scalar(c, "SELECT last_insert_rowid()")!;
    }

    /// <summary>刪除一筆紀錄；若是發票的其中一筆，連同整張發票與其他分類一起刪除。</summary>
    public bool DeleteTransaction(long id)
    {
        using var c = _db.Open();
        var invoiceId = Db.Scalar(c, "SELECT invoice_id FROM transactions WHERE id = $id AND source = 'invoice'", ("$id", id));
        if (invoiceId is long inv)
            return Db.Exec(c, "DELETE FROM invoices WHERE id = $i", ("$i", inv)) > 0; // ON DELETE CASCADE 會刪掉相關紀錄
        return Db.Exec(c, "DELETE FROM transactions WHERE id = $id", ("$id", id)) > 0;
    }

    public List<TransactionRecord> GetTransactions(string month)
    {
        GenerateDueFixedExpenses();
        using var c = _db.Open();
        return Db.Query(c, """
            SELECT t.*, i.number AS invoice_number FROM transactions t
            LEFT JOIN invoices i ON i.id = t.invoice_id
            WHERE substr(t.date, 1, 7) = $m
            ORDER BY t.date DESC, t.id DESC
            """, MapTx, ("$m", month));
    }

    private static TransactionRecord MapTx(SqliteDataReader r) => new(
        Db.Long(r, "id"), Db.ParseDate((string)r["date"]), (string)r["kind"], Db.Dec(r, "amount"), (string)r["category"],
        Db.Str(r, "note"), (string)r["source"], Db.LongN(r, "invoice_id"), Db.Str(r, "invoice_number"), Db.LongN(r, "fixed_expense_id"));

    // ───────────── 固定支出 ─────────────

    public List<FixedExpense> GetFixedExpenses()
    {
        using var c = _db.Open();
        return Db.Query(c, "SELECT * FROM fixed_expenses ORDER BY active DESC, day_of_month, id", MapFixed);
    }

    private static FixedExpense MapFixed(SqliteDataReader r) => new(
        Db.Long(r, "id"), (string)r["name"], Db.Dec(r, "amount"), (string)r["category"], (int)Db.Long(r, "day_of_month"),
        Db.ParseDate((string)r["start_date"]), Db.Long(r, "active") == 1,
        Db.Str(r, "end_date") is { } e ? Db.ParseDate(e) : null);

    public FixedExpense SaveFixedExpense(FixedExpense e)
    {
        if (string.IsNullOrWhiteSpace(e.Name)) throw new ArgumentException("請輸入名稱");
        if (e.Amount <= 0) throw new ArgumentException("金額必須大於 0");
        if (e.DayOfMonth is < 1 or > 31) throw new ArgumentException("扣款日必須是 1～31");
        EnsureCategory(e.Category);
        using var c = _db.Open();
        if (e.Id == 0)
        {
            Db.Exec(c, """
                INSERT INTO fixed_expenses(name, amount, category, day_of_month, start_date, end_date, active)
                VALUES ($n, $a, $c, $d, $s, $e, $act)
                """, ("$n", e.Name.Trim()), ("$a", e.Amount), ("$c", e.Category), ("$d", e.DayOfMonth),
                ("$s", e.StartDate), ("$e", e.EndDate), ("$act", e.Active));
            e = e with { Id = (long)Db.Scalar(c, "SELECT last_insert_rowid()")! };
        }
        else
        {
            Db.Exec(c, """
                UPDATE fixed_expenses SET name = $n, amount = $a, category = $c, day_of_month = $d,
                    start_date = $s, end_date = $e, active = $act WHERE id = $id
                """, ("$n", e.Name.Trim()), ("$a", e.Amount), ("$c", e.Category), ("$d", e.DayOfMonth),
                ("$s", e.StartDate), ("$e", e.EndDate), ("$act", e.Active), ("$id", e.Id));
        }
        GenerateDueFixedExpenses();
        return e;
    }

    public bool DeleteFixedExpense(long id)
    {
        using var c = _db.Open();
        return Db.Exec(c, "DELETE FROM fixed_expenses WHERE id = $id", ("$id", id)) > 0;
    }

    /// <summary>補記所有已到扣款日、還沒記過的固定支出。回傳新增的筆數。</summary>
    public int GenerateDueFixedExpenses()
    {
        var today = Today;
        var added = 0;
        using var c = _db.Open();
        foreach (var e in Db.Query(c, "SELECT * FROM fixed_expenses WHERE active = 1", MapFixed))
        {
            var recorded = Db.Query(c, "SELECT fixed_month FROM transactions WHERE source = 'fixed' AND fixed_expense_id = $id",
                r => (string)r["fixed_month"], ("$id", e.Id)).ToHashSet();
            foreach (var due in FixedExpenseSchedule.DueDatesUntil(e, today, recorded))
            {
                added += Db.Exec(c, """
                    INSERT OR IGNORE INTO transactions(date, kind, amount, category, note, source, fixed_expense_id, fixed_month, created_at)
                    VALUES ($d, 'expense', $a, $c, $n, 'fixed', $id, $m, $now)
                    """, ("$d", due), ("$a", e.Amount), ("$c", e.Category), ("$n", e.Name), ("$id", e.Id),
                    ("$m", FixedExpenseSchedule.MonthKey(due)), ("$now", Now()));
            }
        }
        return added;
    }

    /// <summary>接下來幾天內要扣款的固定支出（扣款前提醒）。</summary>
    public List<UpcomingFixed> UpcomingFixed(int withinDays = 3)
    {
        var today = Today;
        return GetFixedExpenses()
            .Select(e => (e, due: FixedExpenseSchedule.NextDueDate(e, today.AddDays(1))))
            .Where(x => x.due is { } d && d.DayNumber - today.DayNumber <= withinDays)
            .Select(x => new UpcomingFixed(x.e, x.due!.Value, x.due.Value.DayNumber - today.DayNumber))
            .OrderBy(x => x.DueDate)
            .ToList();
    }

    // ───────────── 對獎 ─────────────

    public WinningNumbers? GetWinningNumbers(string? periodKey)
    {
        if (periodKey is null || !InvoicePeriod.TryParseKey(periodKey, out var period)) return null;
        using var c = _db.Open();
        return Db.Query(c, "SELECT * FROM winning_numbers WHERE period = $p", r => MapWinning(r, period), ("$p", periodKey)).FirstOrDefault();
    }

    public List<WinningNumbers> GetAllWinningNumbers()
    {
        using var c = _db.Open();
        return Db.Query(c, "SELECT * FROM winning_numbers ORDER BY period DESC", r =>
        {
            InvoicePeriod.TryParseKey((string)r["period"], out var p);
            return MapWinning(r, p);
        });
    }

    private static WinningNumbers MapWinning(SqliteDataReader r, InvoicePeriod p) => new(
        p, (string)r["special"], (string)r["grand"],
        ((string)r["first"]).Split(',', StringSplitOptions.RemoveEmptyEntries),
        Db.Str(r, "extra_sixth")?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>());

    public IReadOnlyList<string> SaveWinningNumbers(WinningNumbers w)
    {
        var errors = w.Validate().ToList();
        if (!w.Period.IsValid) errors.Insert(0, "期別格式不正確");
        if (errors.Count > 0) return errors;
        using (var c = _db.Open())
        {
            Db.Exec(c, """
                INSERT INTO winning_numbers(period, special, grand, first, extra_sixth) VALUES ($p, $s, $g, $f, $x)
                ON CONFLICT(period) DO UPDATE SET special = $s, grand = $g, first = $f, extra_sixth = $x
                """, ("$p", w.Period.Key), ("$s", w.Special), ("$g", w.Grand), ("$f", string.Join(',', w.First)),
                ("$x", w.AdditionalSixth is { Count: > 0 } x ? string.Join(',', x) : null));
        }
        SyncLotteryIncome();
        return errors;
    }

    /// <summary>所有發票的對獎結果（新到舊）。</summary>
    public List<InvoiceLotteryRow> LotteryOverview()
    {
        var today = Today;
        var winning = GetAllWinningNumbers().ToDictionary(w => w.Period.Key);
        using var c = _db.Open();
        return Db.Query(c, "SELECT id, number, period, date, total FROM invoices ORDER BY date DESC, id DESC", r =>
        {
            var key = Db.Str(r, "period");
            InvoicePeriod? period = key is not null && InvoicePeriod.TryParseKey(key, out var p) ? p : null;
            var number = (string)r["number"];
            var result = LotteryChecker.Check(number[2..], period, key is not null ? winning.GetValueOrDefault(key) : null, today);
            return new InvoiceLotteryRow(Db.Long(r, "id"), number, key, Db.ParseDate((string)r["date"]), Db.Dec(r, "total"), result);
        });
    }

    /// <summary>中獎的發票自動記成收入（每張發票只記一次）。</summary>
    public int SyncLotteryIncome()
    {
        var added = 0;
        foreach (var row in LotteryOverview().Where(x => x.Result.Status is LotteryStatus.Won))
        {
            using var c = _db.Open();
            added += Db.Exec(c, """
                INSERT OR IGNORE INTO transactions(date, kind, amount, category, note, source, invoice_id, created_at)
                VALUES ($d, 'income', $a, $c, $n, 'lottery', $i, $now)
                """, ("$d", row.Result.DrawDate!.Value), ("$a", row.Result.PrizeAmount), ("$c", Categories.LotteryIncome),
                ("$n", $"{InvoiceNumber.Format(row.Number)} 中{LotteryChecker.PrizeName(row.Result.Prize)}"),
                ("$i", row.InvoiceId), ("$now", Now()));
        }
        return added;
    }

    // ───────────── 報表 ─────────────

    public MonthReport Report(string month)
    {
        var tx = GetTransactions(month);
        var budgets = GetCategories().ToDictionary(c => c.Name, c => c.Budget);
        var expense = tx.Where(t => t.Kind == "expense").ToList();
        var byCategory = expense.GroupBy(t => t.Category)
            .Select(g => new CategoryTotal(g.Key, g.Sum(t => t.Amount), budgets.GetValueOrDefault(g.Key)))
            .OrderByDescending(x => x.Amount).ToList();
        // 有設預算但這個月還沒花錢的分類也列出來
        foreach (var (name, budget) in budgets)
            if (budget is not null && byCategory.All(b => b.Category != name))
                byCategory.Add(new CategoryTotal(name, 0, budget));

        var alerts = byCategory
            .Where(b => b.Budget is > 0 && b.Amount >= b.Budget * 0.8m)
            .Select(b => b.Amount >= b.Budget
                ? $"{b.Category}已超出預算（{b.Amount:#,0} / {b.Budget:#,0} 元）"
                : $"{b.Category}已用掉 {b.Amount / b.Budget!.Value:P0} 預算（{b.Amount:#,0} / {b.Budget:#,0} 元）")
            .ToList();

        var daily = expense.GroupBy(t => t.Date).OrderBy(g => g.Key).Select(g => (g.Key, g.Sum(t => t.Amount))).ToList();
        return new MonthReport(month, expense.Sum(t => t.Amount), tx.Where(t => t.Kind == "income").Sum(t => t.Amount),
            byCategory, daily, alerts);
    }
}
