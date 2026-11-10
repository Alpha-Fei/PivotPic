using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace PivotPic.Controls;

/// <summary>
/// 窗口级过渡动画：动的是 **Window 自身** 的 <c>Left / Top / Width / Height / Opacity</c>，
/// 不是窗口里的内容。
/// <para>
/// 为什么必须是真窗口：主窗口用 <c>WindowStyle="None" + WindowChrome</c> 自绘标题栏，
/// 系统自带的打开/关闭/最大化动画全被顶掉了，用户看到的只有「啪」地一下。
/// 只缩放内容并不能解决这个问题——那只是窗口内部在动，边框和阴影纹丝不动。
/// </para>
/// <para>
/// <b>关键约束</b>：所有动画都用 <see cref="FillBehavior.Stop"/>，并在 <c>Completed</c> 里把终值
/// **写死赋值**。用 HoldEnd 会把属性锁在动画末态，之后再也设不动尺寸
/// （上一版「恢复后窗口透明/尺寸错乱」就是踩了这个坑）。
/// </para>
/// </summary>
public sealed class WindowAnimator
{
    /// <summary>关闭：150ms 淡出 + 缩到 96%。</summary>
    private const int CloseMilliseconds = 150;

    /// <summary>最小化：160ms 向任务栏方向收缩。</summary>
    private const int MinimizeMilliseconds = 160;

    /// <summary>最大化 / 还原：180ms 矩形过渡。</summary>
    private const int MaximizeMilliseconds = 180;

    /// <summary>从任务栏恢复：160ms 反向放大回原位。</summary>
    private const int RestoreMilliseconds = 160;

    /// <summary>关闭时的缩放比例。</summary>
    private const double CloseScale = 0.96;

    /// <summary>最小化时收缩到的比例。</summary>
    private const double MinimizeScale = 0.55;

    private readonly Window _window;
    private bool _animating;

