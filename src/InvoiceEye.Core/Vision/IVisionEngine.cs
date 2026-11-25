namespace InvoiceEye.Core.Vision;

public readonly record struct Rect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public double AspectRatio => Width == 0 || Height == 0 ? 0 : (double)Math.Max(Width, Height) / Math.Min(Width, Height);

    public Rect ClampTo(int width, int height)
    {
        var x = Math.Clamp(X, 0, Math.Max(0, width - 1));
        var y = Math.Clamp(Y, 0, Math.Max(0, height - 1));
        var w = Math.Clamp(Width, 1, width - x);
        var h = Math.Clamp(Height, 1, height - y);
        return new Rect(x, y, w, h);
    }
}

/// <summary>要辨識的文字種類，影像引擎依此選擇 OCR 的字元規則（Topology）。</summary>
public enum OcrField
{
    /// <summary>發票號碼：2 個英文字 + 8 位數字。</summary>
    InvoiceNumber,
    /// <summary>期別，例如 115 11-12（中文字讀不到沒關係）。</summary>
    Period,
    /// <summary>金額：數字，可能有逗號。</summary>
    Amount,
}

/// <summary>範本比對要找的圖案。</summary>
public enum TemplateKind
{
    /// <summary>收銀機發票上的「總計」字樣。</summary>
    TotalLabel,
    /// <summary>電子發票證明聯的標題「電子發票證明聯」。</summary>
    EInvoiceTitle,
}

/// <summary>已載入、前處理（轉正）過的影像。由影像引擎建立與釋放。</summary>
public interface IVisionImage : IDisposable
{
    int Width { get; }
    int Height { get; }
}

/// <summary>
/// 影像辨識的基本功能。正式版由 Open eVision 實作（OpenEVisionEngine），
/// 沒有 Open eVision 時用 MockVisionEngine 示範。
/// 判斷流程（先 QR、再條碼、再 OCR、種類判斷、交叉驗證）寫在 RecognitionPipeline，
/// 不放在這裡，所以可以在沒有 Open eVision 的電腦上測試。
/// </summary>
public interface IVisionEngine
{
    string Name { get; }

    /// <summary>載入照片並做前處理：轉灰階、增強對比、找紙張邊緣轉正（EasyImage、EasyGauge）。</summary>
    IVisionImage Load(Stream imageStream);

    /// <summary>讀取所有 QR Code，回傳原始位元組（EasyQRCode）。</summary>
    IReadOnlyList<byte[]> ReadQrCodes(IVisionImage image);

    /// <summary>讀取一維條碼（Code 39）（EasyBarCode）。</summary>
    IReadOnlyList<string> ReadBarcodes(IVisionImage image);

    /// <summary>找出紙張（最大的亮色物件）的外框（EasyObject）。</summary>
    Rect? FindPaper(IVisionImage image);

    /// <summary>找出紅色預印字（發票號碼）的範圍（EasyColor）。</summary>
    Rect? FindRedInk(IVisionImage image, Rect searchArea);

    /// <summary>在指定範圍內找範本圖案（EasyMatch）。</summary>
    Rect? FindTemplate(IVisionImage image, TemplateKind kind, Rect searchArea);

    /// <summary>辨識指定範圍內的文字（EasyOCR2）。</summary>
    string? ReadText(IVisionImage image, Rect area, OcrField field);
}
