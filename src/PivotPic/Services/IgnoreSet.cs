using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;

namespace PivotPic.Services;

/// <summary>自触发抑制集合：记住本进程刚写回的文件，让文件监控事件在 TTL 内被丢弃。</summary>
public sealed class IgnoreSet : IIgnoreSet
{
    /// <summary>记忆有效期：写回 → 监控事件到达通常只有几十毫秒，8 秒足够覆盖磁盘抖动。</summary>
    private static readonly TimeSpan TimeToLive = TimeSpan.FromSeconds(8);

    /// <summary>超过这个规模才顺带做一次过期清理，避免高频写入时每次都全表扫描。</summary>
    private const int CompactThreshold = 512;

    private readonly ConcurrentDictionary<string, Entry> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>一条记忆记录，只保存过期时刻。</summary>
    private readonly struct Entry
    {
        public Entry(DateTime expireAt)
        {
            ExpireAt = expireAt;
        }

        public DateTime ExpireAt { get; }
    }

    /// <summary>在写回文件之前登记，TTL 8 秒。</summary>
    public void Remember(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        _entries[Normalize(path)] = new Entry(DateTime.UtcNow + TimeToLive);

        // 惰性清理：规模可控时才付出全表扫描代价。
        if (_entries.Count > CompactThreshold)
        {
            RemoveExpired();
        }
    }

    /// <summary>消费一条记忆：命中且未过期返回 true 并移除；已过期或不命中返回 false。</summary>
    public bool Consume(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        // 无论是否过期都要移除，过期记录没有保留价值。
        if (!_entries.TryRemove(Normalize(path), out var entry))
        {
            return false;
        }

        return entry.ExpireAt > DateTime.UtcNow;
    }

    /// <summary>统一两侧路径形态，避免 "D:\a\b.jpg" 与相对/大小写不同的写法对不上。</summary>
    private static string Normalize(string path)
    {
        var trimmed = path.Trim();
        try
        {
            // 全路径化保证写入方与监控方即使传参形态不同也能命中同一条记录。
            return Path.GetFullPath(trimmed).ToLowerInvariant();
        }
        catch (Exception)
        {
            // 非法路径（含非法字符等）退化为小写原样，仍比直接抛错更健壮。
            return trimmed.ToLowerInvariant();
        }
    }

    /// <summary>清掉所有已过期记录。</summary>
    private void RemoveExpired()
    {
        var now = DateTime.UtcNow;
        foreach (var pair in _entries)
        {
            if (pair.Value.ExpireAt > now)
            {
                continue;
            }

            _entries.TryRemove(pair.Key, out _);
        }
    }
}
