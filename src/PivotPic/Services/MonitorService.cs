using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using PivotPic.Models;

namespace PivotPic.Services;

/// <summary>多目录监控与全量扫描：FileSystemWatcher 只做过滤入队，单消费者后台队列串行处理。</summary>
public sealed class MonitorService : IMonitorService, IDisposable
{
    /// <summary>新文件落地后先等 400ms，给写盘方留出完成时间。</summary>
    private const int StabilityDelayMs = 400;

    /// <summary>两次采样的间隔。</summary>
    private const int StabilitySampleGapMs = 250;

    /// <summary>稳定性检测最多重试轮数。</summary>
    private const int StabilityAttempts = 3;

    /// <summary>传输未完成时最多重试几次（每次间隔 RetryDelayMs）。</summary>
    private const int MaxStabilityRetries = 5;

    /// <summary>重试前等待的时间，给慢速传输更多余量。</summary>
    private const int RetryDelayMs = 1200;

    /// <summary>同路径事件合并窗口：1 秒内重复事件只入队一次。</summary>
    /// <summary>
    /// 同一路径的事件合并窗口。图片落盘时往往连续抛出 Created + 多次 Changed，
    /// 窗口太短会让同一张图重复入队，进而让进度条的分母虚高。
    /// </summary>
    private const int MergeWindowMs = 3000;

    /// <summary>监控失效后的重挂延迟。</summary>
    private const int RemountDelayMs = 30_000;

    /// <summary>
    /// 启动阶段路径不可用（开机自启时映射的网络盘尚未就绪）时的最大重试次数。
    /// 每次间隔 <see cref="RemountDelayMs"/>，20 次约 10 分钟，足够覆盖慢速登录、
    /// VPN 拨号与工作组机器等"盘符晚几分钟才上线"的常见场景；再往后交给用户手动开启。
    /// </summary>
    public const int MaxMountRetries = 20;

    /// <summary>合并表超过该规模才做一次过期清理。</summary>
    private const int MergeCompactThreshold = 512;

    /// <summary>扫描时每处理多少个文件让出一次线程。</summary>
    private const int YieldEvery = 50;

    /// <summary>扫描时每处理多少个文件上报一次进度。</summary>
    private const int ProgressEvery = 25;

