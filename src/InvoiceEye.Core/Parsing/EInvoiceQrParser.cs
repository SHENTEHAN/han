using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using InvoiceEye.Core.Models;

namespace InvoiceEye.Core.Parsing;

/// <summary>
/// 解析電子發票證明聯的左右兩個 QR Code（財政部「電子發票證明聯一維及二維條碼規格」）。
///
/// 左 QR Code 前 77 碼固定：
///   發票字軌(10) 開立日期(7, 民國年月日) 隨機碼(4) 銷售額(8, 16進位) 總計額(8, 16進位)
///   買方統編(8) 賣方統編(8) 加密驗證資訊(24)
/// 第 77 碼之後以 ':' 分隔：營業人自行使用區(10) : 二維條碼記載完整品目筆數 : 交易品目總筆數 : 中文編碼參數 : 品名:數量:單價:...
/// 中文編碼參數：0 = Big5、1 = UTF-8、2 = Base64。
/// 右 QR Code 以 "**" 開頭，接續品名:數量:單價。
/// </summary>
public static class EInvoiceQrParser
{
    public const int HeaderLength = 77;

    private static readonly Regex LeftHeader = new(@"^[A-Z]{2}\d{8}\d{7}[0-9A-Za-z]{4}[0-9A-Fa-f]{16}\d{16}", RegexOptions.Compiled);

    static EInvoiceQrParser()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static bool LooksLikeLeft(string? s) => s is not null && s.Length >= HeaderLength - 24 && LeftHeader.IsMatch(s);

    public static bool LooksLikeRight(string? s) => s is not null && s.StartsWith("**", StringComparison.Ordinal);

    /// <summary>
    /// 先用 ASCII 讀出前段（都是英數字），找出中文編碼參數，再用正確編碼把品名解碼。
    /// QR 讀取器若能提供原始位元組，請用這個方法，Big5 品名才不會變亂碼。
    /// </summary>
    public static string DecodeBytes(byte[] raw, int? encodingHint = null)
    {
        var latin = Encoding.Latin1.GetString(raw);
        var encoding = encodingHint ?? DetectEncodingParam(latin);
        return encoding switch
        {
            0 => Encoding.GetEncoding(950).GetString(raw), // Big5
            _ => Encoding.UTF8.GetString(raw),
        };
    }

    private static int? DetectEncodingParam(string latinLeft)
    {
        if (!LooksLikeLeft(latinLeft) || latinLeft.Length <= HeaderLength) return null;
        var parts = latinLeft[HeaderLength..].Split(':');
        // parts[0] 是第 77 碼前的空字串（若以 ':' 開頭），之後依序是自用區、筆數、總筆數、編碼參數
        var offset = parts.Length > 0 && parts[0].Length == 0 ? 1 : 0;
        return parts.Length > offset + 3 && int.TryParse(parts[offset + 3], out var e) ? e : null;
    }

    /// <summary>從一組 QR Code 內容中找出左右兩個並解析；沒有左 QR Code 時回傳 null。</summary>
    public static InvoiceData? Parse(IEnumerable<string> qrContents)
    {
        var list = qrContents.Where(s => !string.IsNullOrEmpty(s)).ToList();
        var left = list.FirstOrDefault(LooksLikeLeft);
        if (left is null) return null;
        var right = list.FirstOrDefault(LooksLikeRight);
        return Parse(left, right);
    }

    public static InvoiceData? Parse(string left, string? right)
    {
        if (!LooksLikeLeft(left)) return null;
        var data = new InvoiceData
        {
            Type = InvoiceType.EInvoice,
            Number = left[..10],
            NumberSource = DataSource.QrCode,
            RandomCode = left.Substring(17, 4),
            BuyerTaxId = NullIfZeros(left.Substring(37, 8)),
            SellerTaxId = NullIfZeros(left.Substring(45, 8)),
        };

        var rocDate = left.Substring(10, 7);
        if (TryParseRocDate(rocDate, out var date))
        {
            data.Date = date;
            data.Period = InvoicePeriod.FromDate(date);
        }
        else
        {
            data.Warnings.Add($"QR Code 的開立日期無法解析：{rocDate}");
        }

        data.SalesAmount = ParseHexAmount(left.Substring(21, 8));
        data.TotalAmount = ParseHexAmount(left.Substring(29, 8));
        data.AmountSource = data.TotalAmount is null ? DataSource.None : DataSource.QrCode;

        if (left.Length > HeaderLength)
        {
            ParseItems(left[HeaderLength..], right, data);
        }

        return data;
    }

    private static void ParseItems(string leftRest, string? right, InvoiceData data)
    {
        var parts = leftRest.Split(':').ToList();
        if (parts.Count > 0 && parts[0].Length == 0) parts.RemoveAt(0);
        // parts: [自用區, 本條碼品目筆數, 總品目筆數, 編碼參數, 品名, 數量, 單價, ...]
        if (parts.Count < 4) return;
        var encoding = int.TryParse(parts[3], out var e) ? e : 1;
        var itemFields = parts.Skip(4).ToList();
        // 左 QR Code 結尾可能多一個 ':'（品項接續到右 QR Code），去掉尾端的空欄位
        while (itemFields.Count > 0 && itemFields[^1].Length == 0) itemFields.RemoveAt(itemFields.Count - 1);

        if (!string.IsNullOrEmpty(right))
        {
            var rightRest = right[2..].TrimStart(':');
            if (rightRest.Length > 0)
            {
                if (encoding == 2)
                {
                    // Base64 時，左右兩段合起來才是完整的 Base64 字串
                    var joined = string.Join(':', itemFields) + rightRest;
                    itemFields = joined.Length > 0 ? new List<string> { joined } : new List<string>();
                }
                else
                {
                    itemFields.AddRange(rightRest.Split(':'));
                }
            }
        }

        if (encoding == 2 && itemFields.Count > 0)
        {
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(string.Concat(itemFields)));
                itemFields = decoded.Split(':').ToList();
            }
            catch (FormatException)
            {
                data.Warnings.Add("品項明細是 Base64 編碼，但無法解碼");
                return;
            }
        }

        for (var i = 0; i + 2 < itemFields.Count; i += 3)
        {
            var name = itemFields[i].Trim();
            if (name.Length == 0) continue;
            if (!decimal.TryParse(itemFields[i + 1], NumberStyles.Number, CultureInfo.InvariantCulture, out var qty)) qty = 1;
            if (!decimal.TryParse(itemFields[i + 2], NumberStyles.Number, CultureInfo.InvariantCulture, out var price)) continue;
            data.Items.Add(new InvoiceItem(name, qty, price));
        }
    }

    public static bool TryParseRocDate(string s, out DateOnly date)
    {
        date = default;
        if (s.Length != 7 || !s.All(char.IsAsciiDigit)) return false;
        var y = int.Parse(s[..3], CultureInfo.InvariantCulture) + 1911;
        var m = int.Parse(s.Substring(3, 2), CultureInfo.InvariantCulture);
        var d = int.Parse(s.Substring(5, 2), CultureInfo.InvariantCulture);
        if (m is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(y, m)) return false;
        date = new DateOnly(y, m, d);
        return true;
    }

    private static decimal? ParseHexAmount(string hex) =>
        long.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static string? NullIfZeros(string s) => s.All(c => c == '0') ? null : s;
}
