using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using PivotPic.Models;

namespace PivotPic.Services;

/// <summary>
/// 更新行为编排：加速组件启停、定时检查、提示节奏、下载后拉起安装程序。
/// <para>
/// 与 <see cref="IUpdateService"/>的分工：本类只管「什么时候做、做完怎么提示」，
/// 不碰 HTTP 细节，也不碰界面控件。
/// </para>
/// </summary>
internal sealed class UpdateCoordinator : IUpdateCoordinator
{
    /// <summary>定时器心跳间隔。真正的检查间隔由配置决定，这里只负责「到点没到点」。</summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(30);

    /// <summary>退出前留给界面渲染「正在启动安装程序…」的时间。</summary>
    private const int ExitDelayMs = 600;

    private readonly IUpdateService _updates;

    private System.Threading.Timer? _timer;
    private CancellationTokenSource? _checkCts;
    private ReleaseInfo? _available;
    private UpdateCheckOutcome _outcome = UpdateCheckOutcome.Unknown;
    private DateTime? _lastCheckUtc;
    private DateTime _lastAttemptUtc = DateTime.MinValue;
    private bool _checking;
    private bool _dismissed;
    private bool _promptRaised;
    private bool _disposed;

    public UpdateCoordinator(IUpdateService updates)
    {
        _updates = updates;
        _updates.UpdateAvailable += OnServiceUpdateAvailable;
    }

    /// <inheritdoc />
    public ReleaseInfo? AvailableUpdate => _available;

    /// <inheritdoc />
    public UpdateCheckOutcome LastOutcome => _outcome;

    /// <inheritdoc />
    public bool IsChecking => _checking;

    /// <inheritdoc />
    public bool HasAvailableUpdate => _available is not null;

