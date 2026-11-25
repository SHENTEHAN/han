using InvoiceEye.Core.Models;

namespace InvoiceEye.Core.Lottery;

/// <summary>某一期的中獎號碼（由使用者依財政部公告輸入）。</summary>
public sealed record WinningNumbers(
    InvoicePeriod Period,
    string Special,
    string Grand,
    IReadOnlyList<string> First,
    IReadOnlyList<string>? AdditionalSixth = null)
{
    /// <summary>檢查號碼格式：特別獎、特獎、頭獎皆為 8 位數字；增開六獎為 3 位數字。</summary>
    public IEnumerable<string> Validate()
    {
        if (!IsDigits(Special, 8)) yield return "特別獎必須是 8 位數字";
        if (!IsDigits(Grand, 8)) yield return "特獎必須是 8 位數字";
        if (First.Count == 0) yield return "至少要有一組頭獎號碼";
        foreach (var f in First)
            if (!IsDigits(f, 8)) yield return $"頭獎號碼 {f} 必須是 8 位數字";
        foreach (var s in AdditionalSixth ?? Array.Empty<string>())
            if (!IsDigits(s, 3)) yield return $"增開六獎號碼 {s} 必須是 3 位數字";
    }

    private static bool IsDigits(string s, int len) => s.Length == len && s.All(char.IsAsciiDigit);
}

public enum Prize
{
    None = 0,
    Special,     // 特別獎 1,000 萬
    Grand,       // 特獎 200 萬
    First,       // 頭獎 20 萬
    Second,      // 二獎 4 萬
    Third,       // 三獎 1 萬
    Fourth,      // 四獎 4 千
    Fifth,       // 五獎 1 千
    Sixth,       // 六獎 200
}

public enum LotteryStatus
{
    /// <summary>號碼讀不到或格式錯誤，無法對獎。</summary>
    InvalidNumber,
    /// <summary>還沒到開獎日。</summary>
    NotDrawn,
    /// <summary>已開獎，但還沒輸入這一期的中獎號碼。</summary>
    WinningNumbersMissing,
    NoPrize,
    /// <summary>中獎，且在領獎期間內（或還沒開始領獎）。</summary>
    Won,
    /// <summary>中獎，但已超過領獎期限。</summary>
    WonExpired,
}

public sealed record LotteryResult(
    LotteryStatus Status,
    Prize Prize,
    decimal PrizeAmount,
    InvoicePeriod? Period,
    DateOnly? DrawDate,
    DateOnly? ClaimStart,
    DateOnly? ClaimEnd)
{
    public string Message => Status switch
    {
        LotteryStatus.InvalidNumber => "發票號碼不完整，無法對獎",
        LotteryStatus.NotDrawn => $"尚未開獎（{DrawDate:yyyy/M/d} 開獎）",
        LotteryStatus.WinningNumbersMissing => "已開獎，請先輸入這一期的中獎號碼",
        LotteryStatus.NoPrize => "沒有中獎",
        LotteryStatus.Won => $"恭喜中「{LotteryChecker.PrizeName(Prize)}」{PrizeAmount:#,0} 元，請於 {ClaimEnd:yyyy/M/d} 前兌領",
        LotteryStatus.WonExpired => $"中「{LotteryChecker.PrizeName(Prize)}」{PrizeAmount:#,0} 元，但已超過兌獎期限（{ClaimEnd:yyyy/M/d}）",
        _ => "",
    };
}

public static class LotteryChecker
{
    public static decimal PrizeAmount(Prize p) => p switch
    {
        Prize.Special => 10_000_000,
        Prize.Grand => 2_000_000,
        Prize.First => 200_000,
        Prize.Second => 40_000,
        Prize.Third => 10_000,
        Prize.Fourth => 4_000,
        Prize.Fifth => 1_000,
        Prize.Sixth => 200,
        _ => 0,
    };

    public static string PrizeName(Prize p) => p switch
    {
        Prize.Special => "特別獎",
        Prize.Grand => "特獎",
        Prize.First => "頭獎",
        Prize.Second => "二獎",
        Prize.Third => "三獎",
        Prize.Fourth => "四獎",
        Prize.Fifth => "五獎",
        Prize.Sixth => "六獎",
        _ => "未中獎",
    };

    /// <summary>只比對號碼，不管日期。digits 為發票號碼的 8 位數字。</summary>
    public static Prize Match(string digits, WinningNumbers w)
    {
        if (digits.Length != 8) return Prize.None;
        if (digits == w.Special) return Prize.Special;
        if (digits == w.Grand) return Prize.Grand;

        var best = Prize.None;
        foreach (var first in w.First)
        {
            var p = MatchFirst(digits, first);
            if (p != Prize.None && (best == Prize.None || p < best)) best = p;
        }
        if (best == Prize.None && w.AdditionalSixth is { Count: > 0 } extra && extra.Contains(digits[^3..]))
            best = Prize.Sixth;
        return best;
    }

    private static Prize MatchFirst(string digits, string first)
    {
        // 頭獎 8 碼、二獎末 7 碼 … 六獎末 3 碼
        for (var len = 8; len >= 3; len--)
        {
            if (digits[^len..] == first[^len..])
            {
                return len switch
                {
                    8 => Prize.First,
                    7 => Prize.Second,
                    6 => Prize.Third,
                    5 => Prize.Fourth,
                    4 => Prize.Fifth,
                    _ => Prize.Sixth,
                };
            }
        }
        return Prize.None;
    }

    /// <summary>完整對獎：考慮開獎日、是否有中獎號碼、兌獎期限。</summary>
    public static LotteryResult Check(InvoiceData invoice, WinningNumbers? numbers, DateOnly today) =>
        Check(invoice.Digits, invoice.Period, numbers, today);

    public static LotteryResult Check(string? digits, InvoicePeriod? period, WinningNumbers? numbers, DateOnly today)
    {
        if (digits is null || digits.Length != 8 || period is null || !period.Value.IsValid)
            return new LotteryResult(LotteryStatus.InvalidNumber, Prize.None, 0, period, null, null, null);

        var p = period.Value;
        LotteryResult Make(LotteryStatus s, Prize prize = Prize.None) =>
            new(s, prize, PrizeAmount(prize), p, p.DrawDate, p.ClaimStart, p.ClaimEnd);

        if (today < p.DrawDate) return Make(LotteryStatus.NotDrawn);
        if (numbers is null) return Make(LotteryStatus.WinningNumbersMissing);

        var prize = Match(digits, numbers);
        if (prize == Prize.None) return Make(LotteryStatus.NoPrize);
        return Make(today > p.ClaimEnd ? LotteryStatus.WonExpired : LotteryStatus.Won, prize);
    }
}
