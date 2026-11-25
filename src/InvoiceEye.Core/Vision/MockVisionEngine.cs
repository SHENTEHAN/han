using System.Security.Cryptography;
using System.Text;

namespace InvoiceEye.Core.Vision;

/// <summary>
/// 示範用的影像引擎：沒有 Open eVision（或沒插 dongle）時使用。
/// 不做真的影像辨識，而是依照片內容產生一張固定格式的電子發票 QR Code，
/// 讓 App 的拍照 → 確認 → 記帳 → 對獎流程可以先跑起來。
/// 同一張照片會得到同一個發票號碼，方便測試「重複發票檢查」。
/// </summary>
public sealed class MockVisionEngine : IVisionEngine
{
    private readonly Func<DateOnly> _today;

    public MockVisionEngine(Func<DateOnly>? today = null) => _today = today ?? (() => DateOnly.FromDateTime(DateTime.Now));

    public string Name => "mock";

    private sealed class MockImage(byte[] bytes) : IVisionImage
    {
        public byte[] Bytes { get; } = bytes;
        public int Width => 1000;
        public int Height => 1600;
        public void Dispose() { }
    }

    public IVisionImage Load(Stream imageStream)
    {
        using var ms = new MemoryStream();
        imageStream.CopyTo(ms);
        return new MockImage(ms.ToArray());
    }

    public IReadOnlyList<byte[]> ReadQrCodes(IVisionImage image)
    {
        var hash = SHA256.HashData(((MockImage)image).Bytes);
        var letters = $"{(char)('A' + hash[0] % 26)}{(char)('A' + hash[1] % 26)}";
        var digits = (BitConverter.ToUInt32(hash, 2) % 100_000_000).ToString("00000000");
        var today = _today();
        var roc = $"{today.Year - 1911:000}{today.Month:00}{today.Day:00}";

        // 範例品項：茶葉蛋 10 + 御飯糰 35 + 鮮奶 40 = 85 元（未稅 81 = 0x51，含稅 85 = 0x55）
        var left = $"{letters}{digits}{roc}1234000000510000005500000000" + "12345678" + "MOCKMOCKMOCKMOCKMOCKMOCK"
                   + ":**********:3:3:1:茶葉蛋:1:10:御飯糰:1:35:鮮奶:1:40";
        var right = "**";
        return new[] { Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right) };
    }

    public IReadOnlyList<string> ReadBarcodes(IVisionImage image) => Array.Empty<string>();
    public Rect? FindPaper(IVisionImage image) => new Rect(0, 0, image.Width, image.Height);
    public Rect? FindRedInk(IVisionImage image, Rect searchArea) => null;
    public Rect? FindTemplate(IVisionImage image, TemplateKind kind, Rect searchArea) => null;
    public string? ReadText(IVisionImage image, Rect area, OcrField field) => null;
}
