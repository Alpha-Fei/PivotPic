using System.IO;
using System.Windows;
using System.Windows.Threading;
using PivotPic.Models;
using PivotPic.Services;

namespace PivotPic;

/// <summary>应用入口与全局生命周期编排。</summary>
public partial class App : Application
{
    private ISingleInstanceService? _single;
    private MainWindow? _mainWindow;

    /// <summary>是否以静默方式启动（--tray 参数，用于开机自启）。</summary>
    public static bool StartHidden { get; private set; }

    /// <summary>主窗口实例，供托盘与页面导航复用。</summary>
    public static MainWindow? Shell { get; private set; }

    /// <summary>
    /// 启动引导日志。LogService 尚未就位时（App.xaml 资源加载失败等极早期崩溃），
    /// 只能靠这个文件定位卡在哪一步。正常运行时不产生内容。
    /// </summary>
    private static void BootTrace(string stage)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "pivotpic_boot.log");
            File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff}  {stage}{Environment.NewLine}");
        }
        catch
        {
            // 引导日志本身失败不能影响启动。
        }
    }

    public App()
    {
        BootTrace("App ctor entered");
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        BootTrace("OnStartup entered");

        // 提权修改「所有用户」自启项：安装到 Program Files 时自启写在 HKLM，普通权限改不动。
        var elevateArg = e.Args.FirstOrDefault(a => a.StartsWith("--autostart-machine=", StringComparison.OrdinalIgnoreCase));
        if (elevateArg is not null && AutoStartRegistry.TryParseElevateSwitch(elevateArg, out var machineEnable))
        {
            Shutdown(AutoStartRegistry.SetMachineAutoStart(machineEnable) ? 0 : 1);
            return;
        }

        // 卸载模式：不加载任何服务，确认后清理并退出。
        // 由控制面板的卸载项直接调用 PivotPic.exe --uninstall。
        if (e.Args.Any(a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase)))
        {
            Shutdown(SelfUninstaller.Run());
            return;
        }

        // 启动阶段任何一次未捕获异常都会直接杀进程，托盘里什么都留不下，
        // 用户看到的就是「双击没反应」。因此整段包起来，出错走通知 + 优雅退出。
        try
        {
            StartHidden = e.Args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));

            // 单实例：拿不到锁就把已有实例唤到前台，本进程立刻退出。
            // 必须在托盘初始化之前完成，否则第二个实例会先闪一下托盘图标再消失。
            _single = new SingleInstanceService();
            var acquired = _single.TryAcquire();
            BootTrace($"SingleInstance.TryAcquire = {acquired}");
            if (!acquired)
            {
                BootTrace("单实例判定失败，退出");
                Shutdown(0);
                return;
            }

            _single.ActivationRequested += OnActivationRequested;

            HookGlobalExceptionHandlers();

            // 服务装配：日志必须最先就位，后续初始化失败才有地方记录。
            AppServices.Log = new LogService();
            AppServices.Config = new ConfigService();
            AppServices.Config?.Load();
            AppServices.PathTemplate = new PathTemplateService();
            AppServices.IgnoreSet = new IgnoreSet();
            AppServices.Image = new ImageService();
            AppServices.Alerts = new AlertService();
            AppServices.Monitor = new MonitorService();
            AppServices.Tray = new TrayService();

            BootTrace("核心服务装配完成");

            // 更新与网络：独立装配。任何一步失败都只关掉更新模块，主功能（旋转/监控/托盘）照常。
            AssembleUpdateModule(e.Args);

            BootTrace("服务装配完成");
            ApplyTheme(AppServices.Config?.Current.Theme ?? ThemeMode.Dark);
            BootTrace("主题应用完成");

            _mainWindow = new MainWindow();
            Shell = _mainWindow;
            MainWindow = _mainWindow;

            // 手动启动一律显示窗口；只有 --tray（开机自启）才静默驻留托盘。
            if (!StartHidden)
            {
                _mainWindow.Show();
            }

            BootTrace("主窗口创建完成");
            AppServices.Tray?.Initialize();
            BootTrace("托盘初始化完成");

            // 先订阅再启动监控，否则目录里已有的超标图片会在订阅前就被扫描出来，告警丢失。
            if (AppServices.Alerts is not null)
            {
                AppServices.Alerts.AlertsRaised += OnAlertsRaised;
            }

            if (AppServices.Config?.Current.MonitoringEnabled == true)
            {
                // StartAll 内部已经包含一次后台补扫，这里不再重复扫描。
                AppServices.Monitor?.StartAll();
                AppServices.Tray?.UpdateState(TrayState.Monitoring, AppServices.Alerts?.UnreadCount ?? 0);
            }
            else
            {
                AppServices.Tray?.UpdateState(TrayState.Idle, AppServices.Alerts?.UnreadCount ?? 0);
            }

            if (AppServices.Config is not null)
            {
                AppServices.Config.Current.FirstRun = false;
                AppServices.Config.SaveDebounced();
            }

            // 托盘的「有更新」入口指向主窗口的更新详情；没有更新时跳到设置页的更新卡片。
            TrayService.UpdateEntryInvoked = () =>
            {
                ShowShell();
                Shell?.OpenUpdateEntry();
            };

            // 更新检查最后启动：它会在后台拉起加速组件并延迟 5 秒做静默检查，
            // 放到最后可以确保主界面、监控、托盘都先就位。
            AppServices.UpdateCoordinator?.Start();

            AppServices.Log?.Info("Startup", "程序已启动");
        }
        catch (Exception ex)
        {
            // 走到这里说明连主窗口都建不起来。尽力弹一个系统级提示再退出，
            // 否则用户只会看到「双击没反应」。
            BootTrace($"启动异常：{ex.GetType().Name} {ex.Message}");
            AppServices.Log?.Error("Startup", $"启动失败：{ex}");
            MessageBox.Show(
                $"正画 PivotPic 启动失败：\n\n{ex.Message}\n\n详情见日志：{AppServices.Log?.LogDirectory ?? "%AppData%\\PivotPic\\logs"}",
                "正画 PivotPic",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    // ============================================================
    // 更新模块装配
    // ============================================================

    /// <summary>
    /// 装配「更新与网络」模块。
    /// <para>
    /// 与核心服务分开装配：这一段任何一步失败都只关掉更新模块，
    /// 旋转、监控、托盘等主功能照常工作——绝不因为「更新组件起不来」而让程序打不开。
    /// </para>
    /// <para>
    /// 逃生开关（两个都支持，优先看参数）：启动参数 <c>--no-update</c>、
    /// 环境变量 <c>PIVOTPIC_DISABLE_UPDATER=1</c>。
    /// 现场遇到加速/网络相关疑难时，可以让用户加参数启动来排除更新模块。
    /// </para>
    /// </summary>
    private void AssembleUpdateModule(string[] args)
    {
        // 退出委托先注入：服务层通过它请求退出，不直接引用 App / MainWindow。
        AppServices.ExitRequested = () => Shell?.RequestExit();

        if (IsUpdaterDisabled(args))
        {
            BootTrace("更新模块已按逃生开关禁用");
            AppServices.Log?.Warn("更新", "更新模块已被启动参数或环境变量禁用，其余功能正常");
            return;
        }

        try
        {
            // 依赖顺序不可调换：PAC → 加速进程 → HTTP 客户端 → 更新服务 → 编排
            AppServices.Pac = new PacService();
            AppServices.Proxy = new GitPortService();
            AppServices.ProxyHttp = new ProxyHttpClientProvider();
            AppServices.Updates = new UpdateService();
            AppServices.UpdateCoordinator = new UpdateCoordinator(AppServices.Updates);

            BootTrace("更新模块装配完成");
        }
        catch (Exception ex)
        {
            // 回滚半成品，避免留下「有 Coordinator 但没有 Proxy」这种错位组合。
            AppServices.UpdateCoordinator = null;
            AppServices.Updates = null;
            AppServices.ProxyHttp = null;
            AppServices.Proxy = null;
            AppServices.Pac = null;

            BootTrace($"更新模块装配失败：{ex.GetType().Name} {ex.Message}");
            AppServices.Log?.Warn("更新", $"更新模块装配失败，已跳过（主功能不受影响）：{ex.Message}");
        }
    }

    /// <summary>是否按逃生开关禁用更新模块。</summary>
    private static bool IsUpdaterDisabled(string[] args)
    {
        if (args.Any(a => string.Equals(a, "--no-update", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var flag = Environment.GetEnvironmentVariable("PIVOTPIC_DISABLE_UPDATER");

        return !string.IsNullOrWhiteSpace(flag)
               && (flag.Trim() == "1" || flag.Trim().Equals("true", StringComparison.OrdinalIgnoreCase));
    }

    private void OnAlertsRaised(object? sender, IReadOnlyList<AlertItem> items)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_mainWindow is not null)
            {
                _mainWindow.ShowAlerts(items);
            }

            AppServices.Tray?.UpdateState(TrayState.Alert, AppServices.Alerts?.UnreadCount ?? 0);
        });
    }

    private void OnActivationRequested()
    {
        Dispatcher.BeginInvoke(ShowShell);
    }

    /// <summary>供托盘等非 UI 组件请求刷新外壳界面。</summary>
    public void RefreshShellState()
    {
        Dispatcher.BeginInvoke(() => _mainWindow?.RefreshShellState());
    }

    /// <summary>把主窗口显示并激活到前台。</summary>
    public void ShowShell()
    {
        if (_mainWindow is null)
        {
            return;
        }

        if (!_mainWindow.IsVisible)
        {
            _mainWindow.Show();
        }

        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }

        _mainWindow.Activate();
        Interop.NativeMethods.SetForegroundWindow(new System.Windows.Interop.WindowInteropHelper(_mainWindow).Handle);
    }

    private void HookGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            AppServices.Log?.Error("Unhandled", args.Exception.Message);
            TryNotify($"发生未处理异常：{args.Exception.Message}");
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                AppServices.Log?.Error("Unhandled", ex.Message);
            }
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppServices.Log?.Error("UnobservedTask", args.Exception.Message);
            args.SetObserved();
        };
    }

    /// <summary>
    /// 未处理异常的用户可见提示：只走应用内浮层，绝不发系统通知。
    /// 主窗口还没建起来（启动早期异常）时没有可挂靠的界面，只留日志。
    /// </summary>
    private void TryNotify(string message)
    {
        try
        {
            if (Shell is null)
            {
                return;
            }

            Controls.ToastPopup.Show("正画 PivotPic", message);
        }
        catch
        {
            // 通知失败不得影响主流程。
        }
    }

    /// <summary>按配置切换深色 / 浅色主题字典。</summary>
    public static void ApplyTheme(ThemeMode mode)
    {
        var effective = mode switch
        {
            ThemeMode.Light => false,
            ThemeMode.Dark => true,
            _ => !IsSystemLightTheme()
        };

        var source = effective
            ? new Uri("pack://application:,,,/Themes/Colors.xaml")
            : new Uri("pack://application:,,,/Themes/Colors.Light.xaml");

        var dict = new ResourceDictionary { Source = source };
        var merged = Current.Resources.MergedDictionaries;

        // 第一个字典固定为主题色板，替换它即可整站换肤。
        for (var i = 0; i < merged.Count; i++)
        {
            var s = merged[i].Source?.OriginalString ?? string.Empty;
            if (s.Contains("Colors", StringComparison.OrdinalIgnoreCase))
            {
                merged[i] = dict;
                return;
            }
        }

        merged.Insert(0, dict);
    }

    private static bool IsSystemLightTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            return value is int i && i == 1;
        }
        catch
        {
            return false;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            AppServices.Monitor?.StopAll();

            // ── 更新与网络收尾 ──
            // 顺序是硬契约：先停加速组件 → 再按快照还原系统代理 → 最后释放。
            // 反过来做会留下一个指向已关闭端口的 PAC，用户卸载后所有 GitHub 访问都不通。
            try
            {
                AppServices.UpdateCoordinator?.Dispose();
            }
            catch (Exception)
            {
                // 忽略。
            }

            try
            {
                AppServices.Proxy?.StopAsync().GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                // 停不下进程也要继续还原 PAC。
            }

            try
            {
                AppServices.Pac?.RestoreAsync().GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                // 还原失败时 pac-backup.json 会被保留，下次启动或卸载时再还原。
            }

            try
            {
                (AppServices.ProxyHttp as IDisposable)?.Dispose();
                (AppServices.Proxy as IDisposable)?.Dispose();
            }
            catch (Exception)
            {
                // 忽略。
            }

            AppServices.Config?.SaveNow();

            // 配置写入有 500ms 防抖定时器，不 Dispose 会在进程退出瞬间丢掉最后一次写入。
            (AppServices.Config as IDisposable)?.Dispose();

            AppServices.Tray?.Dispose();
            _single?.Dispose();

            // 退出委托指向本窗口，退出完成后解除引用，避免残留回调。
            AppServices.ExitRequested = null;
        }
        catch
        {
            // 退出阶段的异常不得阻止进程结束。
        }

        base.OnExit(e);
    }
}
