using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using PivotPic.Models;

namespace PivotPic.Services;

/// <summary>
/// 内置加速组件的进程生命周期。
/// <para>
/// 组件以 <c>-headless</c> 启动，纯透传（不解密 TLS、不装证书），只监听 <c>127.0.0.1</c>。
/// 界面上永远称它「内置加速」，本类负责把它的各种技术失败翻译成
/// <see cref="ProxyFailureKind"/>，界面据此显示人话。
/// </para>
/// <para>
/// 崩溃自愈：进程意外退出后最多重启 2 次（5s / 15s 退避），仍失败则降级直连。
/// 降级不抛异常、不弹窗，只把状态推给订阅方——加速失败绝不能影响图片处理主流程。
/// </para>
/// </summary>
internal sealed class GitPortService : IGitPortService
{
    /// <summary>启动参数。组件支持无界面运行，这样不会冒出第二个窗口或托盘图标。</summary>
    private const string HeadlessArgument = "-headless";

    /// <summary>等待健康检查通过的最长时间。</summary>
    private const int ReadyTimeoutMs = 8000;

    /// <summary>健康检查（心跳）间隔。</summary>
    private const int HeartbeatIntervalMs = 30_000;

    /// <summary>单次端口连通性探测的超时。</summary>
    private const int ProbeTimeoutMs = 800;

    /// <summary>最多自动重启次数。</summary>
    private const int MaxRestarts = 2;

    /// <summary>每次重启前的等待时间，按序取用。</summary>
    private static readonly int[] RestartDelaysMs = { 5000, 15_000 };

    /// <summary>稳定运行多久后把「自动重启预算」清零。</summary>
    private static readonly TimeSpan StableResetThreshold = TimeSpan.FromSeconds(60);

    /// <summary>串行化启停，避免用户连点开关时起两个进程。</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>把子进程的标准输出/错误转发到日志文件，排查启动失败时唯一的线索来源。</summary>
    private readonly ConcurrentQueue<string> _processLog = new();

    /// <summary>保护 <see cref="_health"/> 的专用锁对象（不复用队列，避免语义混淆）。</summary>
    private readonly object _stateGate = new();

    private Process? _process;
    private System.Threading.Timer? _heartbeat;
    private ProxyHealth _health = ProxyHealth.Off;
    private int _restartCount;

    /// <summary>上一次「确认健康」的时间，用于稳定期后重置重启计数。</summary>
    private DateTime _lastHealthyUtc = DateTime.UtcNow;

    private bool _stopping;
    private bool _disposed;

    /// <inheritdoc />
    public ProxyHealth Current
    {
        get
        {
            lock (_stateGate)
            {
                return _health;
            }
        }
    }

    /// <inheritdoc />
    public event EventHandler<ProxyHealth>? HealthChanged;

    // ============================================================
    // 对外：启动 / 停止 / 切模式 / 探活
    // ============================================================

    /// <inheritdoc />
    public async Task<ProxyHealth> StartAsync(bool enableSystemPac, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return Current;
            }

            if (Current.IsRunning)
            {
                return Current;
            }

