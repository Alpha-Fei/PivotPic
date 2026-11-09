using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PivotPic.Models;
using PivotPic.Services;

namespace PivotPic.Views;

/// <summary>
/// 设置页：行为 / 外观 / 启动 / 配置 / 占位符帮助五张卡片。
/// <para>
/// 所有控件在 <see cref="LoadFromConfig"/> 期间被 <c>_loading</c> 屏蔽，
/// 避免程序化赋值反过来触发一次落盘。
/// </para>
/// </summary>
public partial class SettingsPage : UserControl
{
    /// <summary>当前卡片对应的引擎模式，用于拼提示语。</summary>
    private RotationMode _maxPixelsMode = RotationMode.LibVips;

    /// <summary>当前输入框对应的像素上限，用于拼提示语。</summary>
    private int _maxPixelsValue;

    /// <summary>
    /// 处理上限卡片：**两个引擎都遵守这个上限**，所以始终显示（以前切到高性能引擎会整卡隐藏，
    /// 但引擎实际仍需要一个上限，藏起来只会让用户以为「换引擎就不受限」）。
    /// 只有提示语随引擎变化：高性能引擎逐块处理、内存占用低，但同样会跳过超限图。
    /// </summary>
    private void UpdateMaxPixelsVisibility(RotationMode mode)
    {
        _maxPixelsMode = mode;
        MaxPixelsCard.Visibility = Visibility.Visible;
        RefreshMaxPixelsHint();
    }

    /// <summary>按「当前引擎 + 当前上限」刷新提示语。</summary>
    private void RefreshMaxPixelsHint()
    {
        var baseText = $"= {_maxPixelsValue:N0} 像素";

        MaxPixelsHint.Text = _maxPixelsMode == RotationMode.LibVips
            ? $"{baseText} · 逐块处理，内存占用低；另有 15 亿像素硬上限"
            : baseText;
    }

    // ============================================================
    // 文件名标记
    // ============================================================

    /// <summary>回填开关状态、后缀文本与示例。</summary>
    private void FillSuffix(AppConfig config)
    {
        ToggleSuffix.IsChecked = config.AddSuffix;
        SuffixBox.Text = config.Suffix;
        SuffixBox.IsEnabled = config.AddSuffix;
        UpdateSuffixHint(config.AddSuffix);
    }

    private void UpdateSuffixHint(bool enabled)
    {
        SuffixHint.Text = enabled ? "示例：图片名" + SuffixBox.Text + ".jpg" : "关闭时不改名";
    }

    /// <summary>开关切换：打开时若后缀为空则补默认值。</summary>
    private void OnSuffixToggleClick(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var configService = AppServices.Config;
        if (configService is null)
        {
            return;
        }

        var enabled = ToggleSuffix.IsChecked == true;
        configService.Current.AddSuffix = enabled;
        SuffixBox.IsEnabled = enabled;

        if (enabled && string.IsNullOrWhiteSpace(SuffixBox.Text))
        {
            SuffixBox.Text = "_已旋转";
            configService.Current.Suffix = SuffixBox.Text;
        }

        configService.SaveDebounced();
        UpdateSuffixHint(enabled);
    }