    public WindowAnimator(Window window)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
    }

    /// <summary>动画进行中。调用方据此屏蔽重复触发（连点最大化不能打架）。</summary>
    public bool IsAnimating => _animating;

    /// <summary>系统是否允许播放动画（用户在「辅助功能」里关掉时会话不播）。</summary>
    public static bool AnimationEnabled => SystemParameters.ClientAreaAnimation;

    /// <summary>当前窗口矩形（DIP）。</summary>
    public Rect CurrentBounds => new(_window.Left, _window.Top, _window.Width, _window.Height);

    /// <summary>关闭：以窗口中心为锚缩到 96% 并淡出，完成后回调（调用方在里面 <c>Close()</c>）。</summary>
    public void PlayClose(Action onCompleted)
    {
        var from = CurrentBounds;
        var to = ScaleAroundCenter(from, CloseScale);

        AnimateTo(to, 0, CloseMilliseconds,
            new CubicEase { EasingMode = EasingMode.EaseOut }, onCompleted);
    }

    /// <summary>
    /// 最小化：整个窗口向任务栏方向收缩并淡出。
    /// </summary>
    /// <param name="current">当前窗口矩形（DIP）。</param>
    /// <param name="screen">所在显示器的完整矩形（DIP）。</param>
    /// <param name="workArea">所在显示器的工作区（DIP，已排除任务栏）。</param>
    /// <param name="onCompleted">完成回调（调用方在里面真正最小化）。</param>
    public void PlayMinimize(Rect current, Rect screen, Rect workArea, Action onCompleted)
    {
        var to = GetMinimizeTarget(current, screen, workArea);

        AnimateTo(to, 0, MinimizeMilliseconds,
            new CubicEase { EasingMode = EasingMode.EaseIn }, onCompleted);
    }

    /// <summary>最大化：从当前矩形平滑过渡到目标矩形（所在显示器的工作区）。</summary>
    public void PlayMaximize(Rect to, Action onCompleted)
    {
        AnimateTo(to, 1, MaximizeMilliseconds,
            new CubicEase { EasingMode = EasingMode.EaseOut }, onCompleted);
    }

    /// <summary>还原：从当前（最大化）矩形过渡回普通矩形。</summary>
    public void PlayRestore(Rect to, Action onCompleted)
    {
        AnimateTo(to, 1, MaximizeMilliseconds,
            new CubicEase { EasingMode = EasingMode.EaseOut }, onCompleted);
    }

    /// <summary>从任务栏恢复：从略小一点的矩形反向放大回原位，顺带淡入。</summary>
    /// <param name="target">恢复后的目标矩形（DIP）。</param>
    /// <param name="onCompleted">完成回调。</param>
    public void PlayRestoreFromTaskbar(Rect target, Action onCompleted)
    {
        var from = ScaleAroundCenter(target, CloseScale);

        // 先把窗口摆到「略小」的位置再放大，视觉上就是从任务栏弹回来。
        ApplyBounds(from);
        _window.Opacity = 0;

        AnimateTo(target, 1, RestoreMilliseconds,
            new CubicEase { EasingMode = EasingMode.EaseOut }, onCompleted);
    }

    /// <summary>
    /// 复位：解除所有动画并恢复不透明度。
    /// 关闭/最小化之后必须调用，否则从任务栏恢复出来是一个透明的窗口。
    /// </summary>
    public void ResetVisualState()
    {
        _window.BeginAnimation(Window.LeftProperty, null);
        _window.BeginAnimation(Window.TopProperty, null);
        _window.BeginAnimation(Window.WidthProperty, null);
        _window.BeginAnimation(Window.HeightProperty, null);
        _window.BeginAnimation(Window.OpacityProperty, null);

        _window.Opacity = 1;
        _animating = false;
    }

    /// <summary>直接把窗口摆到指定矩形（不播动画）。</summary>
    public void ApplyBounds(Rect bounds)
    {
        _window.Left = bounds.X;
        _window.Top = bounds.Y;
        _window.Width = bounds.Width;
        _window.Height = bounds.Height;
    }

    /// <summary>以某个矩形为中心做等比缩放。</summary>
    private static Rect ScaleAroundCenter(Rect source, double scale)
    {
        var width = source.Width * scale;
        var height = source.Height * scale;

        return new Rect(
            source.X + (source.Width - width) / 2,
            source.Y + (source.Height - height) / 2,
            width,
            height);
    }

    /// <summary>按任务栏所在方向算出「缩下去」的目标矩形。</summary>
    private static Rect GetMinimizeTarget(Rect current, Rect screen, Rect workArea)
    {
        var width = current.Width * MinimizeScale;
        var height = current.Height * MinimizeScale;

        var left = current.X + (current.Width - width) / 2;
        var top = current.Y + (current.Height - height) / 2;

        if (workArea.Bottom < screen.Bottom)
        {
            // 任务栏在下：往下收，底边贴住原底边。
            top = current.Bottom - height;
        }
        else if (workArea.Top > screen.Top)
        {
            // 任务栏在上：往上收。
            top = current.Top;
        }
        else if (workArea.Left > screen.Left)
        {
            // 任务栏在左。
            left = current.Left;
        }
        else if (workArea.Right < screen.Right)
        {
            // 任务栏在右。
            left = current.Right - width;
        }
        else
        {
            // 任务栏自动隐藏：默认当在下。
            top = current.Bottom - height;
        }

        return new Rect(left, top, width, height);
    }

    /// <summary>
    /// 一次动画同时推 5 个属性。所有动画 <see cref="FillBehavior.Stop"/>，
    /// 结束后写死终值——这是「不被动画锁死属性」的关键。
    /// </summary>
    private void AnimateTo(Rect target, double targetOpacity, int milliseconds,
        IEasingFunction ease, Action? onCompleted)
    {
        if (_animating)
        {
            // 已经在动就直接执行终态，绝不排队打架。
            ApplyBounds(target);
            _window.Opacity = targetOpacity;
            onCompleted?.Invoke();
            return;
        }

        _animating = true;

        try
        {
            var duration = new Duration(TimeSpan.FromMilliseconds(milliseconds));

            DoubleAnimation Make(double from, double to) => new(to, duration)
            {
                EasingFunction = ease,
                FillBehavior = FillBehavior.Stop
            };

            var left = Make(_window.Left, target.X);
            var top = Make(_window.Top, target.Y);
            var width = Make(_window.Width, target.Width);
            var height = Make(_window.Height, target.Height);
            var opacity = Make(_window.Opacity, targetOpacity);

            // 五个动画同起同停，挂一个 Completed 就够。
            height.Completed += (_, _) =>
            {
                try
                {
                    ResetAnimations();
                    ApplyBounds(target);
                    _window.Opacity = targetOpacity;
                }
                catch (Exception)
                {
                    // 写终值失败也要保证状态可用，不能把窗口卡在半路。
                }
                finally
                {
                    _animating = false;
                    onCompleted?.Invoke();
                }
            };

            _window.BeginAnimation(Window.LeftProperty, left);
            _window.BeginAnimation(Window.TopProperty, top);
            _window.BeginAnimation(Window.WidthProperty, width);
            _window.BeginAnimation(Window.HeightProperty, height);
            _window.BeginAnimation(Window.OpacityProperty, opacity);
        }
        catch (Exception)
        {
            // 动画起不来就直落终态：功能优先，绝不能让窗口关不掉。
            ResetAnimations();
            ApplyBounds(target);
            _window.Opacity = targetOpacity;
            _animating = false;
            onCompleted?.Invoke();
        }
    }

    private void ResetAnimations()
    {
        _window.BeginAnimation(Window.LeftProperty, null);
        _window.BeginAnimation(Window.TopProperty, null);
        _window.BeginAnimation(Window.WidthProperty, null);
        _window.BeginAnimation(Window.HeightProperty, null);
        _window.BeginAnimation(Window.OpacityProperty, null);
    }
}
