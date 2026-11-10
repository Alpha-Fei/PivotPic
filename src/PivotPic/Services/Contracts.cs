using System.Net.Http;
using System.Text.Json;
using PivotPic.Models;

namespace PivotPic.Services;

/// <summary>配置读写。配置位于 %AppData%\PivotPic\config.json。</summary>
public interface IConfigService
{
    AppConfig Current { get; }
    event EventHandler? Changed;

    void Load();
    void SaveNow();
    void SaveDebounced();
    void ResetToDefault();
    void Export(string targetPath);
    void Import(string sourcePath);
    string ConfigDirectory { get; }
}

/// <summary>滚动日志。</summary>
public interface ILogService
{
    IReadOnlyList<LogEntry> Recent { get; }
    event EventHandler<LogEntry>? EntryAdded;
    string LogDirectory { get; }

    void Write(LogLevel level, string folder, string fileName, string? originalSize,
        string action, long elapsedMs, string message);
    void Info(string action, string message, string folder = "", string fileName = "");
    void Warn(string action, string message, string folder = "", string fileName = "");
    void Error(string action, string message, string folder = "", string fileName = "");
}

/// <summary>路径时间占位符展开。</summary>
public interface IPathTemplateService
{
    string Resolve(string template, DateTime? now = null);
    IReadOnlyList<string> FindUnknownPlaceholders(string template);
}

/// <summary>图片探测与方向归一化。</summary>
public interface IImageService
{
    ImageProbe? Probe(string path);
    /// <param name="renameSuffix">旋转成功后追加到文件名末尾的标记；为 null 表示不改名。</param>
    /// <param name="maxSideCm">成品幅面上限（厘米）；0 表示不检查。</param>
    /// <param name="convertProgressiveJpeg">JPEG 兼容模式：连续 JPEG 转基线。</param>
    RotationResult Process(string path, RotationMode mode, RotateDirection direction, int maxPixels,
        string? renameSuffix = null, double maxSideCm = 0, bool convertProgressiveJpeg = false);
}

/// <summary>自触发抑制集合：记录本进程写回的文件，避免被文件监控再次触发。</summary>
public interface IIgnoreSet
{
    /// <summary>在写回文件之前登记。</summary>
    void Remember(string path);
    /// <summary>事件到达时消费；命中返回 true 并移除。</summary>
    bool Consume(string path);
}

/// <summary>多目录监控与全量扫描。</summary>
public interface IMonitorService
{
    event EventHandler<ScanProgress>? ScanProgressChanged;
    event EventHandler<string>? FolderStateChanged;

    /// <summary>本轮队列处理进度。用于底部进度卡片显示整体完成比例。</summary>
    event EventHandler<BatchProgress>? BatchProgressChanged;

    bool IsRunning { get; }
    bool IsFolderBusy(string folderId);

    void StartAll();
    void StopAll();
    void StartFolder(WatchedFolder folder);
    void StopFolder(string folderId);
    Task ScanAsync(WatchedFolder folder, CancellationToken cancellationToken);
    void CancelScan(string folderId);
}

/// <summary>阈值告警。</summary>
public interface IAlertService
{
    event EventHandler<IReadOnlyList<AlertItem>>? AlertsRaised;

    IReadOnlyList<AlertItem> Unread { get; }
    int UnreadCount { get; }
    int TodayCount { get; }

    void Evaluate(string fullPath, ImageProbe probe, WatchedFolder folder);
    void EvaluateFile(string fullPath, WatchedFolder folder);
    void MarkAllRead();
    void Locate(string fullPath);
}

/// <summary>单实例互斥与唤起。</summary>
public interface ISingleInstanceService : IDisposable
{
    bool TryAcquire();
    event Action? ActivationRequested;
}

/// <summary>托盘驻留。</summary>
public interface ITrayService : IDisposable
{
    void Initialize();
    void UpdateState(TrayState state, int alertCount);

    /// <summary>在托盘菜单里显示「发现新版本」入口；传入 null 表示移除该入口。</summary>
    void ShowUpdateBadge(string? versionText);
}

public enum TrayState
{
    Idle,
    Monitoring,
    Alert
}

// ============================================================
// 更新与网络：内置加速组件 + 在线自动更新
// ============================================================

/// <summary>
/// 内置加速组件的生命周期管理。
/// <para>
/// 组件以纯透传模式（不解密 TLS）运行，因此本服务完全不接触任何证书。
/// </para>
/// </summary>
public interface IGitPortService : IDisposable
{
    /// <summary>当前运行态快照。</summary>
    ProxyHealth Current { get; }

