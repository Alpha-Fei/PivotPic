using System.Text.Json;
using System.Text.Json.Serialization;

namespace PivotPic.Models;

/// <summary>阈值判定口径。</summary>
public enum ThresholdMetric
{
    /// <summary>长边像素。</summary>
    LongEdge,
    /// <summary>短边像素。</summary>
    ShortEdge,
    /// <summary>总像素（单位：万像素）。</summary>
    Megapixel,
    /// <summary>文件体积（单位：KB）。</summary>
    FileSizeKb
}

/// <summary>旋转实现方式。</summary>
public enum RotationMode
{
    /// <summary>物理旋转像素（内置 WPF 引擎）。</summary>
    Physical,
    /// <summary>仅改写 EXIF Orientation，不重编码。</summary>
    ExifOnly,
    /// <summary>物理旋转像素（libvips 高性能引擎，低内存，默认为此）。</summary>
    LibVips
}

/// <summary>旋转方向。</summary>
public enum RotateDirection
{
    /// <summary>顺时针 90 度。</summary>
    Clockwise,
    /// <summary>逆时针 90 度。</summary>
    CounterClockwise
}

/// <summary>关闭主窗口时的行为。</summary>
public enum CloseAction
{
    /// <summary>最小化到托盘。</summary>
    MinimizeToTray,
    /// <summary>直接退出。</summary>
    Exit
}

/// <summary>主题。</summary>
public enum ThemeMode
{
    Dark,
    Light,
    System
}

/// <summary>单个被监控的目录配置。</summary>
/// <summary>
/// 一条按文件名关键词生效的幅面规则。
/// 文件名包含 <see cref="Keyword"/> 时，用这里的上限替代目录默认上限。
/// </summary>
public sealed class SizeRule
{
    /// <summary>文件名关键词，不区分大小写。</summary>
    public string Keyword { get; set; } = string.Empty;

    /// <summary>命中该关键词时的成品幅面上限（厘米）。</summary>
    public double MaxSideCm { get; set; } = 100;
}

public sealed class WatchedFolder
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>原始路径模板，可能包含 {mm-dd} 这类时间占位符。</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>是否启用监控。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>是否包含子文件夹，默认开启。</summary>
    public bool IncludeSubdirectories { get; set; } = true;

    /// <summary>是否开启分辨率阈值检查，默认关闭。</summary>
    public bool ResolutionCheckEnabled { get; set; } = false;

    /// <summary>阈值，默认 300。</summary>
    public double Threshold { get; set; } = 300;

    /// <summary>阈值判定口径，默认长边像素。</summary>
    public ThresholdMetric Metric { get; set; } = ThresholdMetric.LongEdge;

    /// <summary>
    /// 包含子文件夹时要忽略的子文件夹名称（不区分大小写）。
    /// 命中这些名字的目录，其中的图片变动一律不处理。
    /// </summary>
    public List<string> IgnoredFolders { get; set; } = new();

    /// <summary>是否启用「成品幅面上限」检查。</summary>
    public bool SizeLimitEnabled { get; set; }

    /// <summary>成品幅面上限（厘米）。宽和高“都”超过时才跳过旋转。</summary>
    public double MaxSideCm { get; set; } = 100;

    /// <summary>按文件名关键词覆盖上限的规则表；未命中时用 <see cref="MaxSideCm"/>。</summary>
    public List<SizeRule> SizeRules { get; set; } = new();

    /// <summary>该目录的今日已处理计数（内存态，不持久化）。</summary>
    [JsonIgnore]
    public int ProcessedToday { get; set; }
}

/// <summary>应用全局配置。</summary>
public sealed class AppConfig
{
    public int SchemaVersion { get; set; } = 1;

    public List<WatchedFolder> Folders { get; set; } = new();

    /// <summary>全局总开关。</summary>
    public bool MonitoringEnabled { get; set; } = true;

    public RotationMode RotationMode { get; set; } = RotationMode.LibVips;

    public RotateDirection RotateDirection { get; set; } = RotateDirection.Clockwise;

    public CloseAction CloseAction { get; set; } = CloseAction.MinimizeToTray;

    public bool AutoStart { get; set; } = false;

    /// <summary>处理图片时是否弹出进度提示，默认开启。</summary>
    public bool ShowProgressPopup { get; set; } = true;

