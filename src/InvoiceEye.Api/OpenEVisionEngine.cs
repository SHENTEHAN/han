#if OPEN_EVISION
using Euresys.Open_eVision;
using InvoiceEye.Core.Vision;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System.Reflection;
using CoreRect = InvoiceEye.Core.Vision.Rect;

namespace InvoiceEye.Api;

/// <summary>
/// 用 Euresys Open eVision 實作的影像引擎。
///
/// ⚠ 這個檔案是依 Open eVision 官方文件的片段寫成，開發時的環境沒有 Open eVision 可以編譯測試。
///   類別名稱（EImageC24、EImageBW8、EROIBW8、EasyImage、EQRCodeReader、EBarCode、EMatcher、EOCR2、
///   EImageEncoder、ECodedImage2、EObjectSelection、EColorLookup、EasyColor）文件上都有；
///   但部分成員名稱各版本不同，所以那些呼叫用 dynamic 寫，名稱不對時只會在執行時記錄錯誤、
///   改走備用做法，不會讓整個程式編譯失敗。第一次在 Windows 執行時請看主控台的錯誤訊息，
///   依你安裝的 Open eVision 版本調整。
/// </summary>
public sealed class OpenEVisionEngine : IVisionEngine
{
    private readonly ILogger _log;
    private readonly string? _ocrModelPath;
    private readonly Dictionary<OcrField, string> _topology;
    private readonly Dictionary<TemplateKind, string> _templatePaths;
    private readonly Dictionary<TemplateKind, (EMatcher Matcher, int W, int H)> _matchers = new();

    public OpenEVisionEngine(IConfiguration config, ILogger log)
    {
        _log = log;
        var baseDir = AppContext.BaseDirectory;
        string? Resolve(string? p) => string.IsNullOrWhiteSpace(p) ? null : Path.IsPathRooted(p) ? p : Path.Combine(baseDir, p);

        _ocrModelPath = Resolve(config["OcrModel"]);
        _topology = new()
        {
            [OcrField.InvoiceNumber] = config["Topology:InvoiceNumber"] ?? "[LN]{10}",
            [OcrField.Period] = config["Topology:Period"] ?? "[LN]{3,12}",
            [OcrField.Amount] = config["Topology:Amount"] ?? "N{1,7}",
        };
        _templatePaths = new()
        {
            [TemplateKind.TotalLabel] = Resolve(config["Templates:TotalLabel"]) ?? "",
            [TemplateKind.EInvoiceTitle] = Resolve(config["Templates:EInvoiceTitle"]) ?? "",
        };

        // 建一張小影像並做一次轉換，確認 Open eVision 可以使用（沒有授權時會在這裡丟例外）
        var probeColor = new EImageC24(8, 8);
        var probeGray = new EImageBW8(8, 8);
        EasyImage.Convert(probeColor, probeGray);
        DisposeQuietly(probeColor);
        DisposeQuietly(probeGray);

        if (_ocrModelPath is null || !File.Exists(_ocrModelPath))
            _log.LogWarning("找不到 EasyOCR2 模型（Vision:OpenEVision:OcrModel = {Path}），傳統發票的文字辨識會跳過", _ocrModelPath);
    }

    public string Name => "open-evision";

    private sealed class Image : IVisionImage
    {
        public required EImageC24 Color { get; init; }
        public required EImageBW8 Gray { get; init; }
        public required string TempFile { get; init; }
        public int Width => Gray.Width;
        public int Height => Gray.Height;

        public void Dispose()
        {
            DisposeQuietly(Color);
            DisposeQuietly(Gray);
            try { File.Delete(TempFile); } catch { /* 暫存檔刪不掉沒關係 */ }
        }
    }

    // ───────── 載入與前處理（EasyImage）─────────