    /// <summary>状态变化通知。可能在后台线程触发，订阅方自行切回 UI 线程。</summary>
    event EventHandler<ProxyHealth>? HealthChanged;

    /// <summary>
    /// 启动加速组件并等待健康检查通过。
    /// </summary>
    /// <param name="enableSystemPac">true 表示请求组件写入系统 PAC（只接管 GitHub 域）。</param>
    Task<ProxyHealth> StartAsync(bool enableSystemPac, CancellationToken cancellationToken);

    /// <summary>停止加速组件。调用方应在退出流程里先停代理、再还原系统代理。</summary>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>切换加速模式；组件不可用时返回 false（调用方应降级直连）。</summary>
    Task<bool> ApplyModeAsync(ProxyMode mode, CancellationToken cancellationToken = default);

    /// <summary>健康探针：进程仍在且端口可连通返回 true。</summary>
    Task<bool> PingAsync(CancellationToken cancellationToken = default);
}

/// <summary>系统代理（PAC）的快照、校验与还原。</summary>
public interface IPacService
{
    /// <summary>加速组件启动前快照当前的系统代理设置。已有快照时不覆盖。</summary>
    Task<bool> CaptureBaselineAsync(CancellationToken cancellationToken = default);

    /// <summary>启动后核对系统代理是否确实指向本机加速端口。</summary>
    Task<bool> VerifyAsync(int proxyPort, CancellationToken cancellationToken = default);

    /// <summary>
    /// 按快照还原系统代理。仅当当前 PAC 仍指向本机加速端口时才写回，
    /// 用户中途改用其它代理软件的设置不会被覆盖。
    /// </summary>
    Task RestoreAsync(CancellationToken cancellationToken = default);

    /// <summary>是否存在上次异常退出遗留的快照。</summary>
    bool HasPendingBackup { get; }
}

/// <summary>按加速可用性提供 HttpClient，供更新检查与下载复用。</summary>
public interface IProxyHttpClientProvider
{
    /// <summary>
    /// 创建客户端。加速组件不可用时返回直连客户端（不抛异常），
    /// 让调用方的重试链路始终保持可用。
    /// </summary>
    HttpClient CreateClient();

    /// <summary>
    /// 创建<b>强制直连</b>的客户端，绕过加速通道。
    /// <para>
    /// 实测：小请求（Release 元数据、校验清单）走加速反而慢约 0.35s，
    /// 且会平白依赖加速组件是否就绪。这类请求应当直连优先，失败再回落加速。
    /// </para>
    /// </summary>
    HttpClient CreateDirectClient();

    /// <summary>当前会使用的来源，用于在界面上透明展示。</summary>
    UpdateDownloadSource CurrentSource { get; }
}

/// <summary>在线更新：检查、下载、校验。</summary>
public interface IUpdateService
{
    /// <summary>最近一次检查发现的新版本；没有则为 null。</summary>
    ReleaseInfo? Available { get; }

    /// <summary>最近一次检查结果。</summary>
    UpdateCheckOutcome LastOutcome { get; }

    /// <summary>发现新版本（仅静默检查会触发，避免手动检查弹两次窗）。</summary>
    event EventHandler<ReleaseInfo>? UpdateAvailable;

    /// <summary>检查更新。静默检查失败只写日志；手动检查通过异常回话。</summary>
    Task<ReleaseInfo?> CheckAsync(UpdateCheckTrigger trigger, CancellationToken cancellationToken);

    /// <summary>
    /// 下载安装包并做 SHA256 校验，成功返回包路径。
    /// 失败抛 <see cref="UpdateFailedException"/>（带人话消息）。
    /// </summary>
    Task<string> DownloadAndVerifyAsync(
        ReleaseInfo release, IProgress<UpdateProgress> progress, CancellationToken cancellationToken);

    /// <summary>
    /// 从本地缓存取出上次检查得到的发布信息，不联网也不改状态。
    /// 用于「重启后仍有已下载的安装包」这种场景，省掉一次网络往返。
    /// </summary>
    ReleaseInfo? GetCachedRelease();
}

/// <summary>更新编排：定时检查、横幅/弹窗状态、下载后拉起安装程序。</summary>
public interface IUpdateCoordinator : IDisposable
{
    /// <summary>当前可用的新版本；没有则为 null。</summary>
    ReleaseInfo? AvailableUpdate { get; }

    /// <summary>最近一次检查结果。</summary>
    UpdateCheckOutcome LastOutcome { get; }

