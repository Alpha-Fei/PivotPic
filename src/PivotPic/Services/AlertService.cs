using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using PivotPic.Models;

namespace PivotPic.Services;

/// <summary>阈值告警：超阈值即记一条告警，去重 24 小时，历史保留 7 天。</summary>
public sealed class AlertService : IAlertService
{
    /// <summary>未读列表上限，超出丢弃最旧的一条。</summary>
    private const int MaxUnreadCount = 200;

    /// <summary>同一文件同一状态的去重窗口。</summary>
    private static readonly TimeSpan DedupeWindow = TimeSpan.FromHours(24);

    /// <summary>历史保留天数。</summary>
    private static readonly TimeSpan HistoryRetention = TimeSpan.FromDays(7);

    /// <summary>UTF-8 无 BOM。</summary>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly object _gate = new();

    /// <summary>未读告警，供 UI 直接绑定；写入发生在后台线程，UI 侧自行切 Dispatcher。</summary>
    private readonly ObservableCollection<AlertItem> _unread = new();

    /// <summary>7 天内的全部告警（含已读），用于统计今日数量与持久化。</summary>
    private readonly List<AlertItem> _history = new();

    /// <summary>去重表：key → 最近一次告警时间（UTC）。</summary>
    private readonly Dictionary<string, DateTime> _dedupe = new(StringComparer.OrdinalIgnoreCase);

    private DateTime _today = DateTime.Today;
    private int _todayCount;

    /// <summary>创建服务、确定历史文件路径并加载历史。</summary>
    public AlertService()
    {
        AlertDirectory = AppPaths.DataRoot;
        HistoryPath = AppPaths.AlertHistoryPath;
        LoadHistory();
    }

    /// <inheritdoc />
    public event EventHandler<IReadOnlyList<AlertItem>>? AlertsRaised;

    /// <summary>历史文件所在目录。</summary>
    public string AlertDirectory { get; }

    /// <summary>历史文件完整路径。</summary>
    public string HistoryPath { get; }

    /// <inheritdoc />
    public IReadOnlyList<AlertItem> Unread => _unread;

    /// <inheritdoc />
    public int UnreadCount
    {
        get
        {
            lock (_gate)
            {
                return _unread.Count;
            }
        }
    }

    /// <inheritdoc />
    public int TodayCount
    {
        get
        {
            lock (_gate)
            {
                RefreshTodayLocked();
                return _todayCount;
            }
        }
    }

    /// <inheritdoc />
    public void EvaluateFile(string fullPath, WatchedFolder folder)
    {
        if (string.IsNullOrWhiteSpace(fullPath) || folder is null)
        {
            return;
        }

        var probe = AppServices.Image?.Probe(fullPath);
        if (probe is null)
        {
            return;
        }

        Evaluate(fullPath, probe, folder);
    }

    /// <inheritdoc />
    public void Evaluate(string fullPath, ImageProbe probe, WatchedFolder folder)
    {
        if (string.IsNullOrWhiteSpace(fullPath) || probe is null || folder is null)
        {
            return;
        }

        // 阈值检查未开启时一律不告警（调用方通常已过滤，这里兜一层防止误用）。
        if (!folder.ResolutionCheckEnabled || folder.Threshold <= 0)
        {
            return;
        }

        // 用 EXIF 校正后的逻辑尺寸，和用户肉眼看到的方向一致。
        var width = probe.OrientedWidth;
        var height = probe.OrientedHeight;
        var value = Measure(width, height, probe.FileSize, folder.Metric);

        // 严格大于阈值才算超标。
        if (value <= folder.Threshold)
        {
            return;
        }

        var key = BuildDedupeKey(fullPath, width, height, folder.Metric);
        AlertItem item;
        var nowUtc = DateTime.UtcNow;

        lock (_gate)
        {
            if (_dedupe.TryGetValue(key, out var lastRaised) && nowUtc - lastRaised < DedupeWindow)
            {
                // 24 小时内同一文件同一尺寸同一口径已经告过警，静默忽略。
                return;
            }

            _dedupe[key] = nowUtc;

            item = new AlertItem
            {
                FullPath = fullPath,
                FileName = Path.GetFileName(fullPath),
                Width = width,
                Height = height,
                FileSize = probe.FileSize,
                Metric = folder.Metric,
                Value = value,
                Threshold = folder.Threshold,
                Time = DateTime.Now
            };

            _unread.Add(item);
            while (_unread.Count > MaxUnreadCount)
            {
                _unread.RemoveAt(0);
            }

            _history.Add(item);
            PruneHistoryLocked(DateTime.Now);
            RefreshTodayLocked();
        }

        // 文件 IO 与事件都放到锁外。
        SaveHistory();
        RaiseAlerts(new[] { item });
    }

    /// <inheritdoc />
    public void MarkAllRead()
    {
        lock (_gate)
        {
            _unread.Clear();
        }
    }

    /// <inheritdoc />
    public void Locate(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return;
        }

