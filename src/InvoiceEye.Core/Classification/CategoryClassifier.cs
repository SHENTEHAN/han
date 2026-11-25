using InvoiceEye.Core.Models;

namespace InvoiceEye.Core.Classification;

public static class Categories
{
    public const string Food = "餐飲";
    public const string Transport = "交通";
    public const string Housing = "居住";
    public const string Daily = "日用品";
    public const string Entertainment = "娛樂";
    public const string Medical = "醫療";
    public const string Education = "教育";
    public const string Other = "其他";
    public const string LotteryIncome = "中獎";

    public static readonly IReadOnlyList<string> DefaultExpense =
        new[] { Food, Transport, Housing, Daily, Entertainment, Medical, Education, Other };
}

/// <summary>分類是依哪一條規則判斷出來的。</summary>
public enum ClassificationSource
{
    None = 0,
    UserItemRule = 1,
    UserStoreRule = 2,
    Keyword = 3,
    Industry = 4,
}

public sealed record ClassifiedLine(string Name, decimal Amount, string? Category, ClassificationSource Source);

public sealed record ClassificationResult(IReadOnlyList<ClassifiedLine> Lines)
{
    /// <summary>有任何一行判斷不出來，需要跳出選單讓使用者選。</summary>
    public bool NeedsUserChoice => Lines.Any(l => l.Category is null);

    /// <summary>依分類加總（混合發票會拆成多筆）。</summary>
    public IReadOnlyDictionary<string, decimal> TotalsByCategory =>
        Lines.Where(l => l.Category is not null)
             .GroupBy(l => l.Category!)
             .ToDictionary(g => g.Key, g => g.Sum(l => l.Amount));
}

/// <summary>使用者記住的分類（之前選過的店家或品項）。</summary>
public interface IUserRuleStore
{
    string? CategoryForStore(string sellerTaxId);
    string? CategoryForItem(string itemName);
}

/// <summary>用賣方統編查行業別（財政部營業稅籍登記開放資料）。</summary>
public interface IBusinessRegistry
{
    string? IndustryOf(string sellerTaxId);
}

/// <summary>
/// 依序套用規則：
/// 1. 使用者之前選過的（先看品項、再看店家）
/// 2. 品名關鍵字
/// 3. 店家行業別
/// 4. 都判斷不出來 → Category = null，由使用者從選單選或自行輸入
/// </summary>
public sealed class CategoryClassifier
{
    private readonly IUserRuleStore _rules;
    private readonly IBusinessRegistry? _registry;

    public CategoryClassifier(IUserRuleStore rules, IBusinessRegistry? registry = null)
    {
        _rules = rules;
        _registry = registry;
    }

    public ClassificationResult Classify(InvoiceData invoice)
    {
        var storeCategory = invoice.SellerTaxId is { } id ? _rules.CategoryForStore(id) : null;
        string? industryCategory = null;
        if (invoice.SellerTaxId is { } tax && _registry?.IndustryOf(tax) is { } industry)
            industryCategory = KeywordDictionary.CategoryForIndustry(industry);

        var lines = new List<ClassifiedLine>();
        if (invoice.Items.Count == 0)
        {
            // 傳統發票通常讀不到品名，只能依店家判斷
            var (cat, src) = storeCategory is not null ? (storeCategory, ClassificationSource.UserStoreRule)
                : industryCategory is not null ? (industryCategory, ClassificationSource.Industry)
                : ((string?)null, ClassificationSource.None);
            lines.Add(new ClassifiedLine("整張發票", invoice.TotalAmount ?? 0, cat, src));
            return new ClassificationResult(lines);
        }

        foreach (var item in invoice.Items)
        {
            string? cat;
            ClassificationSource src;
            if ((cat = _rules.CategoryForItem(item.Name)) is not null) src = ClassificationSource.UserItemRule;
            else if ((cat = storeCategory) is not null) src = ClassificationSource.UserStoreRule;
            else if ((cat = KeywordDictionary.CategoryForItem(item.Name)) is not null) src = ClassificationSource.Keyword;
            else if ((cat = industryCategory) is not null) src = ClassificationSource.Industry;
            else src = ClassificationSource.None;
            lines.Add(new ClassifiedLine(item.Name, item.Amount, cat, src));
        }

        // 品項加總和總金額不同（折扣、運費）時，差額併入金額最大的一行
        if (invoice.TotalAmount is { } total)
        {
            var diff = total - lines.Sum(l => l.Amount);
            if (diff != 0 && lines.Count > 0)
            {
                var idx = lines.IndexOf(lines.MaxBy(l => l.Amount)!);
                lines[idx] = lines[idx] with { Amount = lines[idx].Amount + diff };
            }
        }
        return new ClassificationResult(lines);
    }
}