    /// <inheritdoc />
    public bool ShouldAutoPrompt
    {
        get
        {
            if (!HasAvailableUpdate || _dismissed || _available is null)
            {
                return false;
            }

            var skipped = AppServices.Config?.Current.SkippedVersion ?? string.Empty;
            var current = UpdateDefaults.FormatVersion(_available.Version);
            return !string.Equals(skipped, current, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <inheritdoc />
    public event EventHandler<ReleaseInfo>? UpdateFound;

    /// <inheritdoc />
    public event EventHandler? StateChanged;

    /// <inheritdoc />
    public string LastCheckText
    {
        get
        {
            var timeText = _lastCheckUtc.HasValue
                ? _lastCheckUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                : "尚未检查";

            var outcomeText = _outcome switch
            {
                UpdateCheckOutcome.UpToDate => "已是最新",
                UpdateCheckOutcome.UpdateAvailable => _available is null
                    ? "发现新版本"
                    : $"发现新版本 {_available.TagName}",

                // 失败原因按类分细，不再一律说「网络失败」——404 是「还没发布」，不是网络问题。
                UpdateCheckOutcome.NetworkError => "网络不通",
                UpdateCheckOutcome.NoRelease => "暂无可用的发布信息",
                UpdateCheckOutcome.RateLimited => "请求过于频繁，稍后重试",
                UpdateCheckOutcome.Unrecognized => "发布信息无法识别",
                _ => "—"
            };

            return $"{timeText} · {outcomeText}";
        }
    }

    // ============================================================
    // 启动
    // ============================================================

    /// <inheritdoc />
    public void Start()
    {
        if (_disposed)
        {
            return;
        }

        _ = Task.Run(StartupAsync);
    }

    /// <summary>
    /// 启动编排：还原残留 PAC → 拉起加速 → 恢复待安装状态 → 延迟静默检查 → 开定时器。
    /// 全程不阻塞 UI，任何一步失败都只降级不抛。
    /// </summary>
    private async Task StartupAsync()
    {
        try
        {
            var config = AppServices.Config?.Current;
            _outcome = config?.LastCheckOutcome ?? UpdateCheckOutcome.Unknown;
            _lastCheckUtc = config?.LastCheckUtc;

            // 距上次检查的时间要从配置里的真实时间续算：
            // 否则每次重启都会在半小时后无条件触发一次检查，把启动检查 + 定时检查变成每天两次以上的打扰。
            _lastAttemptUtc = config?.LastCheckUtc ?? DateTime.MinValue;

            // 上次异常退出（任务管理器强杀）可能留下我们写的 PAC，先还回去。
            if (AppServices.Pac?.HasPendingBackup == true)
            {
                AppServices.Log?.Info("更新", "检测到上次遗留的系统代理设置，正在还原");
                await AppServices.Pac.RestoreAsync().ConfigureAwait(false);
            }

            // 加速组件：按配置的模式启动；失败自动降级直连，不影响后续检查。
            var mode = config?.ProxyMode ?? ProxyMode.Off;
            if (mode != ProxyMode.Off && AppServices.Proxy is not null)
            {
                await AppServices.Proxy.ApplyModeAsync(mode).ConfigureAwait(false);
            }

            // 重启前已经下完并校验过的包，不必等下一次网络检查就能提示。
            await TryResumePendingAsync().ConfigureAwait(false);

            if (config?.AutoUpdateCheck == true)
            {
                await Task.Delay(TimeSpan.FromSeconds(UpdateDefaults.StartupDelaySeconds))
                    .ConfigureAwait(false);

                await RunCheckAsync(UpdateCheckTrigger.Silent).ConfigureAwait(false);
            }

            StartTimer();
        }
        catch (Exception ex)
        {
            AppServices.Log?.Warn("更新", $"更新模块启动异常：{ex.Message}");
        }
    }

    private void StartTimer()
    {
        if (_disposed)
        {
            return;
        }

        _timer?.Dispose();
        // System.Threading.Timer 必须写全名：服务层会被测试工程放进同时启用 WinForms 的项目里独立编译。
        _timer = new System.Threading.Timer(
            _ => OnTimerTick(),
            null,
            TickInterval,
            TickInterval);
    }

    /// <summary>定时器只判断「距离上次检查是否够久」，不直接算下一次的绝对时间。</summary>
    private void OnTimerTick()
    {
        if (_disposed || _checking)
        {
            return;
        }

        var config = AppServices.Config?.Current;
        if (config?.AutoUpdateCheck != true)
        {
            return;
        }

        var intervalHours = Math.Clamp(config.UpdateCheckIntervalHours, 1, 168);
        var elapsed = DateTime.UtcNow - _lastAttemptUtc;

        if (elapsed < TimeSpan.FromHours(intervalHours))
        {
            return;
        }

        _ = RunCheckAsync(UpdateCheckTrigger.Silent);
    }

    // ============================================================
    // 检查
    // ============================================================

    /// <inheritdoc />
    public async Task<ReleaseInfo?> CheckNowAsync()
    {
        if (_disposed)
        {
            return null;
        }

        if (_checking)
        {
            // 已经在检查：不重复发请求，直接把当前已知结果给界面。
            return _available;
        }

        return await RunCheckAsync(UpdateCheckTrigger.Manual).ConfigureAwait(false);
    }

    /// <summary>执行一次检查并同步内部状态。手动检查的失败原因会转成文案，不向 UI 抛异常。</summary>
    private async Task<ReleaseInfo?> RunCheckAsync(UpdateCheckTrigger trigger)
    {
        _checking = true;
        _lastAttemptUtc = DateTime.UtcNow;
        RaiseStateChanged();

        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        _checkCts = cts;

        try
        {
            var release = await _updates.CheckAsync(trigger, cts.Token).ConfigureAwait(false);

            _outcome = _updates.LastOutcome;
            _lastCheckUtc = DateTime.UtcNow;

            if (release is not null)
            {
                _available = release;

                // 静默检查由 UpdateAvailable 事件驱动提示；这里兜底一次，
                // 防止服务实现变了之后提示静默消失。
                // NotifyFound 会碰 UI 对象，必须回到 UI 线程（否则每轮静默检查都会
                // 留一条"调用线程无法访问此对象"的告警）。
                if (trigger == UpdateCheckTrigger.Silent)
                {
                    OnUi(() => NotifyFound(release));
                }
            }
            else if (_outcome == UpdateCheckOutcome.UpToDate)
            {
                _available = null;
            }

            return release;
        }
        catch (UpdateFailedException ex)
        {
            // 保留服务层给出的归类（404 / 限流 / 无法识别 / 网络不通），不要一律压成「网络失败」。
            _outcome = ex.Outcome;
            _lastCheckUtc = DateTime.UtcNow;
            AppServices.Log?.Warn("更新", $"检查更新失败（{ex.Outcome}）：{ex.Message}");
            return null;
        }
        catch (OperationCanceledException)
        {
            AppServices.Log?.Info("更新", "检查更新已取消");
            return null;
        }
        catch (Exception ex)
        {
            _outcome = UpdateCheckOutcome.NetworkError;
            _lastCheckUtc = DateTime.UtcNow;
            AppServices.Log?.Warn("更新", $"检查更新异常：{ex.Message}");
            return null;
        }
        finally
        {
            _checking = false;
            _checkCts = null;
            cts.Dispose();
            RaiseStateChanged();
        }
    }

    private void OnServiceUpdateAvailable(object? sender, ReleaseInfo release)
    {
        OnUi(() =>
        {
            _available = release;
            _outcome = UpdateCheckOutcome.UpdateAvailable;
            NotifyFound(release);
        });
    }

    /// <summary>发现新版本：刷新状态，必要时抛事件让主窗弹窗（每次会话只弹一次）。</summary>
    private void NotifyFound(ReleaseInfo release)
    {
        RaiseStateChanged();

        if (!ShouldAutoPrompt)
        {
            // 用户已经明确表示过不想看这个版本：只留横幅/托盘入口。
            AppServices.Log?.Info("更新", $"版本 {release.TagName} 已被用户跳过，仅保留入口");
            return;
        }

        // 事件驱动与兜底两条路径都可能走到这里，只让第一次真正弹窗，
        // 否则用户会看到叠起来的两个模态窗。
        if (_promptRaised)
        {
            return;
        }

        _promptRaised = true;

        try
        {
            UpdateFound?.Invoke(this, release);
        }
        catch (Exception ex)
        {
            AppServices.Log?.Warn("更新", $"更新提示处理异常：{ex.Message}");
        }
    }

    // ============================================================
    // 提示节奏
    // ============================================================

    /// <inheritdoc />
    public void DismissForSession()
    {
        _dismissed = true;
        RaiseStateChanged();
    }

    /// <inheritdoc />
    public void SkipVersion(string versionText)
    {
        var normalized = UpdateDefaults.ParseTag(versionText) is { } parsed
            ? UpdateDefaults.FormatVersion(parsed)
            : versionText;

        try
        {
            var config = AppServices.Config;
            if (config is not null)
            {
                config.Current.SkippedVersion = normalized;
                config.SaveDebounced();
            }
        }
        catch (Exception ex)
        {
            AppServices.Log?.Warn("更新", $"保存跳过版本失败：{ex.Message}");
        }

        _dismissed = true;
        RaiseStateChanged();
        AppServices.Log?.Info("更新", $"用户不再提示版本 {normalized}");
    }

    /// <inheritdoc />
    public Task<bool> TryResumePendingAsync()
    {
        try
        {
            if (!File.Exists(GitPortPaths.PendingPackagePath))
            {
                return Task.FromResult(false);
            }

            var node = JsonNode.Parse(File.ReadAllText(GitPortPaths.PendingPackagePath)) as JsonObject;
            if (node is null)
            {
                return Task.FromResult(false);
            }

            var path = node["path"]?.GetValue<string?>();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return Task.FromResult(false);
            }

            var cached = _updates.GetCachedRelease();
            if (cached is null)
            {
                return Task.FromResult(false);
            }

            var current = Version.TryParse(AppInfo.Version, out var version) ? version : new Version(0, 0, 0);
            if (cached.Version <= current)
            {
                return Task.FromResult(false);
            }

            _available = cached;
            _outcome = UpdateCheckOutcome.UpdateAvailable;
            AppServices.Log?.Info("更新", $"发现已下载的安装包 {cached.TagName}，无需重新下载");
            RaiseStateChanged();
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            AppServices.Log?.Warn("更新", $"读取待安装记录失败：{ex.Message}");
            return Task.FromResult(false);
        }
    }

    // ============================================================
    // 下载并安装
    // ============================================================

    /// <inheritdoc />
    public async Task<string> DownloadAndLaunchAsync(
        ReleaseInfo release, IProgress<UpdateProgress> progress, CancellationToken cancellationToken)
    {
        var packagePath = await _updates
            .DownloadAndVerifyAsync(release, progress, cancellationToken)
            .ConfigureAwait(false);

        if (!File.Exists(packagePath))
        {
            throw new UpdateFailedException("安装包不见了，请重试", $"下载后文件不存在：{packagePath}");
        }

        var processId = Environment.ProcessId;

        try
        {
            Process.Start(new ProcessStartInfo(packagePath, $"--wait-pid {processId}")
            {
                // UseShellExecute=true 才能触发安装程序的 UAC 提权提示。
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(packagePath) ?? Path.GetTempPath()
            });
        }
        catch (Exception ex)
        {
            throw new UpdateFailedException("安装程序启动失败，请手动下载", $"启动安装程序失败：{ex.Message}", ex);
        }

        AppServices.Log?.Info("更新", $"已启动安装程序，准备退出当前版本（{packagePath}）");

        // 给界面一点时间把「正在启动安装程序…」画出来，再走正常退出流程。
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(ExitDelayMs).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 忽略。
            }

            // 通过注入的委托退出，服务层不引用 App / MainWindow（否则无法独立编译与测试）。
            OnUi(() => AppServices.ExitRequested?.Invoke());
        });

        return packagePath;
    }

    // ============================================================
    // 收尾
    // ============================================================

    /// <summary>把动作切回 UI 线程；没有 Application（单元测试）时直接执行。</summary>
    private static void OnUi(Action action)
    {
        // 写全名：同时启用 WinForms 时裸写 Application 会与 System.Windows.Forms.Application 歧义。
        var dispatcher = System.Windows.Application.Current?.Dispatcher;

        if (dispatcher is null)
        {
            action();
            return;
        }

        if (dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }

    private void RaiseStateChanged()
    {
        OnUi(() =>
        {
            try
            {
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception)
            {
                // 订阅方异常不得打断状态机。
            }
        });
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _updates.UpdateAvailable -= OnServiceUpdateAvailable;
        }
        catch (Exception)
        {
            // 忽略。
        }

        try
        {
            _checkCts?.Cancel();
        }
        catch (Exception)
        {
            // 忽略。
        }

        _timer?.Dispose();
        _timer = null;
    }
}