    /// <summary>只允许文件名合法字符，避免用户输入斜杠、冒号等。</summary>
    private void OnSuffixPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = e.Text.Any(ch => Path.GetInvalidFileNameChars().Contains(ch));
    }

    private void OnSuffixKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitSuffix();
            e.Handled = true;
        }
    }

    private void OnSuffixLostFocus(object sender, RoutedEventArgs e) => CommitSuffix();

    /// <summary>写回后缀：去空白、剔非法字符；为空则回退默认值。</summary>
    private void CommitSuffix()
    {
        if (_loading)
        {
            return;
        }

        var configService = AppServices.Config;
        if (configService is null)
        {
            return;
        }

        var text = (SuffixBox.Text ?? string.Empty).Trim();

        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            text = text.Replace(invalid.ToString(), string.Empty);
        }

        text = text.Trim();

        if (text.Length == 0)
        {
            text = "_已旋转";
        }

        if (text.Length > 24)
        {
            text = text.Substring(0, 24);
        }

        configService.Current.Suffix = text;
        configService.SaveDebounced();

        SuffixBox.Text = text;
        UpdateSuffixHint(ToggleSuffix.IsChecked == true);
    }

    // ============================================================
    // 处理上限
    // ============================================================

    /// <summary>把像素上限换算成 MP 填进输入框，并更新提示。</summary>
    private void FillMaxPixelsBox(int maxPixels)
    {
        var mp = maxPixels / 1_000_000.0;
        MaxPixelsBox.Text = mp.ToString(mp >= 100 ? "0" : "0.#", CultureInfo.CurrentCulture);

        _maxPixelsValue = maxPixels;
        RefreshMaxPixelsHint();
    }

    /// <summary>只允许数字和一个小数点。</summary>
    private void OnMaxPixelsPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        var allowed = e.Text.All(ch => char.IsDigit(ch) || ch == '.');
        e.Handled = !allowed;
    }

    /// <summary>回车即提交，省得再点别处。</summary>
    private void OnMaxPixelsKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitMaxPixels();
            e.Handled = true;
        }
    }

    /// <summary>失焦时提交。</summary>
    private void OnMaxPixelsLostFocus(object sender, RoutedEventArgs e) => CommitMaxPixels();

    /// <summary>校验并写回配置：允许 1–2000 MP。</summary>
    private void CommitMaxPixels()
    {
        if (_loading)
        {
            return;
        }

        var configService = AppServices.Config;
        if (configService is null)
        {
            return;
        }

        var text = (MaxPixelsBox.Text ?? string.Empty).Trim().Replace("，", ".").Replace(",", ".");

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var mp)
            || double.IsNaN(mp) || double.IsInfinity(mp))
        {
            FillMaxPixelsBox(configService.Current.MaxPixels);
            return;
        }

        mp = Math.Clamp(mp, 1, 2000);
        configService.Current.MaxPixels = (int)Math.Round(mp * 1_000_000);
        configService.SaveDebounced();
        FillMaxPixelsBox(configService.Current.MaxPixels);
    }

    /// <summary>程序化刷新开关，防止回写配置时递归触发事件。</summary>
    private bool _loading;

    /// <summary>构造页面：装载配置并监听外部配置变化。</summary>
    public SettingsPage()
    {
        InitializeComponent();

        LoadFromConfig();

        if (AppServices.Config is not null)
        {
            AppServices.Config.Changed += OnConfigChanged;
        }

        // 加速与更新的状态由后台线程推进，这里统一转回 UI 线程刷新。
        if (AppServices.Proxy is not null)
        {
            AppServices.Proxy.HealthChanged += OnProxyHealthChanged;
        }

        if (AppServices.UpdateCoordinator is not null)
        {
            AppServices.UpdateCoordinator.StateChanged += OnUpdateStateChanged;
        }
    }

    private void OnProxyHealthChanged(object? sender, ProxyHealth health)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(RefreshProxyStatus));
            return;
        }

        RefreshProxyStatus();
    }

    private void OnUpdateStateChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(RefreshUpdateStatus));
            return;
        }

        RefreshUpdateStatus();
    }

    // ============================================================
    // 读取 / 回写
    // ============================================================

    /// <summary>把配置回填到各个控件。</summary>
    private void LoadFromConfig()
    {
        var config = AppServices.Config?.Current;
        if (config is null)
        {
            return;
        }

        _loading = true;

        try
        {
            CmbRotateDirection.SelectedIndex = config.RotateDirection == RotateDirection.Clockwise ? 0 : 1;
            CmbRotationMode.SelectedIndex = config.RotationMode switch
            {
                RotationMode.LibVips => 0,
                RotationMode.Physical => 1,
                _ => 2
            };
            UpdateMaxPixelsVisibility(config.RotationMode);
            CmbCloseAction.SelectedIndex = config.CloseAction == CloseAction.MinimizeToTray ? 0 : 1;
            CmbTheme.SelectedIndex = config.Theme switch
            {
                ThemeMode.Dark => 0,
                ThemeMode.Light => 1,
                _ => 2
            };

            // 以注册表的真实状态为准，避免配置与系统状态不一致。
            // 以注册表实际状态为准（安装程序可能已写入），并把结果回写到配置，避免两处不一致。
            var autoStartEnabled = AutoStartRegistry.IsEnabled;
            ToggleAutoStart.IsChecked = autoStartEnabled;
            config.AutoStart = autoStartEnabled;
            UpdateAutoStartHint();
            ToggleProgressPopup.IsChecked = config.ShowProgressPopup;
            FillMaxPixelsBox(config.MaxPixels);
            FillSuffix(config);
            FillUpdateSection(config);
        }
        finally
        {
            _loading = false;
        }
    }

    private void OnConfigChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(LoadFromConfig));
            return;
        }

        LoadFromConfig();
    }

    // ============================================================
    // 行为
    // ============================================================

    private void OnRotateDirectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || AppServices.Config?.Current is not { } config)
        {
            return;
        }

        config.RotateDirection = CmbRotateDirection.SelectedIndex == 1
            ? RotateDirection.CounterClockwise
            : RotateDirection.Clockwise;

        AppServices.Config.SaveDebounced();
    }

    private void OnRotationModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || AppServices.Config?.Current is not { } config)
        {
            return;
        }

        var mode = CmbRotationMode.SelectedIndex switch
        {
            0 => RotationMode.LibVips,
            1 => RotationMode.Physical,
            _ => RotationMode.ExifOnly
        };

        config.RotationMode = mode;
        UpdateMaxPixelsVisibility(mode);

        AppServices.Config.SaveDebounced();

        AppServices.Log?.Info("设置", config.RotationMode == RotationMode.Physical
            ? "旋转方式切换为物理旋转像素"
            : "旋转方式切换为仅改写 EXIF 标记");
    }

    private void OnCloseActionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || AppServices.Config?.Current is not { } config)
        {
            return;
        }

        config.CloseAction = CmbCloseAction.SelectedIndex == 1
            ? CloseAction.Exit
            : CloseAction.MinimizeToTray;

        AppServices.Config.SaveDebounced();
    }

    // ============================================================
    // 外观
    // ============================================================

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || AppServices.Config?.Current is not { } config)
        {
            return;
        }

        config.Theme = CmbTheme.SelectedIndex switch
        {
            1 => ThemeMode.Light,
            2 => ThemeMode.System,
            _ => ThemeMode.Dark
        };

        // 立即换肤，再防抖落盘。
        App.ApplyTheme(config.Theme);
        AppServices.Config.SaveDebounced();
    }

    // ============================================================
    // 开机自启
    // ============================================================

    private void OnProgressPopupClick(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var config = AppServices.Config;
        if (config is null)
        {
            return;
        }

        config.Current.ShowProgressPopup = ToggleProgressPopup.IsChecked == true;
        config.SaveDebounced();
    }

    /// <summary>说明文字跟随实际写入位置：所有用户安装时自启在 HKLM。</summary>
    private void UpdateAutoStartHint()
    {
        AutoStartHint.Text = AutoStartRegistry.Probe() == AutoStartRegistry.Scope.Machine
            ? @"写入所有用户启动项（HKLM\...\Run）"
            : @"写入当前用户启动项（HKCU\...\Run）";
    }

    private void OnAutoStartClick(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var config = AppServices.Config;
        if (config is null)
        {
            return;
        }

        var enabled = ToggleAutoStart.IsChecked == true;

        try
        {
            if (!AutoStartRegistry.Set(enabled))
            {
                // 关闭所有用户范围的自启需要管理员权限，用户取消 UAC 时回退开关状态。
                ToggleAutoStart.IsChecked = !enabled;
                return;
            }

            config.Current.AutoStart = enabled;
            config.SaveDebounced();

            // 写入后复核一次，确保界面显示的就是真实状态。
            ToggleAutoStart.IsChecked = AutoStartRegistry.IsEnabled;
            UpdateAutoStartHint();
        }
        catch (Exception ex)
        {
            ToggleAutoStart.IsChecked = !enabled;
            MessageBox.Show($"设置开机自启失败：{ex.Message}", "正画", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ============================================================
    // 导入 / 导出
    // ============================================================

    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "导出配置",
                FileName = "PivotPic-config.json",
                DefaultExt = ".json",
                Filter = "配置文件 (*.json)|*.json|所有文件 (*.*)|*.*",
                AddExtension = true,
                OverwritePrompt = true
            };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            AppServices.Config?.Export(dialog.FileName);
            AppServices.Log?.Info("配置", $"配置已导出到 {dialog.FileName}");
            Notify("导出成功", dialog.FileName);
        }
        catch (Exception ex)
        {
            AppServices.Log?.Error("配置", $"导出配置失败：{ex.Message}");
            Notify("导出失败", ex.Message);
        }
    }

    private void OnImportClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "导入配置",
                DefaultExt = ".json",
                Filter = "配置文件 (*.json)|*.json|所有文件 (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            var confirm = System.Windows.MessageBox.Show(
                "导入会覆盖当前的全部配置（含监控目录），是否继续？",
                "导入配置",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.OK)
            {
                return;
            }

            AppServices.Config?.Import(dialog.FileName);

            // 导入后主题、监控状态都可能变化，逐项重新应用。
            App.ApplyTheme(AppServices.Config?.Current.Theme ?? ThemeMode.Dark);
            LoadFromConfig();

            if (AppServices.Config?.Current.MonitoringEnabled == true)
            {
                AppServices.Monitor?.StartAll();
            }
            else
            {
                AppServices.Monitor?.StopAll();
            }

            AppServices.Log?.Info("配置", $"已从 {dialog.FileName} 导入配置");
            Notify("导入成功", "配置已生效");

            if (Application.Current is App app)
            {
                app.RefreshShellState();
            }
        }
        catch (Exception ex)
        {
            AppServices.Log?.Error("配置", $"导入配置失败：{ex.Message}");
            Notify("导入失败", ex.Message);
        }
    }

    /// <summary>应用内轻提示（不再使用系统通知）。</summary>
    private static void Notify(string title, string message)
    {
        try
        {
            Controls.ToastPopup.Show(title, message);
        }
        catch (Exception)
        {
            // 通知失败不得影响主流程。
        }
    }

    // ============================================================
    // 更新与网络
    // ============================================================

    /// <summary>回填「更新与网络」整卡。</summary>
    private void FillUpdateSection(AppConfig config)
    {
        ToggleAutoUpdate.IsChecked = config.AutoUpdateCheck;
        AutoUpdateHint.Text = config.AutoUpdateCheck
            ? $"启动后检查 · 每 {config.UpdateCheckIntervalHours} 小时一次"
            : "已关闭自动检查，仍可手动检查";

        ToggleProxy.IsChecked = config.ProxyMode != ProxyMode.Off;
        ToggleSystemPac.IsChecked = config.ProxyMode == ProxyMode.SystemWide;
        ToggleSystemPac.IsEnabled = config.ProxyMode != ProxyMode.Off;

        RefreshProxyStatus();
        RefreshUpdateStatus();
    }

    /// <summary>按加速组件的真实状态刷新状态点与文案。文案里不出现任何底层组件名。</summary>
    private void RefreshProxyStatus()
    {
        var health = AppServices.Proxy?.Current ?? ProxyHealth.Off;

        ProxyStatusText.Text = string.IsNullOrEmpty(health.FailureText)
            ? health.DisplayText
            : $"{health.DisplayText} · {health.FailureText}";

        // 端口与作用范围放进悬停提示：需要排查的人能看到，普通用户不必被技术细节打扰。
        ProxyStatusText.ToolTip = health.DisplayDetail;

        var colorKey = health.Status switch
        {
            ProxyStatus.RunningSystem => "Success",
            ProxyStatus.RunningProcess => "Success",
            ProxyStatus.Starting => "Warning",
            ProxyStatus.Failed => "Warning",
            _ => "Text.Tertiary"
        };

        ProxyDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, colorKey);
    }

    /// <summary>刷新版本号、上次检查与「查看新版本」入口。</summary>
    private void RefreshUpdateStatus()
    {
        CurrentVersionText.Text = AppInfo.VersionText;

        var coordinator = AppServices.UpdateCoordinator;
        LastCheckText.Text = coordinator?.LastCheckText ?? "尚未检查";

        var release = coordinator?.AvailableUpdate;
        var hasUpdate = coordinator?.HasAvailableUpdate == true && release is not null;

        BtnViewUpdate.Visibility = hasUpdate ? Visibility.Visible : Visibility.Collapsed;

        if (hasUpdate && release is not null)
        {
            BtnViewUpdate.Content = $"查看 {release.TagName}";
        }
    }

    private void OnAutoUpdateClick(object sender, RoutedEventArgs e)
    {
        if (_loading || AppServices.Config?.Current is not { } config)
        {
            return;
        }

        config.AutoUpdateCheck = ToggleAutoUpdate.IsChecked == true;
        AppServices.Config.SaveDebounced();

        AutoUpdateHint.Text = config.AutoUpdateCheck
            ? $"启动后检查 · 每 {config.UpdateCheckIntervalHours} 小时一次"
            : "已关闭自动检查，仍可手动检查";

        AppServices.Log?.Info("更新", config.AutoUpdateCheck ? "已开启自动检查更新" : "已关闭自动检查更新");
    }

    private async void OnProxyToggleClick(object sender, RoutedEventArgs e)
    {
        if (_loading || AppServices.Config?.Current is not { } config)
        {
            return;
        }

        var enable = ToggleProxy.IsChecked == true;

        config.ProxyMode = enable
            ? (ToggleSystemPac.IsChecked == true ? ProxyMode.SystemWide : ProxyMode.Passthrough)
            : ProxyMode.Off;

        AppServices.Config.SaveDebounced();
        ToggleSystemPac.IsEnabled = enable;

        await ApplyProxyModeAsync(config.ProxyMode);
    }

    private async void OnSystemPacToggleClick(object sender, RoutedEventArgs e)
    {
        if (_loading || AppServices.Config?.Current is not { } config)
        {
            return;
        }

        var enable = ToggleSystemPac.IsChecked == true;

        if (enable && config.ProxyMode == ProxyMode.Off)
        {
            // 打开系统分流必然要启动加速，顺手把总开关拨上去，避免出现「分流开但加速关」的矛盾状态。
            ToggleProxy.IsChecked = true;
            config.ProxyMode = ProxyMode.SystemWide;
        }
        else
        {
            config.ProxyMode = enable ? ProxyMode.SystemWide : ProxyMode.Passthrough;
        }

        ToggleSystemPac.IsEnabled = config.ProxyMode != ProxyMode.Off;
        AppServices.Config.SaveDebounced();

        await ApplyProxyModeAsync(config.ProxyMode);
    }

    /// <summary>把配置里的模式应用到加速组件，失败只降级直连并提示一次。</summary>
    private async Task ApplyProxyModeAsync(ProxyMode mode)
    {
        if (AppServices.Proxy is null)
        {
            return;
        }

        try
        {
            var ok = await AppServices.Proxy.ApplyModeAsync(mode);

            if (!ok && mode != ProxyMode.Off)
            {
                Notify("加速未启用", "加速组件启动失败，已改用直连。");
            }
        }
        catch (Exception ex)
        {
            AppServices.Log?.Warn("加速", $"切换加速模式失败：{ex.Message}");
            Notify("加速未启用", "已改用直连。");
        }
        finally
        {
            RefreshProxyStatus();
        }
    }

    /// <summary>手动检查更新。进行中按钮置灰显示「检查中…」，结束后用气泡回报结果。</summary>
    private async void OnCheckUpdateClick(object sender, RoutedEventArgs e)
    {
        var coordinator = AppServices.UpdateCoordinator;
        if (coordinator is null)
        {
            Notify("检查更新", "更新模块尚未就绪，请稍后重试");
            return;
        }

        BtnCheckUpdate.IsEnabled = false;
        BtnCheckUpdate.Content = "检查中…";

        try
        {
            var release = await coordinator.CheckNowAsync();
            RefreshUpdateStatus();

            if (release is not null)
            {
                Notify("发现新版本", $"{release.TagName} 已可更新");
                OpenUpdateDialog(release);
            }
            else
            {
                Notify("检查更新", coordinator.LastCheckText);
            }
        }
        finally
        {
            BtnCheckUpdate.IsEnabled = true;
            BtnCheckUpdate.Content = "检查更新";
        }
    }

    private void OnViewUpdateClick(object sender, RoutedEventArgs e)
    {
        if (AppServices.UpdateCoordinator?.AvailableUpdate is { } release)
        {
            OpenUpdateDialog(release);
        }
    }

    /// <summary>打开更新详情弹窗（模态），关闭后同步一次卡片状态。</summary>
    private void OpenUpdateDialog(ReleaseInfo release)
    {
        var coordinator = AppServices.UpdateCoordinator;
        if (coordinator is null)
        {
            return;
        }

        try
        {
            var dialog = new UpdateDetailsDialog(release, coordinator)
            {
                Owner = Window.GetWindow(this)
            };

            dialog.ShowDialog();
        }
        catch (Exception ex)
        {
            AppServices.Log?.Warn("更新", $"打开更新详情失败：{ex.Message}");
            Notify("更新", "无法打开更新详情");
        }
        finally
        {
            RefreshUpdateStatus();
        }
    }
}
