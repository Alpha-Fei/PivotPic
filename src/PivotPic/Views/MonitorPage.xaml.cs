using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PivotPic.Models;
using PivotPic.Services;
using PivotPic.ViewModels;

namespace PivotPic.Views;

/// <summary>
/// 监控页：总控开关 + 目录卡片列表 + 空态 + 拖拽添加。
/// <para>
/// 页面自身从 <see cref="AppServices"/> 取服务，不依赖外部注入；
/// 扫描进度按 FolderId 落到对应卡片上，卡片是唯一的数据源。
/// </para>
/// </summary>
public partial class MonitorPage : UserControl
{
    /// <summary>阈值输入只允许数字与小数点。</summary>
    private static readonly Regex NumericInputPattern = new(
        @"^[0-9.]$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>卡片集合，直接作为 <see cref="ItemsControl.ItemsSource"/>。</summary>
    private readonly ObservableCollection<FolderItemViewModel> _folders = new();

    /// <summary>定时刷新路径健康度（存在性 + 网络延迟），15 秒一轮。</summary>
    private readonly DispatcherTimer _healthTimer = new() { Interval = TimeSpan.FromSeconds(15) };

    /// <summary>拖拽高亮是否已经显示，避免每帧重复起动画。</summary>
    private bool _dropHintVisible;

    /// <summary>今日已处理张数（增量维护，不在每次读取时全量扫描）。</summary>
    private int _processedToday;

    /// <summary><see cref="_processedToday"/> 对应的日期，用于跨天归零。</summary>
    private DateTime _processedDate = DateTime.Today;

    /// <summary>构造页面：接好服务事件并首次装载配置。</summary>
    public MonitorPage()
    {
        InitializeComponent();

        FolderList.ItemsSource = _folders;

        if (AppServices.Monitor is not null)
        {
            AppServices.Monitor.ScanProgressChanged += OnScanProgressChanged;
        }

        if (AppServices.Config is not null)
        {
            AppServices.Config.Changed += OnConfigChanged;
        }

        if (AppServices.Log is not null)
        {
            AppServices.Log.EntryAdded += OnLogEntryAdded;
        }

        // 先回读当日日志播种「今日处理」，再建卡片，保证首帧数字就是对的。
        _processedToday = SeedProcessedToday();

        ReloadFromConfig();

        _healthTimer.Tick += async (_, _) => await RefreshHealthAsync();
        _healthTimer.Start();
        Loaded += async (_, _) => await RefreshHealthAsync();
    }

    /// <summary>今日已处理张数，主窗口状态栏读取。</summary>
    public int ProcessedToday => _processedToday;

    /// <summary>按当前配置重建卡片列表；配置变更或外部导入后调用。</summary>
    public void ReloadFromConfig()
    {
        SyncFolderViewModels(AppServices.Config?.Current?.Folders);
        UpdateEmptyState();
        SyncMasterToggle();
        _ = RefreshHealthAsync();
    }

    /// <summary>
    /// 把配置里的总开关状态同步到开关控件与整页视觉。
    /// 启动、托盘切换、配置重载都必须走这里——否则开关会停在 XAML 默认的「关」，
    /// 而实际监控仍在跑（看起来“关了却没生效”）。只改 IsChecked 不会触发 Click，不会反向写配置。
    /// </summary>
    private void SyncMasterToggle()
    {
        var enabled = AppServices.Config?.Current?.MonitoringEnabled ?? false;

        if (MasterToggle.IsChecked != enabled)
        {
            MasterToggle.IsChecked = enabled;
        }

        ApplyMonitoringVisual(enabled);
    }

    /// <summary>
    /// 刷新每张卡片的路径健康度。
    /// 各目录并行探测、谁先返回谁先上屏——单个慢速网络路径不会拖住其它卡片。
    /// </summary>
    private async Task RefreshHealthAsync()
    {
        var snapshot = _folders.ToList();
        if (snapshot.Count == 0)
        {
            return;
        }

        var tasks = snapshot.Select(async vm =>
        {
            var path = vm.ResolvedPath;
            var exists = !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);
            var isNetwork = NetworkPath.TryGetHost(path, out var host);

            long? latency = null;
            if (isNetwork)
            {
                latency = await NetworkPath.MeasureAsync(host).ConfigureAwait(false);
            }

            return (Vm: vm, Exists: exists, IsNetwork: isNetwork, Latency: latency);
        }).ToList();

        while (tasks.Count > 0)
        {
            var finished = await Task.WhenAny(tasks).ConfigureAwait(true);
            tasks.Remove(finished);

            var result = await finished.ConfigureAwait(true);
            result.Vm.UpdateHealth(result.Exists, result.IsNetwork, result.Latency);
        }
    }

