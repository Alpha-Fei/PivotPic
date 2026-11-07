using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using PivotPic.Models;
using PivotPic.Services;

namespace PivotPic.Views;

/// <summary>
/// 日志页：级别筛选 + 关键字搜索（300ms 防抖）+ 虚拟化列表。
/// <para>
/// 新条目按 100ms 批量合并后一次性插入顶部，避免高频写日志时把 UI 线程刷爆。
/// </para>
/// </summary>
public partial class LogPage : UserControl
{
    /// <summary>内存里最多展示多少条（与 LogService 的保留上限一致）。</summary>
    private const int MaxRows = 2000;

    /// <summary>搜索防抖窗口。</summary>
    private const int SearchDebounceMs = 300;

    /// <summary>新条目批量合并窗口。</summary>
    private const int BatchFlushMs = 100;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly ObservableCollection<LogRow> _entries = new();
    private readonly ICollectionView _view;

    /// <summary>待插入的新条目，可能来自后台处理线程。</summary>
    private readonly List<LogEntry> _pending = new();

    /// <summary>保护 <see cref="_pending"/>。</summary>
    private readonly object _pendingGate = new();

    private readonly DispatcherTimer _flushTimer;
    private readonly DispatcherTimer _searchTimer;

    private bool _flushScheduled;
    private string _searchText = string.Empty;
    private LogLevel? _levelFilter;

    /// <summary>构造页面：装载历史日志并挂上增量订阅。</summary>
    public LogPage()
    {
        InitializeComponent();

        _view = CollectionViewSource.GetDefaultView(_entries);
        _view.Filter = MatchEntry;
        LogList.ItemsSource = _view;

        _flushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(BatchFlushMs) };
        _flushTimer.Tick += (_, _) => FlushPending();

        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SearchDebounceMs) };
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            ApplyFilter();
        };

        CmbLevel.SelectedIndex = 0;

        LoadExistingEntries();

        if (AppServices.Log is not null)
        {
            AppServices.Log.EntryAdded += OnEntryAdded;
        }
    }

    // ============================================================
    // 初始装载
    // ============================================================

    private void LoadExistingEntries()
    {
        var recent = AppServices.Log?.Recent;

        // 注意：不能在 DeferRefresh 的作用域内改动数据源，
        // ListCollectionView 在延迟刷新期间禁止内容变更（会抛 InvalidOperationException）。
        _entries.Clear();

        if (recent is not null)
        {
            // Recent 是时间正序，界面上要最新的在最上面，所以反向插入。
            for (var index = recent.Count - 1; index >= 0; index--)
            {
                var entry = recent[index];

                if (entry is null)
                {
                    continue;
                }

                _entries.Add(new LogRow(entry));

                if (_entries.Count >= MaxRows)
                {
                    break;
                }
            }
        }

        UpdateCount();
    }

    // ============================================================
    // 增量条目
    // ============================================================

    private void OnEntryAdded(object? sender, LogEntry entry)
    {
        if (entry is null)
        {
            return;
        }

        lock (_pendingGate)
        {
            _pending.Add(entry);
        }

        // 后台线程不能直接碰 DispatcherTimer 之外的东西，统一切回 UI 线程调度。
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(ScheduleFlush));
            return;
        }

        ScheduleFlush();
    }

    private void ScheduleFlush()
    {
        if (_flushScheduled)
        {
            return;
        }

        _flushScheduled = true;
        _flushTimer.Start();
    }

    /// <summary>把攒下的条目一次性插入列表顶部。</summary>
    private void FlushPending()
    {
        _flushTimer.Stop();
        _flushScheduled = false;

        List<LogEntry> batch;

        lock (_pendingGate)
        {
            if (_pending.Count == 0)
            {
                return;
            }

            batch = new List<LogEntry>(_pending);
            _pending.Clear();
        }

        // 逐个插入即可：ListCollectionView 会在每次插入时增量套用过滤器，
        // 比 DeferRefresh + 整体 Refresh 更省，也不违反延迟刷新期间禁止改源集合的规则。
        foreach (var entry in batch)
        {
            _entries.Insert(0, new LogRow(entry));
        }

        while (_entries.Count > MaxRows)
        {
            _entries.RemoveAt(_entries.Count - 1);
        }

        UpdateCount();
    }

    // ============================================================
    // 筛选与搜索
    // ============================================================

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            // 构造期 XAML 初始化时 SelectedIndex 会被赋值，此时还没绑数据，直接返回。
            return;
        }

        _levelFilter = CmbLevel.SelectedIndex switch
        {
            1 => LogLevel.Info,
            2 => LogLevel.Warn,
            3 => LogLevel.Error,
            _ => null
        };

        ApplyFilter();
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        _searchText = (TxtSearch.Text ?? string.Empty).Trim();

        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void ApplyFilter()
    {
        using (_view.DeferRefresh())
        {
            // 过滤器可变，Refresh 才会重新求值。
        }

        UpdateCount();
    }

    private bool MatchEntry(object item)
    {
        if (item is not LogRow row)
        {
            return false;
        }

        var entry = row.Entry;

        if (_levelFilter is { } level && entry.Level != level)
        {
            return false;
        }

        if (_searchText.Length == 0)
        {
            return true;
        }

        return ContainsIgnoringCase(entry.FileName)
               || ContainsIgnoringCase(entry.Action)
               || ContainsIgnoringCase(entry.Folder)
               || ContainsIgnoringCase(entry.Message);
    }

    private bool ContainsIgnoringCase(string? value)
    {
        return !string.IsNullOrEmpty(value)
               && value.Contains(_searchText, StringComparison.OrdinalIgnoreCase);
    }

    // ============================================================
    // 工具条动作
    // ============================================================

    private void OnOpenLogDirectoryClick(object sender, RoutedEventArgs e)
    {
        var directory = AppServices.Log?.LogDirectory;

        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppServices.Log?.Warn("日志", $"打开日志目录失败：{ex.Message}");
            Notify("打开失败", ex.Message);
        }
    }

    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "导出日志",
                FileName = $"PivotPic-log-{DateTime.Now:yyyyMMdd-HHmmss}.log",
                DefaultExt = ".log",
                Filter = "日志文件 (*.log)|*.log|文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
                AddExtension = true,
                OverwritePrompt = true
            };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            var lines = _view.Cast<LogRow>().Select(row => row.Entry.ToString());
            File.WriteAllLines(dialog.FileName, lines, Utf8NoBom);

            AppServices.Log?.Info("日志", $"日志已导出到 {dialog.FileName}");
            Notify("导出成功", dialog.FileName);
        }
        catch (Exception ex)
        {
            AppServices.Log?.Error("日志", $"导出日志失败：{ex.Message}");
            Notify("导出失败", ex.Message);
        }
    }

    // ============================================================
    // 辅助
    // ============================================================

    private void UpdateCount()
    {
        var count = 0;

        try
        {
            foreach (var _ in _view)
            {
                count++;
            }
        }
        catch (InvalidOperationException)
        {
            // 视图正处在延迟刷新中时不允许枚举，退回未过滤总数，下一次刷新会纠正。
            count = _entries.Count;
        }

        CountText.Text = $"共 {count} 条";
    }

    /// <summary>用托盘气泡做轻量提示。</summary>
    private static void Notify(string title, string message)
    {
        try
        {
            AppServices.Tray?.ShowBalloon(title, message);
        }
        catch (Exception)
        {
            // 通知失败不得影响主流程。
        }
    }
}

