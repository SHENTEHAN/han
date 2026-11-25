using System.Globalization;
using System.Text.RegularExpressions;

namespace InvoiceEye.Core.Models;

/// <summary>
/// 統一發票期別：民國年 + 期別末月（雙數月），例如 115 年 11-12 月 = "11512"。
/// 電子發票一維條碼的前 5 碼就是這個格式。
/// </summary>
public readonly record struct InvoicePeriod(int RocYear, int EndMonth) : IComparable<InvoicePeriod>
{
    public int StartMonth => EndMonth - 1;
    public int GregorianYear => RocYear + 1911;

    /// <summary>例如 "11512"。</summary>
    public string Key => $"{RocYear:000}{EndMonth:00}";

    /// <summary>例如 "115年11-12月"。</summary>
    public string Display => $"{RocYear}年{StartMonth:00}-{EndMonth:00}月";

    public bool IsValid => RocYear is >= 1 and <= 999 && EndMonth is >= 2 and <= 12 && EndMonth % 2 == 0;

    public static InvoicePeriod FromDate(DateOnly date)
    {
        var endMonth = date.Month % 2 == 0 ? date.Month : date.Month + 1;
        return new InvoicePeriod(date.Year - 1911, endMonth);
    }

    /// <summary>解析 "11512" 這種 5 碼期別；末月必須是雙數。</summary>
    public static bool TryParseKey(string? key, out InvoicePeriod period)
    {
        period = default;
        if (key is null || key.Length != 5 || !key.All(char.IsAsciiDigit)) return false;
        var p = new InvoicePeriod(int.Parse(key[..3], CultureInfo.InvariantCulture), int.Parse(key[3..], CultureInfo.InvariantCulture));
        if (!p.IsValid) return false;
        period = p;
        return true;
    }

    private static readonly Regex PrintedPattern = new(@"(\d{2,3})\D{0,3}?(\d{1,2})\s*[-~－–]\s*(\d{1,2})", RegexOptions.Compiled);

    /// <summary>
    /// 從 OCR 讀到的期別文字解析，例如 "115年11-12月"、"115 11-12"。
    /// OCR 讀不到中文時，數字部分仍可解析。
    /// </summary>
    public static bool TryParsePrinted(string? text, out InvoicePeriod period)
    {
        period = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (Match m in PrintedPattern.Matches(text))
        {
            var year = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var start = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            var end = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            var p = new InvoicePeriod(year, end);
            if (p.IsValid && start == end - 1)
            {
                period = p;
                return true;
            }
        }
        return false;
    }

    /// <summary>開獎日：期別末月的下個月 25 日（單數月）。</summary>
    public DateOnly DrawDate => new DateOnly(GregorianYear, EndMonth, 1).AddMonths(1).AddDays(24);

    /// <summary>領獎期間：開獎後次月 6 日起，共 3 個月。</summary>
    public DateOnly ClaimStart => new DateOnly(DrawDate.Year, DrawDate.Month, 6).AddMonths(1);

    public DateOnly ClaimEnd => ClaimStart.AddMonths(3).AddDays(-1);

    public int CompareTo(InvoicePeriod other) => string.CompareOrdinal(Key, other.Key);

    public override string ToString() => Key;
}