    public IVisionImage Load(Stream imageStream)
    {
        // 先用 ImageSharp 依 EXIF 轉正、縮到長邊 2400 px 並存成 PNG，再交給 Open eVision 載入
        var temp = Path.Combine(Path.GetTempPath(), $"invoiceeye_{Guid.NewGuid():N}.png");
        using (var img = SixLabors.ImageSharp.Image.Load<Rgb24>(imageStream))
        {
            img.Mutate(x => x.AutoOrient());
            var longSide = Math.Max(img.Width, img.Height);
            if (longSide > 2400)
            {
                var scale = 2400.0 / longSide;
                img.Mutate(x => x.Resize((int)(img.Width * scale), (int)(img.Height * scale)));
            }
            img.SaveAsPng(temp);
        }

        var color = new EImageC24();
        color.Load(temp);
        var gray = new EImageBW8(color.Width, color.Height);
        EasyImage.Convert(color, gray);
        // 熱感紙常偏淡：把灰階拉到全範圍增加對比（失敗就略過）
        TryRun("EasyImage.Normalize", () => CallStatic(typeof(EasyImage), "Normalize", gray, gray));
        return new Image { Color = color, Gray = gray, TempFile = temp };
    }

    // ───────── QR Code（EasyQRCode）─────────

    public IReadOnlyList<byte[]> ReadQrCodes(IVisionImage image)
    {
        var img = (Image)image;
        var result = new List<byte[]>();
        try
        {
            var reader = new EQRCodeReader();
            EQRCode[] codes = reader.Read(img.Gray);
            foreach (var code in codes)
            {
                var bytes = TryGetRawBytes(code);
                if (bytes is not null) result.Add(bytes);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "EasyQRCode 讀取失敗");
        }
        return result;
    }