/// <summary>
/// 日志列表的行视图：把 <see cref="LogEntry"/> 拍平成「每列一个字符串」，
/// 让 XAML 里的 Grid 各列宽度固定、纵向严格对齐。
/// <para>
/// 空字段统一显示 <c>—</c>，而不是让模板里出现连续分隔符（旧实现会出现「扫描 · · · 0 ms」）。
/// 列宽与对齐都在模板里控制，这里只负责取值。
/// </para>
/// </summary>
public sealed class LogRow
{
    /// <summary>空字段占位符。</summary>
    public const string Placeholder = "—";

    /// <summary>包装一条日志，预先算好各列显示文本。</summary>
    /// <param name="entry">原始日志条目，不能为 null。</param>
    public LogRow(LogEntry entry)
    {
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));

        TimeText = entry.TimeText;
        LevelText = entry.LevelText;
        Level = entry.Level;

        ActionText = TranslateAction(entry.Action);
        FileNameText = OrPlaceholder(entry.FileName);
        SizeText = OrPlaceholder(entry.OriginalSize);

        // ElapsedMs 为 0 表示这条日志没有计时（Info/Warn 走的 Write 重载就是 0），显示占位符更诚实。
        ElapsedText = entry.ElapsedMs > 0 ? $"{entry.ElapsedMs} ms" : Placeholder;
    }

    /// <summary>原始条目，供筛选与导出使用。</summary>
    public LogEntry Entry { get; }

    /// <summary>HH:mm:ss。</summary>
    public string TimeText { get; }

    /// <summary>信息 / 警告 / 错误。</summary>
    public string LevelText { get; }

    /// <summary>级别枚举，模板据此染色。</summary>
    public LogLevel Level { get; }

    /// <summary>动作列。</summary>
    public string ActionText { get; }

    /// <summary>把内部动作标识翻译成界面用语；未知标识原样返回，便于排查。</summary>
    private static string TranslateAction(string action) => action switch
    {
        "Rotated" => "已旋转",
        "SkippedVertical" => "竖版跳过",
        "SkippedSquare" => "正方形跳过",
        "SkippedTooLarge" => "超大跳过",
        "SkippedTooBigCm" => "幅面超限",
        "SkippedReadOnly" => "只读跳过",
        "SkippedEmpty" => "空文件跳过",
        "SkippedUnsupportedEncoder" => "格式不支持",
        "Failed" => "处理失败",
        _ => action
    };

    /// <summary>文件名列。</summary>
    public string FileNameText { get; }

    /// <summary>原始尺寸列。</summary>
    public string SizeText { get; }

    /// <summary>耗时列。</summary>
    public string ElapsedText { get; }

    /// <summary>空白（含纯空格）一律转成占位符。</summary>
    private static string OrPlaceholder(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? Placeholder : value;
    }
}
