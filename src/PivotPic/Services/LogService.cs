using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using PivotPic.Models;

namespace PivotPic.Services;

/// <summary>滚动文件日志：数据目录下 logs\app-yyyyMMdd.log，内存保留最近 2000 条。</summary>
public sealed class LogService : ILogService, IDisposable
{
    /// <summary>内存中保留的条目上限。</summary>
    private const int MaxMemoryEntries = 2000;

    /// <summary>单个日志文件上限 5 MB，超出即轮转。</summary>
    private const long MaxFileBytes = 5L * 1024 * 1024;

    /// <summary>启动时保留的日志文件数量。</summary>
    private const int RetainFileCount = 7;

    /// <summary>保护内存集合。</summary>
    private readonly object _recentGate = new();

    /// <summary>保护文件写入，与内存锁分离，避免磁盘 IO 阻塞 UI 读取。</summary>
    private readonly object _fileGate = new();

    private readonly ObservableCollection<LogEntry> _recent = new();
    private readonly ReadOnlyObservableCollection<LogEntry> _recentView;

    private readonly UTF8Encoding _utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private bool _disposed;

    /// <summary>创建服务、准备日志目录并清理过期日志文件。</summary>
    public LogService()
    {
        LogDirectory = AppPaths.LogDirectory;
        _recentView = new ReadOnlyObservableCollection<LogEntry>(_recent);

        try
        {
            Directory.CreateDirectory(LogDirectory);
        }
        catch (Exception)
        {
            // 目录建不出来时文件日志会静默失效，内存日志仍然可用。
        }

        TrimOldFiles();
    }

    /// <inheritdoc />
    public string LogDirectory { get; }

    /// <summary>
    /// 内存中的最近日志。为方便 WPF 绑定返回可观察集合的只读包装；
    /// 集合写入发生在调用线程，UI 侧自行切回 Dispatcher 再订阅/绑定。
    /// </summary>
    public IReadOnlyList<LogEntry> Recent => _recentView;

    /// <inheritdoc />
    public event EventHandler<LogEntry>? EntryAdded;

    /// <summary>写入一条日志：先落文件，再进内存，最后触发 EntryAdded。</summary>
    public void Write(LogLevel level, string folder, string fileName, string? originalSize,
        string action, long elapsedMs, string message)
    {
        var entry = new LogEntry
        {
            Time = DateTime.Now,
            Level = level,
            Folder = folder ?? string.Empty,
            FileName = fileName ?? string.Empty,
            OriginalSize = originalSize,
            Action = action ?? string.Empty,
            ElapsedMs = elapsedMs,
            Message = message ?? string.Empty
        };

        AppendToFile(entry);

        lock (_recentGate)
        {
            _recent.Add(entry);

            // 环形裁剪：超限时从最旧的一端丢弃。
            while (_recent.Count > MaxMemoryEntries)
            {
                _recent.RemoveAt(0);
            }
        }

        // 事件在锁外触发，订阅方即使回读 Recent 也不会自锁。
        EntryAdded?.Invoke(this, entry);
    }

    /// <inheritdoc />
    public void Info(string action, string message, string folder = "", string fileName = "")
    {
        Write(LogLevel.Info, folder, fileName, null, action, 0, message);
    }

    /// <inheritdoc />
    public void Warn(string action, string message, string folder = "", string fileName = "")
    {
        Write(LogLevel.Warn, folder, fileName, null, action, 0, message);
    }

    /// <inheritdoc />
    public void Error(string action, string message, string folder = "", string fileName = "")
    {
        Write(LogLevel.Error, folder, fileName, null, action, 0, message);
    }

    /// <summary>停止接受新条目（文件与内存均不再写入）。</summary>
    public void Dispose()
    {
        lock (_recentGate)
        {
            _disposed = true;
        }
    }

    /// <summary>追加一行到当日日志文件，超出 5 MB 先轮转；任何异常都吞掉。</summary>
    private void AppendToFile(LogEntry entry)
    {
        if (_disposed)
        {
            return;
        }

        lock (_fileGate)
        {
            try
            {
                Directory.CreateDirectory(LogDirectory);

                var path = CurrentFilePath();
                if (File.Exists(path) && new FileInfo(path).Length >= MaxFileBytes)
                {
                    path = RotateCurrentFile(path);
                }

                // LogEntry.ToString() 已含完整字段，按行追加，UTF-8 无 BOM。
                File.AppendAllText(path, entry + Environment.NewLine, _utf8NoBom);
            }
            catch (Exception)
            {
                // 磁盘满、权限不足、文件被占用——日志失败绝不允许冒泡到业务线程。
            }
        }
    }

    /// <summary>当日日志文件的完整路径。</summary>
    private string CurrentFilePath()
    {
        return Path.Combine(LogDirectory, $"app-{DateTime.Now:yyyyMMdd}.log");
    }

    /// <summary>把已满的当日日志改名为 app-yyyyMMdd.{序号}.log，并返回继续写入的新文件路径。</summary>
    private string RotateCurrentFile(string activePath)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd");

        for (var index = 1; index <= 999; index++)
        {
            var candidate = Path.Combine(LogDirectory, $"app-{stamp}.{index}.log");
            if (File.Exists(candidate))
            {
                continue;
            }

            try
            {
                File.Move(activePath, candidate);
                // 轮转完成后继续写回原名字（新建空文件），保持「当日文件名固定」的语义。
                return activePath;
            }
            catch (Exception)
            {
                // 改名失败（例如被占用）时直接继续追加原文件，宁可文件大一点也不丢日志。
                return activePath;
            }
        }

        // 序号耗尽属于极端情况，同样退化为直接追加。
        return activePath;
    }

    /// <summary>按最后写入时间保留最近 7 个日志文件，其余删除。</summary>
    private void TrimOldFiles()
    {
        try
        {
            var directory = new DirectoryInfo(LogDirectory);
            if (!directory.Exists)
            {
                return;
            }

            var files = directory.GetFiles("app-*.log");
            if (files.Length <= RetainFileCount)
            {
                return;
            }

            var stale = files
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Skip(RetainFileCount);

            foreach (var file in stale)
            {
                try
                {
                    file.Delete();
                }
                catch (Exception)
                {
                    // 正在被其它进程占用的日志留给下次启动再清。
                }
            }
        }
        catch (Exception)
        {
            // 清理失败不影响日志功能本身。
        }
    }
}
