using System;
using System.Collections.Generic;

namespace PivotPic.Models;

/// <summary>
/// 加速模式。加速组件只做 GitHub 域的纯透传，绝不解密 TLS、不安装任何证书。
/// </summary>
public enum ProxyMode
{
    /// <summary>不启动加速组件，全部请求直连。</summary>
    Off,

    /// <summary>
    /// 进程内代理：加速组件运行在 127.0.0.1，只有本进程的 HttpClient 走它。
    /// 零系统写入，是系统分流不可用时的降级档。
    /// </summary>
    Passthrough,

    /// <summary>
    /// 系统级 GitHub 分流（默认）：向加速组件声明接管 GitHub 域名，
    /// 由它写入系统 PAC；退出或关闭加速时按快照还原。
    /// </summary>
    SystemWide
}

/// <summary>加速组件的运行状态，直接驱动设置页的状态点与文案。</summary>
public enum ProxyStatus
{
    /// <summary>未启用（用户关闭加速，或总开关关闭）。</summary>
    Off,

    /// <summary>正在启动，尚未通过健康检查。</summary>
    Starting,

    /// <summary>已就绪，系统级 GitHub 分流生效。</summary>
    RunningSystem,

    /// <summary>已就绪，仅本进程走加速（系统分流未生效或已被用户关闭）。</summary>
    RunningProcess,

    /// <summary>不可用，需要看 <see cref="ProxyHealth.Failure"/> 给出人话解释。</summary>
    Failed
}

/// <summary>加速组件不可用的原因。界面只按它翻译成人话，不暴露技术名词。</summary>
public enum ProxyFailureKind
{
    None,

    /// <summary>随包分发的加速组件缺失或损坏。</summary>
    BinaryMissing,

    /// <summary>端口被占用，且探测区间内没有可用端口。</summary>
    PortBusy,

    /// <summary>启动后未在超时时间内通过健康检查。</summary>
    StartTimeout,

    /// <summary>就绪后进程异常退出，且重启次数已用尽。</summary>
    Crashed,

    /// <summary>用户关闭加速或程序退出，主动停止。</summary>
    Stopped
}

/// <summary>一次加速组件运行态快照。所有字段都是值语义，可以安全跨线程传递。</summary>
public sealed class ProxyHealth
{
    /// <summary>当前状态。</summary>
    public ProxyStatus Status { get; init; } = ProxyStatus.Off;

    /// <summary>实际监听端口；未就绪时为 0。</summary>
    public int Port { get; init; }

    /// <summary>加速组件的进程号；未就绪时为 0。</summary>
    public int ProcessId { get; init; }

    /// <summary>当前生效的加速模式。</summary>
    public ProxyMode Mode { get; init; } = ProxyMode.Off;

    /// <summary>不可用原因。</summary>
    public ProxyFailureKind Failure { get; init; } = ProxyFailureKind.None;

    /// <summary>原始失败描述，只写日志，不直接上界面。</summary>
    public string? Detail { get; init; }

    /// <summary>最近一次健康检查时间（UTC）。</summary>
    public DateTime LastCheckUtc { get; init; } = DateTime.UtcNow;

    /// <summary>已自动重启次数。</summary>
    public int RestartCount { get; init; }

    /// <summary>是否已经可用（两种 Running 态都算）。</summary>
    public bool IsRunning => Status is ProxyStatus.RunningSystem or ProxyStatus.RunningProcess;

    /// <summary>给本进程 HttpClient 用的代理地址；不可用时为 null（调用方应直连）。</summary>
    public string? LocalProxyUrl => Port > 0 ? $"http://127.0.0.1:{Port}" : null;

