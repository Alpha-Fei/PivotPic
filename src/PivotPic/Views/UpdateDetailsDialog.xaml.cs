using System;
using System.Threading;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using PivotPic.Models;
using PivotPic.Services;

namespace PivotPic.Views;

/// <summary>
/// 「更新详情」模态对话框：展示版本对比、更新说明、下载进度，并承载
/// 下载 → 校验 → 拉起安装程序的整条链路。
/// <para>
/// 所有对用户可见的文案都只出现「内置加速 / 第三方加速 / 直连」这类表述，
/// 不暴露任何底层组件名。
/// </para>
/// </summary>
public partial class UpdateDetailsDialog : Window
{
    /// <summary>更新说明的最大展示长度。超长说明截断，避免弹窗高度失控。</summary>
    private const int MaxBodyLength = 4000;

    /// <summary>首屏展示的说明行数，超出部分折叠。</summary>
    private const int BodyPreviewLines = 6;

    /// <summary>清洗后的完整说明（展开时用）。</summary>
    private string _bodyFull = string.Empty;

    /// <summary>对话框自身的阶段状态机。</summary>
    private enum Stage
    {
        /// <summary>待下载，主按钮为「立即更新」。</summary>
        Ready,

        /// <summary>下载 / 校验中，主按钮为「取消」。</summary>
        Downloading,

        /// <summary>正在拉起安装程序，按钮全部禁用。</summary>
        Launching,

        /// <summary>失败，主按钮为「重试」。</summary>
        Failed
    }

    private readonly ReleaseInfo _release;
    private readonly IUpdateCoordinator _coordinator;

    private Stage _stage = Stage.Ready;
    private bool _skipRequested;
    private bool _closingForReal;

    /// <summary>构造对话框。</summary>
    /// <param name="release">发现的新版本。</param>
    /// <param name="coordinator">下载与安装编排。</param>
    public UpdateDetailsDialog(ReleaseInfo release, IUpdateCoordinator coordinator)
    {
        _release = release ?? throw new ArgumentNullException(nameof(release));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

        InitializeComponent();

        FillStaticInfo();
        ApplyStage(Stage.Ready);

        // 之前已经下完（甚至窗口关了让它继续下）→ 直接显示「已就绪」，不要重下。
        if (coordinator.HasDownloadedPackage)
        {
            ApplyStage(Stage.Launching);
        }

        Loaded += OnDialogLoaded;
        Closing += OnDialogClosing;
    }

    // ============================================================
    // 初始化
    // ============================================================

    private void FillStaticInfo()
    {
        VersionFromText.Text = AppInfo.VersionText;
        VersionToText.Text = string.IsNullOrWhiteSpace(_release.TagName)
            ? UpdateDefaults.FormatTag(_release.Version)
            : _release.TagName;

        PublishedText.Text = _release.PublishedAt == DateTime.MinValue
            ? "发布时间未知"
            : "发布于 " + _release.PublishedAt.ToLocalTime().ToString("yyyy-MM-dd");

        // 资产体积缺失时用破折号占位：写「未知」会让人以为程序出了错。
        SizeText.Text = _release.SetupAsset.Size > 0
            ? UpdateProgress.Format(_release.SetupAsset.Size)
            : "—";

        // 通道是动态的（加速可用时用内置加速），先按当前状态给个初始值。
        ChannelText.Text = AppServices.ProxyHttp?.CurrentSource switch
        {
            UpdateDownloadSource.BuiltInProxy => "内置加速",
            _ => AppServices.Proxy?.Current.IsRunning == true ? "内置加速" : "直连"
        };

        ApplyBodyText(BuildBodyText(_release.Body));
    }

    /// <summary>
    /// 把 Markdown 原文降级成**纯文本**：`##`、`**`、`-`、反引号、链接语法全部去掉，
    /// 只留下人能直接读的句子。用户看的是「这次改了什么」，不是排版源码。
    /// </summary>
    private static string BuildBodyText(string body)
    {
        var text = (body ?? string.Empty).Replace("\r\n", "\n").Trim();

        if (text.Length == 0)
        {
            return "本次更新没有提供说明。";
        }

        var lines = text.Split('\n');
        var cleaned = new List<string>(lines.Length);

        foreach (var raw in lines)
        {
            var line = raw.Trim();

            // 图片语法整行丢弃（弹窗里也没法显示图）。
            if (line.StartsWith("![", StringComparison.Ordinal))
            {
                continue;
            }

            // 标题：去掉前导 # 之后当普通段落。
            line = line.TrimStart('#', ' ').Trim();

            // 列表符号统一成「· 」。
            if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
            {
                line = "· " + line[2..].Trim();
            }

            // 引用块。
            if (line.StartsWith("> ", StringComparison.Ordinal))
            {
                line = line[2..].Trim();
            }

            // 链接 [文字](地址) → 文字；裸 URL 保留（用户可能要手动打开）。
            line = Regex.Replace(line, @"\[([^\]]*)\]\([^)]*\)", "$1");

            // 行内代码 / 粗体 / 斜体标记。
            line = line.Replace("**", string.Empty)
                       .Replace("__", string.Empty)
                       .Replace("`", string.Empty);

            // 水平分割线没有信息量，跳过。
            if (line is "---" or "***" or "___")
            {
                continue;
            }

            cleaned.Add(line);
        }

        var result = string.Join('\n', cleaned).Trim();

        // 收敛连续空行，避免弹窗里出现大片空白。
        result = Regex.Replace(result, @"\n{3,}", "\n\n");

        return result.Length > MaxBodyLength
            ? result[..MaxBodyLength] + "\n\n…（说明过长已截断，可在浏览器打开发布页查看完整内容）"
            : result;
    }

