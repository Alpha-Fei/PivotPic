using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace PivotPic.Behaviors;

/// <summary>
/// 「状态展开」动画：把 bool 状态平滑映射为内容高度 0 ↔ 自然高度。
/// 展开时高度从 0 逐步撑开（下方内容被顺滑推开、内容自底缘逐渐露出），
/// 收起时高度逐步塌陷，动画结束才真正 Collapsed。
/// 用法：behaviors:Reveal.IsExpanded="{Binding SomeBool}"，建议元素同时设置 ClipToBounds="True"。
/// </summary>
public static class Reveal
{
    /// <summary>展开时长：从容一点，避免生硬。</summary>
    private static readonly TimeSpan ExpandTime = TimeSpan.FromSeconds(0.30);

    /// <summary>收起时长：比展开略快。</summary>
    private static readonly TimeSpan CollapseTime = TimeSpan.FromSeconds(0.22);

    /// <summary>透明度淡出比高度塌陷更早完成，让内容先“隐”再“收”。</summary>
    private static readonly TimeSpan FadeTime = TimeSpan.FromSeconds(0.18);

    /// <summary>
    /// 目标展开状态。刻意用 <see cref="Nullable{Boolean}"/> 且默认 null：
    /// 绑定在 DataContext 还没到位时取不到值（null），之后解析成 false 才算一次真实变更，
    /// 从而能触发回调把内容同步成「收起」。若默认 false，源值为 false 时根本不会有变更通知，
    /// 元素就会一直停在 XAML 默认的 Visible（弹窗一打开白名单是展开的，就是这个原因）。
    /// </summary>
    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.RegisterAttached(
        "IsExpanded",
        typeof(bool?),
        typeof(Reveal),
        new FrameworkPropertyMetadata(null, OnIsExpandedChanged));

    /// <summary>内部标记：首次绑定求值只同步终态、不播动画（避免弹窗打开时闪现）。</summary>
    private static readonly DependencyPropertyKey IsInitializedPropertyKey = DependencyProperty.RegisterAttachedReadOnly(
        "IsInitialized",
        typeof(bool),
        typeof(Reveal),
        new PropertyMetadata(false));

    /// <summary>内部记录：元素本来的外边距（动画会把 Margin 一起动，收尾要还原）。</summary>
    private static readonly DependencyPropertyKey OriginalMarginPropertyKey = DependencyProperty.RegisterAttachedReadOnly(
        "OriginalMargin",
        typeof(Thickness),
        typeof(Reveal),
        new PropertyMetadata(default(Thickness)));

    /// <summary>取元素原始外边距。不能直接读 element.Margin —— 动画进行中读到的是动画值。</summary>
    private static Thickness GetOriginalMargin(FrameworkElement element)
    {
        if (element.GetValue(OriginalMarginPropertyKey.DependencyProperty) is Thickness stored && stored != default)
        {
            return stored;
        }

        var margin = element.Margin;
        element.SetValue(OriginalMarginPropertyKey, margin);
        return margin;
    }

    public static bool GetIsExpanded(DependencyObject o) => o.GetValue(IsExpandedProperty) as bool? ?? false;

    public static void SetIsExpanded(DependencyObject o, bool value) => o.SetValue(IsExpandedProperty, value);

    private static void OnIsExpandedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        bool expanded = e.NewValue as bool? ?? false;

        // 首次求值（绑定初始化）：直接呈现终态。
        if (!(bool)element.GetValue(IsInitializedPropertyKey.DependencyProperty))
        {
            element.SetValue(IsInitializedPropertyKey, true);
            ApplyInstant(element, expanded);
            return;
        }