    /// <summary>
    /// 界面用的中文状态文案。只用「加速中 / 未启用 / 启动中 / 不可用」四个词，
    /// 不暴露端口、组件名等技术细节（细节放进 <see cref="DisplayDetail"/> 作为悬停提示）。
    /// </summary>
    public string DisplayText => Status switch
    {
        ProxyStatus.Starting => "启动中",
        ProxyStatus.RunningSystem => "加速中",
        ProxyStatus.RunningProcess => "加速中",
        ProxyStatus.Failed => "不可用",
        _ => "未启用"
    };

    /// <summary>悬停提示里的补充说明：作用范围 + 本机端口，供需要排查的用户查看。</summary>
    public string DisplayDetail => Status switch
    {
        ProxyStatus.RunningSystem => $"GitHub 域名已交给本机加速通道（127.0.0.1:{Port}）",
        ProxyStatus.RunningProcess => $"仅本程序使用加速通道（127.0.0.1:{Port}），系统设置未改动",
        ProxyStatus.Failed => FailureText ?? "加速不可用，已改用直连",
        ProxyStatus.Starting => "正在启动加速通道",
        _ => "未启用加速"
    };

    /// <summary>失败原因的人话解释；无失败时返回 null。</summary>
    public string? FailureText => Failure switch
    {
        ProxyFailureKind.BinaryMissing => "加速组件缺失，已改用直连",
        ProxyFailureKind.PortBusy => "加速端口被占用，已改用直连",
        ProxyFailureKind.StartTimeout => "加速组件启动失败，已改用直连",
        ProxyFailureKind.Crashed => "加速组件多次异常退出，已改用直连",
        _ => null
    };

    /// <summary>未启用/不可用时的空快照。</summary>
    public static ProxyHealth Off => new()
    {
        Status = ProxyStatus.Off,
        Failure = ProxyFailureKind.Stopped,
        LastCheckUtc = DateTime.UtcNow
    };
}

/// <summary>更新包下载的可选来源，按顺序尝试。</summary>
public enum UpdateDownloadSource
{
    /// <summary>内置加速通道（本机加速组件直连 GitHub）。</summary>
    BuiltInProxy,

    /// <summary>第三方镜像前缀。</summary>
    GhProxy,

    /// <summary>完全直连。</summary>
    Direct
}

/// <summary>单个 Release 资产。</summary>
public sealed class ReleaseAsset
{
    /// <summary>资产文件名，例如 <c>正画-Setup-1.3.0.exe</c>。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>字节数；GitHub 未提供时为 0。</summary>
    public long Size { get; init; }

    /// <summary>原始下载地址。</summary>
    public string BrowserDownloadUrl { get; init; } = string.Empty;
}

/// <summary>一条 Release 的必需信息。</summary>
public sealed class ReleaseInfo
{
    /// <summary>原始 tag，例如 <c>v1.3.0</c>。</summary>
    public string TagName { get; init; } = string.Empty;

    /// <summary>去掉前缀 <c>v</c> 后的三段版本号。</summary>
    public Version Version { get; init; } = new(0, 0, 0);

    /// <summary>发布时间（UTC）。</summary>
    public DateTime PublishedAt { get; init; } = DateTime.MinValue;

    /// <summary>更新说明原文（Markdown，界面上按纯文本展示）。</summary>
    public string Body { get; init; } = string.Empty;

    /// <summary>匹配到的安装包资产；未匹配到时为占位对象。</summary>
    public ReleaseAsset SetupAsset { get; init; } = new();

    /// <summary>校验清单资产；未提供时为 null。</summary>
    public ReleaseAsset? ChecksumsAsset { get; init; }

    /// <summary>是否匹配到了可下载的安装包。</summary>
    public bool HasSetupAsset => SetupAsset.Size > 0 && SetupAsset.BrowserDownloadUrl.Length > 0;
}

/// <summary>
/// 一次检查更新的结果类型，用于生成「上次检查」文案。
/// <para>
/// 失败原因必须分细：把 404（仓库还没发布）说成「网络失败」会把排查方向带偏。
/// 这些值会写进配置，**只能追加、不能改动已有取值**（旧配置里已有 0–3）。
/// </para>
/// </summary>
public enum UpdateCheckOutcome
{
    /// <summary>尚未检查过。</summary>
    Unknown = 0,

