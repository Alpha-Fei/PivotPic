using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Interop;
using PivotPic.Controls;
using PivotPic.Models;
using PivotPic.Services;
using PivotPic.Views;

namespace PivotPic;

/// <summary>主窗口：自绘标题栏 + 左侧导航 + 页面容器 + 状态条。</summary>
public partial class MainWindow : Window
{
    private readonly Dictionary<string, UserControl> _pages = new();
    private string _currentPage = "monitor";
    private bool _exiting;

    /// <summary>选中态滑动块：靠 TranslateTransform 平滑移动到目标项。</summary>
    private void AnimateNavIndicator(int index)
    {
        var target = NavListTopMargin + index * NavItemHeight;
        var ease = new System.Windows.Media.Animation.CubicEase
        {
            EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
        };
        NavShift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
            new System.Windows.Media.Animation.DoubleAnimation(
                NavShift.Y, target,
                new Duration(TimeSpan.FromMilliseconds(240)))
            {
                EasingFunction = ease
            });
    }

    /// <summary>导航项对应的图标，按顺序：监控/设置/日志/关于。</summary>
    private System.Windows.Shapes.Path[] NavIcons => new[]
    {
        IconMonitor, IconSettings, IconLog, IconAbout
    };

    /// <summary>
    /// 选中项图标变品牌紫，其余用次要色。
    /// 用 SetResourceReference 而非硬编码颜色——切换主题时自动跟随刷新。
    /// </summary>
    private void UpdateNavIconColors(int selectedIndex)
    {
        var icons = NavIcons;
        for (var i = 0; i < icons.Length; i++)
        {
            var icon = icons[i];
            if (icon is null)
            {
                continue;
            }

            icon.SetResourceReference(
                System.Windows.Shapes.Path.StrokeProperty,
                i == selectedIndex ? "Accent" : "Icon.Secondary");
        }
    }

    /// <summary>处理进度浮窗。独立窗口，主窗口隐藏到托盘时依然可见。</summary>
    private ProcessingPopup? _progress;

    /// <summary>导航列表上边距，必须与 XAML 中 NavList.Margin 的 8 一致。</summary>
    private const double NavListTopMargin = 8;

    /// <summary>单个导航按钮的高度，必须与 XAML 中每个按钮的 Height 一致。</summary>
    private const double NavItemHeight = 56;

    /// <summary>
    /// 程序是否正在退出。
    /// 退出流程中任何窗口都不得取消关闭——<c>Application.Shutdown</c> 只要遇到一个窗口取消就会整体取消。
    /// </summary>
    public bool IsExiting => _exiting;

    private MonitorPage? _monitorPage;
    private SettingsPage? _settingsPage;
    private LogPage? _logPage;
    private AboutPage? _aboutPage;

    public MainWindow()
    {
        InitializeComponent();

        RestoreWindowPlacement();
        WireTitleBar();
        WireShellEvents();
        WireUpdateEvents();

        _progress = new ProcessingPopup();
        WireProgressPopup();

        NavigateTo("monitor", animate: false);
        UpdateStatusBar();
    }

    // ============================================================
    // 更新提示（横幅 / 托盘徽标 / 模态详情）
    // ============================================================

    private void WireUpdateEvents()
    {
        var coordinator = AppServices.UpdateCoordinator;
        if (coordinator is null)
        {
            return;
        }

        coordinator.UpdateFound += OnUpdateFound;
        coordinator.StateChanged += (_, _) => RefreshUpdateBanner();

        RefreshUpdateBanner();
    }

    /// <summary>
    /// 静默检查发现新版本。窗口在前台就弹一次模态；常态驻留托盘时只用气泡 + 横幅，
    /// 不硬弹窗口打断用户手头的图片处理。
    /// </summary>
    private void OnUpdateFound(object? sender, ReleaseInfo release)
    {
        RefreshUpdateBanner();

        if (_exiting)
        {
            return;
        }

        if (!IsVisible || WindowState == WindowState.Minimized)
        {
            // 窗口在托盘：底部横幅与托盘菜单的「有更新」入口已经把信息给到了，
            // 这里不弹模态、也不发系统通知——等用户主动打开窗口自然能看到。
            return;
        }

        OpenUpdateDialog(release);
    }

    /// <summary>按协调器状态刷新底部横幅与托盘徽标。</summary>
    private void RefreshUpdateBanner()
    {
        var coordinator = AppServices.UpdateCoordinator;
        var release = coordinator?.AvailableUpdate;
        var hasUpdate = coordinator?.HasAvailableUpdate == true && release is not null;

        UpdateBanner.Visibility = hasUpdate ? Visibility.Visible : Visibility.Collapsed;

        if (hasUpdate && release is not null)
        {
            UpdateBannerText.Text = $"发现新版本 {release.TagName} · 当前 {AppInfo.VersionText}";
        }

        AppServices.Tray?.ShowUpdateBadge(hasUpdate ? release?.TagName : null);
    }

    /// <summary>更新弹窗是否已打开，避免模态窗叠加。</summary>
    private bool _updateDialogOpen;

    /// <summary>打开「更新详情」模态弹窗。已经打开时直接前置，不叠两个窗。</summary>
    public void OpenUpdateDialog(ReleaseInfo release)
    {
        var coordinator = AppServices.UpdateCoordinator;
        if (coordinator is null)
        {
            return;
        }

        if (_updateDialogOpen)
        {
            return;
        }

        _updateDialogOpen = true;

        try
        {
            var dialog = new UpdateDetailsDialog(release, coordinator) { Owner = this };
            dialog.ShowDialog();
        }
        catch (Exception ex)
        {
            AppServices.Log?.Warn("更新", $"打开更新详情失败：{ex.Message}");
        }
        finally
        {
            _updateDialogOpen = false;
            RefreshUpdateBanner();
        }
    }

    /// <summary>
    /// 供托盘菜单与关于页调用：有更新就打开详情，没有就跳到设置页的更新卡片，
    /// 保证任何入口点下去都有反馈。
    /// </summary>
    public void OpenUpdateEntry()
    {
        if (AppServices.UpdateCoordinator?.AvailableUpdate is { } release)
        {
            OpenUpdateDialog(release);
            return;
        }

        NavigateTo("settings");
    }

    private void OnBannerViewClick(object sender, RoutedEventArgs e)
    {
        if (AppServices.UpdateCoordinator?.AvailableUpdate is { } release)
        {
            OpenUpdateDialog(release);
        }
    }

    /// <summary>「忽略」只隐藏本次会话的横幅，不再自动弹窗；设置页入口仍在。</summary>
    private void OnBannerIgnoreClick(object sender, RoutedEventArgs e)
    {
        UpdateBanner.Visibility = Visibility.Collapsed;
        AppServices.UpdateCoordinator?.DismissForSession();
    }

    // ============================================================
    // 初始化
    // ============================================================

    /// <summary>
    /// 用服务端的批次进度驱动弹窗。总数由队列入队数决定，
    /// 界面只负责按 已处理/总数 填充进度条，不再自己猜"这批有多少张"。
    /// </summary>
    private void WireProgressPopup()
    {
        // 监控服务可能因装配失败为空：事件订阅不能因此炸掉主窗口构造。
        var monitor = AppServices.Monitor;
        if (monitor is null)
        {
            return;
        }

        monitor.BatchProgressChanged += (_, progress) =>
        {
            Dispatcher.BeginInvoke(() => _progress?.Update(progress));
        };
    }

    private void RestoreWindowPlacement()
    {
        var cfg = AppServices.Config?.Current;
        if (cfg is null)
        {
            return;
        }

        if (cfg.WindowWidth >= MinWidth && cfg.WindowHeight >= MinHeight)
        {
            Width = cfg.WindowWidth;
            Height = cfg.WindowHeight;
        }

        if (cfg.WindowLeft.HasValue && cfg.WindowTop.HasValue)
        {
            var virtualLeft = SystemParameters.VirtualScreenLeft;
            var virtualTop = SystemParameters.VirtualScreenTop;
            var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
            var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;

            // 屏幕布局变化后旧坐标可能落到可用区域之外，此时回到居中。
            var left = cfg.WindowLeft.Value;
            var top = cfg.WindowTop.Value;

            var visible = left >= virtualLeft - 40
                          && top >= virtualTop - 40
                          && left + 200 <= virtualRight
                          && top + 120 <= virtualBottom;

            if (visible)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = cfg.WindowLeft.Value;
                Top = cfg.WindowTop.Value;
            }
        }
    }

    private void WireTitleBar()
    {
        TitleBar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                ToggleMaximize();
                return;
            }

            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        };
    }

    private void WireShellEvents()
    {
        StateChanged += (_, _) =>
        {
            var maximized = WindowState == WindowState.Maximized;
            GlyphMax.Visibility = maximized ? Visibility.Collapsed : Visibility.Visible;
            GlyphRestore.Visibility = maximized ? Visibility.Visible : Visibility.Collapsed;

            if (maximized)
            {
                RootFrame.CornerRadius = new CornerRadius(0);
            }
            else
            {
                RootFrame.SetResourceReference(Border.CornerRadiusProperty, "Radius.Window");
            }
        };

        SourceInitialized += (_, _) => ApplyWindowChromeEffects();

        Closing += (_, e) =>
        {
            if (_exiting)
            {
                PersistWindowPlacement();
                return;
            }

            var action = AppServices.Config?.Current.CloseAction ?? CloseAction.MinimizeToTray;
            if (action == CloseAction.MinimizeToTray)
            {
                e.Cancel = true;
                Hide();
            }
            else
            {
                // 必须阻止默认关闭，改走完整退出流程。
                // 若只 Close() 而不 Shutdown，OnExplicitShutdown 模式下进程会存活，
                // 但窗口对象已销毁，托盘再唤出时 Show() 会抛「无法在 Window 关闭后调用 Show」。
                e.Cancel = true;
                RequestExit();
            }
        };

        LocationChanged += (_, _) => PersistWindowPlacement();
        SizeChanged += (_, _) => PersistWindowPlacement();
    }

    private void ApplyWindowChromeEffects()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var dark = !IsLightThemeActive();
        Interop.NativeMethods.TrySetDarkTitleBar(handle, dark);
        Interop.NativeMethods.TryEnableRoundedCorners(handle);

        if (dark)
        {
            Interop.NativeMethods.TryEnableMica(handle);
        }

        var source = HwndSource.FromHwnd(handle);
        source?.AddHook(WndProc);
    }

    private static bool IsLightThemeActive()
    {
        var mode = AppServices.Config?.Current.Theme ?? ThemeMode.Dark;
        return mode == ThemeMode.Light;
    }

    private void PersistWindowPlacement()
    {
        var cfg = AppServices.Config;
        if (cfg is null)
        {
            return;
        }

        if (WindowState == WindowState.Normal)
        {
            cfg.Current.WindowWidth = Width;
            cfg.Current.WindowHeight = Height;
            cfg.Current.WindowLeft = Left;
            cfg.Current.WindowTop = Top;
            cfg.SaveDebounced();
        }
    }

    /// <summary>让最大化按钮具备 Windows 11 的贴靠布局（Snap Layouts）悬停热区。</summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // 不再对最大化按钮返回 HTMAXBUTTON：让它保持客户区身份，
        // WPF Click 事件才能正常触发（Win11 贴靠弹窗也不会拦截）。
        return IntPtr.Zero;
    }

    // ============================================================
    // 标题栏按钮
    // ============================================================

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
    {
        // 交给系统：恢复 DWM 帧之后，最小化/恢复的过渡由 Windows 自己做。
        SystemCommands.MinimizeWindow(this);
    }

    private void OnMaximizeClick(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void ToggleMaximize()
    {
        if (WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(this);
        }
        else
        {
            SystemCommands.MaximizeWindow(this);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();


    private void OnNavClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string key })
        {
            NavigateTo(key);
        }
    }

    /// <summary>切换到指定页面，key 取 monitor / settings / log / about。</summary>
    public void NavigateTo(string key) => NavigateTo(key, animate: true);

    private void NavigateTo(string key, bool animate)
    {
        var index = key switch
        {
            "monitor" => 0,
            "settings" => 1,
            "log" => 2,
            "about" => 3,
            _ => 0
        };

        _currentPage = key;

        PageHost.Content = GetPage(key);

        AnimateNavIndicator(index);
        UpdateNavIconColors(index);

        if (animate && PageHost.Content is FrameworkElement fe)
        {
            // 页面切入只做位移 + 透明度，避免触发布局重排。
            var transform = new TranslateTransform(0, 12);
            fe.RenderTransform = transform;
            fe.Opacity = 0;

            var duration = new Duration(TimeSpan.FromMilliseconds(240));
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

            fe.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
            transform.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(12, 0, duration) { EasingFunction = ease });
        }
        else if (PageHost.Content is FrameworkElement plain)
        {
            plain.Opacity = 1;
        }

        UpdateNavState(key);
    }

    private void UpdateNavState(string key)
    {
        var accent = (Brush)FindResource("Icon.Primary");
        var muted = (Brush)FindResource("Icon.Secondary");

        NavMonitor.Tag = "monitor";
        SetIconBrush(NavMonitor, key == "monitor" ? accent : muted);
        SetIconBrush(NavSettings, key == "settings" ? accent : muted);
        SetIconBrush(NavLog, key == "log" ? accent : muted);
        SetIconBrush(NavAbout, key == "about" ? accent : muted);
    }

    private static void SetIconBrush(Button button, Brush brush)
    {
        if (button.Content is Path path)
        {
            path.Stroke = brush;
            return;
        }

        if (button.Content is Panel panel)
        {
            foreach (var child in panel.Children)
            {
                if (child is Path p)
                {
                    p.Stroke = brush;
                }
            }
        }
    }

    private UserControl GetPage(string key)
    {
        if (_pages.TryGetValue(key, out var cached))
        {
            return cached;
        }

        UserControl page = key switch
        {
            "settings" => _settingsPage ??= new SettingsPage(),
            "log" => _logPage ??= new LogPage(),
            "about" => _aboutPage ??= new AboutPage(),
            _ => _monitorPage ??= new MonitorPage()
        };

        _pages[key] = page;
        return page;
    }

    // ============================================================
    // 状态与对外接口
    // ============================================================

    private void UpdateStatusBar()
    {
        var cfg = AppServices.Config?.Current;
        var running = cfg?.MonitoringEnabled ?? false;

        StatusText.Text = running ? "监控中" : "已暂停";
        StatusDot.Fill = (Brush)FindResource(running ? "Success" : "Text.Tertiary");
        StatsText.Text = $"今日处理 {_monitorPage?.ProcessedToday ?? 0} 张 · 告警 {AppServices.Alerts?.TodayCount ?? 0} 条";
        QueueText.Text = $"目录 {AppServices.Config?.Current.Folders.Count ?? 0} 个";
        VersionText.Text = AppInfo.VersionText;
    }

    /// <summary>托盘切换监控状态后刷新界面。</summary>
    public void RefreshShellState()
    {
        _monitorPage?.ReloadFromConfig();
        UpdateStatusBar();
        AppServices.Tray?.UpdateState(
            (AppServices.Config?.Current.MonitoringEnabled ?? false) ? TrayState.Monitoring : TrayState.Idle,
            AppServices.Alerts?.UnreadCount ?? 0);
    }

    /// <summary>显示一批新告警（右侧滑入 Toast）。</summary>
    public void ShowAlerts(IReadOnlyList<AlertItem> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        _monitorPage?.RefreshAlerts(items.Count);
        UpdateStatusBar();

        ToastLayer.Show(items, this);
    }

    /// <summary>真正退出程序（区别于关闭窗口到托盘）。</summary>
    public void RequestExit()
    {
        _exiting = true;
        _progress?.Shutdown();

        // 轻提示是独立浮层窗口，退出前必须显式关掉，否则会留下一个没有主人的置顶窗口。
        Controls.ToastPopup.ShutdownIfAny();

        PersistWindowPlacement();
        AppServices.Config?.SaveNow();
        Application.Current.Shutdown(0);
    }
}
