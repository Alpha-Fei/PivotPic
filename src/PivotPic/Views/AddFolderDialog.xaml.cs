using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PivotPic.Services;
using WinForms = System.Windows.Forms;

namespace PivotPic.Views;

/// <summary>
/// 「添加监控目录」模态对话框：手写路径（含时间占位符）或点浏览按钮选择。
/// <para>
/// 确定后通过 <see cref="ResultPath"/> 回传路径模板（保留占位符原文），
/// 由调用方写入配置并挂监控。取消或按 Esc 时不回传。
/// </para>
/// </summary>
public partial class AddFolderDialog : Window
{
    /// <summary>用户确认的路径模板（可能含 {mm-dd} 等占位符），取消时为 null。</summary>
    public string? ResultPath { get; private set; }

    /// <summary>构造对话框：初始化预览状态。</summary>
    public AddFolderDialog()
    {
        InitializeComponent();

        // 初始状态：空输入框 + 提示文字 + 默认预览。
        UpdatePreview(string.Empty);

        Loaded += OnDialogLoaded;
    }

    // ============================================================
    // 生命周期
    // ============================================================

    /// <summary>打开时自动聚焦并全选输入框。</summary>
    private void OnDialogLoaded(object sender, RoutedEventArgs e)
    {
        // 弹出动画：以中心为原点缩放淡入（0.94 → 1）
        var ease = new System.Windows.Media.Animation.CubicEase
        {
            EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
        };
        var duration = new Duration(TimeSpan.FromMilliseconds(180));

        var fade = new System.Windows.Media.Animation.DoubleAnimation(0, 1, duration)
        {
            EasingFunction = ease
        };
        BeginAnimation(OpacityProperty, fade);

        if (Content is FrameworkElement root && root.RenderTransform is System.Windows.Media.ScaleTransform scale)
        {
            scale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty,
                new System.Windows.Media.Animation.DoubleAnimation(0.94, 1, duration) { EasingFunction = ease });
            scale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty,
                new System.Windows.Media.Animation.DoubleAnimation(0.94, 1, duration) { EasingFunction = ease });
        }

        PathInput.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            PathInput.Focus();
            PathInput.SelectAll();
        }));
    }

    // ============================================================
    // 输入与预览
    // ============================================================

    /// <summary>输入变化时更新预览行与占位符提示显隐。</summary>
    private void OnPathTextChanged(object sender, TextChangedEventArgs e)
    {
        var text = PathInput.Text ?? string.Empty;

        ConfirmButton.IsEnabled = !string.IsNullOrWhiteSpace(text);
        UpdatePreview(text);
    }

    /// <summary>
    /// 实时解析占位符并显示预览行。
    /// 逻辑与 <c>FolderItemViewModel.EditingPreviewLine</c> 保持一致：
    /// 空输入显示提示，未识别占位符转警告色并列出。
    /// </summary>
    private void UpdatePreview(string text)
    {
        var trimmed = (text ?? string.Empty).Trim();

        if (trimmed.Length == 0)
        {
            PreviewText.Text = "输入路径，可插入 {mm-dd} 这类时间占位符";
            SetPreviewColor("Text.Tertiary");
            return;
        }

        try
        {
            var resolved = AppServices.PathTemplate?.Resolve(trimmed) ?? trimmed;
            var unknown = AppServices.PathTemplate?.FindUnknownPlaceholders(trimmed)
                          ?? Array.Empty<string>();

            if (unknown.Count > 0)
            {
                PreviewText.Text = $"预览：{resolved} · 未识别占位符：{string.Join("、", unknown)}";
                SetPreviewColor("Warning");
            }
            else
            {
                PreviewText.Text = $"预览：{resolved}";
                SetPreviewColor("Text.Tertiary");
            }
        }
        catch (Exception)
        {
            PreviewText.Text = "预览：路径包含非法字符";
            SetPreviewColor("Warning");
        }
    }

    /// <summary>设置预览行前景色（走 DynamicResource 以支持主题切换）。</summary>
    private void SetPreviewColor(string resourceKey)
    {
        PreviewText.SetResourceReference(TextBlock.ForegroundProperty, resourceKey);
    }

    // ============================================================
    // 浏览
    // ============================================================

    /// <summary>
    /// 智能识别路径中的日期片段并替换为时间占位符（规则见 <see cref="PathTemplateService.ConvertDatesToPlaceholders"/>）。
    /// 例如 C://图库//11-04//喷绘 → C://图库//{mm-dd}//喷绘
    /// </summary>
    private void OnConvertPlaceholderClick(object sender, RoutedEventArgs e)
    {
        var text = PathInput.Text?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return;
        }

        var converted = PathTemplateService.ConvertDatesToPlaceholders(text);
        if (string.Equals(converted, text, StringComparison.Ordinal))
        {
            // 没识别到日期就不动，避免误改
            return;
        }

        PathInput.Text = converted;
        PathInput.CaretIndex = converted.Length;
        PathInput.Focus();
    }

    /// <summary>
    /// 打开系统文件夹选择器，选中后把路径填入输入框。
    /// <b>不自动确定</b>——用户可能还要补占位符，填完再按 Enter 或点确定。
    /// </summary>
    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        try
        {
            using var dialog = new WinForms.FolderBrowserDialog
            {
                Description = "选择需要自动处理图片的目录",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true
            };

            // 当前输入若是一个存在的目录，用它作为选择器的初始位置。
            var current = (PathInput.Text ?? string.Empty).Trim();
            if (current.Length > 0 && Directory.Exists(current))
            {
                dialog.SelectedPath = current;
            }

            if (dialog.ShowDialog() == WinForms.DialogResult.OK)
            {
                PathInput.Text = dialog.SelectedPath;
                PathInput.Focus();
                PathInput.CaretIndex = PathInput.Text.Length;
            }
        }
        catch (Exception ex)
        {
            AppServices.Log?.Warn("添加目录", $"打开目录选择器失败：{ex.Message}");
        }
    }

    // ============================================================
    // 确定 / 取消
    // ============================================================

    /// <summary>确定：校验路径非空白后回传并关闭。</summary>
    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        var path = (PathInput.Text ?? string.Empty).Trim();

        if (path.Length == 0)
        {
            FlashInvalidInput();
            return;
        }

        ResultPath = path;
        DialogResult = true;
    }

    /// <summary>取消：关闭对话框，不回传路径。</summary>
    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    /// <summary>Enter = 确定；Esc = 取消。PreviewKeyDown 隧道确保在 TextBox 处理前拦截。</summary>
    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            OnConfirmClick(this, new RoutedEventArgs());
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            OnCancelClick(this, new RoutedEventArgs());
        }
    }

    // ============================================================
    // 视觉辅助
    // ============================================================

    /// <summary>空路径时输入框短暂用 Danger 色闪 200ms 提示。</summary>
    private void FlashInvalidInput()
    {
        PathInput.SetResourceReference(Control.BorderBrushProperty, "Danger");
        PathInput.SetResourceReference(Control.ForegroundProperty, "Danger");

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };

        timer.Tick += (_, _) =>
        {
            timer.Stop();
            PathInput.ClearValue(Control.BorderBrushProperty);
            PathInput.ClearValue(Control.ForegroundProperty);
        };

        timer.Start();
        PathInput.Focus();
    }
}
