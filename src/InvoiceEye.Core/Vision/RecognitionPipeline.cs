using System.Globalization;
using System.Text.RegularExpressions;
using InvoiceEye.Core.Models;
using InvoiceEye.Core.Parsing;

namespace InvoiceEye.Core.Vision;

/// <summary>
/// 發票辨識流程：
/// 1. 讀 QR Code → 電子發票（號碼、日期、金額、品項）
/// 2. 讀不到改讀一維條碼 → 電子發票（期別、號碼）
/// 3. 都沒有 → 依紙張長寬比判斷收銀機發票或手開發票，
///    用紅色定位號碼再 OCR；收銀機發票找「總計」讀金額，手開發票金額由使用者輸入
/// </summary>
public sealed class RecognitionPipeline
{
    /// <summary>紙張長寬比大於這個值就當作細長的收銀機發票。</summary>
    public const double CashRegisterMinAspect = 2.2;

    private readonly IVisionEngine _engine;

    public RecognitionPipeline(IVisionEngine engine) => _engine = engine;

    public string EngineName => _engine.Name;

    public InvoiceData Recognize(Stream imageStream)
    {
        using var image = _engine.Load(imageStream);
        return Recognize(image);
    }

    public InvoiceData Recognize(IVisionImage image)
    {
        var qrRaw = _engine.ReadQrCodes(image);
        var qrStrings = qrRaw.Select(b => EInvoiceQrParser.DecodeBytes(b)).ToList();
        // 右 QR Code 本身沒有編碼參數，要用左 QR Code 的參數重新解碼
        var fromQr = EInvoiceQrParser.Parse(ReDecodeWithLeftEncoding(qrRaw, qrStrings));

        // 條碼一律也讀：有 QR Code 時用來交叉驗證，沒有時當備援
        var fromBarcode = EInvoiceBarcodeParser.Parse(_engine.ReadBarcodes(image));

        if (fromQr is not null)
        {
            if (fromBarcode is not null)
            {
                if (fromBarcode.Number != fromQr.Number)
                    fromQr.Warnings.Add($"QR Code 號碼（{fromQr.Number}）和條碼號碼（{fromBarcode.Number}）不一致，請確認");
                if (fromBarcode.Period is { } bp && fromQr.Period is { } qp && bp != qp)
                    fromQr.Warnings.Add($"QR Code 期別（{qp.Display}）和條碼期別（{bp.Display}）不一致，請確認");
            }
            return fromQr;
        }

        if (fromBarcode is not null)
        {
            fromBarcode.Warnings.Add("QR Code 讀不到，改用條碼；金額請手動確認");
            return fromBarcode;
        }

        return RecognizeTraditional(image);
    }

    private static List<string> ReDecodeWithLeftEncoding(IReadOnlyList<byte[]> raw, List<string> decoded)
    {
        var leftIndex = decoded.FindIndex(EInvoiceQrParser.LooksLikeLeft);
        if (leftIndex < 0) return decoded;
        var left = decoded[leftIndex];
        var parts = left.Length > EInvoiceQrParser.HeaderLength ? left[EInvoiceQrParser.HeaderLength..].Split(':') : Array.Empty<string>();
        var offset = parts.Length > 0 && parts[0].Length == 0 ? 1 : 0;
        if (parts.Length <= offset + 3 || !int.TryParse(parts[offset + 3], out var enc)) return decoded;
        return raw.Select(b => EInvoiceQrParser.DecodeBytes(b, enc)).ToList();
    }

    private InvoiceData RecognizeTraditional(IVisionImage image)
    {
        var full = new Rect(0, 0, image.Width, image.Height);
        var paper = _engine.FindPaper(image) ?? full;
        var type = paper.AspectRatio >= CashRegisterMinAspect ? InvoiceType.CashRegister : InvoiceType.Handwritten;
        var data = new InvoiceData { Type = type };

        // 發票號碼：紅色預印字
        var red = _engine.FindRedInk(image, paper);
        if (red is { } redRect)
        {
            var area = Inflate(redRect, 0.15, image);
            var raw = _engine.ReadText(image, area, OcrField.InvoiceNumber);
            var normalized = InvoiceNumber.Normalize(raw);
            if (normalized is not null)
            {
                data.Number = normalized;
                data.NumberSource = DataSource.Ocr;
                if (!string.Equals(raw?.Replace(" ", "").Replace("-", ""), normalized, StringComparison.Ordinal))
                    data.Warnings.Add($"號碼經過 OCR 校正（原始：{raw}），請確認");
            }
            else
            {
                data.Warnings.Add("找到紅色號碼區域，但辨識不出完整號碼，請手動輸入");
            }
        }
        else
        {
            data.Warnings.Add("找不到紅色的發票號碼，請手動輸入");
        }

        // 期別：在紙張上方約 1/3 的範圍找
        var header = new Rect(paper.X, paper.Y, paper.Width, Math.Max(1, paper.Height / 3)).ClampTo(image.Width, image.Height);
        var periodText = _engine.ReadText(image, header, OcrField.Period);
        if (InvoicePeriod.TryParsePrinted(periodText, out var period)) data.Period = period;
        else data.Warnings.Add("讀不到期別，請手動選擇");

        // 金額
        if (type == InvoiceType.CashRegister)
        {
            var label = _engine.FindTemplate(image, TemplateKind.TotalLabel, paper);
            if (label is { } l)
            {
                // 「總計」右邊同一行，一直到紙張右緣
                var amountArea = new Rect(l.Right, l.Y - l.Height / 4, Math.Max(1, paper.Right - l.Right), l.Height + l.Height / 2)
                    .ClampTo(image.Width, image.Height);
                var amount = ParseAmount(_engine.ReadText(image, amountArea, OcrField.Amount));
                if (amount is not null)
                {
                    data.TotalAmount = amount;
                    data.AmountSource = DataSource.Ocr;
                }
                else data.Warnings.Add("找到「總計」，但讀不出金額，請手動輸入");
            }
            else data.Warnings.Add("找不到「總計」字樣，請手動輸入金額");
        }
        else
        {
            data.Warnings.Add("手開發票的金額是手寫的，請手動輸入");
        }

        return data;
    }

    private static readonly Regex AmountPattern = new(@"\d[\d,]*", RegexOptions.Compiled);

    /// <summary>從 OCR 字串取出最大的數字當金額，例如 "$1,235元" → 1235。</summary>
    public static decimal? ParseAmount(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        decimal? best = null;
        foreach (Match m in AmountPattern.Matches(text.Replace('O', '0').Replace('o', '0')))
        {
            if (decimal.TryParse(m.Value.Replace(",", ""), NumberStyles.None, CultureInfo.InvariantCulture, out var v) &&
                (best is null || v > best)) best = v;
        }
        return best;
    }

    private static Rect Inflate(Rect r, double ratio, IVisionImage img)
    {
        var dx = (int)(r.Width * ratio);
        var dy = (int)(r.Height * ratio);
        return new Rect(r.X - dx, r.Y - dy, r.Width + 2 * dx, r.Height + 2 * dy).ClampTo(img.Width, img.Height);
    }
}