    public bool FirstRun { get; set; } = true;

    public ThemeMode Theme { get; set; } = ThemeMode.Dark;

    public double WindowWidth { get; set; } = 1000;

    public double WindowHeight { get; set; } = 680;

    /// <summary>上次窗口左边界；从未记录过时为 null。</summary>
    public double? WindowLeft { get; set; }

    /// <summary>上次窗口上边界；从未记录过时为 null。</summary>
    public double? WindowTop { get; set; }

    /// <summary>单图解码像素上限，超出即跳过。</summary>
    public int MaxPixels { get; set; } = 300_000_000;

    /// <summary>旋转成功后是否在文件名结尾追加标记。</summary>
    public bool AddSuffix { get; set; }

    /// <summary>追加的标记文本，加在扩展名之前。</summary>
    public string Suffix { get; set; } = "_已旋转";

    public List<string> SupportedExtensions { get; set; } = new()
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".tif", ".tiff"
    };

    // ============================================================
    // 更新与网络（配置架构 v4 新增）
    // ============================================================

    /// <summary>启动后延迟检查 + 定时自动检查更新。关闭后只能手动检查。</summary>
    public bool AutoUpdateCheck { get; set; } = true;

    /// <summary>两次自动检查之间的间隔（小时），限定在 1–168。</summary>
    public int UpdateCheckIntervalHours { get; set; } = 24;

    /// <summary>上次检查完成的时间（UTC）；从未检查过为 null。</summary>
    public DateTime? LastCheckUtc { get; set; }

    /// <summary>上次检查的结果，用于设置页的「上次检查」文案。</summary>
    public UpdateCheckOutcome LastCheckOutcome { get; set; } = UpdateCheckOutcome.Unknown;

    /// <summary>
    /// 用户点过「稍后」的版本号（形如 1.3.0）。
    /// 同一个版本不再弹模态详情，但横幅入口常驻，用户仍可主动升级。
    /// </summary>
    public string SkippedVersion { get; set; } = string.Empty;

    /// <summary>
    /// 加速模式。默认系统级 GitHub 分流；降级档为进程内加速（零系统写入）。
    /// </summary>
    public ProxyMode ProxyMode { get; set; } = ProxyMode.SystemWide;

    /// <summary>深拷贝，用于配置回滚与变更检测。</summary>
    public AppConfig Clone()
    {
        var json = JsonSerializer.Serialize(this);
        return JsonSerializer.Deserialize<AppConfig>(json)!;
    }
}

/// <summary>日志级别。</summary>
public enum LogLevel
{
    Info,
    Warn,
    Error
}

/// <summary>一条结构化日志。</summary>
public sealed class LogEntry
{
    public DateTime Time { get; set; } = DateTime.Now;
    public LogLevel Level { get; set; }
    public string Folder { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string? OriginalSize { get; set; }
    public string Action { get; set; } = string.Empty;
    public long ElapsedMs { get; set; }
    public string Message { get; set; } = string.Empty;

    public string TimeText => Time.ToString("HH:mm:ss");

    public string LevelText => Level switch
    {
        LogLevel.Info => "信息",
        LogLevel.Warn => "警告",
        _ => "错误"
    };

    /// <summary>
    /// 把日志压成一行，由 <c>LogService.AppendToFile</c> 追加到 <c>logs\app-yyyyMMdd.log</c>。
    /// <para>
    /// <b>列顺序是硬契约</b>：<c>MonitorPage.SeedProcessedToday</c>（经 <c>CountRotatedInTodayLog</c>）
    /// 按 <c>\t</c> 切分后取下标 5 的「动作」列来统计「今日处理 N 张」。
    /// 改动列顺序、改用别的分隔符、或去掉任何一列，都会让该统计静默错位（不报错、数字变少），必须同步修改解析方。
    /// </para>
    /// <para>列顺序：时间 \t 级别 \t 目录 \t 文件名 \t 尺寸 \t 动作 \t 耗时 \t 说明。</para>
    /// </summary>
    public override string ToString()
    {
        var size = string.IsNullOrEmpty(OriginalSize) ? "-" : OriginalSize;
        return $"{Time:yyyy-MM-dd HH:mm:ss}\t{LevelText}\t{Folder}\t{FileName}\t{size}\t{Action}\t{ElapsedMs}ms\t{Message}";
    }
}

/// <summary>图片探测结果。</summary>
public sealed class ImageProbe
{
    /// <summary>像素宽度（未按 EXIF 校正）。</summary>
    public int PixelWidth { get; init; }

