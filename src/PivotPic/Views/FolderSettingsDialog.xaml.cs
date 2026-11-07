using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PivotPic.Services;
using PivotPic.ViewModels;
using WinForms = System.Windows.Forms;

namespace PivotPic.Views;

/// <summary>
/// 单个监控目录的设置弹窗。
/// 卡片上只保留路径、网络状态与启用开关，其余配置集中到这里，避免卡片过高。
/// </summary>
public partial class FolderSettingsDialog : Window
{
    /// <summary>
    /// 这些属性是扫描进度、路径健康度之类的运行时状态，它们的变更不代表用户改过设置，
    /// 记录改动时必须排除，否则弹窗开着时健康度轮询一变就会误触发全量重扫。
    /// </summary>
    private static readonly HashSet<string> RuntimeOnlyProperties = new(StringComparer.Ordinal)
    {
        nameof(FolderItemViewModel.IsScanning),
        nameof(FolderItemViewModel.ScanScanned),
        nameof(FolderItemViewModel.ScanTotal),
        nameof(FolderItemViewModel.ScanPercent),
        nameof(FolderItemViewModel.ScanFraction),
        nameof(FolderItemViewModel.ScanText),
        nameof(FolderItemViewModel.IsExpanded),
        nameof(FolderItemViewModel.PathExists),
        nameof(FolderItemViewModel.IsNetwork),
        nameof(FolderItemViewModel.LatencyMs),
        nameof(FolderItemViewModel.HasStatus),
        nameof(FolderItemViewModel.StatusText),
        nameof(FolderItemViewModel.StatusIsWarning),
        nameof(FolderItemViewModel.EditingPath),
        nameof(FolderItemViewModel.EditingPreviewLine),
        nameof(FolderItemViewModel.HasEditingUnknown),
        nameof(FolderItemViewModel.DisplayPath),
        nameof(FolderItemViewModel.ResolvedLine),
        nameof(FolderItemViewModel.PathTooltip),
        nameof(FolderItemViewModel.ResolvedPath),
        nameof(FolderItemViewModel.UnknownTokens),
        nameof(FolderItemViewModel.HasUnknownTokens),
        nameof(FolderItemViewModel.UnknownTokenText),
    };

    private readonly FolderItemViewModel _viewModel;

    /// <summary>弹窗打开期间是否真的动过设置（决定关闭时要不要补扫一次）。</summary>
    private bool _settingsChanged;

    /// <summary>补扫是否已经启动过（Closing 可能被多个关闭路径触发，用重入开关保证只扫一次）。</summary>
    private bool _closing;

    public FolderSettingsDialog(FolderItemViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = _viewModel;
        PathText.Text = _viewModel.PathTooltip;

        // 订阅放在绑定都建立之后：初始化阶段的绑定回写不算用户改动。
        // 忽略名单与幅面规则存在集合里，属性通知覆盖不到，需要单独订阅集合变更。
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.IgnoredFolders.CollectionChanged += OnSettingsCollectionChanged;
        _viewModel.SizeRuleChips.CollectionChanged += OnSettingsCollectionChanged;
        Closing += OnDialogClosing;

        // 路径以草稿形式编辑（EditingPath），关闭时统一提交；订阅就位之后再装载草稿。
        _viewModel.BeginEdit();
        Closed += OnDialogClosed;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // PropertyName 为空表示「所有属性都可能变了」，按改动处理最安全。
        if (string.IsNullOrEmpty(e.PropertyName) || !RuntimeOnlyProperties.Contains(e.PropertyName))
        {
            _settingsChanged = true;
        }
    }