    /// <summary>已是最新。</summary>
    UpToDate = 1,

    /// <summary>发现新版本。</summary>
    UpdateAvailable = 2,

    /// <summary>链路层失败：超时 / DNS / 连接被拒。</summary>
    NetworkError = 3,

    /// <summary>没有可用的发布（404，或 Release 里没有安装包）。</summary>
    NoRelease = 4,

    /// <summary>被限流（403 且配额用尽）。</summary>
    RateLimited = 5,

    /// <summary>发布信息无法识别（JSON 解析失败 / 版本号非法）。</summary>
    Unrecognized = 6
}

/// <summary>下载进度快照，供弹窗直接绑定。</summary>
public sealed class UpdateProgress
{
    /// <summary>0–1 的整体进度；总量未知时为 0。</summary>
    public double Percent { get; init; }

    /// <summary>已接收字节数。</summary>
    public long ReceivedBytes { get; init; }

    /// <summary>总字节数；未知时为 0。</summary>
    public long TotalBytes { get; init; }

    /// <summary>瞬时速度（字节/秒）。</summary>
    public double BytesPerSecond { get; init; }

    /// <summary>当前使用的来源。</summary>
    public UpdateDownloadSource Source { get; init; } = UpdateDownloadSource.Direct;

    /// <summary>已下载 / 总量。</summary>
    public string SizeText => TotalBytes > 0
        ? $"{Format(ReceivedBytes)} / {Format(TotalBytes)}"
        : Format(ReceivedBytes);

    /// <summary>速度文案。</summary>
    public string SpeedText => BytesPerSecond <= 0 ? "—" : $"{Format((long)BytesPerSecond)}/s";

    /// <summary>来源文案，透明展示当前通道。</summary>
    public string SourceText => Source switch
    {
        UpdateDownloadSource.BuiltInProxy => "内置加速",
        UpdateDownloadSource.GhProxy => "第三方加速",
        _ => "直连"
    };

    /// <summary>把字节格式化成 MB（≥1 GB 时用 GB），统一保留一位小数。</summary>
    public static string Format(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
        {
            return $"{bytes / 1024d / 1024d / 1024d:F2} GB";
        }

        if (bytes >= 1024L * 1024)
        {
            return $"{bytes / 1024d / 1024d:F1} MB";
        }

        if (bytes >= 1024)
        {
            return $"{bytes / 1024d:F0} KB";
        }

        return $"{bytes} B";
    }
}

/// <summary>检查更新的调用来源。静默检查不打扰用户，手动检查要回话。</summary>
public enum UpdateCheckTrigger
{
    /// <summary>启动后或定时器的后台检查。</summary>
    Silent,

    /// <summary>用户点了「检查更新」。</summary>
    Manual
}

/// <summary>
/// 编译期常量与默认配置。仓库、资产名规则、端口区间都集中在这里，
/// 避免散落在服务与界面里各写一份。
/// </summary>
public static class UpdateDefaults
{
    /// <summary>GitHub 仓库（owner/repo）。</summary>
    public const string Repository = "Alpha-Fei/PivotPic";

    /// <summary>Release 的 API 地址。</summary>
    public const string LatestReleaseApiUrl =
        "https://api.github.com/repos/" + Repository + "/releases/latest";

    /// <summary>Release 页面（失败时给用户的兜底出口）。</summary>
    public const string ReleasePageUrl = "https://github.com/" + Repository + "/releases/latest";

    /// <summary>校验清单的资产文件名。</summary>
    public const string ChecksumsAssetName = "checksums.txt";

    /// <summary>检查更新的默认间隔（小时）。v1.3.3 起不再参与调度，仅作旧配置兼容保留。</summary>
    public const int DefaultIntervalHours = 24;