    /// <summary>首屏只显示 6 行，剩下的点「查看完整说明」展开。</summary>
    private void ApplyBodyText(string full)
    {
        _bodyFull = full;

        var lines = full.Split('\n');
        if (lines.Length <= BodyPreviewLines)
        {
            BodyText.Text = full;
            BodyMoreButton.Visibility = Visibility.Collapsed;
            return;
        }

        BodyText.Text = string.Join('\n', lines.Take(BodyPreviewLines)) + "\n…";
        BodyMoreButton.Visibility = Visibility.Visible;
    }

    private void OnBodyMoreClick(object sender, RoutedEventArgs e)
    {
        BodyText.Text = _bodyFull;
        BodyMoreButton.Visibility = Visibility.Collapsed;
        BodyScroll.MaxHeight = 360;
    }

    /// <summary>弹出动画：0.94 → 1 缩放 + 淡入，与「添加监控目录」保持同一节奏。</summary>
    private void OnDialogLoaded(object sender, RoutedEventArgs e)
    {
        var ease = new System.Windows.Media.Animation.CubicEase
        {
            EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
        };
        var duration = new Duration(TimeSpan.FromMilliseconds(180));

        BeginAnimation(OpacityProperty,
            new System.Windows.Media.Animation.DoubleAnimation(0, 1, duration) { EasingFunction = ease });

        if (Content is FrameworkElement root
            && root.RenderTransform is System.Windows.Media.ScaleTransform scale)
        {
            scale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty,
                new System.Windows.Media.Animation.DoubleAnimation(0.94, 1, duration) { EasingFunction = ease });
            scale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty,
                new System.Windows.Media.Animation.DoubleAnimation(0.94, 1, duration) { EasingFunction = ease });
        }

        // 让键盘用户可以直接回车触发主操作。
        PrimaryButton.Dispatcher.BeginInvoke(DispatcherPriority.Input,
            new Action(() => PrimaryButton.Focus()));
    }

    // ============================================================
    // 状态机
    // ============================================================

    /// <summary>按阶段刷新所有控件的可用性与文案。</summary>
    private void ApplyStage(Stage stage)
    {
        _stage = stage;

        switch (stage)
        {
            case Stage.Ready:
                ProgressPanel.Visibility = Visibility.Collapsed;
                ErrorPanel.Visibility = Visibility.Collapsed;
                DownloadBar.Value = 0;
                PrimaryButton.Content = "立即更新";
                PrimaryButton.IsEnabled = true;
                LaterButton.IsEnabled = true;
                SkipButton.IsEnabled = true;
                break;

            case Stage.Downloading:
                ProgressPanel.Visibility = Visibility.Visible;
                ErrorPanel.Visibility = Visibility.Collapsed;
                ProgressTitle.Text = "正在下载";
                PrimaryButton.Content = "取消";
                PrimaryButton.IsEnabled = true;
                LaterButton.IsEnabled = true;
                SkipButton.IsEnabled = false;
                break;

            case Stage.Launching:
                ProgressPanel.Visibility = Visibility.Visible;
                ErrorPanel.Visibility = Visibility.Collapsed;
                ProgressTitle.Text = "正在启动安装程序…";
                ProgressPercent.Text = "100%";
                DownloadBar.Value = 100;
                PrimaryButton.Content = "请稍候";
                PrimaryButton.IsEnabled = false;
                LaterButton.IsEnabled = false;
                SkipButton.IsEnabled = false;
                break;

            case Stage.Failed:
                ProgressPanel.Visibility = Visibility.Collapsed;
                ErrorPanel.Visibility = Visibility.Visible;
                PrimaryButton.Content = "重试";
                PrimaryButton.IsEnabled = true;
                LaterButton.IsEnabled = true;
                SkipButton.IsEnabled = true;
                break;
        }
    }

    // ============================================================
    // 主流程
    // ============================================================

    private async void OnPrimaryClick(object sender, RoutedEventArgs e)
    {
        switch (_stage)
        {
            case Stage.Downloading:
                CancelDownload();
                return;

            case Stage.Ready:
            case Stage.Failed:
                await StartDownloadAsync();
                return;

            default:
                // Launching：程序马上会退出，忽略重复点击。
                return;
        }
    }

    /// <summary>
    /// 启动下载。**下载归协调器所有**：窗口关掉、按 Esc、点「稍后」都不会中断它。
    /// 这里只是「视图」，负责把进度画出来、下完了拉起安装程序。
    /// </summary>
    private async Task StartDownloadAsync()
    {
        ApplyStage(Stage.Downloading);

        var progress = new Progress<UpdateProgress>(OnProgress);

        try
        {
            await _coordinator.StartBackgroundDownload(_release, progress);

            if (_closingForReal)
            {
                // 窗口已经关了：只把状态准备好，安装由托盘入口负责，绝不弹窗打扰。
                return;
            }

            ApplyStage(Stage.Launching);
            _coordinator.LaunchDownloadedPackage();
        }
        catch (OperationCanceledException)
        {
            AppServices.Log?.Info("更新", "用户取消了更新下载");
            ApplyStage(Stage.Ready);
        }
        catch (UpdateFailedException ex)
        {
            AppServices.Log?.Warn("更新", $"更新失败：{ex.Message}");
            ShowError(ex.UserMessage);
            ApplyStage(Stage.Failed);
        }
        catch (Exception ex)
        {
            AppServices.Log?.Warn("更新", $"更新异常：{ex.Message}");
            ShowError("更新失败，请稍后重试");
            ApplyStage(Stage.Failed);
        }
        finally
        {
            // 下载的取消源归协调器，这里不持有任何资源。
        }
    }

    /// <summary>下载进度回调（已在 UI 线程）。</summary>
    private void OnProgress(UpdateProgress progress)
    {
        var percent = Math.Clamp(progress.Percent * 100, 0, 100);
        DownloadBar.Value = percent;
        ProgressPercent.Text = $"{(int)percent}%";
        ProgressSize.Text = progress.SizeText;
        ProgressSpeed.Text = progress.SpeedText;
        ProgressSource.Text = progress.SourceText;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
    }

    /// <summary>只有用户**显式点「取消下载」**时才会走到这里。</summary>
    private void CancelDownload()
    {
        _coordinator.CancelBackgroundDownload();
        ApplyStage(Stage.Ready);
    }

    // ============================================================
    // 关闭 / 稍后 / 不再提示
    // ============================================================

    /// <summary>「不再提示此版本」开关。</summary>
    private void OnSkipToggleClick(object sender, RoutedEventArgs e)
    {
        _skipRequested = !_skipRequested;
        SkipButton.Content = _skipRequested ? "✓ 将不再提示此版本" : "不再提示此版本";
    }

    private void OnLaterClick(object sender, RoutedEventArgs e) => FinishWithLater();

    private void OnCloseClick(object sender, RoutedEventArgs e) => FinishWithLater();

    /// <summary>
    /// 按「稍后」语义收尾：本次会话不再打扰；勾了「不再提示此版本」则持久跳过。
    /// 无论哪种，主窗横幅与托盘入口都保留，用户随时能主动升级。
    /// </summary>
    private void FinishWithLater()
    {
        // 注意：**不再取消下载**。下载的生命期归协调器，关掉窗口只是关掉「视图」，
        // 后台会继续下完，完成后由托盘提示。

        if (_stage == Stage.Launching)
        {
            // 已经在拉起安装程序，此时关窗只会让用户以为取消成功。
            return;
        }

        try
        {
            if (_skipRequested)
            {
                _coordinator.SkipVersion(_release.TagName);
            }
            else
            {
                _coordinator.DismissForSession();
            }
        }
        catch (Exception)
        {
            // 忽略。
        }

        _closingForReal = true;
        Close();
    }

    private void OnDialogClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closingForReal || _stage == Stage.Launching)
        {
            return;
        }

        // 主程序正在退出（安装程序已拉起 / 托盘退出）：这里若取消关闭，
        // Application.Shutdown 会被这一个窗口整体拦下，用户会以为「点了退出没反应」。
        if (App.Shell?.IsExiting == true)
        {
            return;
        }

        // 点右上角 X / Alt+F4 与「稍后」等效，但要走完提示节奏的处理。
        e.Cancel = true;
        FinishWithLater();
    }

    /// <summary>Enter = 主操作；Esc = 稍后。</summary>
    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            FinishWithLater();
            return;
        }

        if (e.Key == Key.Enter && _stage is Stage.Ready or Stage.Failed)
        {
            e.Handled = true;
            _ = StartDownloadAsync();
        }
    }
}
