using System.Text;
using System.Text.RegularExpressions;

namespace InvoiceEye.Core.Models;

/// <summary>統一發票號碼（字軌 2 碼英文 + 8 碼數字）的檢查與 OCR 校正。</summary>
public static class InvoiceNumber
{
    private static readonly Regex Valid = new("^[A-Z]{2}[0-9]{8}$", RegexOptions.Compiled);

    public static bool IsValid(string? number) => number is not null && Valid.IsMatch(number);

    /// <summary>顯示用格式，例如 "AB-12345678"。</summary>
    public static string Format(string number) => IsValid(number) ? $"{number[..2]}-{number[2..]}" : number;

    // OCR 常見混淆：英文位置把數字改回字母，數字位置把字母改回數字。
    private static readonly Dictionary<char, char> DigitToLetter = new()
    {
        ['0'] = 'O', ['1'] = 'I', ['2'] = 'Z', ['4'] = 'A', ['5'] = 'S', ['6'] = 'G', ['8'] = 'B',
    };

    private static readonly Dictionary<char, char> LetterToDigit = new()
    {
        ['O'] = '0', ['Q'] = '0', ['D'] = '0', ['U'] = '0',
        ['I'] = '1', ['L'] = '1', ['|'] = '1', ['!'] = '1', ['T'] = '1',
        ['Z'] = '2', ['S'] = '5', ['B'] = '8', ['G'] = '6', ['A'] = '4',
    };

    /// <summary>
    /// 校正 OCR 結果：去掉空白和連字號，依「前 2 碼英文、後 8 碼數字」修正常見誤認。
    /// 無法湊成合法號碼時回傳 null。
    /// </summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var compact = new StringBuilder();
        foreach (var c in raw.ToUpperInvariant())
        {
            if (char.IsWhiteSpace(c) || c is '-' or '－' or '_' or '.' or '·') continue;
            compact.Append(c);
        }

        var s = compact.ToString();
        // 在較長的 OCR 字串中，找第一個能校正成合法號碼的 10 字元片段。
        for (var start = 0; start + 10 <= s.Length; start++)
        {
            var candidate = TryFix(s.AsSpan(start, 10));
            if (candidate is not null) return candidate;
        }
        return null;
    }

    private static string? TryFix(ReadOnlySpan<char> s)
    {
        var chars = new char[10];
        for (var i = 0; i < 10; i++)
        {
            var c = s[i];
            if (i < 2)
            {
                if (c is >= 'A' and <= 'Z') chars[i] = c;
                else if (DigitToLetter.TryGetValue(c, out var l)) chars[i] = l;
                else return null;
            }
            else
            {
                if (char.IsAsciiDigit(c)) chars[i] = c;
                else if (LetterToDigit.TryGetValue(c, out var d)) chars[i] = d;
                else return null;
            }
        }
        // 前兩碼必須原本至少有一個是英文字，避免把一串純數字誤當成號碼。
        if (!char.IsAsciiLetter(s[0]) && !char.IsAsciiLetter(s[1])) return null;
        return new string(chars);
    }
}