            _stopping = false;
            _restartCount = 0;
            return await StartCoreAsync(enableSystemPac, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _stopping = true;
            StopHeartbeat();
            KillProcess();

            SetHealth(new ProxyHealth
            {
                Status = ProxyStatus.Off,
                Failure = ProxyFailureKind.Stopped,
                Mode = ProxyMode.Off,
                LastCheckUtc = DateTime.UtcNow
            });
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> ApplyModeAsync(ProxyMode mode, CancellationToken cancellationToken = default)
    {
        if (mode == ProxyMode.Off)
        {
            await StopAsync(cancellationToken).ConfigureAwait(false);

            // 关闭加速时立刻还 PAC：不给用户留一个指向已停端口的系统代理。
            if (AppServices.Pac is not null)
            {
                await AppServices.Pac.RestoreAsync(cancellationToken).ConfigureAwait(false);
            }

            return true;
        }

        var wantSystemPac = mode == ProxyMode.SystemWide;

        // 关键顺序：先停再改再起。
        // githubPAC / mitm 只在组件启动时读取一次，若在运行中直接改设置文件再 StartAsync，
        // StartAsync 会因为「已在运行」而早退，设置根本没生效——表现为「开关点了没用，要重启程序」。
        await StopAsync(cancellationToken).ConfigureAwait(false);

        // 系统分流要先留基线快照，否则退出时无从还原。已有快照就沿用更早的那份。
        if (wantSystemPac && AppServices.Pac is not null && !AppServices.Pac.HasPendingBackup)
        {
            await AppServices.Pac.CaptureBaselineAsync(cancellationToken).ConfigureAwait(false);
        }

        var health = await StartAsync(wantSystemPac, cancellationToken).ConfigureAwait(false);
        if (!health.IsRunning)
        {
            // 启动失败也必须收尾：组件可能在退出前已经把 PAC 写进系统设置，
            // 留着就是一个指向死端口的系统代理（用户会以为「GitHub 打不开了」）。
            if (wantSystemPac && AppServices.Pac is not null)
            {
                await AppServices.Pac.RestoreAsync(cancellationToken).ConfigureAwait(false);
            }

            return false;
        }

        if (!wantSystemPac)
        {
            // 降级档：确保系统里没有我们留下的 PAC。
            if (AppServices.Pac is not null)
            {
                await AppServices.Pac.RestoreAsync(cancellationToken).ConfigureAwait(false);
            }

            SetHealth(new ProxyHealth
            {
                Status = ProxyStatus.RunningProcess,
                Port = health.Port,
                ProcessId = health.ProcessId,
                Mode = ProxyMode.Passthrough,
                LastCheckUtc = DateTime.UtcNow
            });

            return true;
        }

        // 启动成功但 PAC 没写进去：不算失败，降级为进程内加速即可。
        var verified = AppServices.Pac is not null
                       && await AppServices.Pac.VerifyAsync(health.Port, cancellationToken).ConfigureAwait(false);

        if (!verified)
        {
            AppServices.Log?.Warn("加速", "系统代理未能指向本机加速端口，降级为仅本程序加速");
            SetHealth(new ProxyHealth
            {
                Status = ProxyStatus.RunningProcess,
                Port = health.Port,
                ProcessId = health.ProcessId,
                Mode = ProxyMode.Passthrough,
                LastCheckUtc = DateTime.UtcNow
            });
        }

        return true;
    }

    /// <inheritdoc />
    public async Task<bool> PingAsync(CancellationToken cancellationToken = default)
    {
        var process = _process;
        var port = Current.Port;

        if (process is null || port <= 0)
        {
            return false;
        }

        try
        {
            if (process.HasExited)
            {
                return false;
            }
        }
        catch (Exception)
        {
            return false;
        }

        return await ProbePortAsync(port, ProbeTimeoutMs, cancellationToken).ConfigureAwait(false);
    }

    // ============================================================
    // 内部：启动流程
    // ============================================================

    private async Task<ProxyHealth> StartCoreAsync(bool enableSystemPac, CancellationToken cancellationToken)
    {
        SetHealth(new ProxyHealth
        {
            Status = ProxyStatus.Starting,
            Mode = enableSystemPac ? ProxyMode.SystemWide : ProxyMode.Passthrough,
            LastCheckUtc = DateTime.UtcNow
        });

        // 1) 二进制就位（SHA256 比对，命中就复用，不每次复制 7 MB）
        var binaryReady = await GitPortPaths.EnsureBinaryAsync(cancellationToken).ConfigureAwait(false);
        if (!binaryReady)
        {
            return Fail(ProxyFailureKind.BinaryMissing, "加速组件缺失或校验失败", enableSystemPac);
        }

        // 2) 端口：组件固定用 7810。先清掉自己上一次的残留，再看端口是否被他人占用。
        KillOwnLeftovers();

        var port = UpdateDefaults.ProxyPort;
        if (!IsPortFree(port))
        {
            return Fail(ProxyFailureKind.PortBusy, $"端口 {port} 被其它程序占用", enableSystemPac);
        }

        // 3) 写设置：永远 mitm=false（纯透传），githubPAC 由模式决定
        if (!GitPortPaths.PatchSettings(enableSystemPac))
        {
            return Fail(ProxyFailureKind.BinaryMissing, "无法写入加速组件设置", enableSystemPac);
        }

        // 4) 拉起进程
        try
        {
            LaunchProcess();
        }
        catch (Exception ex)
        {
            return Fail(ProxyFailureKind.StartTimeout, $"启动进程失败：{ex.Message}", enableSystemPac);
        }

        // 5) 等健康检查通过
        var ready = await WaitUntilReadyAsync(port, ReadyTimeoutMs, cancellationToken).ConfigureAwait(false);
        if (!ready)
        {
            KillProcess();
            return Fail(ProxyFailureKind.StartTimeout, $"{ReadyTimeoutMs}ms 内未通过健康检查", enableSystemPac);
        }

        var health = new ProxyHealth
        {
            Status = enableSystemPac ? ProxyStatus.RunningSystem : ProxyStatus.RunningProcess,
            Port = port,
            ProcessId = SafeProcessId(),
            Mode = enableSystemPac ? ProxyMode.SystemWide : ProxyMode.Passthrough,
            LastCheckUtc = DateTime.UtcNow,
            RestartCount = _restartCount
        };

        SetHealth(health);
        _lastHealthyUtc = DateTime.UtcNow;
        StartHeartbeat();
        AppServices.Log?.Info("加速", $"内置加速已就绪，端口 {port}");
        return health;
    }

    /// <summary>创建并启动子进程，同时把输出接到日志队列。</summary>
    private void LaunchProcess()
    {
        Directory.CreateDirectory(GitPortPaths.WorkingDirectory);

        var startInfo = new ProcessStartInfo(GitPortPaths.BinaryPath, HeadlessArgument)
        {
            WorkingDirectory = GitPortPaths.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => PumpProcessLog(e.Data);
        process.ErrorDataReceived += (_, e) => PumpProcessLog(e.Data);
        process.Exited += OnProcessExited;

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        _process = process;
    }

    /// <summary>把子进程输出收进队列并落盘，避免管道缓冲区写满导致子进程卡死。</summary>
    private void PumpProcessLog(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        _processLog.Enqueue(line);
        while (_processLog.Count > 200 && _processLog.TryDequeue(out _))
        {
            // 只保留最近 200 行，避免长期运行时内存缓慢增长。
        }

        FlushProcessLog(line);
    }

    /// <summary>把一行输出追加到 proxy.log；失败忽略（日志不是主流程）。</summary>
    private static void FlushProcessLog(string line)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(GitPortPaths.ProxyLogPath)!);
            File.AppendAllText(GitPortPaths.ProxyLogPath, $"{DateTime.Now:HH:mm:ss} {line}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // 加速组件的日志写不进去不影响它的运行。
        }
    }