    private byte[]? TryGetRawBytes(EQRCode code)
    {
        // 優先取原始位元組（Big5 品名才能正確解碼）
        try
        {
            dynamic stream = ((dynamic)code).GetDecodedStream();
            var all = new List<byte>();
            foreach (dynamic part in stream.DecodedStreamParts)
                all.AddRange((byte[])part.DecodedData);
            if (all.Count > 0) return all.ToArray();
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "取得 QR Code 原始位元組失敗，改用 GetDecodedString");
        }
        try
        {
            string s = code.GetDecodedString();
            return System.Text.Encoding.UTF8.GetBytes(s);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "QR Code 解碼失敗");
            return null;
        }
    }

    // ───────── 一維條碼（EasyBarCode）─────────

    public IReadOnlyList<string> ReadBarcodes(IVisionImage image)
    {
        var img = (Image)image;
        try
        {
            // EBarCode 預設啟用的標準條碼種類包含 Code 39
            dynamic reader = new EBarCode();
            string text = reader.Read(img.Gray);
            return string.IsNullOrWhiteSpace(text) ? Array.Empty<string>() : new[] { text.Trim() };
        }
        catch (Exception ex)
        {
            _log.LogInformation("EasyBarCode 沒有讀到條碼：{Message}", ex.Message);
            return Array.Empty<string>();
        }
    }

    // ───────── 紙張外框（EasyObject）─────────

    public CoreRect? FindPaper(IVisionImage image)
    {
        var img = (Image)image;
        try
        {
            var bin = new EImageBW8(img.Width, img.Height);
            CallStatic(typeof(EasyImage), "Threshold", img.Gray, bin); // 不給門檻值時用自動門檻（Minimum Residue）

            var encoder = new EImageEncoder();
            TryRun("設定只編碼白色物件", () =>
            {
                dynamic seg = ((dynamic)encoder).GrayscaleSingleThresholdSegmenter;
                seg.WhiteLayerEncoded = true;
                seg.BlackLayerEncoded = false;
            });
            var coded = new ECodedImage2();
            ((dynamic)encoder).Encode(bin, coded);

            var selection = new EObjectSelection();
            selection.AddObjects(coded);
            var minArea = (uint)(img.Width * img.Height / 20);
            selection.RemoveUsingUnsignedIntegerFeature(EFeature.Area, minArea, ESingleThresholdMode.Less);

            CoreRect? best = null;
            // ElementCount / GetElement 用 uint（Open eVision 26.6）；用 dynamic 傳 int 會在執行時找不到多載
            var count = selection.ElementCount;
            for (uint i = 0; i < count; i++)
            {
                ECodedElement el = selection.GetElement(i);
                float cx = el.BoundingBoxCenterX, cy = el.BoundingBoxCenterY;
                float w = el.BoundingBoxWidth, h = el.BoundingBoxHeight;
                var r = new CoreRect((int)(cx - w / 2), (int)(cy - h / 2), (int)w, (int)h).ClampTo(img.Width, img.Height);
                if (best is null || r.Width * r.Height > best.Value.Width * best.Value.Height) best = r;
            }
            DisposeQuietly(bin);
            return best;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "EasyObject 找紙張外框失敗，改用整張影像");
            return null;
        }
    }

    // ───────── 紅色預印號碼（EasyColor）─────────

    public CoreRect? FindRedInk(IVisionImage image, CoreRect searchArea)
    {
        var img = (Image)image;
        bool[,]? mask = null;
        try
        {
            mask = RedMaskWithEasyColor(img);
        }
        catch (Exception ex)
        {
            _log.LogInformation("EasyColor 取紅色失敗（{Message}），改用像素計算", ex.Message);
        }
        mask ??= RedMaskFromPixels(img.TempFile, img.Width, img.Height);
        return LargestRedTextBand(mask, searchArea);
    }

    /// <summary>
    /// 用 EasyColor 轉到 LSH 色彩空間，取出色相、飽和度、亮度三張灰階圖，
    /// 存成暫存檔後判斷：色相接近紅色、飽和度夠高的像素就是紅色預印字。
    /// </summary>
    private bool[,] RedMaskWithEasyColor(Image img)
    {
        var lut = new EColorLookup();
        lut.ConvertFromRgb(EColorSystem.Lsh);
        var planes = new byte[3][,];
        for (var component = 0; component < 3; component++) // LSH：0=亮度 L、1=飽和度 S、2=色相 H
        {
            var plane = new EImageBW8(img.Width, img.Height);
            EasyColor.GetComponent(img.Color, plane, (uint)component, lut);
            var file = Path.Combine(Path.GetTempPath(), $"invoiceeye_lsh{component}_{Guid.NewGuid():N}.png");
            try
            {
                ((dynamic)plane).Save(file);
                planes[component] = ReadGray(file, img.Width, img.Height);
            }
            finally
            {
                DisposeQuietly(plane);
                try { File.Delete(file); } catch { }
            }
        }

        var mask = new bool[img.Width, img.Height];
        for (var y = 0; y < img.Height; y++)
        for (var x = 0; x < img.Width; x++)
        {
            int l = planes[0][x, y], sat = planes[1][x, y], h = planes[2][x, y];
            // 色相 0～255 對應 0～360 度，紅色在兩端
            mask[x, y] = (h < 14 || h > 241) && sat > 80 && l is > 40 and < 220;
        }
        return mask;
    }

    private static byte[,] ReadGray(string file, int width, int height)
    {
        using var img = SixLabors.ImageSharp.Image.Load<L8>(file);
        var result = new byte[width, height];
        img.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < Math.Min(height, rows.Height); y++)
            {
                var row = rows.GetRowSpan(y);
                for (var x = 0; x < Math.Min(width, row.Length); x++) result[x, y] = row[x].PackedValue;
            }
        });
        return result;
    }

    /// <summary>備用做法：直接讀像素判斷紅色（R 明顯大於 G、B）。</summary>
    private static bool[,] RedMaskFromPixels(string file, int width, int height)
    {
        using var img = SixLabors.ImageSharp.Image.Load<Rgb24>(file);
        var mask = new bool[width, height];
        img.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < Math.Min(height, rows.Height); y++)
            {
                var row = rows.GetRowSpan(y);
                for (var x = 0; x < Math.Min(width, row.Length); x++)
                {
                    var p = row[x];
                    mask[x, y] = p.R > 110 && p.R > p.G * 1.5 && p.R > p.B * 1.4;
                }
            }
        });
        return mask;
    }

    /// <summary>在紅色像素中找出最像一行字的區塊：紅色像素最多的水平帶，再取左右範圍。</summary>
    private static CoreRect? LargestRedTextBand(bool[,] mask, CoreRect area)
    {
        var w = mask.GetLength(0);
        var h = mask.GetLength(1);
        area = area.ClampTo(w, h);
        var rowCounts = new int[area.Height];
        for (var y = 0; y < area.Height; y++)
        for (var x = 0; x < area.Width; x++)
            if (mask[area.X + x, area.Y + y]) rowCounts[y]++;

        var threshold = Math.Max(3, area.Width / 100);
        int bestStart = -1, bestEnd = -1, bestSum = 0, start = -1, sum = 0;
        for (var y = 0; y <= area.Height; y++)
        {
            var on = y < area.Height && rowCounts[y] >= threshold;
            if (on) { if (start < 0) { start = y; sum = 0; } sum += rowCounts[y]; }
            else if (start >= 0)
            {
                if (sum > bestSum && y - start >= 6) { bestSum = sum; bestStart = start; bestEnd = y; }
                start = -1;
            }
        }
        if (bestStart < 0) return null;

        int minX = int.MaxValue, maxX = -1;
        for (var y = bestStart; y < bestEnd; y++)
        for (var x = 0; x < area.Width; x++)
            if (mask[area.X + x, area.Y + y]) { minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); }
        if (maxX < 0) return null;
        return new CoreRect(area.X + minX, area.Y + bestStart, maxX - minX + 1, bestEnd - bestStart);
    }

    // ───────── 範本比對（EasyMatch）─────────

    public CoreRect? FindTemplate(IVisionImage image, TemplateKind kind, CoreRect searchArea)
    {
        var img = (Image)image;
        try
        {
            if (!_matchers.TryGetValue(kind, out var m))
            {
                var path = _templatePaths[kind];
                if (!File.Exists(path))
                {
                    _log.LogWarning("找不到範本圖檔 {Path}（請從發票照片裁下「總計」字樣存成這個檔案）", path);
                    return null;
                }
                var color = new EImageC24();
                color.Load(path);
                var pattern = new EImageBW8(color.Width, color.Height);
                EasyImage.Convert(color, pattern);
                var matcher = new EMatcher();
                ((dynamic)matcher).LearnPattern(pattern);
                TryRun("設定比對角度與縮放", () =>
                {
                    dynamic d = matcher;
                    d.MinAngle = -10f; d.MaxAngle = 10f;
                    d.MinScale = 0.6f; d.MaxScale = 1.6f;
                });
                m = (matcher, color.Width, color.Height);
                _matchers[kind] = m;
                DisposeQuietly(color);
                DisposeQuietly(pattern);
            }

            var area = searchArea.ClampTo(img.Width, img.Height);
            var roi = new EROIBW8();
            roi.Attach(img.Gray);
            roi.SetPlacement(area.X, area.Y, area.Width, area.Height);
            dynamic dm = m.Matcher;
            dm.Match(roi);
            if ((int)dm.NumPositions < 1) return null;

            dynamic pos = dm.GetPosition(0);
            if ((float)pos.Score < 0.6f) return null;
            float scale = 1f;
            TryRun("讀取比對縮放", () => scale = (float)pos.Scale);
            var w = (int)(m.W * scale);
            var h = (int)(m.H * scale);
            // 位置是相對於 ROI 的中心點
            return new CoreRect(area.X + (int)(float)pos.CenterX - w / 2, area.Y + (int)(float)pos.CenterY - h / 2, w, h)
                .ClampTo(img.Width, img.Height);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "EasyMatch 範本比對失敗");
            return null;
        }
    }

    // ───────── 文字辨識（EasyOCR2）─────────

    private EOCR2? _ocr;

    public string? ReadText(IVisionImage image, CoreRect area, OcrField field)
    {
        if (_ocrModelPath is null || !File.Exists(_ocrModelPath)) return null;
        var img = (Image)image;
        try
        {
            if (_ocr is null)
            {
                _ocr = new EOCR2();
                _ocr.Load(_ocrModelPath);
            }
            _ocr.Topology = _topology[field];
            var a = area.ClampTo(img.Width, img.Height);
            var roi = new EROIBW8();
            roi.Attach(img.Gray);
            roi.SetPlacement(a.X, a.Y, a.Width, a.Height);
            string text = _ocr.Read(roi);
            _log.LogInformation("EasyOCR2 {Field}: {Text}", field, text);
            return text;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "EasyOCR2 辨識 {Field} 失敗", field);
            return null;
        }
    }

    // ───────── 共用工具 ─────────

    /// <summary>用反射呼叫靜態方法：成員名稱或參數各版本不同時，錯誤只會在執行時出現。</summary>
    private static object? CallStatic(Type type, string method, params object[] args) =>
        type.InvokeMember(method,
            BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.Static | BindingFlags.OptionalParamBinding,
            Type.DefaultBinder, null, args);

    private static void DisposeQuietly(object? o)
    {
        try { (o as IDisposable)?.Dispose(); } catch { }
    }

    private void TryRun(string what, Action action)
    {
        try { action(); }
        catch (Exception ex) { _log.LogDebug("{What} 略過：{Message}", what, ex.Message); }
    }
}
#endif