    /// <summary>像素高度（未按 EXIF 校正）。</summary>
    public int PixelHeight { get; init; }

    /// <summary>EXIF Orientation，缺省为 1。</summary>
    public int Orientation { get; init; } = 1;

    /// <summary>按 EXIF 校正后的逻辑宽度。</summary>
    public int OrientedWidth => Orientation >= 5 && Orientation <= 8 ? PixelHeight : PixelWidth;

    /// <summary>按 EXIF 校正后的逻辑高度。</summary>
    public int OrientedHeight => Orientation >= 5 && Orientation <= 8 ? PixelWidth : PixelHeight;

    /// <summary>水平分辨率（DPI），缺省按 96 处理。</summary>
    public double DpiX { get; init; } = 96;

    /// <summary>垂直分辨率（DPI），缺省按 96 处理。</summary>
    public double DpiY { get; init; } = 96;

    /// <summary>按 DPI 换算的成品宽度（厘米）。</summary>
    public double WidthCm => PixelToCm(OrientedWidth, DpiX);

    /// <summary>按 DPI 换算的成品高度（厘米）。</summary>
    public double HeightCm => PixelToCm(OrientedHeight, DpiY);

    /// <summary>成品最长边（厘米）。</summary>
    public double MaxSideCm => Math.Max(WidthCm, HeightCm);

    private static double PixelToCm(int pixels, double dpi)
    {
        var effective = dpi is >= 5 and <= 10_000 ? dpi : 96;
        return pixels / effective * 2.54;
    }

    /// <summary>文件字节数。</summary>
    public long FileSize { get; init; }

    /// <summary>解码器名称。</summary>
    public string DecoderName { get; init; } = string.Empty;

    public long PixelCount => (long)PixelWidth * PixelHeight;
}

/// <summary>单个文件的处理结果。</summary>
public sealed class RotationResult
{
    public bool Success { get; init; }
    public bool Rotated { get; init; }
    public string Action { get; init; } = string.Empty;
    public string? Message { get; init; }
    public int BeforeWidth { get; init; }
    public int BeforeHeight { get; init; }
    public int AfterWidth { get; init; }
    public int AfterHeight { get; init; }
    public long ElapsedMs { get; init; }

    public static RotationResult Skip(string action, string? message = null) =>
        new() { Success = true, Rotated = false, Action = action, Message = message };

    /// <summary>复制一份并追加说明，用于「旋转成功但后续还有动作」的场景。</summary>
    public RotationResult WithExtraMessage(string extra) => new()
    {
        Success = Success,
        Rotated = Rotated,
        Action = Action,
        Message = string.IsNullOrWhiteSpace(Message) ? extra : Message + "；" + extra,
        BeforeWidth = BeforeWidth,
        BeforeHeight = BeforeHeight,
        AfterWidth = AfterWidth,
        AfterHeight = AfterHeight,
        ElapsedMs = ElapsedMs
    };

    public static RotationResult Fail(string message) =>
        new() { Success = false, Action = "Failed", Message = message };
}

/// <summary>一条超阈值告警。</summary>
public sealed class AlertItem
{
    public string FullPath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public long FileSize { get; set; }
    public ThresholdMetric Metric { get; set; }
    public double Value { get; set; }
    public double Threshold { get; set; }
    public DateTime Time { get; set; } = DateTime.Now;

    public string SizeText => $"{Width} × {Height}";

    public string ValueText => Metric switch
    {
        ThresholdMetric.LongEdge => $"长边 {Value:0} px",
        ThresholdMetric.ShortEdge => $"短边 {Value:0} px",
        ThresholdMetric.Megapixel => $"总像素 {Value:0} 万",
        _ => $"体积 {Value:0} KB"
    };
}

/// <summary>扫描进度快照。</summary>
public sealed class ScanProgress
{
    public string FolderId { get; init; } = string.Empty;
    public int Scanned { get; init; }
    public int Total { get; init; }
    public bool Completed { get; init; }
    public int Processed { get; init; }
}