        if (expanded)
        {
            Expand(element);
        }
        else
        {
            Collapse(element);
        }
    }

    private static void ApplyInstant(FrameworkElement element, bool expanded)
    {
        // 首次求值时记下原始外边距，供后续动画还原。
        GetOriginalMargin(element);
        element.BeginAnimation(FrameworkElement.HeightProperty, null);
        element.Height = double.NaN;
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = expanded ? 1 : 0;
        element.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
    }

    private static IEasingFunction Ease() => new CubicEase { EasingMode = EasingMode.EaseOut };

    /// <summary>
    /// 取「完全展开时」的真实高度。手算 Measure 会因可用宽度与真实布局不同
    /// （滚动条、内边距、WrapPanel 换行）算出偏差，动画收尾回 Auto 时会“咔”跳一下，
    /// 所以这里强制一次真实布局。窗口若开了 SizeToContent=Height，测量期间先钉住当前高度，
    /// 否则整窗会先跳成完全展开再弹回来，肉眼看到闪一下。
    /// </summary>
    private static double MeasureTargetHeight(FrameworkElement element, out Action? restore)
    {
        restore = null;

        var window = Window.GetWindow(element);
        var sizeToContent = window?.SizeToContent ?? SizeToContent.Manual;

        if (window is not null && sizeToContent != SizeToContent.Manual)
        {
            var pinnedHeight = window.ActualHeight;
            window.SizeToContent = SizeToContent.Manual;
            window.Height = pinnedHeight;

            // 还原动作必须等元素被压回起点之后再执行：若此刻内容还是完全展开态，
            // 一恢复 SizeToContent 整窗会瞬间顶到最大再缩回，肉眼就是闪一下。
            var target = window;
            restore = () =>
            {
                target.Height = double.NaN;
                target.SizeToContent = sizeToContent;
            };
        }

        element.Height = double.NaN;
        element.UpdateLayout();
        return element.ActualHeight;
    }

    private static void Expand(FrameworkElement element)
    {
        // Collapsed 元素保留着上一次排列的 ActualHeight（不是 0），必须先记下来判方向。
        bool wasCollapsed = element.Visibility != Visibility.Visible;
        double before = element.ActualHeight;

        element.Visibility = Visibility.Visible;

        double target = MeasureTargetHeight(element, out var restoreWindow);

        if (target <= 0)
        {
            restoreWindow?.Invoke();
            ApplyInstant(element, true);
            return;
        }

        // 折叠态从 0 起（残留的 ActualHeight 会吃掉整段动画）；中途反向则从当前高度续。
        double from = wasCollapsed ? 0 : Math.Min(before, target);

        // 元素自身的上下外边距也要一起动：只动 Height 时，开头/结尾会各跳一个外边距的高度。
        var margin = GetOriginalMargin(element);
        var flat = new Thickness(margin.Left, 0, margin.Right, 0);

        element.Margin = flat;
        element.Height = from;

        // 内容已压到起点，此时恢复窗口自动高度不会再顶大窗口。
        restoreWindow?.Invoke();

        var height = new DoubleAnimation(from, target, new Duration(ExpandTime)) { EasingFunction = Ease() };
        height.Completed += (_, _) =>
        {
            // 回归 Auto：之后内容增删（加/删忽略名单）仍可自适应。
            element.BeginAnimation(FrameworkElement.HeightProperty, null);
            element.BeginAnimation(FrameworkElement.MarginProperty, null);
            element.Height = double.NaN;
            element.Margin = margin;
        };
        element.BeginAnimation(FrameworkElement.HeightProperty, height);
        element.BeginAnimation(
            FrameworkElement.MarginProperty,
            new ThicknessAnimation(flat, margin, new Duration(ExpandTime)) { EasingFunction = Ease() });

        var opacity = new DoubleAnimation(element.Opacity, 1, new Duration(ExpandTime)) { EasingFunction = Ease() };
        opacity.Completed += (_, _) =>
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = 1;
        };
        element.BeginAnimation(UIElement.OpacityProperty, opacity);
    }

    private static void Collapse(FrameworkElement element)
    {
        double current = element.ActualHeight;
        if (current <= 0)
        {
            ApplyInstant(element, false);
            return;
        }

        var margin = GetOriginalMargin(element);
        var flat = new Thickness(margin.Left, 0, margin.Right, 0);

        var height = new DoubleAnimation(current, 0, new Duration(CollapseTime)) { EasingFunction = Ease() };
        height.Completed += (_, _) =>
        {
            element.BeginAnimation(FrameworkElement.HeightProperty, null);
            element.BeginAnimation(FrameworkElement.MarginProperty, null);
            element.Height = double.NaN;
            element.Margin = margin;
            element.Visibility = Visibility.Collapsed;
        };
        element.BeginAnimation(FrameworkElement.HeightProperty, height);
        element.BeginAnimation(
            FrameworkElement.MarginProperty,
            new ThicknessAnimation(margin, flat, new Duration(CollapseTime)) { EasingFunction = Ease() });

        var opacity = new DoubleAnimation(element.Opacity, 0, new Duration(FadeTime)) { EasingFunction = Ease() };
        element.BeginAnimation(UIElement.OpacityProperty, opacity);
    }
}
