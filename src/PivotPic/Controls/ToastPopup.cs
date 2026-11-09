using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace PivotPic.Controls;

/// <summary>
/// 应用内轻提示（替代托盘气泡 / 系统通知中心）。
/// <para>
/// 为什么不用托盘气泡接口（NotifyIcon 的气泡方法）：Win10/11 会把它升级成系统通知中心里的通知，
/// 用户会在右下角收到一条带应用名的系统横幅，还可能留在通知中心里——对一个「常驻托盘的图片小工具」来说太重了。
/// 这里改成自绘浮层：无边框、置顶、不抢焦点、不进任务栏与 Alt+Tab，2.4 秒后自动淡出。
/// </para>
/// <para>
/// 复用 <see cref="ProcessingPopup"/> 的窗口套路（AllowsTransparency + WS_EX_TOOLWINDOW + 不激活），
/// 但位置固定在**右下角**，避免和底部居中的进度卡片打架。
/// 同一时刻只保留一条：新提示直接覆盖旧提示并重新计时。
/// </para>
/// </summary>
public sealed class ToastPopup : Window
{
    /// <summary>卡片宽度（不含阴影留白）。</summary>
    private const double CardWidth = 320;

    /// <summary>四周留给阴影的透明边距。</summary>
    private const double ShadowPadding = 18;

    /// <summary>距屏幕工作区右侧的距离。</summary>
    private const double RightGap = 20;

    /// <summary>距屏幕工作区底部的距离。</summary>
    private const double BottomGap = 20;

    /// <summary>停留时长，之后开始淡出。</summary>
    private static readonly TimeSpan HoldDuration = TimeSpan.FromMilliseconds(2400);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    /// <summary>全局唯一实例。只在 UI 线程读写。</summary>
    private static ToastPopup? _instance;

    private readonly TextBlock _title;
    private readonly TextBlock _message;
    private readonly DispatcherTimer _closeTimer;

    private ToastPopup()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SizeToContent = SizeToContent.Height;
        Width = CardWidth + ShadowPadding * 2;
        Opacity = 0;
        Visibility = Visibility.Hidden;

        _title = new TextBlock
        {
            Text = string.Empty,
            FontFamily = (FontFamily)FindResource("Font.UI"),
            FontSize = (double)FindResource("Font.Size.Subtitle"),
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("Text.Primary"),
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        _message = new TextBlock
        {
            Text = string.Empty,
            FontFamily = (FontFamily)FindResource("Font.UI"),
            FontSize = (double)FindResource("Font.Size.Caption"),
            Foreground = (Brush)FindResource("Text.Secondary"),
            LineHeight = 19,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = CardWidth - 30,
            Margin = new Thickness(0, 6, 0, 0)
        };

        var stack = new StackPanel();
        stack.Children.Add(_title);
        stack.Children.Add(_message);

        var card = new Border
        {
            Width = CardWidth,
            Background = (Brush)FindResource("Bg.Flyout"),
            BorderBrush = (Brush)FindResource("Stroke.Medium"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(15, 12, 15, 13),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = stack,
            IsHitTestVisible = false
        };

        Content = card;

        _closeTimer = new DispatcherTimer { Interval = HoldDuration };
        _closeTimer.Tick += (_, _) => FadeOut();

        SourceInitialized += OnSourceInitialized;
    }

    /// <summary>
    /// 显示一条轻提示。可从任意线程调用：内部自动切到 UI 线程。
    /// 应用尚未就绪（无 <see cref="Application.Current"/>）时静默忽略。
    /// </summary>
    public static void Show(string title, string message)
    {
        try
        {
            var app = Application.Current;
            if (app is null)
            {
                return;
            }

            if (app.Dispatcher.CheckAccess())
            {
                Present(title, message);
            }
            else
            {
                app.Dispatcher.BeginInvoke(new Action(() => Present(title, message)));
            }
        }
        catch (Exception)
        {
            // 提示失败绝不能影响主流程。
        }
    }

    private static void Present(string title, string message)
    {
        try
        {
            _instance ??= new ToastPopup();
            _instance.PresentCore(title, message);
        }
        catch (Exception ex)
        {
            // 提示是装饰、不是功能，所以吞掉异常；但一定要留日志，
            // 否则「浮层没弹出来」在现场完全无从查起。
            Services.AppServices.Log?.Warn("提示", $"应用内提示显示失败：{ex.Message}");
        }
    }

    /// <summary>程序退出前彻底关闭，避免残留窗口句柄。</summary>
    public static void ShutdownIfAny()
    {
        try
        {
            var instance = _instance;
            _instance = null;

            if (instance is null)
            {
                return;
            }

            instance._closeTimer.Stop();
            instance.Close();
        }
        catch (Exception)
        {
            // 退出阶段忽略。
        }
    }

    /// <summary>刷新内容并重新计时；新旧提示共用同一个窗口，不会叠出两条。</summary>
    private void PresentCore(string title, string message)
    {
        _closeTimer.Stop();

        _title.Text = string.IsNullOrWhiteSpace(title) ? "正画 PivotPic" : title;
        _message.Text = message ?? string.Empty;
        _message.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;

        // 先以 Opacity=0 显示，量完高度再定位，避免在 (0,0) 位置闪一帧。
        if (!IsVisible)
        {
            Opacity = 0;
            Show();
        }

        UpdateLayout();
        PositionAtBottomRight();

        var duration = new Duration(TimeSpan.FromMilliseconds(150));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(OpacityProperty, new DoubleAnimation(Opacity, 1, duration) { EasingFunction = ease });

        _closeTimer.Start();
    }

    /// <summary>贴到主屏工作区右下角（工作区已排除任务栏）。</summary>
    private void PositionAtBottomRight()
    {
        var work = SystemParameters.WorkArea;

        // SizeToContent=Height 时 Height 是 NaN，量不到 ActualHeight 就直接用会把 Top 算成 NaN 并抛异常。
        var height = ActualHeight;
        if (double.IsNaN(height) || height <= 0)
        {
            height = DesiredSize.Height;
        }

        if (double.IsNaN(height) || height <= 0)
        {
            height = 0;
        }

        Left = Math.Max(work.Left, work.Right - Width - RightGap);
        Top = Math.Max(work.Top, work.Bottom - height - BottomGap);
    }

    private void FadeOut()
    {
        _closeTimer.Stop();

        var fade = new DoubleAnimation(Opacity, 0, new Duration(TimeSpan.FromMilliseconds(200)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        fade.Completed += (_, _) =>
        {
            Hide();
            _title.Text = string.Empty;
            _message.Text = string.Empty;
        };

        BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>不激活、不进 Alt+Tab、鼠标穿透：浮层绝不抢焦点也不挡点击。</summary>
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var exStyle = GetWindowLong(handle, GWL_EXSTYLE);
        SetWindowLong(handle, GWL_EXSTYLE,
            exStyle | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