    private void OnSettingsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _settingsChanged = true;
    }

    /// <summary>窗口关闭后解除订阅：ViewModel 的生命周期比弹窗长，不解除会让弹窗实例一直被持有。</summary>
    private void OnDialogClosed(object? sender, EventArgs e)
    {
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.IgnoredFolders.CollectionChanged -= OnSettingsCollectionChanged;
        _viewModel.SizeRuleChips.CollectionChanged -= OnSettingsCollectionChanged;
        Closing -= OnDialogClosing;
        Closed -= OnDialogClosed;
    }

    private void OnHeaderDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    /// <summary>
    /// 关闭窗口时，若设置真的改过就对该目录补扫一次。
    /// <para>
    /// 挂在 <see cref="Window.Closing"/> 而不是具体的关闭按钮上：标题栏 ✕、Esc、Alt+F4
    /// （SC_CLOSE）三条路径最终都会走到这里，补扫不会漏。
    /// 配置本身是即时保存的（每个 setter 都会落盘），这里只补「让新设置对已有图片生效」：
    /// 包含子文件夹、忽略名单、分辨率阈值、幅面上限都只在下一次扫描时重新判定。
    /// 这里不取消关闭（e.Cancel 始终为 false），只做「判定 + 启扫描 + 写日志」。
    /// </para>
    /// </summary>
    private void OnDialogClosing(object? sender, CancelEventArgs e)
    {
        if (_closing)
        {
            return;
        }

        _closing = true;

        // 路径是草稿态，必须在关窗时提交（空路径等同放弃，不做改动）。
        var pathChanged = CommitPath();

        if ((!pathChanged && !_settingsChanged)
            || !_viewModel.Enabled
            || !IsMonitoringEnabled())
        {
            return;
        }

        // 关窗即开始扫描，用户回到页面就能看到卡片上的扫描进度。
        AppServices.Log?.Info("目录设置", $"设置已更新，重新扫描 {_viewModel.ResolvedPath}");
        _viewModel.StartScan();
    }

    // ============================================================
    // 监控路径
    // ============================================================

    /// <summary>
    /// 提交路径草稿。返回 true 表示路径确实变了，调用方需要把它当作一次设置变更。
    /// <para>
    /// 提交流程与卡片原来的内联编辑一致：落盘 → 卸载旧路径的监视 → 按新路径重挂。
    /// 换路径等于换了一批文件，随后的补扫由关闭流程统一触发。
    /// </para>
    /// </summary>
    private bool CommitPath()
    {
        if (!_viewModel.CommitEdit())
        {
            // 未改动或空输入：把草稿还原成已保存的模板，界面上不留下无效内容。
            _viewModel.BeginEdit();
            return false;
        }

        var folder = _viewModel.Model;
        AppServices.Config?.SaveNow();
        AppServices.Monitor?.StopFolder(folder.Id);

        if (folder.Enabled && IsMonitoringEnabled())
        {
            AppServices.Monitor?.StartFolder(folder);
        }

        AppServices.Log?.Info("目录设置", $"路径已修改为 {folder.Path}", folder.Path);
        _viewModel.BeginEdit();
        return true;
    }

    /// <summary>浏览并选择文件夹；选中后写进草稿，用户还能继续补占位符。</summary>
    private void OnBrowsePathClick(object sender, RoutedEventArgs e)
    {
        try
        {
            using var dialog = new WinForms.FolderBrowserDialog
            {
                Description = "选择需要自动处理图片的目录",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true
            };

            // 当前路径模板展开后若是一个存在的目录，用它作为选择器的初始位置。
            var current = (_viewModel.ResolvedPath ?? string.Empty).Trim();
            if (current.Length > 0 && Directory.Exists(current))
            {
                dialog.SelectedPath = current;
            }

            if (dialog.ShowDialog() != WinForms.DialogResult.OK)
            {
                return;
            }

            _viewModel.EditingPath = dialog.SelectedPath;
            PathInput.CaretIndex = PathInput.Text.Length;
            PathInput.Focus();
        }
        catch (Exception ex)
        {
            AppServices.Log?.Warn("目录设置", $"打开目录选择器失败：{ex.Message}");
        }
    }

    /// <summary>把路径里的日期片段一键转成时间占位符；没识别到日期就不动，避免误改。</summary>
    private void OnConvertPathClick(object sender, RoutedEventArgs e)
    {
        var text = (PathInput.Text ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return;
        }

        var converted = PathTemplateService.ConvertDatesToPlaceholders(text);
        if (string.Equals(converted, text, StringComparison.Ordinal))
        {
            return;
        }

        _viewModel.EditingPath = converted;
        PathInput.CaretIndex = converted.Length;
        PathInput.Focus();
    }

    /// <summary>点占位符胶囊：在光标处插入该占位符。</summary>
    private void OnInsertTokenClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string token } || token.Length == 0)
        {
            return;
        }

        var caret = PathInput.CaretIndex;
        var text = PathInput.Text ?? string.Empty;

        if (caret < 0 || caret > text.Length)
        {
            caret = text.Length;
        }

        _viewModel.EditingPath = text.Insert(caret, token);
        PathInput.CaretIndex = caret + token.Length;
        PathInput.Focus();
    }

    /// <summary>路径框里按回车立即提交（不必等关窗）；Esc 交给窗口统一处理。</summary>
    private void OnPathKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        CommitPath();
    }

    /// <summary>全局监控总开关是否打开；暂停状态下不扫描，与卡片扫描按钮的口径保持一致。</summary>
    private static bool IsMonitoringEnabled()
    {
        try
        {
            return AppServices.Config?.Current?.MonitoringEnabled ?? false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ============================================================
    // 分辨率阈值
    // ============================================================

    private void OnThresholdPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = e.Text.Any(ch => !char.IsDigit(ch) && ch != '.');
    }

    private void OnThresholdLostFocus(object sender, RoutedEventArgs e)
    {
        // 输入非法时回退到上一个合法值，避免留下空/脏数据。
        if (sender is TextBox box && !double.TryParse(box.Text, out _))
        {
            _viewModel.RevertThresholdText();
        }
    }

    // ============================================================
    // 忽略的子文件夹
    // ============================================================

    private void OnIgnoredFolderKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box)
        {
            return;
        }

        _viewModel.AddIgnoredFolder(box.Text);
        box.Text = string.Empty;
        e.Handled = true;
    }

    private void OnIgnoredFolderLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox box)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(box.Text))
        {
            _viewModel.AddIgnoredFolder(box.Text);
            box.Text = string.Empty;
        }
    }

    private void OnRemoveIgnoredFolderClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string name })
        {
            _viewModel.RemoveIgnoredFolder(name);
        }
    }

    // ============================================================
    // 幅面上限与规则
    // ============================================================

    private void OnMaxSideCmKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        _viewModel.CommitMaxSideCm();
        e.Handled = true;
    }

    private void OnMaxSideCmLostFocus(object sender, RoutedEventArgs e) => _viewModel.CommitMaxSideCm();

    private void OnAddSizeRuleClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Parent is not Panel panel)
        {
            return;
        }

        TextBox? keywordBox = null;
        TextBox? cmBox = null;

        foreach (var child in panel.Children)
        {
            if (child is not TextBox box)
            {
                continue;
            }

            if (Equals(box.Tag, "kw"))
            {
                keywordBox = box;
            }
            else if (Equals(box.Tag, "cm"))
            {
                cmBox = box;
            }
        }

        if (keywordBox is null || cmBox is null)
        {
            return;
        }

        if (_viewModel.AddSizeRule(keywordBox.Text, cmBox.Text))
        {
            keywordBox.Text = string.Empty;
            cmBox.Text = string.Empty;
            keywordBox.Focus();
        }
    }

    private void OnRemoveSizeRuleClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: SizeRuleChip chip })
        {
            _viewModel.RemoveSizeRule(chip);
        }
    }
}