    /// <summary>配置尚未就绪时的兜底扩展名白名单。</summary>
    private static readonly string[] FallbackExtensions =
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".tif", ".tiff"
    };

    private readonly Channel<WorkItem> _queue;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private readonly ConcurrentDictionary<string, FolderRuntime> _runtimes =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 路径暂不可用（多为映射网络盘未就绪）的目录登记表。
    /// 与 _runtimes 分开维护：路径不存在时 FileSystemWatcher 根本构造不出来，
    /// 只能用轻量的 <see cref="PendingMount"/> 记住"这个目录还欠一次挂载"，
    /// 由一次性 Timer 到点回来重试。开机自启（--tray）漏扫描的根因就在这里。
    /// </summary>
    private readonly ConcurrentDictionary<string, PendingMount> _pending =
        new(StringComparer.OrdinalIgnoreCase);
    /// <summary>一批处理结束的空闲宽限期：这么久没有新文件进队列就判定本批完成。</summary>
    private static readonly TimeSpan BatchIdleGrace = TimeSpan.FromMilliseconds(1600);

    /// <summary>
    /// 超过这个体积就算「大文件」，开始处理时单独记一行日志。
    /// 作用是给现场留证据：真出现「卡住」时，日志里至少能看到卡在哪一张上。
    /// </summary>
    private const long BigFileLogThresholdBytes = 20L * 1024 * 1024;

    /// <summary>单张图耗时超过这个值就记一条「慢图」警告，便于定位是哪一类图拖慢了整批。</summary>
    private const long SlowImageThresholdMs = 3000;

    private readonly object _batchLock = new();
    private int _batchTotal;
    private int _batchDone;
    private int _batchRotated;
    private CancellationTokenSource? _batchIdleCts;

    // 进度口径按"独立文件"计数，不按事件次数。同一文件的 Created/Changed/SizeChanged
    // 会接连触发多次，若每次都在 total 上 +1，进度条分母会虚高到用户看不懂。
    private readonly ConcurrentDictionary<string, byte> _batchFiles = new();
    private readonly ConcurrentDictionary<string, byte> _batchProcessedFiles = new();

    private readonly ConcurrentDictionary<string, DateTime> _recentEnqueues =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>上次清理去重表的时间；避免每个文件事件都全表扫一遍（O(n²)）。</summary>
    private DateTime _lastCompactUtc = DateTime.MinValue;

    private readonly ConcurrentDictionary<string, CancellationTokenSource> _scans =
        new(StringComparer.OrdinalIgnoreCase);

    private volatile bool _running;
    private bool _disposed;

    /// <summary>是否有文件正在处理中（消费者线程写入，批次定时器线程读取）。</summary>
    private volatile bool _isProcessing;

    /// <summary>
    /// 手动维护的队列深度：TryEnqueue 写入成功 +1，HandleItemAsync finally -1。
    /// 不能用 ChannelReader.Count——在某些运行时环境下它抛 NotSupportedException。
    /// </summary>
    private int _queueDepth;

    // ==================== 两阶段管线：先检测计数，再处理 ====================

    /// <summary>待检测的候选文件（已过扩展名过滤与去重）。</summary>
    private readonly ConcurrentQueue<WorkItem> _candidates = new();

    /// <summary>文件事件静默这么久就统一做一次检测，避免逐个文件反复起批。</summary>
    private static readonly TimeSpan DetectQuietWindow = TimeSpan.FromMilliseconds(700);

    /// <summary>最后一次写入候选队列的时刻（UTC Ticks）。</summary>
    private long _lastCandidateTicks = DateTime.UtcNow.Ticks;

    /// <summary>正在进行的全量扫描数量；扫描期间暂不派发，保证整批一次算清分母。</summary>
    private int _scansInProgress;

    /// <summary>检测是否正在跑，避免重入。</summary>
    private int _detecting;

    /// <summary>检测阶段的并发闸门：解图片头是 IO，并行 6 路足够快又不会打满磁盘。</summary>
    private readonly SemaphoreSlim _detectGate = new(6);

    private System.Threading.Timer? _detectTimer;

    /// <summary>创建服务并启动单消费者后台队列。</summary>
    public MonitorService()
    {
        _queue = Channel.CreateUnbounded<WorkItem>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

        _worker = Task.Run(ConsumeLoopAsync);

        // 200ms 检查一次：候选队列静默满 700ms（且没有扫描在跑）就统一检测。
        _detectTimer = new System.Threading.Timer(_ => OnDetectTick(), null, 200, 200);
    }

    /// <inheritdoc />
    public event EventHandler<ScanProgress>? ScanProgressChanged;

    /// <inheritdoc />
    public event EventHandler<string>? FolderStateChanged;

    /// <inheritdoc />
    public event EventHandler<BatchProgress>? BatchProgressChanged;

    /// <inheritdoc />
    public bool IsRunning => _running && _runtimes.Count > 0;

    /// <inheritdoc />
    public bool IsFolderBusy(string folderId)
    {
        return !string.IsNullOrWhiteSpace(folderId) && _scans.ContainsKey(folderId);
    }

    /// <inheritdoc />
    public void StartAll()
    {
        IReadOnlyList<WatchedFolder>? folders = null;

        try
        {
            folders = AppServices.Config?.Current?.Folders;
        }
        catch (Exception ex)
        {
            AppServices.Log?.Warn("监控", $"读取配置失败，未启动任何监控：{ex.Message}");
        }

        if (folders is null)
        {
            return;
        }

        foreach (var folder in folders)
        {
            if (folder is null || !folder.Enabled)
            {
                continue;
            }

            StartFolder(folder);
        }

        _running = _runtimes.Count > 0;

        // 开启监控时补扫一遍：关闭期间进来的图片不会产生文件事件，不扫就永久漏处理。
        // 后台执行，不阻塞调用方（启动流程或开关回调都在 UI 线程上）。
        foreach (var folder in folders)
        {
            if (folder is null || !folder.Enabled)
            {
                continue;
            }

            var target = folder;
            _ = Task.Run(async () =>
            {
                try
                {
                    await ScanAsync(target, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    AppServices.Log?.Warn("扫描", $"开启监控补扫失败：{ex.Message}", target.Path);
                }
            });
        }
    }

    /// <inheritdoc />
    public void StopAll()
    {
        // Keys 返回快照，遍历期间移除是安全的。
        foreach (var folderId in _runtimes.Keys)
        {
            StopFolderCore(folderId, cancelPending: false);
        }

        // 待重试登记必须一并注销：否则对象释放后仍有 Timer 在后台触发，
        // 把已经停止的目录重新挂回来（Dispose 之后尤其危险）。
        foreach (var folderId in _pending.Keys)
        {
            CancelPendingMount(folderId);
        }

        _running = false;
    }

    /// <inheritdoc />
    public void StartFolder(WatchedFolder folder)
    {
        // 契约上非空，但调用方来自界面与启动流程，早退比让下层抛 NRE 更安全。
        if (folder is null)
        {
            return;
        }

        // 调用方（启动流程 / 界面开关）主动开启：视为一次全新的开始，
        // 重试计数归零，之前残留的待重试登记作废。
        // 但 resetRetry 会先注销登记，若此刻网络盘刚好恢复，StartFolderCore 只能返回
        // Watching（而不是 WatchingResumed），补扫就会被漏掉——改「包含子文件夹」时
        // 走的正是这条路（FolderItemViewModel.RemountIfMonitoring 只调 StartFolder，
        // 不调 StartScan）。所以必须在调用**之前**取快照。
        var hadPending = !string.IsNullOrWhiteSpace(folder.Id) && _pending.ContainsKey(folder.Id);

        if (StartFolderCore(folder, resetRetry: true) == MountOutcome.Watching && hadPending)
        {
            // 之前处于"待重试"、这次手动挂载正好赶上盘恢复：补扫一次，
            // 否则中断期间落地的图片要等下次重启才会被处理。
            ScanAfterMount(folder);
        }
    }

    /// <summary>
    /// 挂载单个目录的实际实现。
    /// </summary>
    /// <param name="folder">目录配置快照。</param>
    /// <param name="resetRetry">
    /// true：这是一次"全新开启"，先注销该 Id 的待重试登记（重试次数从 0 开始）。
    /// false：来自延迟重试回调，保留登记以便继续累加尝试次数。
    /// </param>
    /// <returns>本次挂载的结果，供调用方决定是否补扫。</returns>
    private MountOutcome StartFolderCore(WatchedFolder folder, bool resetRetry)
    {
        if (folder is null || string.IsNullOrWhiteSpace(folder.Id))
        {
            return MountOutcome.Failed;
        }

        // 重复启动同一目录时先卸载旧实例，保证一个 Id 只对应一个 watcher。
        StopFolderCore(folder.Id, cancelPending: resetRetry);

        var resolvedPath = ResolveFolderPath(folder.Path);
        if (string.IsNullOrWhiteSpace(resolvedPath))
        {
            RaiseFolderState(folder.Id, "unavailable");
            return MountOutcome.Failed;
        }

        // 只做检测，绝不替用户创建目录——路径写错时创建空文件夹会掩盖问题。
        if (!Directory.Exists(resolvedPath))
        {
            // 开机自启时映射的网络盘往往还没挂好。这里若直接 return，等盘符上线后
            // 没有任何人会回来重试，整个会话都不会处理任何图片——用户反馈的
            // "自启后不自动扫描"正是这条路径，因此必须登记一次延迟重试。
            RaiseFolderState(folder.Id, "missing");
            SchedulePendingMount(folder, resolvedPath);
            return MountOutcome.RetryScheduled;
        }

        // 提到 try 外面：异常发生在 Attach 内部时 runtime 已持有未启用的 watcher，
        // catch 里必须能拿到它做清理，否则会留下半挂实例。
        FolderRuntime? runtime = null;

        try
        {
            // created 同时供事件回调捕获（非空局部变量），runtime 供 catch 清理。
            var created = new FolderRuntime(folder, resolvedPath);
            runtime = created;

            var watcher = new FileSystemWatcher(resolvedPath)
            {
                IncludeSubdirectories = folder.IncludeSubdirectories,
                InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
            };

            watcher.Created += (_, e) => OnFileEvent(created, e.FullPath);
            watcher.Changed += (_, e) => OnFileEvent(created, e.FullPath);
            watcher.Renamed += (_, e) => OnFileEvent(created, e.FullPath);
            watcher.Error += (_, e) => OnWatcherError(created, e);

            created.Attach(watcher);
            _runtimes[folder.Id] = created;
            _running = true;
            RaiseFolderState(folder.Id, "watching");

            // 挂载成功且之前挂着"待重试"登记，说明这是从网络盘未就绪的状态恢复过来的。
            // 必须立刻补扫：中断期间落地的图片不会产生任何文件事件，不扫就永久漏处理。
            if (TakePending(folder.Id) is { } recovered)
            {
                var attempts = recovered.Attempt;
                recovered.Dispose();
                AppServices.Log?.Info("监控",
                    $"目录已就绪，已重新挂载并补扫（第 {attempts} 次重试后成功）", resolvedPath);
                ScanAfterMount(folder);
                return MountOutcome.WatchingResumed;
            }

            return MountOutcome.Watching;
        }
        catch (Exception ex)
        {
            // 网络盘刚映射、ACL 还没生效时 new FileSystemWatcher / EnableRaisingEvents
            // 会瞬时抛异常，这与"路径不存在"是同一类故障（盘还没真正可用），
            // 同样必须进延迟重试，否则只修 Directory.Exists 会留下同样的洞。
            // 重试次数与目录不可用共用同一条 pending 登记，不会被单独计数。

            // 兜底清理半挂状态：Attach 在 EnableRaisingEvents 之前就写了 _watcher，
            // 异常可能留在"watcher 已存在但未启用"或"已登记进 _runtimes"两种中间态。
            // 条件删除（TryRemove 的 KeyValuePair 重载按引用比较）保证只摘除本次实例：
            // 并发下（UI 开关 vs 重试回调）别的线程可能已经装上新实例，
            // 无条件 TryRemove(key) 会把别人装好的 watcher 抹掉，监控会静默失效。
            if (runtime is not null
                && _runtimes.TryRemove(new KeyValuePair<string, FolderRuntime>(folder.Id, runtime)))
            {
                _running = _runtimes.Count > 0;
            }

            runtime?.Dispose();

            AppServices.Log?.Error("监控",
                $"挂载目录失败（{ex.Message}），{RemountDelayMs / 1000} 秒后自动重试", resolvedPath);
            RaiseFolderState(folder.Id, "unavailable");
            SchedulePendingMount(folder, resolvedPath);
            return MountOutcome.RetryScheduled;
        }
    }

    /// <inheritdoc />
    public void StopFolder(string folderId)
    {
        // 用户主动停止：连待重试登记一起注销，避免 Timer 到点把目录又挂回来。
        StopFolderCore(folderId, cancelPending: true);
    }

    /// <summary>卸载目录并（可选）注销待重试登记。</summary>
    private void StopFolderCore(string folderId, bool cancelPending)
    {
        if (string.IsNullOrWhiteSpace(folderId))
        {
            return;
        }

        if (_runtimes.TryRemove(folderId, out var runtime))
        {
            runtime.Dispose();
        }

        if (cancelPending)
        {
            CancelPendingMount(folderId);
        }

        _running = _runtimes.Count > 0;
    }

    /// <inheritdoc />
    public async Task ScanAsync(WatchedFolder folder, CancellationToken cancellationToken)
    {
        if (folder is null || string.IsNullOrWhiteSpace(folder.Id))
        {
            return;
        }

        var resolvedPath = ResolveFolderPath(folder.Path);
        if (string.IsNullOrWhiteSpace(resolvedPath) || !Directory.Exists(resolvedPath))
        {
            AppServices.Log?.Warn("扫描", "目录不存在，扫描取消", resolvedPath);
            RaiseProgress(new ScanProgress
            {
                FolderId = folder.Id,
                Scanned = 0,
                Total = 0,
                Processed = 0,
                Completed = true
            });
            return;
        }

        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _scans[folder.Id] = linkedSource;

        // 扫描期间先不做检测派发，等整批扫完再一次性判定，
        // 否则进度条分母会随着扫描进度一段段跳。
        Interlocked.Increment(ref _scansInProgress);

        var token = linkedSource.Token;
        var processed = 0;
        var enqueued = 0;

        try
        {
            // 目录枚举是同步 IO：如果调用方是 UI 线程（点"添加目录"时就是），
            // 直接跑会把窗口卡死几秒。放到线程池，UI 只负责等结果。
            var ignored = folder.IgnoredFolders;
            var files = await Task.Run(() =>
            {
                var list = new List<string>();
                CollectFiles(resolvedPath, folder.IncludeSubdirectories, list, token, ignored);
                return list;
            }, token).ConfigureAwait(false);

            var total = files.Count;
            RaiseProgress(new ScanProgress
            {
                FolderId = folder.Id,
                Scanned = 0,
                Total = total,
                Processed = 0,
                Completed = false
            });

            for (var index = 0; index < files.Count; index++)
            {
                token.ThrowIfCancellationRequested();

                if (TryEnqueue(files[index], folder))
                {
                    enqueued++;
                }

                processed++;
                var scanned = index + 1;

                if (scanned % YieldEvery == 0)
                {
                    // 扫描是大批量同步 IO，定期让出线程避免界面卡顿。
                    await Task.Yield();
                }

                if (scanned % ProgressEvery == 0)
                {
                    RaiseProgress(new ScanProgress
                    {
                        FolderId = folder.Id,
                        Scanned = scanned,
                        Total = total,
                        Processed = processed,
                        Completed = false
                    });
                }
            }

            // 横竖版判定紧接着由检测阶段完成，这里先记"提交检测"的张数，
            // 真正需要处理的张数会在检测完成后体现在进度浮窗上。
            AppServices.Log?.Info("扫描",
                enqueued <= 0
                    ? $"扫描完成，{total} 个文件中无可检测的图片"
                    : $"扫描完成，{total} 个文件中 {enqueued} 张已提交检测",
                resolvedPath);
        }
        catch (OperationCanceledException)
        {
            AppServices.Log?.Info("扫描", $"扫描已取消，已入队 {enqueued} 张需要处理的文件", resolvedPath);
        }
        catch (Exception ex)
        {
            AppServices.Log?.Error("扫描", $"扫描失败：{ex.Message}", resolvedPath);
        }
        finally
        {
            // 只摘除自己的登记：并发扫描同一个目录时后启动的那次会覆盖 entry，
            // 无条件 Remove 会把别人的 CTS 删掉，导致 CancelScan 空转、
            // IsFolderBusy 误报，被覆盖的 CTS 也无人释放。
            _scans.TryRemove(new KeyValuePair<string, CancellationTokenSource>(folder.Id, linkedSource));

            if (Interlocked.Decrement(ref _scansInProgress) == 0)
            {
                // 扫描结束，立即对这批候选做一次检测。
                _ = DispatchDetectedAsync();
            }

            // 无论完成还是取消都发一次 Completed，保证 UI 不会卡在进度条上。
            RaiseProgress(new ScanProgress
            {
                FolderId = folder.Id,
                Scanned = processed,
                Total = processed,
                Processed = processed,
                Completed = true
            });
        }
    }

    /// <inheritdoc />
    public void CancelScan(string folderId)
    {
        if (string.IsNullOrWhiteSpace(folderId))
        {
            return;
        }

        if (_scans.TryGetValue(folderId, out var source))
        {
            try
            {
                source.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 扫描刚刚自行结束并释放，取消动作可以直接忽略。
            }
        }
    }

    /// <summary>停止队列消费者并卸载全部监控。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopAll();
        _shutdown.Cancel();
        _queue.Writer.TryComplete();

        // 停机时清零队列深度：未出队的残留项不会再经过 HandleItemAsync 的 Decrement，
        // 若不归零 CompleteBatch 会永远等不到空闲。
        Interlocked.Exchange(ref _queueDepth, 0);

        _detectTimer?.Dispose();
        _detectTimer = null;

        _shutdown.Dispose();
    }

    /// <summary>解析目录路径模板。</summary>
    private static string ResolveFolderPath(string template)
    {
        try
        {
            return AppServices.PathTemplate?.Resolve(template) ?? template ?? string.Empty;
        }
        catch (Exception)
        {
            // 模板里有非法字符时退回原文，后续 Directory 操作会给出具体错误。
            return template ?? string.Empty;
        }
    }

    /// <summary>判断文件是否属于需要处理的图片（扩展名白名单 + 排除临时文件）。</summary>
    private static bool IsSupportedImage(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return false;
        }

        // 本服务自己写回的临时文件是 *.tmp，必须排除，否则会陷入自触发循环。
        if (fullPath.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var extension = Path.GetExtension(fullPath);
        if (string.IsNullOrEmpty(extension))
        {
            return false;
        }

        List<string>? supported = null;
        try
        {
            supported = AppServices.Config?.Current?.SupportedExtensions;
        }
        catch (Exception)
        {
            // 配置尚未就绪，走兜底白名单。
        }

        if (supported is null || supported.Count == 0)
        {
            foreach (var fallback in FallbackExtensions)
            {
                if (string.Equals(fallback, extension, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        foreach (var item in supported)
        {
            if (string.Equals(item, extension, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>把文件写入处理队列（带 1 秒同路径事件合并）。</summary>
    /// <summary>尝试入队。返回 true 表示这张图确实需要处理并已进入队列。</summary>
    private bool TryEnqueue(string fullPath, WatchedFolder folder)
    {
        if (!IsSupportedImage(fullPath))
        {
            return false;
        }

        var now = DateTime.UtcNow;

        // 去重键带上文件长度：同一文件的 Created/Changed 会被合并，
        // 但内容真的变了（尺寸不同）就重新入队，不会漏处理。
        var dedupeKey = fullPath + "|" + ReadSizeSafe(fullPath);

        if (_recentEnqueues.TryGetValue(dedupeKey, out var lastSeen)
            && (now - lastSeen).TotalMilliseconds < MergeWindowMs)
        {
            // 同一次保存往往连续抛出 Changed 多次，这里合并成一次处理。
            return false;
        }

        _recentEnqueues[dedupeKey] = now;
        CompactMergeTable(now);

        // 这里只做最便宜的事：扩展名过滤 + 去重 + 丢进候选队列。
        // 真正的横竖版判定由检测阶段统一完成，这样进度条的分母是"先算清楚再开始处理"。
        _candidates.Enqueue(new WorkItem(fullPath, folder));
        Interlocked.Exchange(ref _lastCandidateTicks, DateTime.UtcNow.Ticks);

        return true;
    }

    /// <summary>批次计数用的规范化键：忽略大小写与路径写法差异。</summary>
    private static string NormalizeKey(string path)
    {
        try
        {
            return Path.GetFullPath(path).ToLowerInvariant();
        }
        catch (Exception)
        {
            return path.Trim().ToLowerInvariant();
        }
    }

    /// <summary>读文件大小，失败返回 0（文件可能正在写入或已被删）。</summary>
    private static long ReadSizeSafe(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>合并表惰性清理，避免长期运行时无限增长。</summary>
    private void CompactMergeTable(DateTime now)
    {
        if (_recentEnqueues.Count <= MergeCompactThreshold)
        {
            return;
        }

        // 每 5 秒最多清理一次：否则表一大，每个文件事件都要全表扫一遍，O(n²)。
        if ((now - _lastCompactUtc).TotalSeconds < 5)
        {
            return;
        }

        _lastCompactUtc = now;

        foreach (var pair in _recentEnqueues)
        {
            if ((now - pair.Value).TotalSeconds < 10)
            {
                continue;
            }

            _recentEnqueues.TryRemove(pair.Key, out _);
        }
    }

    /// <summary>FileSystemWatcher 事件入口；回调里只做过滤与入队，绝不在此处理图片。</summary>
    private void OnFileEvent(FolderRuntime runtime, string fullPath)
    {
        try
        {
            if (_disposed)
            {
                return;
            }

            // 命中忽略名单的子文件夹，里面的图片变动一律不处理。
            if (IsInIgnoredFolder(fullPath, runtime.ResolvedPath, runtime.Folder.IgnoredFolders))
            {
                return;
            }

            TryEnqueue(fullPath, runtime.Folder);
        }
        catch (Exception ex)
        {
            // 事件回调里的异常会直接终止进程，必须兜住。
            AppServices.Log?.Warn("监控", $"处理文件事件失败：{ex.Message}", runtime.ResolvedPath);
        }
    }

    /// <summary>监控出错（多为缓冲区溢出）：上报状态并安排 30 秒后重挂。</summary>
    private void OnWatcherError(FolderRuntime runtime, ErrorEventArgs args)
    {
        try
        {
            var reason = args.GetException()?.Message ?? "未知原因";
            AppServices.Log?.Warn("监控", $"文件监控失效（{reason}），{RemountDelayMs / 1000} 秒后重挂",
                runtime.ResolvedPath);
            RaiseFolderState(runtime.Folder.Id, "unavailable");
            ScheduleRemount(runtime);
        }
        catch (Exception ex)
        {
            AppServices.Log?.Error("监控", $"重挂调度失败：{ex.Message}", runtime.ResolvedPath);
        }
    }

    /// <summary>安排一次延迟重挂，重挂前先卸载旧 watcher。</summary>
    private void ScheduleRemount(FolderRuntime runtime)
    {
        // 项目同时启用 WinForms，Timer 必须写全名避免与 System.Windows.Forms.Timer 歧义。
        var timer = new System.Threading.Timer(
            _ => Remount(runtime), null, RemountDelayMs, Timeout.Infinite);
        runtime.ScheduleRetry(timer);
    }

    /// <summary>执行重挂：先从注册表摘除旧实例，再按原配置重新挂载。</summary>
    private void Remount(FolderRuntime runtime)
    {
        try
        {
            if (_disposed)
            {
                return;
            }

            _runtimes.TryRemove(runtime.Folder.Id, out _);
            runtime.Dispose();

            // resetRetry: false——若这次其实是从"待重试"状态恢复，StartFolderCore 内部
            // 已经补扫过并返回 WatchingResumed，这里只在普通重挂成功时补扫，避免扫两遍。
            if (StartFolderCore(runtime.Folder, resetRetry: false) == MountOutcome.Watching)
            {
                // watcher 失效的这半分钟里落地的图片同样没有文件事件，必须补扫。
                ScanAfterMount(runtime.Folder);
            }
        }
        catch (Exception ex)
        {
            AppServices.Log?.Error("监控", $"重挂失败：{ex.Message}", runtime.ResolvedPath);
        }
    }

    /// <summary>
    /// 登记一次"路径可用后重新挂载"的延迟重试。
    /// 同一目录只保留一条登记，重复登记只累加尝试次数并重置计时器。
    /// </summary>
    private void SchedulePendingMount(WatchedFolder folder, string resolvedPath)
    {
        if (_disposed || folder is null || string.IsNullOrWhiteSpace(folder.Id))
        {
            return;
        }

        // 意图闸：目录已被停用（或总开关已关）就不要再登记，
        // 否则关掉开关后盘一上线仍会被挂回来。
        // 用 IsFolderStillWanted 而不是裸 folder.Enabled：登记里拿的是配置快照，
        // 配置导入 / 重建视图模型会换新实例，快照的 Enabled 可能永久停在 true。
        if (!IsFolderStillWanted(folder))
        {
            return;
        }

        // GetOrAdd 保证同一 Id 只存在一个 PendingMount，多余的候选实例立刻释放。
        var candidate = new PendingMount(folder, resolvedPath);
        var entry = _pending.GetOrAdd(folder.Id, candidate);

        if (!ReferenceEquals(entry, candidate))
        {
            candidate.Dispose();
        }

        var attempt = entry.NextAttempt();

        if (!ShouldKeepMountRetrying(attempt))
        {
            // 次数用尽：停止重试并把结论写清楚，绝不能让后台无声空转。
            TakePending(folder.Id)?.Dispose();
            AppServices.Log?.Warn("监控",
                $"已重试 {attempt - 1} 次仍不可用，已停止自动重试；请在网络盘就绪后手动开启一次监控",
                resolvedPath);
            RaiseFolderState(folder.Id, "missing");
            return;
        }

        // 项目同时启用 WinForms，Timer 必须写全名避免与 System.Windows.Forms.Timer 歧义。
        var timer = new System.Threading.Timer(
            _ => TryMountPending(entry), null, RemountDelayMs, Timeout.Infinite);

        if (!entry.TrySetTimer(timer))
        {
            // 登记已被注销（用户中途停止监控或已恢复），定时器由 TrySetTimer 内部释放。
            return;
        }

        AppServices.Log?.Info("监控",
            attempt <= 1
                ? $"目录暂不可用（网络盘可能未就绪），{RemountDelayMs / 1000} 秒后自动重试"
                : $"目录仍不可用，第 {attempt} 次重试将在 {RemountDelayMs / 1000} 秒后开始",
            resolvedPath);
    }

    /// <summary>重试回调（运行在 Timer 线程上）：仍不可用就继续登记，可用则挂载并补扫。</summary>
    private void TryMountPending(PendingMount entry)
    {
        try
        {
            if (_disposed)
            {
                return;
            }

            // 登记已被 StopFolder / StopAll 注销，说明用户不想再监控这个目录。
            if (!_pending.TryGetValue(entry.Folder.Id, out var current) || !ReferenceEquals(current, entry))
            {
                return;
            }

            // 入口意图闸：覆盖"用户已停用该目录 / 已关总开关"这条路。
            if (!IsFolderStillWanted(entry.Folder))
            {
                CancelPendingMount(entry.Folder.Id);
                return;
            }

            // 复用完整挂载流程：成功会写入 _runtimes 并自动补扫；
            // 失败（路径仍不可用）会重新进入 SchedulePendingMount 继续累加次数。
            _ = StartFolderCore(entry.Folder, resetRetry: false);

            // 回滚：Directory.Exists 打在死网络盘上可能耗时数百毫秒到数秒，这段时间里
            // 用户完全来得及关掉开关。此时挂载已经生效，必须撤销，否则表现为
            // "关了开关几十秒后监控自己回来"。
            //
            // 判据是**这条登记自己**被标记为已取消（per-folder），而不是全局计数：
            // 全局计数会让"停用目录 B"把正在恢复的目录 A 一起撤掉，
            // 结果是 A 永久静默失去监控（开关看着还是开的），比原 bug 更难查。
            //
            // 注意：这里只摘 watcher 与登记，**不打断已经在跑的那轮扫描**
            // （StopFolderCore 本来就不 CancelScan）。"停止监控不中断正在进行的扫描"
            // 是既有语义，回滚沿用同一语义；所以极少数情况下这轮扫描仍会跑完并入队，
            // 属于已知边界而非缺陷，不要在这里补 CancelScan 当例外。
            if (entry.IsCancelled)
            {
                StopFolderCore(entry.Folder.Id, cancelPending: true);
            }
        }
        catch (Exception ex)
        {
            AppServices.Log?.Error("监控", $"重试挂载失败：{ex.Message}", entry.ResolvedPath);
        }
    }

    /// <summary>
    /// 注销一条待重试登记：先打"已被用户停止"标记，再取走并释放。
    /// 标记必须先于释放，这样卡在 StartFolderCore 里的回调拿到的一定是被取消的登记，
    /// 挂载成功后据 <see cref="PendingMount.IsCancelled"/> 回滚。
    /// </summary>
    private void CancelPendingMount(string folderId)
    {
        var pending = TakePending(folderId);
        if (pending is null)
        {
            return;
        }

        pending.MarkCancelled();
        pending.Dispose();
    }

    /// <summary>
    /// 判断用户是否还想要监控这个目录：总开关 + 按 Id 回查的最新配置。
    /// <para>
    /// 不单看 <see cref="WatchedFolder.Enabled"/> 快照——配置导入 / 重建视图模型会换新实例，
    /// 快照可能永久停在 true；快照只作为查不到配置时的兜底。
    /// </para>
    /// <para>
    /// <b>查不到配置时返回 true 是刻意的</b>：这里的返回值决定"要不要撤掉一个已经建好的
    /// watcher"，属于破坏性操作。Id 丢失被重生成、配置并发改写、配置尚未加载，都会让按 Id
    /// 查不到目录，但这些情况下目录其实仍然是想要的；宁可漏一次回滚，也不能误杀监控。
    /// 若把兜底方向改成 false，表现会是"开关开着却不再监控"——比原 bug 更难查。别改回去。
    /// </para>
    /// </summary>
    private static bool IsFolderStillWanted(WatchedFolder snapshot)
    {
        try
        {
            var config = AppServices.Config?.Current;
            if (config is null)
            {
                // 配置不可用属于异常态：按"想要"处理，不做破坏性回滚。
                return true;
            }

            if (!config.MonitoringEnabled)
            {
                return false;
            }

            var latest = config.Folders?.FirstOrDefault(f => f is not null && f.Id == snapshot.Id);
            return latest?.Enabled ?? snapshot.Enabled;
        }
        catch (Exception)
        {
            // Folders 是无锁的 List：ConfigService.Current 的 lock 只保护"返回引用"，
            // 而界面删除目录走无锁的 Folders.Remove(...)，本方法又常在 Timer 线程上被调用，
            // 枚举期间撞上并发改写会抛"集合已修改"。
            // 这里必须就地兜住：异常一旦冒到 TryMountPending 的 catch，会被记成
            // "重试挂载失败"并顺势跳过意图闸/回滚——守卫恰好在最需要它的竞态里哑火，
            // 事后还会被那条误导性日志带偏。
            return snapshot.Enabled;
        }
    }

    /// <summary>取走并注销待重试登记；不负责释放，由调用方 Dispose，避免 Timer 悬挂。</summary>
    private PendingMount? TakePending(string folderId)
    {
        if (string.IsNullOrWhiteSpace(folderId))
        {
            return null;
        }

        return _pending.TryRemove(folderId, out var pending) ? pending : null;
    }

    /// <summary>
    /// 判断是否还能继续重试。抽成静态纯函数（无副作用、不碰任何字段），
    /// 方便测试直接覆盖边界，不必真的等 30 秒 × N 次。
    /// </summary>
    /// <param name="attempt">本次是第几次尝试（从 1 开始）。</param>
    public static bool ShouldKeepMountRetrying(int attempt)
    {
        return attempt > 0 && attempt <= MaxMountRetries;
    }

    /// <summary>
    /// 挂载（或重挂）成功后补扫一次。
    /// 中断期间落地的图片不会产生任何文件事件，不补扫就是永久漏处理。
    /// </summary>
    private void ScanAfterMount(WatchedFolder folder)
    {
        if (folder is null || string.IsNullOrWhiteSpace(folder.Id))
        {
            return;
        }

        // 后台执行，不阻塞调用方：重试回调在 Timer 线程，重挂回调在事件线程。
        _ = Task.Run(async () =>
        {
            try
            {
                await ScanAsync(folder, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppServices.Log?.Warn("扫描", $"补挂后补扫失败：{ex.Message}", folder.Path);
            }
        });
    }

    /// <summary>单消费者循环：串行处理WorkItem，保证同一时刻只处理一张图。</summary>
    private async Task ConsumeLoopAsync()
    {
        var token = _shutdown.Token;

        try
        {
            while (await _queue.Reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                while (_queue.Reader.TryRead(out var item))
                {
                    try
                    {
                        await HandleItemAsync(item).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        // 单张图失败绝不能拖垮消费者。
                        AppServices.Log?.Error("处理", $"处理文件异常：{ex}", item.Folder.Path,
                            Path.GetFileName(item.FullPath));
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常关机。
        }
        catch (Exception ex)
        {
            AppServices.Log?.Error("处理", $"处理队列异常退出：{ex.Message}");
        }
    }

    /// <summary>处理单个文件的完整流程：稳定性检测 → 自触发抑制 → 旋转 → 阈值检查。</summary>
    private Task HandleItemAsync(WorkItem item)
    {
        _isProcessing = true;
        var counted = false;
        var rotated = false;

        // 先把「正在处理哪一张」推给界面。
        // 大图（几百 MB 的喷绘 TIFF）单张要跑几秒到几十秒，期间界面上如果连文件名都不动，
        // 用户看到的就是「进度条卡住」——而实际上它在干活。
        RaiseBatchProgress(item.FullPath, completed: false);

        try
        {
            (counted, rotated) = ProcessItem(item);
        }
        catch (Exception ex)
        {
            // 已经出队的成员必须计入分母：否则进度永远差一张（N-1/N），
            // 而队列已经空了，表现出来就是「卡住不再动」。
            counted = true;
            AppServices.Log?.Error("处理", $"处理异常，已跳过：{ex.Message}",
                item.Folder.Path, Path.GetFileName(item.FullPath));
        }
        finally
        {
            // 队列深度 -1（与 TryEnqueue 中的 Increment 配对），放 finally 确保任何早退路径都减。
            Interlocked.Decrement(ref _queueDepth);

            // 只有计入过分母的文件才推进度；竖版跳过、传输失败等未计数的文件不动进度条。
            var key = NormalizeKey(item.FullPath);

            if (counted)
            {
                if (rotated && _batchProcessedFiles.TryAdd("r:" + key, 0))
                {
                    Interlocked.Increment(ref _batchRotated);
                }

                if (_batchProcessedFiles.TryAdd("d:" + key, 0))
                {
                    Interlocked.Increment(ref _batchDone);
                }
            }

            _isProcessing = false;
            RaiseBatchProgress(item.FullPath, completed: false);

            // 只有队列已排空时才启动"空闲超时"倒计时。
            // 若不判断就 TouchBatch，连续处理慢图时倒计时会在处理中途到期，
            // CompleteBatch 把累计计数清零，剩余文件被错误地切成新批次。
            if (Volatile.Read(ref _queueDepth) == 0)
            {
                TouchBatch();
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>真正的处理逻辑；返回是否发生了旋转。</summary>
    private (bool Counted, bool Rotated) ProcessItem(WorkItem item)
    {
        // 是否计入分母、是否需要旋转，已经在检测阶段判定完毕。
        // 这里只负责真正落盘处理；中途文件消失或被忽略，也算"处理完"，否则进度会卡住。

        // 命中说明这是本进程自己写回的文件，静默跳过——这里绝不打日志，否则会被自触发事件刷屏。
        if (AppServices.IgnoreSet?.Consume(item.FullPath) == true)
        {
            return (true, false);
        }

        if (!File.Exists(item.FullPath))
        {
            return (true, false);
        }

        var config = AppServices.Config?.Current;
        var mode = config?.RotationMode ?? RotationMode.Physical;
        var direction = config?.RotateDirection ?? RotateDirection.Clockwise;
        var maxPixels = config?.MaxPixels ?? 120_000_000;

        var suffix = config?.AddSuffix == true ? config.Suffix : null;
        var maxSideCm = ResolveMaxSideCm(item.Folder, item.FullPath);

        // 大文件先记一行「开始处理」：万一日志里只有开始没有结束，就能立刻定位是哪一张卡住了。
        var fileLength = ReadSizeSafe(item.FullPath);
        if (fileLength >= BigFileLogThresholdBytes)
        {
            AppServices.Log?.Info("处理",
                $"开始处理大文件（{fileLength / 1024d / 1024:0} MB，幅面上限={(maxSideCm > 0 ? $"{maxSideCm:0.#} cm" : "未启用")}）",
                item.Folder.Path, Path.GetFileName(item.FullPath));
        }

        var result = AppServices.Image?.Process(item.FullPath, mode, direction, maxPixels, suffix, maxSideCm)
                     ?? RotationResult.Fail("图像服务未就绪，已跳过");


        AppServices.Log?.Write(
            result.Success ? LogLevel.Info : LogLevel.Error,
            item.Folder.Path,
            Path.GetFileName(item.FullPath),
            result.BeforeWidth > 0 ? $"{result.BeforeWidth}×{result.BeforeHeight}" : null,
            result.Action,
            result.ElapsedMs,
            BuildLogMessage(result));

        // 慢图单独留一条警告：批量处理里「整体很慢」通常是被少数几张超大图拖住的，
        // 只有把具体的文件与耗时标出来，用户和我们都才知道该调什么。
        if (result.ElapsedMs >= SlowImageThresholdMs)
        {
            AppServices.Log?.Warn("处理",
                $"慢图：耗时 {result.ElapsedMs} ms（{result.BeforeWidth}×{result.BeforeHeight}，{result.Action}）",
                item.Folder.Path, Path.GetFileName(item.FullPath));
        }

        if (item.Folder.ResolutionCheckEnabled && File.Exists(item.FullPath))
        {
            AppServices.Alerts?.EvaluateFile(item.FullPath, item.Folder);
        }

        return (true, result.Rotated);
    }

    /// <summary>把当前批次快照推给订阅方（界面进度条）。</summary>
    private void RaiseBatchProgress(string? currentFile, bool completed)
    {
        var total = Volatile.Read(ref _batchTotal);
        if (total <= 0)
        {
            return;
        }

        BatchProgressChanged?.Invoke(this, new BatchProgress
        {
            Processed = Volatile.Read(ref _batchDone),
            Total = total,
            Rotated = Volatile.Read(ref _batchRotated),
            CurrentFile = string.IsNullOrEmpty(currentFile) ? null : Path.GetFileName(currentFile),
            Completed = completed
        });
    }

    /// <summary>把"本批完成"的判定往后推，避免连续处理时被误判为结束。</summary>
    private void TouchBatch()
    {
        CancellationTokenSource? previous;
        CancellationTokenSource current;

        lock (_batchLock)
        {
            previous = _batchIdleCts;
            current = new CancellationTokenSource();
            _batchIdleCts = current;
        }

        previous?.Cancel();
        previous?.Dispose();

        _ = Task.Delay(BatchIdleGrace, current.Token).ContinueWith(
            t =>
            {
                if (!t.IsCanceled)
                {
                    CompleteBatch();
                }
            },
            TaskScheduler.Default);
    }

    /// <summary>本批结束：推一次完成态的进度，然后清零准备下一批。</summary>
    private void CompleteBatch()
    {
        // 队列仍有待处理文件，或恰好有一张图还在处理中（队列读空但 _isProcessing 尚未翻 false），
        // 都说明这批还没跑完——重新计时等待，绝不能在这里清零。
        if (Volatile.Read(ref _queueDepth) > 0
            || _isProcessing
            || !_candidates.IsEmpty
            || Volatile.Read(ref _detecting) == 1)
        {
            TouchBatch();
            return;
        }

        int total;

        lock (_batchLock)
        {
            total = _batchTotal;
            if (total <= 0)
            {
                return;
            }
        }

        RaiseBatchProgress(null, completed: true);

        lock (_batchLock)
        {
            _batchTotal = 0;
            _batchDone = 0;
            _batchRotated = 0;
        }

        _batchFiles.Clear();
        _batchProcessedFiles.Clear();
    }

    /// <summary>把结果拼成一行可读日志。</summary>
    private static string BuildLogMessage(RotationResult result)
    {
        var message = result.Message ?? string.Empty;

        if (result.Rotated)
        {
            var sizeText = $"→ {result.AfterWidth}×{result.AfterHeight}";
            return message.Length == 0 ? sizeText : sizeText + "；" + message;
        }

        return message;
    }

    /// <summary>等待文件状态稳定：长度与写入时间两次一致，且能独占打开。</summary>
    private static async Task<bool> WaitUntilStableAsync(string fullPath)
    {
        for (var attempt = 0; attempt < StabilityAttempts; attempt++)
        {
            await Task.Delay(StabilityDelayMs).ConfigureAwait(false);

            var first = SampleFile(fullPath);
            if (first is null)
            {
                return false;
            }

            await Task.Delay(StabilitySampleGapMs).ConfigureAwait(false);

            var second = SampleFile(fullPath);
            if (second is null)
            {
                return false;
            }

            if (first.Value.Length == second.Value.Length
                && first.Value.LastWriteUtc == second.Value.LastWriteUtc
                && CanOpenExclusive(fullPath))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>定时器回调：候选队列静默且没有扫描在跑时，统一做一次检测派发。</summary>
    private void OnDetectTick()
    {
        try
        {
            if (_disposed || Volatile.Read(ref _scansInProgress) > 0)
            {
                return;
            }

            if (_candidates.IsEmpty)
            {
                return;
            }

            var last = new DateTime(Interlocked.Read(ref _lastCandidateTicks), DateTimeKind.Utc);
            if (DateTime.UtcNow - last < DetectQuietWindow)
            {
                return;
            }

            _ = DispatchDetectedAsync();
        }
        catch (Exception)
        {
            // 定时器里绝不能让异常逃逸。
        }
    }

    /// <summary>
    /// 检测一批候选文件：判断谁需要处理，先把总数一次性加到进度分母，再交给处理队列。
    /// 这样进度条分母在开始处理前就固定下来，不会边处理边跳。
    /// </summary>
    private async Task DispatchDetectedAsync()
    {
        if (Interlocked.Exchange(ref _detecting, 1) == 1)
        {
            return;
        }

        try
        {
            var batch = new List<WorkItem>();
            while (_candidates.TryDequeue(out var candidate))
            {
                batch.Add(candidate);
            }

            if (batch.Count == 0)
            {
                return;
            }

            var ready = new System.Collections.Concurrent.ConcurrentBag<WorkItem>();

            var tasks = batch.Select(async item =>
            {
                await _detectGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (await NeedsProcessingAsync(item).ConfigureAwait(false))
                    {
                        ready.Add(item);
                    }
                }
                finally
                {
                    _detectGate.Release();
                }
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);

            var picked = ready.ToList();

            // 「已提交检测」之后到底发生了什么，必须留一行。
            // 否则现场只看到「扫描完成，3 张已提交检测」，然后永远没有任何动静——
            // 用户会认为卡死，我们也无从判断它们是被判成竖版丢掉了，还是真的没跑。
            var dropped = batch.Count - picked.Count;
            if (dropped > 0)
            {
                AppServices.Log?.Info("检测",
                    $"本批 {batch.Count} 张：{picked.Count} 张进入处理，{dropped} 张判定无需处理（竖版/正方形/无法读取/重复）");
            }

            if (picked.Count == 0)
            {
                return;
            }

            // 关键顺序：先一次性把分母加上，再投递处理队列。
            var added = 0;
            foreach (var item in picked)
            {
                if (_batchFiles.TryAdd(NormalizeKey(item.FullPath), 0))
                {
                    added++;
                }
            }

            if (added > 0)
            {
                Interlocked.Add(ref _batchTotal, added);
                RaiseBatchProgress(null, completed: false);
            }

            foreach (var item in picked)
            {
                if (_queue.Writer.TryWrite(item))
                {
                    Interlocked.Increment(ref _queueDepth);
                }
            }

            TouchBatch();
        }
        catch (Exception ex)
        {
            AppServices.Log?.Warn("检测", $"批量检测失败：{ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _detecting, 0);
        }
    }

    /// <summary>
    /// 判断单个文件是否需要处理：先确认写入已结束，再解图片头看横竖版。
    /// 文件正在传输时走稳定性等待，等不完就退避重试，绝不误判成"竖版"丢掉。
    /// </summary>
    private async Task<bool> NeedsProcessingAsync(WorkItem item)
    {
        try
        {
            if (!File.Exists(item.FullPath))
            {
                return false;
            }

            // 快速路径：能独占打开说明写盘方已经放手，直接探测，省掉 650ms 等待。
            if (!CanOpenExclusive(item.FullPath))
            {
                if (!await WaitUntilStableAsync(item.FullPath).ConfigureAwait(false))
                {
                    ScheduleRetry(item);
                    return false;
                }
            }

            var probe = AppServices.Image?.Probe(item.FullPath);
            return probe is not null && probe.OrientedWidth > probe.OrientedHeight;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 判断文件是否位于被忽略的子文件夹内（只看目录层级，不看文件名）。
    /// 例如忽略 "已打" 时，A:\图\已打\x.jpg 会被判定为忽略。
    /// </summary>
    private static bool IsInIgnoredFolder(string fullPath, string rootPath, IReadOnlyList<string> ignoredFolders)
    {
        if (ignoredFolders is null || ignoredFolders.Count == 0)
        {
            return false;
        }

        try
        {
            var root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar)
                       + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(fullPath);

            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var relative = full.Substring(root.Length);
            var segments = relative.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);

            // 最后一段是文件名，不参与匹配。
            for (var i = 0; i < segments.Length - 1; i++)
            {
                foreach (var name in ignoredFolders)
                {
                    if (string.Equals(segments[i], name, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
        }
        catch (Exception)
        {
            // 路径异常时按不忽略处理，宁可多处理也不漏。
        }

        return false;
    }

    /// <summary>
    /// 按文件名选出本次使用的幅面上限：
    /// 命中多条规则时取「关键词最长」的那条（更具体者优先），都不命中用目录默认值。
    /// 未启用幅面检查时返回 0，表示不限制。
    /// </summary>
    private static double ResolveMaxSideCm(WatchedFolder folder, string fullPath)
    {
        if (!folder.SizeLimitEnabled)
        {
            return 0;
        }

        var name = Path.GetFileName(fullPath);
        if (string.IsNullOrEmpty(name) || folder.SizeRules.Count == 0)
        {
            return folder.MaxSideCm;
        }

        var best = string.Empty;
        var limit = folder.MaxSideCm;

        foreach (var rule in folder.SizeRules)
        {
            var keyword = rule.Keyword;
            if (string.IsNullOrWhiteSpace(keyword)
                || !name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (keyword.Length > best.Length)
            {
                best = keyword;
                limit = rule.MaxSideCm;
            }
        }

        return limit;
    }

    /// <summary>传输未完成时，退避后把文件重新排队（有上限，避免死循环）。</summary>
    private void ScheduleRetry(WorkItem item)
    {
        if (item.Attempt >= MaxStabilityRetries)
        {
            AppServices.Log?.Warn("处理", "文件长时间未完成传输，等待下次文件事件再处理", item.Folder.Path,
                Path.GetFileName(item.FullPath));
            return;
        }

        var next = new WorkItem(item.FullPath, item.Folder, item.Attempt + 1);

        _ = Task.Delay(RetryDelayMs).ContinueWith(_ =>
        {
            try
            {
                if (_disposed)
                {
                    return;
                }

                _candidates.Enqueue(next);
                Interlocked.Exchange(ref _lastCandidateTicks, DateTime.UtcNow.Ticks);
            }
            catch (Exception)
            {
                // 队列关闭时忽略。
            }
        }, TaskScheduler.Default);
    }

    /// <summary>采样文件长度与最后写入时间。</summary>
    private static (long Length, DateTime LastWriteUtc)? SampleFile(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
            {
                return null;
            }

            return (file.Length, file.LastWriteTimeUtc);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>尝试以 FileShare.None 独占打开，成功说明写盘方已放手。</summary>
    private static bool CanOpenExclusive(string path)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>递归收集文件，跳过 ReparsePoint（符号链接/联接点）以免死循环。</summary>
    private static void CollectFiles(string directory, bool includeSubdirectories, List<string> files,
        CancellationToken token, IReadOnlyList<string>? ignoredFolders = null)
    {
        token.ThrowIfCancellationRequested();

        try
        {
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                token.ThrowIfCancellationRequested();

                try
                {
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }
                }
                catch (Exception)
                {
                    // 个别文件无权限时跳过，不影响整批扫描。
                    continue;
                }

                files.Add(file);
            }
        }
        catch (UnauthorizedAccessException)
        {
            AppServices.Log?.Warn("扫描", "目录无权访问，已跳过", directory);
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }
        catch (IOException ex)
        {
            AppServices.Log?.Warn("扫描", $"枚举文件失败：{ex.Message}", directory);
        }

        if (!includeSubdirectories)
        {
            return;
        }

        try
        {
            foreach (var subdirectory in Directory.EnumerateDirectories(directory))
            {
                token.ThrowIfCancellationRequested();

                try
                {
                    if ((File.GetAttributes(subdirectory) & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }
                }
                catch (Exception)
                {
                    continue;
                }

                // 目录名命中忽略名单时整棵子树都不进扫描。
                var subName = Path.GetFileName(subdirectory);
                if (ignoredFolders is not null
                    && ignoredFolders.Any(name => string.Equals(name, subName, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                CollectFiles(subdirectory, true, files, token, ignoredFolders);
            }
        }
        catch (UnauthorizedAccessException)
        {
            AppServices.Log?.Warn("扫描", "子目录无权访问，已跳过", directory);
        }
        catch (DirectoryNotFoundException)
        {
            // 扫描过程中目录被删，正常结束。
        }
        catch (IOException ex)
        {
            AppServices.Log?.Warn("扫描", $"枚举子目录失败：{ex.Message}", directory);
        }
    }

    /// <summary>触发目录状态变更事件；订阅方异常不影响监控。</summary>
    private void RaiseFolderState(string folderId, string state)
    {
        try
        {
            FolderStateChanged?.Invoke(this, folderId + "|" + state);
        }
        catch (Exception ex)
        {
            AppServices.Log?.Error("监控", $"目录状态回调异常：{ex.Message}");
        }
    }

    /// <summary>上报扫描进度；订阅方异常不影响扫描。</summary>
    private void RaiseProgress(ScanProgress progress)
    {
        try
        {
            ScanProgressChanged?.Invoke(this, progress);
        }
        catch (Exception ex)
        {
            AppServices.Log?.Error("扫描", $"进度回调节异常：{ex.Message}");
        }
    }

    /// <summary>一个待处理的文件。</summary>
    private sealed class WorkItem
    {
        public WorkItem(string fullPath, WatchedFolder folder, int attempt = 0)
        {
            FullPath = fullPath;
            Folder = folder;
            Attempt = attempt;
        }

        /// <summary>文件全路径。</summary>
        public string FullPath { get; }

        /// <summary>所属目录配置快照。</summary>
        public WatchedFolder Folder { get; }

        /// <summary>这是第几次尝试；传输太慢时用来退避重试。</summary>
        public int Attempt { get; }
    }

    /// <summary>单个目录的挂载结果。</summary>
    private enum MountOutcome
    {
        /// <summary>已挂上 watcher，调用方按需决定是否补扫。</summary>
        Watching,

        /// <summary>从"待重试"状态恢复成功，内部已完成补扫。</summary>
        WatchingResumed,

        /// <summary>路径暂不可用，已登记延迟重试。</summary>
        RetryScheduled,

        /// <summary>路径无效或挂载过程抛异常。</summary>
        Failed
    }

    /// <summary>
    /// 一条"等路径可用后重新挂载"的登记。
    /// 路径不存在时构造不出 FileSystemWatcher，所以这里只保存配置快照、已尝试次数与一次性 Timer。
    /// 内部状态加锁，保证 StopFolder/Dispose 与 Timer 回调并发时不会出现悬挂 Timer。
    /// </summary>
    private sealed class PendingMount : IDisposable
    {
        private readonly object _gate = new();
        private System.Threading.Timer? _timer;
        private int _attempt;
        private int _cancelled;
        private bool _disposed;

        public PendingMount(WatchedFolder folder, string resolvedPath)
        {
            Folder = folder;
            ResolvedPath = resolvedPath;
        }

        /// <summary>目录配置快照。</summary>
        public WatchedFolder Folder { get; }

        /// <summary>首次判定不可用时的真实路径，仅用于日志。</summary>
        public string ResolvedPath { get; }

        /// <summary>累计已安排的尝试次数（从 1 开始）。</summary>
        public int Attempt => Volatile.Read(ref _attempt);

        /// <summary>
        /// 用户已停止监控这个目录（StopFolder / StopAll 会在注销时标记）。
        /// 重试回调据此回滚"已经发生但不应存在"的挂载。
        /// </summary>
        public bool IsCancelled => Volatile.Read(ref _cancelled) != 0;

        /// <summary>标记为"已被用户停止"。只置位不清零，可重复调用。</summary>
        public void MarkCancelled()
        {
            Interlocked.Exchange(ref _cancelled, 1);
        }

        /// <summary>累加尝试次数并返回累加后的值。</summary>
        public int NextAttempt()
        {
            return Interlocked.Increment(ref _attempt);
        }

        /// <summary>登记新的重试定时器（替换旧的）；已释放时立即销毁定时器并返回 false。</summary>
        public bool TrySetTimer(System.Threading.Timer timer)
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    timer.Dispose();
                    return false;
                }

                _timer?.Dispose();
                _timer = timer;
                return true;
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _timer?.Dispose();
                _timer = null;
            }
        }
    }

    /// <summary>单个目录的运行时状态：watcher 与重挂定时器。</summary>
    private sealed class FolderRuntime : IDisposable
    {
        private readonly object _gate = new();
        private FileSystemWatcher? _watcher;
        private System.Threading.Timer? _retryTimer;
        private bool _disposed;

        public FolderRuntime(WatchedFolder folder, string resolvedPath)
        {
            Folder = folder;
            ResolvedPath = resolvedPath;
        }

        /// <summary>目录配置快照。</summary>
        public WatchedFolder Folder { get; }

        /// <summary>占位符展开后的真实路径。</summary>
        public string ResolvedPath { get; }

        /// <summary>挂上并启用 watcher。</summary>
        public void Attach(FileSystemWatcher watcher)
        {
            lock (_gate)
            {
                DisposeWatcherLocked();
                _watcher = watcher;
                watcher.EnableRaisingEvents = true;
            }
        }

        /// <summary>登记重挂定时器，登记前先卸载旧 watcher。</summary>
        public void ScheduleRetry(System.Threading.Timer timer)
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    timer.Dispose();
                    return;
                }

                DisposeWatcherLocked();
                _retryTimer?.Dispose();
                _retryTimer = timer;
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                DisposeWatcherLocked();
                _retryTimer?.Dispose();
                _retryTimer = null;
            }
        }

        /// <summary>卸载 watcher（调用方必须已持有 _gate）。</summary>
        private void DisposeWatcherLocked()
        {
            if (_watcher is null)
            {
                return;
            }

            try
            {
                _watcher.EnableRaisingEvents = false;
            }
            catch (Exception)
            {
                // watcher 已失效时关闭事件可能抛异常，忽略。
            }

            try
            {
                _watcher.Dispose();
            }
            catch (Exception)
            {
                // 释放失败无需上报。
            }

            _watcher = null;
        }
    }
}