        try
        {
            var startInfo = new ProcessStartInfo("explorer.exe", $"/select,\"{fullPath}\"")
            {
                UseShellExecute = true
            };
            Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            AppServices.Log?.Warn("告警", $"定位文件失败：{ex.Message}", string.Empty, Path.GetFileName(fullPath));
        }
    }

    /// <summary>按口径计算度量值。</summary>
    private static double Measure(int width, int height, long fileSize, ThresholdMetric metric)
    {
        return metric switch
        {
            ThresholdMetric.LongEdge => Math.Max(width, height),
            ThresholdMetric.ShortEdge => Math.Min(width, height),
            ThresholdMetric.Megapixel => width * (double)height / 10000.0,
            ThresholdMetric.FileSizeKb => fileSize / 1024.0,
            _ => Math.Max(width, height)
        };
    }

    /// <summary>构造去重键：路径 + 最后写入时间 + 尺寸 + 口径。</summary>
    private static string BuildDedupeKey(string fullPath, int width, int height, ThresholdMetric metric)
    {
        long ticks = 0;
        try
        {
            ticks = File.GetLastWriteTimeUtc(fullPath).Ticks;
        }
        catch (Exception)
        {
            // 文件在告警瞬间被删除时仍要能生成 key，退化为 0。
            ticks = 0;
        }

        return $"{fullPath.ToLowerInvariant()}|{ticks}|{width}x{height}|{metric}";
    }

    /// <summary>必须在持有 _gate 的前提下调用：跨天时重算今日计数。</summary>
    private void RefreshTodayLocked()
    {
        var today = DateTime.Today;
        if (today == _today)
        {
            return;
        }

        _today = today;
        CountTodayLocked();
    }

    /// <summary>必须在持有 _gate 的前提下调用：重新统计今日条数。</summary>
    private void CountTodayLocked()
    {
        var count = 0;
        foreach (var entry in _history)
        {
            if (entry.Time.Date == _today)
            {
                count++;
            }
        }

        _todayCount = count;
    }

    /// <summary>必须在持有 _gate 的前提下调用：丢弃超过保留期的历史与被淘汰的去重键。</summary>
    private void PruneHistoryLocked(DateTime now)
    {
        var threshold = now - HistoryRetention;

        for (var index = _history.Count - 1; index >= 0; index--)
        {
            if (_history[index].Time < threshold)
            {
                _history.RemoveAt(index);
            }
        }

        // 去重键只服务 24 小时窗口，超过即删，避免字典无界增长。
        var expiredKeys = new List<string>();
        foreach (var pair in _dedupe)
        {
            if (now.ToUniversalTime() - pair.Value >= DedupeWindow)
            {
                expiredKeys.Add(pair.Key);
            }
        }

        foreach (var key in expiredKeys)
        {
            _dedupe.Remove(key);
        }

        CountTodayLocked();
    }

    /// <summary>触发告警事件；订阅方异常不影响调用方。</summary>
    private void RaiseAlerts(IReadOnlyList<AlertItem> items)
    {
        try
        {
            AlertsRaised?.Invoke(this, items);
        }
        catch (Exception ex)
        {
            AppServices.Log?.Error("告警", $"告警回调异常：{ex.Message}");
        }
    }

    /// <summary>加载历史；文件缺失或损坏时静默从空历史开始。</summary>
    private void LoadHistory()
    {
        try
        {
            if (!File.Exists(HistoryPath))
            {
                return;
            }

            var json = File.ReadAllText(HistoryPath, Encoding.UTF8);
            var file = JsonSerializer.Deserialize<AlertHistoryFile>(json, AppServices.JsonOptions);
            if (file is null)
            {
                return;
            }

            lock (_gate)
            {
                _history.Clear();
                if (file.Items is not null)
                {
                    foreach (var item in file.Items)
                    {
                        if (item is null)
                        {
                            continue;
                        }

                        _history.Add(item);
                    }
                }

                _dedupe.Clear();
                if (file.Dedupe is not null)
                {
                    foreach (var pair in file.Dedupe)
                    {
                        _dedupe[pair.Key] = pair.Value;
                    }
                }

                _today = DateTime.Today;
                PruneHistoryLocked(DateTime.Now);
                CountTodayLocked();
            }
        }
        catch (Exception ex)
        {
            AppServices.Log?.Warn("告警", $"读取告警历史失败，已按空历史启动：{ex.Message}");
        }
    }

    /// <summary>把历史与去重表写盘；任何异常都只记日志。</summary>
    private void SaveHistory()
    {
        try
        {
            AlertHistoryFile snapshot;

            lock (_gate)
            {
                var items = new List<AlertItem>(_history.Count);
                foreach (var item in _history)
                {
                    items.Add(item);
                }

                var dedupe = new Dictionary<string, DateTime>(_dedupe.Count, StringComparer.OrdinalIgnoreCase);
                foreach (var pair in _dedupe)
                {
                    dedupe[pair.Key] = pair.Value;
                }

                snapshot = new AlertHistoryFile { Items = items, Dedupe = dedupe };
            }

            var directory = Path.GetDirectoryName(HistoryPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(snapshot, AppServices.JsonOptions);
            File.WriteAllText(HistoryPath, json, Utf8NoBom);
        }
        catch (Exception ex)
        {
            AppServices.Log?.Warn("告警", $"保存告警历史失败：{ex.Message}");
        }
    }

    /// <summary>历史文件结构：告警条目 + 去重表（跨重启保持 24 小时不重复）。</summary>
    private sealed class AlertHistoryFile
    {
        public List<AlertItem> Items { get; set; } = new();

        public Dictionary<string, DateTime> Dedupe { get; set; } = new();
    }
}
