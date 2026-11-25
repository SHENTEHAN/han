#if OPEN_EVISION
using Euresys.Open_eVision;
using InvoiceEye.Core.Vision;
using SkiaSharp;
using CoreRect = InvoiceEye.Core.Vision.Rect;
using EasyBarCode2 = Euresys.Open_eVision.EasyBarCode2;

namespace InvoiceEye.Api;

/// <summary>
/// 用 Euresys Open eVision 實作的影像引擎。
///
/// 所有 Open eVision 呼叫都用實際型別寫成，並已對照 Open eVision 26.6 的 Open_eVision_NetApi.dll 檢查過。
/// 換成其他版本時如果成員名稱或參數不同，會在建置時就出現編譯錯誤，而不是執行時才默默失敗。
/// </summary>
public sealed class OpenEVisionEngine : IVisionEngine
{
    private readonly ILogger _log;
    private readonly string? _ocrModelPath;
    private readonly Dictionary<OcrField, string> _topology;
    private readonly Dictionary<TemplateKind, string> _templatePaths;
    private readonly Dictionary<TemplateKind, (EMatcher Matcher, int W, int H)> _matchers = new();
    private readonly HashSet<TemplateKind> _missingTemplatesLogged = new();

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
        {
            // 預設位置沒有模型是 vision/README.md 說明的正常狀態：傳統發票改由使用者輸入。
            // 只有特別指定了別的路徑卻找不到檔案，才是設定錯誤。
            var configured = config["OcrModel"];
            var isDefaultLocation = string.IsNullOrWhiteSpace(configured)
                || string.Equals(configured.Replace('\\', '/'), DefaultOcrModel, StringComparison.OrdinalIgnoreCase);
            if (isDefaultLocation)
                _log.LogInformation("沒有 EasyOCR2 模型（{Path}），傳統發票的號碼與金額改由使用者輸入；放入模型檔後重新啟動即可啟用文字辨識", _ocrModelPath);
            else
                _log.LogWarning("找不到設定的 EasyOCR2 模型（Vision:OpenEVision:OcrModel = {Path}），傳統發票的文字辨識會跳過", _ocrModelPath);
        }
    }

    private const string DefaultOcrModel = "vision/invoice.o2m";

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
        // 先用 SkiaSharp 依 EXIF 轉正、縮到長邊 2400 px 並存成 PNG，再交給 Open eVision 載入
        var temp = Path.Combine(Path.GetTempPath(), $"invoiceeye_{Guid.NewGuid():N}.png");
        using (var managed = new SKManagedStream(imageStream))
        using (var codec = SKCodec.Create(managed) ?? throw new InvalidDataException("無法解讀這張照片的格式"))
        {
            var bmp = SKBitmap.Decode(codec) ?? throw new InvalidDataException("無法解碼這張照片");
            try
            {
                bmp = ReplaceWith(bmp, AutoOrient(bmp, codec.EncodedOrigin));
                var longSide = Math.Max(bmp.Width, bmp.Height);
                if (longSide > 2400)
                {
                    var scale = 2400.0 / longSide;
                    var size = new SKImageInfo((int)(bmp.Width * scale), (int)(bmp.Height * scale));
                    bmp = ReplaceWith(bmp, bmp.Resize(size, new SKSamplingOptions(SKCubicResampler.Mitchell))
                        ?? throw new InvalidOperationException("縮小照片失敗"));
                }
                using var png = bmp.Encode(SKEncodedImageFormat.Png, 100);
                using var file = File.Create(temp);
                png.SaveTo(file);
            }
            finally
            {
                bmp.Dispose();
            }
        }

        var color = new EImageC24();
        color.Load(temp);
        var gray = new EImageBW8(color.Width, color.Height);
        EasyImage.Convert(color, gray);
        // 熱感紙常偏淡：把灰階拉到全範圍增加對比（失敗就略過）
        TryRun("拉高灰階對比", () => StretchContrast(gray));
        return new Image { Color = color, Gray = gray, TempFile = temp };
    }

    /// <summary>把灰階的最暗～最亮線性拉到 0～255（已經接近全範圍時不動）。</summary>
    private static void StretchContrast(EImageBW8 gray)
    {
        EasyImage.PixelMin(gray, out EBW8 min);
        EasyImage.PixelMax(gray, out EBW8 max);
        int lo = min.Value, hi = max.Value;
        if (hi - lo < 8 || hi - lo >= 240) return; // 幾乎全白／全黑，或已經夠清楚
        var gain = 255f / (hi - lo);
        EasyImage.GainOffset(gray, gray, gain, -lo * gain);
    }

    /// <summary>換成新的點陣圖並釋放舊的（兩者相同時不釋放）。</summary>
    private static SKBitmap ReplaceWith(SKBitmap old, SKBitmap replacement)
    {
        if (!ReferenceEquals(old, replacement)) old.Dispose();
        return replacement;
    }

    /// <summary>依 EXIF 方向把照片轉正；已經是正的就回傳原本的點陣圖。</summary>
    private static SKBitmap AutoOrient(SKBitmap src, SKEncodedOrigin origin)
    {
        if (origin == SKEncodedOrigin.TopLeft) return src;
        float w = src.Width, h = src.Height;
        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        // 每種方向對應的轉換：x' = ScaleX·x + SkewX·y + TransX，y' = SkewY·x + ScaleY·y + TransY
        var m = origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),     // 左右翻轉
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1), // 轉 180°
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),   // 上下翻轉
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),       // 沿主對角線翻轉
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),     // 順時針 90°
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1), // 沿副對角線翻轉
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),   // 逆時針 90°
            _ => SKMatrix.Identity,
        };
        var dst = new SKBitmap(new SKImageInfo(swap ? src.Height : src.Width, swap ? src.Width : src.Height, src.ColorType, src.AlphaType));
        using var canvas = new SKCanvas(dst);
        canvas.SetMatrix(m);
        using var image = SKImage.FromBitmap(src);
        // 方向轉換都是整數像素對應，用最近鄰取樣就不會糊掉
        canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest));
        return dst;
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
            var all = new List<byte>();
            foreach (var part in code.DecodedStream.DecodedStreamParts)
                all.AddRange(part.DecodedData);
            if (all.Count > 0) return all.ToArray();
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "取得 QR Code 原始位元組失敗，改用 GetDecodedString");
        }
        try
        {
            // 不指定模式的 GetDecodedString() 遇到位元組模式（Big5）會丟例外，所以指定 Auto
            string s = code.GetDecodedString(EByteInterpretationMode.Auto);
            return System.Text.Encoding.UTF8.GetBytes(s);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "QR Code 解碼失敗");
            return null;
        }
    }

    // ───────── 一維條碼（EasyBarCode2）─────────

    // 同一個 Open eVision 物件不能給多個執行緒同時用，所以共用的讀取器都用鎖保護（多張照片同時上傳時）
    private readonly object _barcodeLock = new();
    private EasyBarCode2.EBarCodeReader? _barcodeReader;

    public IReadOnlyList<string> ReadBarcodes(IVisionImage image)
    {
        var img = (Image)image;
        try
        {
            lock (_barcodeLock)
            {
                if (_barcodeReader is null)
                {
                    // 電子發票證明聯的一維條碼是 Code 39，只啟用這一種，讀得比較快也比較不會誤判
                    var reader = new EasyBarCode2.EBarCodeReader();
                    reader.DisableAllSymbologies();
                    reader.EnableSymbology(EasyBarCode2.EBarCodeSymbologies.Code39);
                    _barcodeReader = reader;
                }
                return _barcodeReader.Read(img.Gray)
                    .Select(code => code.GetDecodedString().Trim())
                    .Where(text => text.Length > 0)
                    .ToArray();
            }
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
            EasyImage.Threshold(img.Gray, bin); // 不給門檻值時用自動門檻（Minimum Residue）

            var encoder = new EImageEncoder();
            var seg = encoder.GrayscaleSingleThresholdSegmenter; // 只編碼白色物件（紙張）
            seg.WhiteLayerEncoded = true;
            seg.BlackLayerEncoded = false;
            var coded = new ECodedImage2();
            encoder.Encode(bin, coded);

            var selection = new EObjectSelection();
            selection.AddObjects(coded);
            var minArea = (uint)(img.Width * img.Height / 20);
            selection.RemoveUsingUnsignedIntegerFeature(EFeature.Area, minArea, ESingleThresholdMode.Less);

            CoreRect? best = null;
            // ElementCount / GetElement 用 uint
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
                plane.Save(file);
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

    /// <summary>把影像檔解碼成指定的像素格式（灰階 Gray8 或 RGBA8888）。</summary>
    private static SKBitmap DecodeAs(string file, SKColorType colorType)
    {
        using var codec = SKCodec.Create(file) ?? throw new InvalidDataException($"無法讀取影像 {file}");
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, colorType, SKAlphaType.Opaque);
        return SKBitmap.Decode(codec, info) ?? throw new InvalidDataException($"無法解碼影像 {file}");
    }

    private static byte[,] ReadGray(string file, int width, int height)
    {
        using var bmp = DecodeAs(file, SKColorType.Gray8);
        var pixels = bmp.GetPixelSpan();
        var result = new byte[width, height];
        for (var y = 0; y < Math.Min(height, bmp.Height); y++)
        {
            var row = pixels.Slice(y * bmp.RowBytes, bmp.Width);
            for (var x = 0; x < Math.Min(width, bmp.Width); x++) result[x, y] = row[x];
        }
        return result;
    }

    /// <summary>備用做法：直接讀像素判斷紅色（R 明顯大於 G、B）。</summary>
    private static bool[,] RedMaskFromPixels(string file, int width, int height)
    {
        using var bmp = DecodeAs(file, SKColorType.Rgba8888);
        var pixels = bmp.GetPixelSpan();
        var mask = new bool[width, height];
        for (var y = 0; y < Math.Min(height, bmp.Height); y++)
        {
            var row = pixels.Slice(y * bmp.RowBytes, bmp.Width * 4);
            for (var x = 0; x < Math.Min(width, bmp.Width); x++)
            {
                int r = row[x * 4], g = row[x * 4 + 1], b = row[x * 4 + 2];
                mask[x, y] = r > 110 && r > g * 1.5 && r > b * 1.4;
            }
        }
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
            // 範本快取（Dictionary）和 EMatcher 都是共用的，整段用同一把鎖
            lock (_matchers)
            {
                if (!_matchers.TryGetValue(kind, out var m))
                {
                    var path = _templatePaths[kind];
                    if (!File.Exists(path))
                    {
                        // 和 EasyOCR2 模型一樣，範本是選用的（vision/README.md）：沒有時只提示一次，不每張照片都警告
                        if (_missingTemplatesLogged.Add(kind))
                            _log.LogInformation("沒有範本圖檔 {Path}，這個步驟會跳過（做法見 vision/README.md）", path);
                        return null;
                    }
                    var color = new EImageC24();
                    color.Load(path);
                    var pattern = new EImageBW8(color.Width, color.Height);
                    EasyImage.Convert(color, pattern);
                    var matcher = new EMatcher();
                    matcher.LearnPattern(pattern);
                    matcher.MinAngle = -10f;
                    matcher.MaxAngle = 10f;
                    matcher.MinScale = 0.6f;
                    matcher.MaxScale = 1.6f;
                    matcher.MaxPositions = 1;
                    m = (matcher, color.Width, color.Height);
                    _matchers[kind] = m;
                    DisposeQuietly(color);
                    DisposeQuietly(pattern);
                }

                var area = searchArea.ClampTo(img.Width, img.Height);
                var roi = new EROIBW8();
                roi.Attach(img.Gray);
                roi.SetPlacement(area.X, area.Y, area.Width, area.Height);
                // Match 直接回傳結果；找不到時仍可能回傳一筆分數很低（例如 -1）的結果，所以要看分數
                var found = m.Matcher.Match(roi);
                if (found.Length == 0 || found[0].Score < 0.6f) return null;

                var pos = found[0];
                var w = (int)(m.W * pos.Scale);
                var h = (int)(m.H * pos.Scale);
                // Center 是相對於 ROI 的中心點（已用 Open eVision 26.6 實測確認）
                return new CoreRect(area.X + (int)pos.Center.X - w / 2, area.Y + (int)pos.Center.Y - h / 2, w, h)
                    .ClampTo(img.Width, img.Height);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "EasyMatch 範本比對失敗");
            return null;
        }
    }

    // ───────── 文字辨識（EasyOCR2）─────────

    private readonly object _ocrLock = new();
    private EOCR2? _ocr;

    public string? ReadText(IVisionImage image, CoreRect area, OcrField field)
    {
        if (_ocrModelPath is null || !File.Exists(_ocrModelPath)) return null;
        var img = (Image)image;
        try
        {
            string text;
            // 先設定 Topology 再 Read：兩張照片同時處理時，不鎖住的話會用到別人設定的 Topology
            lock (_ocrLock)
            {
                if (_ocr is null)
                {
                    var ocr = new EOCR2();
                    ocr.Load(_ocrModelPath); // 載入成功才存起來，失敗時下次會重試
                    _ocr = ocr;
                }
                _ocr.Topology = _topology[field];
                var a = area.ClampTo(img.Width, img.Height);
                var roi = new EROIBW8();
                roi.Attach(img.Gray);
                roi.SetPlacement(a.X, a.Y, a.Width, a.Height);
                text = _ocr.Read(roi);
            }
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
