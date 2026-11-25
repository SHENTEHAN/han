using InvoiceEye.Core.Vision;

namespace InvoiceEye.Api;

/// <summary>
/// 依設定選擇影像引擎：
///   Vision:Engine = "Auto"（預設）：有編譯 Open eVision 就用，失敗（例如沒插 dongle）就改用示範模式
///   Vision:Engine = "OpenEVision"：一定要用 Open eVision，失敗就停止啟動
///   Vision:Engine = "Mock"：示範模式
/// </summary>
public static class VisionEngineFactory
{
    public static IVisionEngine Create(IConfiguration config, ILogger logger)
    {
        var mode = config["Vision:Engine"] ?? "Auto";
        if (mode.Equals("Mock", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("使用示範模式（MockVisionEngine），不會真的辨識照片");
            return new MockVisionEngine();
        }

#if OPEN_EVISION
        try
        {
            var engine = new OpenEVisionEngine(config.GetSection("Vision:OpenEVision"), logger);
            logger.LogInformation("已啟用 Open eVision 影像引擎");
            return engine;
        }
        catch (Exception ex) when (!mode.Equals("OpenEVision", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogError(ex, "Open eVision 初始化失敗（請確認已安裝 Open eVision、USB dongle 已插上且有授權），改用示範模式");
            return new MockVisionEngine();
        }
#else
        if (mode.Equals("OpenEVision", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "這個版本沒有編譯 Open eVision。請在 Windows 安裝 Open eVision 後，用 -p:OpenEVisionDll=<Open_eVision_NetApi.dll 路徑> 重新建置。");
        logger.LogWarning("這個版本沒有編譯 Open eVision，使用示範模式（MockVisionEngine）");
        return new MockVisionEngine();
#endif
    }
}