    /// <summary>是否正在检查（手动检查时按钮要置灰）。</summary>
    bool IsChecking { get; }

    /// <summary>是否有可提示的新版本（用户跳过或本次已忽略时为 false）。</summary>
    bool HasAvailableUpdate { get; }

    /// <summary>
    /// 是否应该自动弹出更新详情。用户点过「稍后」或勾过「不再提示此版本」时为 false，
    /// 此时横幅入口仍在，由用户主动打开。
    /// </summary>
    bool ShouldAutoPrompt { get; }

    /// <summary>发现可提示的新版本。</summary>
    event EventHandler<ReleaseInfo>? UpdateFound;

    /// <summary>任意状态变化（检查中 / 结果 / 横幅显隐）。</summary>
    event EventHandler? StateChanged;

    /// <summary>启动时调用：还残留 PAC 先还原，再排定时检查。</summary>
    void Start();

    /// <summary>立即检查一次（用户点「检查更新」）。失败返回 null 并写日志。</summary>
    Task<ReleaseInfo?> CheckNowAsync();

    /// <summary>本次会话不再提示（横幅仍保留入口）。</summary>
    void DismissForSession();

    /// <summary>持久化「不再提示此版本」，同版本不再自动弹窗。</summary>
    void SkipVersion(string versionText);

    /// <summary>尝试复用上次已下载并校验通过的安装包。命中返回 true。</summary>
    Task<bool> TryResumePendingAsync();

    /// <summary>
    /// 下载 → 校验 → 以 <c>--wait-pid</c> 拉起安装程序 → 请求主程序退出。
    /// 返回安装包路径；失败抛 <see cref="UpdateFailedException"/>。
    /// </summary>
    Task<string> DownloadAndLaunchAsync(
        ReleaseInfo release, IProgress<UpdateProgress> progress, CancellationToken cancellationToken);

    /// <summary>设置页「上次检查」的展示文案。</summary>
    string LastCheckText { get; }
}

/// <summary>
/// 更新链路失败。<see cref="Exception.Message"/> 只进日志，
/// 界面统一使用 <see cref="UserMessage"/>（人话，不含技术名词）。
/// </summary>
public sealed class UpdateFailedException : Exception
{
    /// <summary>可以直接展示给用户的中文说明。</summary>
    public string UserMessage { get; }

    /// <summary>
    /// 失败归类，用于「上次检查」文案与状态持久化。
    /// 下载阶段的失败不会写进配置，这里取默认值即可。
    /// </summary>
    public UpdateCheckOutcome Outcome { get; }

    public UpdateFailedException(
        string userMessage,
        string technicalMessage,
        Exception? inner = null,
        UpdateCheckOutcome outcome = UpdateCheckOutcome.NetworkError)
        : base(technicalMessage, inner)
    {
        UserMessage = userMessage;
        Outcome = outcome;
    }
}

/// <summary>
/// 服务定位器，避免引入 DI 容器。
/// <para>
/// <b>全部槽位可空</b>：任何一个服务构造失败（缺 DLL、无权限、磁盘异常）都不应该让程序起不来。
/// 装配方（<c>App.OnStartup</c>）逐段 try/catch，失败的槽位保持 null，
/// 调用方一律用 <c>?.</c>，从而把「更新模块挂了」限制在更新模块内部。
/// </para>
/// </summary>
public static class AppServices
{
    public static IConfigService? Config { get; set; }
    public static ILogService? Log { get; set; }
    public static IPathTemplateService? PathTemplate { get; set; }
    public static IImageService? Image { get; set; }
    public static IIgnoreSet? IgnoreSet { get; set; }
    public static IMonitorService? Monitor { get; set; }
    public static IAlertService? Alerts { get; set; }
    public static ISingleInstanceService? SingleInstance { get; set; }
    public static ITrayService? Tray { get; set; }

    // ── 更新与网络 ──
    public static IGitPortService? Proxy { get; set; }
    public static IPacService? Pac { get; set; }
    public static IProxyHttpClientProvider? ProxyHttp { get; set; }
    public static IUpdateService? Updates { get; set; }
    public static IUpdateCoordinator? UpdateCoordinator { get; set; }

    /// <summary>
    /// 请求主程序退出。由 <c>App</c> 注入实现。
    /// <para>
    /// 服务层不允许直接引用 <c>App</c> / <c>MainWindow</c>——那会让服务无法脱离 UI 单独编译与测试。
    /// </para>
    /// </summary>
    public static Action? ExitRequested { get; set; }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