    /// <summary>
    /// 自动检查更新的实际间隔（分钟）。用户要求「5 分钟一次」，且前端不再展示间隔。
    /// </summary>
    public const int CheckIntervalMinutes = 5;

    /// <summary>启动后延迟多久做第一次静默检查（秒）。</summary>
    public const int StartupDelaySeconds = 5;

    /// <summary>
    /// 加速组件的代理端口。
    /// <para>
    /// <b>实测结论</b>：组件的 <c>settings.json</c> 里写 <c>port</c> 会被忽略，
    /// 它固定监听 7810，并在 7811 上提供 PAC 与状态接口
    /// （<c>GET /api/status</c> 返回 <c>{"running":true,"port":7810,"mitm":false}</c>）。
    /// 所以这里不做端口探测，端口冲突就按「不可用」降级直连。
    /// </para>
    /// </summary>
    public const int ProxyPort = 7810;

    /// <summary>PAC 与状态接口所在端口（代理端口 +1），用于核对系统分流是否生效。</summary>
    public const int ProxyWebUiPort = ProxyPort + 1;

    /// <summary>加速组件的进程名（不含扩展名）。</summary>
    public const string ProxyProcessName = "gitport-github";

    /// <summary>第三方镜像前缀，仅作为第二来源使用。</summary>
    public const string GhProxyPrefix = "https://gh-proxy.com/";

    /// <summary>
    /// 下载前要求的可用空间倍数。安装包下载完还要留给安装程序解压，
    /// 只留一倍空间会在最后一步失败。
    /// </summary>
    public const int DiskHeadroomFactor = 2;

    /// <summary>单源最多重试次数（1 表示首次失败后再试一次）。</summary>
    public const int RetriesPerSource = 1;

    /// <summary>每个源之间的退避毫秒数，按顺序取用。</summary>
    public static readonly int[] RetryBackoffMilliseconds = { 2000, 5000 };

    /// <summary>下载进度上报的最小间隔（毫秒），避免灌爆 UI 线程。</summary>
    public const int ProgressReportIntervalMs = 100;

    /// <summary>安装包的最小合理体积（字节）。低于它的资产不认为是安装包。</summary>
    public const long MinSetupAssetBytes = 20L * 1024 * 1024;

    /// <summary>安装包资产名的宽松匹配规则：包含 Setup 字样且以 .exe 结尾。</summary>
    public static readonly string[] SetupAssetKeyword = { "Setup", "setup", "SETUP" };

    /// <summary>版本号文本，形如 1.3.0。</summary>
    public static string FormatVersion(Version version) =>
        $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";

    /// <summary>版本号文本，形如 v1.3.0。</summary>
    public static string FormatTag(Version version) => "v" + FormatVersion(version);

    /// <summary>三段版本号比较：只比 Major/Minor/Build，忽略修订号。</summary>
    public static int Compare(Version left, Version right)
    {
        var major = left.Major.CompareTo(right.Major);
        if (major != 0)
        {
            return major;
        }

        var minor = left.Minor.CompareTo(right.Minor);
        if (minor != 0)
        {
            return minor;
        }

        return Math.Max(0, left.Build).CompareTo(Math.Max(0, right.Build));
    }

    /// <summary>把 tag（可带 v 前缀）解析成三段版本号；解析失败返回 null。</summary>
    public static Version? ParseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        var text = tag.Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
        {
            text = text[1..];
        }

        // 允许 v1.3.0-beta 这类带后缀的 tag：只取前三段数字。
        var core = new List<string>(3);
        foreach (var part in text.Split('.', '-', '+'))
        {
            if (core.Count >= 3)
            {
                break;
            }

            if (!int.TryParse(part.Trim(), out var number) || number < 0)
            {
                break;
            }

            core.Add(number.ToString());
        }

        return core.Count == 0 ? null : Version.TryParse(string.Join('.', core), out var parsed) ? parsed : null;
    }
}