    /// <summary>把 <see cref="_folders"/> 调整成与配置一致：能复用就复用，必要时增删与换位。</summary>
    private void SyncFolderViewModels(IReadOnlyList<WatchedFolder>? folders)
    {
        var scrollOffset = CardScroll.VerticalOffset;

        // 先算出目标序列：同一个 WatchedFolder 实例 + 同一个 Id 才复用，保证导入/重置后能重新绑定。
        var wanted = new List<FolderItemViewModel>(folders?.Count ?? 0);

        if (folders is not null)
        {
            foreach (var folder in folders)
            {
                if (folder is null)
                {
                    continue;
                }

                var reused = FindReusable(folder);

                if (reused is null)
                {
                    reused = new FolderItemViewModel(folder)
                    {
                        IsScanning = AppServices.Monitor?.IsFolderBusy(folder.Id) ?? false
                    };
                }

                wanted.Add(reused);
            }
        }

        // 1. 删掉目标序列里不再存在的卡片（从尾部删，索引不会错位）。
        for (var index = _folders.Count - 1; index >= 0; index--)
        {
            if (!wanted.Contains(_folders[index]))
            {
                _folders.RemoveAt(index);
            }
        }

        // 2. 逐位对齐：缺则补，错位则移动，内容变了则替换（替换时旧 VM 已不在目标序列中）。
        for (var index = 0; index < wanted.Count; index++)
        {
            var target = wanted[index];

            if (index >= _folders.Count)
            {
                _folders.Add(target);
                continue;
            }

            if (ReferenceEquals(_folders[index], target))
            {
                continue;
            }

            var existingIndex = _folders.IndexOf(target);

            if (existingIndex >= 0)
            {
                _folders.Move(existingIndex, index);
            }
            else
            {
                _folders[index] = target;
            }
        }

        if (scrollOffset > 0 && CardScroll.Visibility == Visibility.Visible)
        {
            // 布局在下一个渲染帧才完成，等排完再回填偏移，否则会被 ItemsControl 的首次测量抹掉。
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
                new Action(() => CardScroll.ScrollToVerticalOffset(scrollOffset)));
        }
    }

    /// <summary>在现有卡片里找可以复用的 VM（同一 Id 且同一配置实例）。</summary>
    private FolderItemViewModel? FindReusable(WatchedFolder folder)
    {
        foreach (var existing in _folders)
        {
            if (ReferenceEquals(existing.Model, folder))
            {
                return existing;
            }
        }

        return null;
    }

    /// <summary>有新告警时刷新徽标并做一次脉冲提示。</summary>
    /// <param name="newCount">本次新增的告警数量。</param>
    public void RefreshAlerts(int newCount)
    {
        UpdateStats();

        if (newCount <= 0)
        {
            return;
        }

        PulseAlertBadge();
    }

    // ============================================================
    // 服务事件
    // ============================================================

    private void OnConfigChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(ReloadFromConfig));
            return;
        }

        ReloadFromConfig();
    }

    private void OnLogEntryAdded(object? sender, LogEntry entry)
    {
        // 只对满足条件的条目做一次自增，绝不在此全量重扫 Recent。
        if (entry is not null && IsRotationAction(entry.Action))
        {
            EnsureProcessedDate();

            if (entry.Time.Date == _processedDate)
            {
                // 日志可能来自处理线程，用 Interlocked 保证自增不丢。
                Interlocked.Increment(ref _processedToday);
            }
        }

        // 文字刷新需要 UI 线程。
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(UpdateStats));
            return;
        }

        UpdateStats();
    }

    private void OnScanProgressChanged(object? sender, ScanProgress progress)
    {
        if (progress is null)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => ApplyScanProgress(progress)));
            return;
        }

        ApplyScanProgress(progress);
    }

    /// <summary>把一次进度快照落到对应卡片；Completed 时让进度条淡出 2 秒再隐藏。</summary>
    private void ApplyScanProgress(ScanProgress progress)
    {
        var vm = _folders.FirstOrDefault(item =>
            string.Equals(item.FolderId, progress.FolderId, StringComparison.OrdinalIgnoreCase));

        if (vm is null)
        {
            return;
        }

        vm.ScanScanned = progress.Scanned;
        vm.ScanTotal = progress.Total;

        if (!progress.Completed)
        {
            vm.IsScanning = true;
            return;
        }

        if (vm.IsScanning)
        {
            FadeOutScanRow(vm);
        }
        else
        {
            vm.IsScanning = false;
        }

        UpdateStats();
    }

    // ============================================================
    // 总控开关
    // ============================================================

    private void OnMasterToggleClick(object sender, RoutedEventArgs e)
    {
        var enabled = MasterToggle.IsChecked == true;
        var configService = AppServices.Config;
        var config = configService?.Current;

        if (configService is null || config is null)
        {
            return;
        }

        config.MonitoringEnabled = enabled;
        configService.SaveDebounced();

        if (enabled)
        {
            AppServices.Monitor?.StartAll();
        }
        else
        {
            AppServices.Monitor?.StopAll();
        }

        ApplyMonitoringVisual(enabled);
        UpdateStats();

        AppServices.Log?.Info("监控", enabled ? "已开启全部目录监控" : "已暂停全部目录监控");
        AppServices.Tray?.UpdateState(
            enabled ? TrayState.Monitoring : TrayState.Idle,
            AppServices.Alerts?.UnreadCount ?? 0);

        // 让外壳状态条同步（会回调 ReloadFromConfig，卡片按最新配置重建）。
        RefreshShell();
    }

    /// <summary>按总开关状态更新状态灯、文字与卡片可交互性。</summary>
    private void ApplyMonitoringVisual(bool enabled)
    {
        StatusText.Text = enabled ? "监控中" : "已暂停";
        StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty,
            enabled ? "Success" : "Text.Tertiary");

        if (enabled)
        {
            StartStatusBreathing();
        }
        else
        {
            StopStatusBreathing();
        }

        // 暂停时整块卡片区置灰并禁用交互；各目录自己的 Enabled 值不变。
        FolderList.IsEnabled = enabled;
        FolderList.Opacity = enabled ? 1 : 0.4;
        AddCard.IsEnabled = enabled;
        AddCard.Opacity = enabled ? 1 : 0.4;

        // 暂停时拖拽高亮也不再出现（拖放回调里同样有拦截），避免「界面说暂停、实际却在监控」的错觉。
        if (!enabled)
        {
            SetDropHint(false);
        }
    }

    private void StartStatusBreathing()
    {
        var animation = new DoubleAnimation(1, 0.45, new Duration(TimeSpan.FromSeconds(1.2)))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };

        StatusDot.BeginAnimation(OpacityProperty, animation);
    }

    private void StopStatusBreathing()
    {
        StatusDot.BeginAnimation(OpacityProperty, null);
        StatusDot.Opacity = 1;
    }

    // ============================================================
    // 卡片操作
    // ============================================================

    private void OnPathClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FolderItemViewModel vm } element)
        {
            return;
        }

        // 单击只展开/折叠长路径；双击打开目录设置（路径编辑已经收进弹窗里）。
        if (e.ClickCount >= 2)
        {
            OpenFolderSettings(vm);
        }
        else
        {
            vm.IsExpanded = !vm.IsExpanded;
        }

        e.Handled = true;
    }

    /// <summary>打开该目录的设置弹窗（路径、子文件夹、分辨率检查、忽略名单、幅面上限）。</summary>
    private void OnFolderSettingsClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FolderItemViewModel vm })
        {
            return;
        }

        OpenFolderSettings(vm);
    }

    /// <summary>打开目录设置弹窗；工具栏按钮与双击路径共用同一入口。</summary>
    private void OpenFolderSettings(FolderItemViewModel vm)
    {
        var dialog = new FolderSettingsDialog(vm)
        {
            Owner = Window.GetWindow(this)
        };

        dialog.ShowDialog();
    }


    // ============================================================
    // 忽略的子文件夹
    // ============================================================

    private static void CommitIgnoredFolder(TextBox box)
    {
        if (box.DataContext is not FolderItemViewModel vm)
        {
            return;
        }

        vm.AddIgnoredFolder(box.Text);
        box.Text = string.Empty;
    }


    /// <summary>从控件往上找卡片对应的视图模型。</summary>
    private static FolderItemViewModel? FindFolderViewModel(DependencyObject node)
    {
        for (var current = node; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is FrameworkElement { DataContext: FolderItemViewModel vm })
            {
                return vm;
            }
        }

        return null;
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FolderItemViewModel vm })
        {
            return;
        }

        try
        {
            var path = vm.ResolvedPath;
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            // 目录不存在时展开到最近的已存在父目录
            var target = System.IO.Directory.Exists(path)
                ? path
                : System.IO.Path.GetDirectoryName(path);

            if (!string.IsNullOrEmpty(target) && System.IO.Directory.Exists(target))
            {
                System.Diagnostics.Process.Start("explorer.exe", target);
                AppServices.Log?.Info("打开", $"已打开文件夹 {target}");
            }
            else
            {
                Notify("打开失败", "目录不存在");
            }
        }
        catch (Exception ex)
        {
            AppServices.Log?.Warn("打开", $"打开文件夹失败：{ex.Message}");
            Notify("打开失败", ex.Message);
        }
    }

    private void OnDeleteFolderClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FolderItemViewModel vm })
        {
            return;
        }

        var configService = AppServices.Config;
        var config = configService?.Current;

        if (configService is null || config is null)
        {
            return;
        }

        AppServices.Monitor?.StopFolder(vm.FolderId);
        config.Folders.Remove(vm.Model);
        configService.SaveNow();

        AppServices.Log?.Info("移除目录", $"已移除监控目录 {vm.ResolvedPath}");

        ReloadFromConfig();
        RefreshShell();
    }

    private void OnScanFolderClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FolderItemViewModel vm })
        {
            return;
        }

        if (!IsMonitoringEnabled())
        {
            Notify("监控已暂停", "请先打开右上角的监控开关，再执行扫描。");
            return;
        }

        // 扫描的通用逻辑（清零进度、置扫描态、后台跑、失败复位）已下沉到 ViewModel，
        // 目录设置弹窗关闭时的补扫复用同一个入口。
        vm.StartScan();
    }

    private void OnCancelScanClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FolderItemViewModel vm })
        {
            return;
        }

        AppServices.Monitor?.CancelScan(vm.FolderId);
    }

    // ============================================================
    // 添加目录
    // ============================================================

    private void OnAddFolderClick(object sender, RoutedEventArgs e)
    {
        if (!IsMonitoringEnabled())
        {
            Notify("监控已暂停", "请先打开右上角的监控开关，再添加目录。");
            return;
        }

        try
        {
            var dialog = new AddFolderDialog();
            var owner = Window.GetWindow(this);
            if (owner is not null)
            {
                dialog.Owner = owner;
            }

            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.ResultPath))
            {
                AddFolderByPath(dialog.ResultPath);
            }
        }
        catch (Exception ex)
        {
            AppServices.Log?.Error("添加目录", $"打开添加目录对话框失败：{ex.Message}");
            Notify("添加失败", ex.Message);
        }
    }

    /// <summary>把目录写入配置、挂上 watcher 并触发一次全量扫描。</summary>
    private void AddFolderByPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        // 含 { } 的路径视为时间占位符模板，展开后的目录将来才出现，跳过存在性检查直接添加。
        var isTemplate = path.Contains('{') && path.Contains('}');

        if (!isTemplate)
        {
            // 纯路径：目录不存在时明确提示用户，不静默跳过、也不写入配置。
            if (!Directory.Exists(path))
            {
                Notify("目录不存在", $"请先创建该目录：{path}");
                return;
            }
        }
        else
        {
            // 模板路径：仅在展开后目录尚不存在时记一条 Warn，方便排查。
            try
            {
                var resolvedPath = AppServices.PathTemplate?.Resolve(path) ?? path;
                if (!Directory.Exists(resolvedPath))
                {
                    AppServices.Log?.Warn("添加目录", $"模板展开后的目录暂不存在（稍后自动出现）：{resolvedPath}", path);
                }
            }
            catch (Exception)
            {
                // 解析失败不影响添加流程，监控启动时会再处理。
            }
        }

        var configService = AppServices.Config;
        var config = configService?.Current;

        if (configService is null || config is null)
        {
            return;
        }

        var normalized = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        foreach (var existing in config.Folders)
        {
            if (existing is null)
            {
                continue;
            }

            var current = (existing.Path ?? string.Empty)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (string.Equals(current, normalized, StringComparison.OrdinalIgnoreCase))
            {
                Notify("目录已存在", "该目录已经在监控列表中。");
                return;
            }
        }

        // 新目录默认关闭：先让用户把包含子文件夹、忽略名单、阈值等配好，再手动启用。
        // 这样刚加进来时不会立刻对目录里已有的图片动手。
        var folder = new WatchedFolder { Path = path, Enabled = false };
        config.Folders.Add(folder);
        configService.SaveNow();

        AppServices.Log?.Info("添加目录", $"已添加监控目录 {path}（默认关闭，配置后再启用）");

        ReloadFromConfig();
        RefreshShell();

        Notify("目录已添加（未启用）", "点卡片上的齿轮配置规则，配好后打开卡片开关即开始监控。");
    }

    // ============================================================
    // 拖拽添加
    // ============================================================

    private void OnPageDragOver(object sender, DragEventArgs e)
    {
        var accepted = IsMonitoringEnabled()
                       && e.Data.GetDataPresent(DataFormats.FileDrop)
                       && HasDirectory(e.Data.GetData(DataFormats.FileDrop) as string[]);

        e.Effects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;

        SetDropHint(accepted);
    }

    private void OnPageDragLeave(object sender, DragEventArgs e)
    {
        SetDropHint(false);
    }

    private void OnPageDrop(object sender, DragEventArgs e)
    {
        SetDropHint(false);

        if (!IsMonitoringEnabled())
        {
            return;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths)
        {
            return;
        }

        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                AddFolderByPath(path);
            }
        }

        e.Handled = true;
    }

    private static bool HasDirectory(string[]? paths)
    {
        return paths is not null && paths.Any(Directory.Exists);
    }

    /// <summary>显示/隐藏拖拽高亮：1px 强调描边 + 轻微缩放。</summary>
    private void SetDropHint(bool visible)
    {
        if (_dropHintVisible == visible)
        {
            return;
        }

        _dropHintVisible = visible;
        DropOverlay.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

        var duration = new Duration(TimeSpan.FromMilliseconds(160));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var scale = visible ? 0.995 : 1.0;

        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(scale, duration) { EasingFunction = ease });
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(scale, duration) { EasingFunction = ease });
    }

    // ============================================================
    // 阈值输入
    // ============================================================

    private void OnThresholdPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        // 非数字 / 非小数点直接吞掉，避免脏字符进入绑定。
        e.Handled = !NumericInputPattern.IsMatch(e.Text);
    }

    private void OnThresholdLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox box || box.DataContext is not FolderItemViewModel vm)
        {
            return;
        }

        if (double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value)
            && FolderItemViewModel.IsThresholdInRange(value))
        {
            vm.Threshold = value;
            return;
        }

        // 越界或非法：回退上次合法值，并短暂用 Danger 色标出来。
        vm.RevertThresholdText();
        FlashInvalidInput(box);
    }

    /// <summary>用 Danger 色闪 200ms 提示输入非法，然后恢复样式默认值。</summary>
    private static void FlashInvalidInput(Control control)
    {
        control.SetResourceReference(Control.BorderBrushProperty, "Danger");
        control.SetResourceReference(Control.ForegroundProperty, "Danger");

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };

        timer.Tick += (_, _) =>
        {
            timer.Stop();
            control.ClearValue(Control.BorderBrushProperty);
            control.ClearValue(Control.ForegroundProperty);
        };

        timer.Start();
    }

    // ============================================================
    // 视觉辅助
    // ============================================================

    private void UpdateEmptyState()
    {
        var empty = _folders.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        CardScroll.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateStats()
    {
        TodayText.Text = $"今日处理 {ProcessedToday} 张";
        AlertText.Text = $"告警 {AppServices.Alerts?.TodayCount ?? 0} 条";

        var unread = AppServices.Alerts?.UnreadCount ?? 0;
        AlertBadgeText.Text = unread.ToString(CultureInfo.CurrentCulture);
        AlertBadge.Visibility = unread > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PulseAlertBadge()
    {
        var duration = new Duration(TimeSpan.FromMilliseconds(180));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        var animation = new DoubleAnimation(1, 1.18, duration)
        {
            AutoReverse = true,
            EasingFunction = ease
        };

        BadgeScale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        BadgeScale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    /// <summary>扫描结束：把该卡片的进度行淡出 2 秒再隐藏（不对 Height 做动画）。</summary>
    private void FadeOutScanRow(FolderItemViewModel vm)
    {
        var row = FindScanRow(vm);

        if (row is null)
        {
            // 卡片尚未完成布局时退化为延时隐藏，保证状态一定会被复位。
            var fallback = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            fallback.Tick += (_, _) =>
            {
                fallback.Stop();
                vm.IsScanning = false;
            };
            fallback.Start();
            return;
        }

        var fade = new DoubleAnimation(1, 0, new Duration(TimeSpan.FromSeconds(2)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        fade.Completed += (_, _) =>
        {
            row.BeginAnimation(OpacityProperty, null);
            row.Opacity = 1;
            vm.IsScanning = false;
        };

        row.BeginAnimation(OpacityProperty, fade);
    }

    private FrameworkElement? FindScanRow(FolderItemViewModel vm)
    {
        var container = FolderList.ItemContainerGenerator.ContainerFromItem(vm);

        return container is null ? null : FindDescendantByName(container, "ScanRow");
    }

    private static FrameworkElement? FindDescendantByName(DependencyObject root, string name)
    {
        if (root is FrameworkElement element && string.Equals(element.Name, name, StringComparison.Ordinal))
        {
            return element;
        }

        var count = VisualTreeHelper.GetChildrenCount(root);

        for (var index = 0; index < count; index++)
        {
            var found = FindDescendantByName(VisualTreeHelper.GetChild(root, index), name);

            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>判断动作是否代表「真的改写了图片方向」。</summary>
    private static bool IsRotationAction(string? action)
    {
        return string.Equals(action, "Rotated", StringComparison.OrdinalIgnoreCase)
               || string.Equals(action, "ExifRewritten", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>跨天时把计数归零并重新播种。</summary>
    private void EnsureProcessedDate()
    {
        var today = DateTime.Today;

        if (_processedDate == today)
        {
            return;
        }

        _processedDate = today;
        Volatile.Write(ref _processedToday, CountRotatedInTodayLog());
    }

    /// <summary>
    /// 启动播种：优先回读当日日志文件（因此重启后不会归零），
    /// 文件不可用时退回内存里的 <see cref="ILogService.Recent"/>。
    /// </summary>
    private int SeedProcessedToday()
    {
        _processedDate = DateTime.Today;

        var fromFile = CountRotatedInTodayLog();

        if (fromFile >= 0)
        {
            return fromFile;
        }

        return CountRotatedInRecent();
    }

    /// <summary>
    /// 数当日日志文件里「改写了方向」的行数；文件不存在或读取失败返回 -1。
    /// <para>
    /// 行格式由 <see cref="LogEntry.ToString"/> 固定：时间 \t 级别 \t 目录 \t 文件名 \t 尺寸 \t 动作 \t 耗时 \t 说明，
    /// 所以第 6 列（下标 5）就是动作。文件名自带日期，无需再解析时间列。
    /// </para>
    /// </summary>
    private static int CountRotatedInTodayLog()
    {
        try
        {
            var directory = AppServices.Log?.LogDirectory;

            if (string.IsNullOrWhiteSpace(directory))
            {
                return -1;
            }

            var path = Path.Combine(directory, $"app-{DateTime.Now:yyyyMMdd}.log");

            if (!File.Exists(path))
            {
                return -1;
            }

            var count = 0;

            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrEmpty(line))
                {
                    continue;
                }

                var columns = line.Split('\t');

                if (columns.Length >= 6 && IsRotationAction(columns[5]))
                {
                    count++;
                }
            }

            return count;
        }
        catch (Exception)
        {
            // 日志文件被占用 / 无权限时退回内存统计，绝不让展示数字影响页面。
            return -1;
        }
    }

    /// <summary>内存窗口内的今日计数，供日志文件不可用时兜底。</summary>
    private static int CountRotatedInRecent()
    {
        var recent = AppServices.Log?.Recent;

        if (recent is null)
        {
            return 0;
        }

        var today = DateTime.Today;
        var count = 0;

        foreach (var entry in recent)
        {
            if (entry is not null && entry.Time.Date == today && IsRotationAction(entry.Action))
            {
                count++;
            }
        }

        return count;
    }

    private static bool IsMonitoringEnabled()
    {
        try
        {
            return AppServices.Config?.Current?.MonitoringEnabled ?? false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>请求外壳刷新状态条与托盘状态。</summary>
    private static void RefreshShell()
    {
        if (Application.Current is App app)
        {
            app.RefreshShellState();
        }
    }

    /// <summary>用托盘气泡做轻量提示（项目未引入独立 Toast 服务）。</summary>
    private static void Notify(string title, string message)
    {
        try
        {
            AppServices.Tray?.ShowBalloon(title, message);
        }
        catch (Exception)
        {
            // 通知失败不得影响主流程。
        }
    }
}
