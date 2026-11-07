using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using PivotPic.Models;
using PivotPic.Services;

namespace PivotPic.Controls;

/// <summary>
/// 右下角自绘通知层。非模态、不抢焦点、5 秒自动淡出、悬停暂停计时。
/// 用代码构建视觉树，避免为通知再引入一套 XAML 模板。
/// </summary>
public sealed class ToastHost : Grid
{
    private const int MaxVisible = 3;
    private const double CardWidth = 360;

    private readonly StackPanel _stack;

    public ToastHost()
    {
        IsHitTestVisible = false;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Bottom;

        _stack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(0, 0, 24, 44),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom
        };

        Children.Add(_stack);
    }

    /// <summary>展示一批告警。超过 3 条时折叠为一条汇总卡片。</summary>
    public void Show(IReadOnlyList<AlertItem> items, Window owner)
    {
        var overflow = 0;
        var shown = items;

        if (_stack.Children.Count + items.Count > MaxVisible)
        {
            var room = Math.Max(1, MaxVisible - _stack.Children.Count);
            shown = items.Take(room).ToList();
            overflow = items.Count - shown.Count;
        }

        foreach (var item in shown.Reverse())
        {
            var card = BuildCard(item);
            _stack.Children.Insert(0, card);
            AnimateIn(card);
            ScheduleDismiss(card);
        }

        if (overflow > 0)
        {
            var summary = BuildSummary(overflow, items);
            _stack.Children.Insert(0, summary);
            AnimateIn(summary);
            ScheduleDismiss(summary);
        }

        TrimOverflow();
    }

    private void TrimOverflow()
    {
        while (_stack.Children.Count > MaxVisible)
        {
            _stack.Children.RemoveAt(_stack.Children.Count - 1);
        }
    }

    private Border BuildCard(AlertItem item)
    {
        var isDanger = true;

        var accentBar = new Border
        {
            Width = 3,
            CornerRadius = new CornerRadius(2),
            Background = (Brush)FindResource(isDanger ? "Danger" : "Accent"),
            Margin = new Thickness(0, 2, 0, 2)
        };

        var title = new TextBlock
        {
            Text = item.FileName,
            FontFamily = (FontFamily)FindResource("Font.UI"),
            FontSize = (double)FindResource("Font.Size.Subtitle"),
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("Text.Primary"),
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var detail = new TextBlock
        {
            Text = $"{item.SizeText}  ·  {item.ValueText}（阈值 {item.Threshold:0}）",
            FontFamily = (FontFamily)FindResource("Font.Mono"),
            FontSize = (double)FindResource("Font.Size.Caption"),
            Foreground = (Brush)FindResource("Text.Secondary"),
            Margin = new Thickness(0, 4, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var locate = new Button
        {
            Content = "定位",
            Style = (Style)FindResource("Btn.Ghost"),
            Foreground = (Brush)FindResource("Accent"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(0, 2, 0, 0),
            Margin = new Thickness(0, 6, 0, 0)
        };

        var path = item.FullPath;
        locate.Click += (_, _) => AppServices.Alerts?.Locate(path);

        var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        textStack.Children.Add(title);
        textStack.Children.Add(detail);
        textStack.Children.Add(locate);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(accentBar, 0);
        Grid.SetColumn(textStack, 1);
        textStack.Margin = new Thickness(12, 0, 12, 0);
        grid.Children.Add(accentBar);
        grid.Children.Add(textStack);

        var close = BuildCloseGlyph(() =>
        {
            if (_stack.Children.Contains(grid))
            {
                _stack.Children.Remove(grid);
            }
        });

        Grid.SetColumn(close, 2);
        grid.Children.Add(close);

        var card = new Border
        {
            Width = CardWidth,
            Background = (Brush)FindResource("Bg.Flyout"),
            BorderBrush = (Brush)FindResource("Stroke.Medium"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(14, 12, 10, 12),
            Margin = new Thickness(0, 0, 0, 10),
            Child = grid,
            IsHitTestVisible = true,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 24,
                ShadowDepth = 8,
                Opacity = 0.28,
                Color = Colors.Black
            },
            RenderTransform = new TranslateTransform(28, 0)
        };

        card.MouseEnter += (_, _) => card.Tag = "hovered";
        card.MouseLeave += (_, _) => card.Tag = null;

        return card;
    }

    private Border BuildSummary(int count, IReadOnlyList<AlertItem> all)
    {
        var title = new TextBlock
        {
            Text = $"另有 {count} 个文件超出阈值",
            FontFamily = (FontFamily)FindResource("Font.UI"),
            FontSize = (double)FindResource("Font.Size.Subtitle"),
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("Text.Primary")
        };

        var view = new Button
        {
            Content = "查看全部",
            Style = (Style)FindResource("Btn.Ghost"),
            Foreground = (Brush)FindResource("Accent"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(0, 2, 0, 0),
            Margin = new Thickness(0, 6, 0, 0)
        };

        view.Click += (_, _) =>
        {
            if (Application.Current is App app)
            {
                app.ShowShell();
                App.Shell?.NavigateTo("monitor");
            }
        };

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(title);
        stack.Children.Add(view);

        var card = new Border
        {
            Width = CardWidth,
            Background = (Brush)FindResource("Bg.Flyout"),
            BorderBrush = (Brush)FindResource("Stroke.Medium"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(16, 12, 16, 12),
            Margin = new Thickness(0, 0, 0, 10),
            Child = stack,
            IsHitTestVisible = true,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 24,
                ShadowDepth = 8,
                Opacity = 0.28,
                Color = Colors.Black
            },
            RenderTransform = new TranslateTransform(28, 0)
        };

        card.MouseEnter += (_, _) => card.Tag = "hovered";
        card.MouseLeave += (_, _) => card.Tag = null;

        return card;
    }

    private UIElement BuildCloseGlyph(Action onClose)
    {
        var path = new Path
        {
            Data = (Geometry)FindResource("Icon.Close"),
            Stroke = (Brush)FindResource("Icon.Secondary"),
            StrokeThickness = 1.5,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Width = 9,
            Height = 9,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Top,
            Cursor = Cursors.Hand
        };

        path.MouseLeftButtonUp += (_, _) => onClose();
        path.Margin = new Thickness(6, 2, 2, 0);
        return path;
    }

    private void AnimateIn(UIElement element)
    {
        var duration = new Duration(TimeSpan.FromMilliseconds(240));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        element.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });

        if (element.RenderTransform is TranslateTransform t)
        {
            t.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(28, 0, duration) { EasingFunction = ease });
        }
    }

    private void ScheduleDismiss(UIElement element)
    {
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5)
        };

        timer.Tick += (_, _) =>
        {
            // 悬停时暂停计时，避免用户正要点「定位」时卡片消失。
            if (element is FrameworkElement { Tag: "hovered" })
            {
                return;
            }

            timer.Stop();

            var fade = new DoubleAnimation(1, 0, new Duration(TimeSpan.FromMilliseconds(160)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            fade.Completed += (_, _) => _stack.Children.Remove(element);
            element.BeginAnimation(OpacityProperty, fade);
        };

        timer.Start();
    }
}