    /// <summary>轮询端口连通性直到就绪或超时；期间进程退出立即判失败。</summary>
    private async Task<bool> WaitUntilReadyAsync(int port, int timeoutMs, CancellationToken cancellationToken)
    {
        var deadline = Environment.TickCount64 + timeoutMs;

        while (Environment.TickCount64 < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var process = _process;
            if (process is null)
            {
                return false;
            }

            try
            {
                if (process.HasExited)
                {
                    return false;
                }
            }
            catch (Exception)
            {
                return false;
            }

            if (await ProbePortAsync(port, ProbeTimeoutMs, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            try
            {
                await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>探测 127.0.0.1:port 是否可连接。</summary>
    private static async Task<bool> ProbePortAsync(int port, int timeoutMs, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(timeoutMs);

            await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 清掉上一次运行留下的、属于我们自己那份二进制的进程。
    /// <para>
    /// 只杀「布局属于本软件」的进程（见 <see cref="IsOwnBinaryPath"/>）：
    /// 用户可能自己也装了同款工具，杀错会破坏别人的代理。
    /// </para>
    /// </summary>
    private static void KillOwnLeftovers()
    {
        Process[] processes;

        try
        {
            processes = Process.GetProcessesByName(UpdateDefaults.ProxyProcessName);
        }
        catch (Exception)
        {
            return;
        }

        foreach (var process in processes)
        {
            try
            {
                if (process.HasExited)
                {
                    continue;
                }

                var path = process.MainModule?.FileName;
                if (!IsOwnBinaryPath(path))
                {
                    continue;
                }

                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
                AppServices.Log?.Info("加速", "已清理上次遗留的加速组件进程");
            }
            catch (Exception)
            {
                // 拿不到 MainModule（位数不同 / 权限不足）时不动它，交给端口占用判定处理。
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    /// <summary>
    /// 判断某个进程是不是「我们自己那份」加速组件。
    /// <para>
    /// 不能只比对 <see cref="GitPortPaths.BinaryPath"/>：调试版、绿色版、安装版各自有一份
    /// <c>proxy\</c> 目录，换了位置（或升级后）留下的旧实例路径就变了。这类残留若不被清理，
    /// 新实例启动时组件会弹出一个「已在运行（请看系统托盘）」的消息框——用户看到只会莫名其妙。
    /// </para>
    /// <para>
    /// 因此按布局特征识别：路径必须以 <c>\proxy\gitport-github.exe</c> 结尾。
    /// 用户自己在别处安装的同款工具路径不匹配，不会被误杀。
    /// </para>
    /// </summary>
    private static bool IsOwnBinaryPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (string.Equals(path, GitPortPaths.BinaryPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var normalized = path.Replace('/', '\\');
        var suffix = @"\proxy\" + UpdateDefaults.ProxyProcessName + ".exe";
        return normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>试绑 127.0.0.1:port 判断是否空闲。绑定成功即释放，中间不留监听。</summary>
    private static bool IsPortFree(int port)
    {
        TcpListener? listener = null;

        try
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            try
            {
                listener?.Stop();
            }
            catch (Exception)
            {
                // 忽略。
            }
        }
    }

    // ============================================================
    // 内部：心跳与自愈
    // ============================================================

    private void StartHeartbeat()
    {
        StopHeartbeat();

        // 必须写全名 System.Threading.Timer：本项目同时启用 WinForms，
        // 裸写 Timer 会与 System.Windows.Forms.Timer 歧义（服务层被测试工程独立编译时尤其明显）。
        _heartbeat = new System.Threading.Timer(
            async _ => await HeartbeatTickAsync().ConfigureAwait(false),
            null,
            HeartbeatIntervalMs,
            HeartbeatIntervalMs);
    }

    private void StopHeartbeat()
    {
        var timer = Interlocked.Exchange(ref _heartbeat, null);
        timer?.Dispose();
    }

    /// <summary>心跳：进程还在但端口不通，视同崩溃。</summary>
    private async Task HeartbeatTickAsync()
    {
        if (_disposed || _stopping)
        {
            return;
        }

        var health = Current;
        if (!health.IsRunning)
        {
            return;
        }

        if (await PingAsync().ConfigureAwait(false))
        {
            // 稳定运行满 60 秒就把重启预算清零：
            // 否则常驻一整天偶发崩 3 次就会永久降级，直到用户重启程序。
            if (_restartCount > 0
                && DateTime.UtcNow - _lastHealthyUtc >= StableResetThreshold)
            {
                AppServices.Log?.Info("加速", $"已稳定运行 {StableResetThreshold.TotalSeconds:0} 秒，重置自动重启计数");
                _restartCount = 0;
            }

            return;
        }

        AppServices.Log?.Warn("加速", "心跳失败，尝试重启加速组件");
        await TryAutoRestartAsync().ConfigureAwait(false);
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        if (_disposed || _stopping)
        {
            return;
        }

        var health = Current;
        if (!health.IsRunning)
        {
            // 启动阶段就退出，交给 WaitUntilReadyAsync 判失败，这里不重复处理。
            return;
        }

        AppServices.Log?.Warn("加速", "加速组件意外退出");
        _ = TryAutoRestartAsync();
    }

    /// <summary>按退避序列重启；用尽次数后降级直连。</summary>
    private async Task TryAutoRestartAsync()
    {
        if (_disposed || _stopping)
        {
            return;
        }

        if (!await _gate.WaitAsync(0).ConfigureAwait(false))
        {
            // 已有启停在进行中，交给它处理。
            return;
        }

        try
        {
            if (_restartCount >= MaxRestarts)
            {
                StopHeartbeat();
                KillProcess();

                // 重启预算用尽 = 彻底降级直连。此时必须把系统代理还回去，
                // 否则用户会带着一个指向死端口的 PAC（GitHub 全站打不开，且与图片功能无关）。
                var wasSystemPac = Current.Mode == ProxyMode.SystemWide;
                Fail(ProxyFailureKind.Crashed, $"已自动重启 {_restartCount} 次仍失败", wasSystemPac);

                if (wasSystemPac && AppServices.Pac is not null)
                {
                    await AppServices.Pac.RestoreAsync(CancellationToken.None).ConfigureAwait(false);
                }

                return;
            }

            var delay = RestartDelaysMs[Math.Min(_restartCount, RestartDelaysMs.Length - 1)];
            _restartCount++;

            StopHeartbeat();
            KillProcess();

            SetHealth(new ProxyHealth
            {
                Status = ProxyStatus.Starting,
                Mode = Current.Mode,
                RestartCount = _restartCount,
                LastCheckUtc = DateTime.UtcNow
            });

            try
            {
                await Task.Delay(delay).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 退出竞态，继续尝试一次即可。
            }

            var enableSystemPac = Current.Mode == ProxyMode.SystemWide;
            var health = await StartCoreAsync(enableSystemPac, CancellationToken.None).ConfigureAwait(false);

            if (health.IsRunning)
            {
                AppServices.Log?.Info("加速", $"加速组件第 {_restartCount} 次重启成功");
            }
        }
        catch (Exception ex)
        {
            AppServices.Log?.Warn("加速", $"自动重启失败：{ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    // ============================================================
    // 内部：收尾
    // ============================================================

    private void KillProcess()
    {
        var process = Interlocked.Exchange(ref _process, null);
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
        }
        catch (Exception)
        {
            // 已退出或无权限时忽略。
        }
        finally
        {
            try
            {
                process.Exited -= OnProcessExited;
                process.Dispose();
            }
            catch (Exception)
            {
                // 忽略。
            }
        }
    }

    private int SafeProcessId()
    {
        try
        {
            return _process?.Id ?? 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>把状态置为失败并返回快照。</summary>
    private ProxyHealth Fail(ProxyFailureKind kind, string detail, bool wasSystemPac)
    {
        var health = new ProxyHealth
        {
            Status = ProxyStatus.Failed,
            Failure = kind,
            Detail = detail,
            Mode = wasSystemPac ? ProxyMode.SystemWide : ProxyMode.Passthrough,
            RestartCount = _restartCount,
            LastCheckUtc = DateTime.UtcNow
        };

        SetHealth(health);
        AppServices.Log?.Warn("加速", $"内置加速不可用（{kind}）：{detail}");
        return health;
    }

    /// <summary>更新快照并通知订阅方。通知在锁外发出，避免订阅方回调查状态时死锁。</summary>
    private void SetHealth(ProxyHealth health)
    {
        lock (_stateGate)
        {
            _health = health;
        }

        try
        {
            HealthChanged?.Invoke(this, health);
        }
        catch (Exception)
        {
            // 订阅方异常不得影响加速组件的状态机。
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopping = true;

        StopHeartbeat();
        KillProcess();

        try
        {
            _gate.Dispose();
        }
        catch (Exception)
        {
            // 忽略。
        }
    }
}
