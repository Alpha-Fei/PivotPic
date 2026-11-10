using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PivotPic.Models;
using PivotPic.Services;

namespace PivotPic.Controls;

/// <summary>
/// 处理进度提示：独立的无边框浮窗，屏幕工作区底部居中，显示本批的整体完成比例。
/// <para>
/// 之所以做成独立窗口而不是主窗口内的覆盖层：程序常驻托盘，主窗口通常是隐藏的，
/// 挂在主窗口上就永远看不到。独立窗口 + Topmost + 不抢焦点，托盘状态下也能提示。
/// </para>
/// <para>
/// 进度条是**确定进度**：宽度按「已处理 / 总数」实际填充，不做来回扫动的动画。
/// 总数与完成判定由 <c>MonitorService.BatchProgressChanged</c> 提供 —— 它知道这一轮
/// 到底进了队列多少文件，界面自己猜不出来。
/// </para>
/// </summary>
public sealed class ProcessingPopup : Window
{
    private const double CardWidth = 340;
    private const double CardHeight = 108;
    private const double InnerWidth = CardWidth - 32;

    /// <summary>窗口四周留给阴影的透明边距。</summary>
    private const double ShadowPadding = 20;

    /// <summary>距屏幕工作区底部的距离。</summary>
    private const double BottomGap = 56;

    /// <summary>「处理完毕」停留时长。</summary>
    private static readonly TimeSpan HoldAfterDone = TimeSpan.FromMilliseconds(1600);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    private readonly Border _card;
    private readonly TextBlock _title;
    private readonly TextBlock _detail;
    private readonly Border _fill;
    private readonly TranslateTransform _fillShift;
    private readonly DispatcherTimer _closeTimer;

    public ProcessingPopup()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SizeToContent = SizeToContent.Manual;
        Width = CardWidth + ShadowPadding * 2;
        Height = CardHeight + ShadowPadding * 2;
        Opacity = 0;
        Visibility = Visibility.Hidden;

        _title = new TextBlock
        {
            Text = string.Empty,
            FontFamily = (FontFamily)FindResource("Font.UI"),
            FontSize = (double)FindResource("Font.Size.Subtitle"),
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("Text.Primary"),
            VerticalAlignment = VerticalAlignment.Center
        };

        var icon = new System.Windows.Shapes.Path
        {
            Data = (Geometry)FindResource("Icon.Rotate"),
            Stroke = (Brush)FindResource("Accent"),
            StrokeThickness = 1.5,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Width = 16,
            Height = 16,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };

        var titleRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        titleRow.Children.Add(icon);
        titleRow.Children.Add(_title);

        var track = new Border
        {
            Height = 4,
            CornerRadius = new CornerRadius(2),
            Background = (Brush)FindResource("Stroke.Medium"),
            Margin = new Thickness(0, 14, 0, 0),
            ClipToBounds = true
        };

        _fillShift = new TranslateTransform(0, 0);
        _fill = new Border
        {
            Width = 0,
            Height = 4,
            CornerRadius = new CornerRadius(2),
            Background = (Brush)FindResource("Accent"),
            HorizontalAlignment = HorizontalAlignment.Left,
            RenderTransform = _fillShift
        };
        track.Child = _fill;

        _detail = new TextBlock
        {
            Text = string.Empty,
            FontFamily = (FontFamily)FindResource("Font.Mono"),
            FontSize = (double)FindResource("Font.Size.Caption"),
            Foreground = (Brush)FindResource("Text.Secondary"),
            Margin = new Thickness(0, 10, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var stack = new StackPanel();
        stack.Children.Add(titleRow);
        stack.Children.Add(track);
        stack.Children.Add(_detail);

        _card = new Border
        {
            Width = CardWidth,
            Background = (Brush)FindResource("Bg.Flyout"),
            BorderBrush = (Brush)FindResource("Stroke.Medium"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(16, 14, 16, 14),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = stack,
            IsHitTestVisible = false
        };

        Content = _card;

        _closeTimer = new DispatcherTimer { Interval = HoldAfterDone };
        _closeTimer.Tick += (_, _) => HidePopup();

        SourceInitialized += OnSourceInitialized;
    }

    /// <summary>设置鼠标穿透与工具窗口样式，避免遮挡操作、避免出现在 Alt+Tab。</summary>
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var exStyle = GetWindowLong(handle, GWL_EXSTYLE);
        SetWindowLong(handle, GWL_EXSTYLE, exStyle | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW);
    }

    /// <summary>贴到主屏工作区底部居中；多屏时跟随主屏，避免弹到用户看不见的地方。</summary>
    private void PositionAtBottom()
    {
        var work = SystemParameters.WorkArea;
        Left = work.Left + (work.Width - Width) / 2;
        Top = work.Bottom - Height - BottomGap;
    }

    /// <summary>
    /// 用一批处理进度刷新卡片：进度条按 已处理/总数 实际填充，并显示「3 / 10」这样的分数。
    /// 配置关闭时直接返回，不创建任何视觉效果。
    /// </summary>
    public void Update(BatchProgress progress)
    {
        if (AppServices.Config?.Current.ShowProgressPopup != true)
        {
            return;
        }

        _closeTimer.Stop();

        if (!IsVisible)
        {
            PositionAtBottom();
            Show();
        }

        if (progress.Completed)
        {
            ShowCompleted(progress);
            return;
        }

        _title.Text = $"检测到 {progress.Total} 张需要处理的图片";
        // 形如「3 / 8 · a.jpg · 转基线中」：用户要求写清楚正在做哪种处理。
        var text = string.IsNullOrWhiteSpace(progress.CurrentFile)
            ? $"{progress.Processed} / {progress.Total}"
            : $"{progress.Processed} / {progress.Total} · {progress.CurrentFile}";

        if (!string.IsNullOrWhiteSpace(progress.Phase))
        {
            text += $" · {progress.Phase}";
        }

        _detail.Text = text;

        AnimateFill(progress.Ratio);

        var duration = new Duration(TimeSpan.FromMilliseconds(180));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(OpacityProperty, new DoubleAnimation(Opacity, 1, duration) { EasingFunction = ease });
    }

    /// <summary>进度条平滑推进到目标比例；用动画而不是直接赋值，避免跳帧。</summary>
    private void AnimateFill(double ratio)
    {
        var clamped = Math.Clamp(ratio, 0, 1);
        var target = Math.Max(3, InnerWidth * clamped);

        var animation = new DoubleAnimation
        {
            To = target,
            Duration = new Duration(TimeSpan.FromMilliseconds(180)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        _fill.BeginAnimation(WidthProperty, animation);
        _fill.HorizontalAlignment = HorizontalAlignment.Left;
    }

    /// <summary>本批结束：进度条满格，显示总数与真正旋转的张数，停留后自动关闭。</summary>
    private void ShowCompleted(BatchProgress progress)
    {
        _fill.BeginAnimation(WidthProperty, null);
        _fill.Width = InnerWidth;

        _title.Text = "处理完毕";
        _detail.Text = progress.Rotated >= progress.Total
            ? $"共处理 {progress.Rotated} 张图片"
            : $"共 {progress.Total} 张 · 旋转 {progress.Rotated} 张";

        _closeTimer.Start();
    }

    private void HidePopup()
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
            _detail.Text = string.Empty;
            _fill.BeginAnimation(WidthProperty, null);
            _fill.Width = 0;
        };

        BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>程序退出前彻底关闭，避免残留窗口句柄。</summary>
    public void Shutdown()
    {
        _closeTimer.Stop();
        Close();
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
