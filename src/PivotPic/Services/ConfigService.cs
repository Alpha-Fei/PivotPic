using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using PivotPic.Models;

namespace PivotPic.Services;

/// <summary>配置读写：优先安装目录下的 config.json（不可写时回退 %AppData%），UTF-8 无 BOM，原子替换落盘。</summary>
public sealed class ConfigService : IConfigService, IDisposable
{
    /// <summary>保存防抖窗口，连续改动只落盘一次。</summary>
    private const int DebounceMilliseconds = 500;

    private const int DefaultMaxPixels = 300_000_000;

    /// <summary>上限允许的最大值：2000 MP。再大基本不可能在普通机器上解码成功。</summary>
    private const int MaxAllowedPixels = 2_000_000_000;

    /// <summary>v1 时代的默认上限。用于识别"用户从没改过"，升级时替换成新默认。</summary>
    private const int LegacyDefaultMaxPixels = 120_000_000;

    /// <summary>当前配置架构版本。v4 起新增「更新与网络」相关键。</summary>
    private const int CurrentSchemaVersion = 5;

    /// <summary>自动检查更新间隔的允许区间（小时）。</summary>
    private const int MinCheckIntervalHours = 1;
    private const int MaxCheckIntervalHours = 168;

    /// <summary>后缀为空时的默认值。</summary>
    private const string DefaultSuffix = "_已旋转";

    /// <summary>成品幅面上限的默认值（厘米）。</summary>
    private const double DefaultMaxSideCm = 100;
    private const double DefaultThreshold = 300;

    private static readonly string[] DefaultExtensions =
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".tif", ".tiff"
    };

    /// <summary>UTF-8 无 BOM，方便用户用记事本直接编辑。</summary>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>全局互斥：Load/Save/Import/Reset 都靠它串行化。</summary>
    private readonly object _gate = new();

    /// <summary>用 System.Threading.Timer 而非 DispatcherTimer：SaveDebounced 可能来自后台线程；
    /// 项目同时启用 WinForms，故必须写全名避免与 System.Windows.Forms.Timer 歧义。</summary>
    private readonly System.Threading.Timer _debounceTimer;

    private AppConfig _current = new();
    private bool _pendingSave;
    private bool _disposed;

    /// <summary>创建服务并确定配置目录（不会自动读取配置，需显式调用 Load）。</summary>
    public ConfigService()
    {
        ConfigDirectory = AppPaths.DataRoot;
        ConfigPath = AppPaths.ConfigPath;
        _debounceTimer = new System.Threading.Timer(
            OnDebounceElapsed, null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <inheritdoc />
    public string ConfigDirectory { get; }

    /// <summary>config.json 的完整路径。</summary>
    public string ConfigPath { get; }

    /// <inheritdoc />
    public AppConfig Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <summary>读取配置；文件缺失或损坏时回退到默认配置并隔离坏文件。</summary>
    public void Load()
    {
        // 锁内只做数据操作，日志与事件都留到锁外，避免订阅方回调时产生锁竞争。
        var quarantinedPath = string.Empty;
        var failureReason = string.Empty;

        lock (_gate)
        {
            EnsureDirectoryQuietly();

            if (!File.Exists(ConfigPath))
            {
                // 首次运行：内存里直接给出「已经过清洗的当前版本配置」。
                // 关键：必须显式写 SchemaVersion 并走 Sanitize，否则首启的 SchemaVersion 停在 1，
                // 用户首启关掉「网络加速 / 自动检查」后，第二次启动会被 v3→v4 迁移改回默认值并落盘，
                // 表现为「我关过的开关自己又打开了」，还会静默重新接管系统代理。
                _current = Sanitize(new AppConfig
                {
                    FirstRun = true,
                    SchemaVersion = CurrentSchemaVersion
                });
            }
            else
            {
                AppConfig? loaded = null;

                try
                {
                    var json = File.ReadAllText(ConfigPath, Encoding.UTF8);
                    loaded = JsonSerializer.Deserialize<AppConfig>(json, AppServices.JsonOptions);
                    if (loaded is null)
                    {
                        failureReason = "配置文件内容为空或结构与 AppConfig 不匹配";
                    }
                }
                catch (Exception ex)
                {
                    failureReason = ex.Message;
                }

                if (loaded is null)
                {
                    quarantinedPath = QuarantineBrokenConfig() ?? string.Empty;
                    _current = Sanitize(new AppConfig
                    {
                        FirstRun = true,
                        SchemaVersion = CurrentSchemaVersion
                    });
                }
                else
                {
                    _current = Sanitize(loaded);
                }
            }
        }

        if (failureReason.Length > 0)
        {
            AppServices.Log?.Warn("配置", $"配置文件无法解析，已重置为默认配置：{failureReason}");
            if (quarantinedPath.Length > 0)
            {
                AppServices.Log?.Info("配置", $"损坏的配置已另存为 {quarantinedPath}");
            }
        }

        // 无论成功还是回退都通知订阅方，保证 UI 显示的永远是清洗后的状态。
        RaiseChanged();
    }

    /// <summary>立即落盘（同步），失败只记日志不抛异常。</summary>
    public void SaveNow()
    {
        string? error;
        lock (_gate)
        {
            _pendingSave = false;
            error = WriteConfigFileLocked();
        }

        LogSaveError(error);
    }

    /// <summary>防抖落盘：500ms 内的多次调用只写一次文件。</summary>
    public void SaveDebounced()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _pendingSave = true;
            try
            {
                _debounceTimer.Change(DebounceMilliseconds, Timeout.Infinite);
            }
            catch (ObjectDisposedException)
            {
                // 极端的退出竞态：定时器已释放，直接同步落盘兜底。
                var error = WriteConfigFileLocked();
                _pendingSave = false;
                LogSaveError(error);
            }
        }
    }

    /// <summary>把当前配置导出到指定路径（不影响正在使用的配置）。</summary>
    public void Export(string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            throw new ArgumentException("导出路径不能为空。", nameof(targetPath));
        }

        AppConfig snapshot;
        lock (_gate)
        {
            // 取引用快照即可：调用方导出时不会并发写入配置项。
            snapshot = _current;
        }

        var fullPath = Path.GetFullPath(targetPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(snapshot, AppServices.JsonOptions);
        File.WriteAllText(fullPath, json, Utf8NoBom);
    }

    /// <summary>从指定路径导入配置并立即生效，失败抛出 <see cref="InvalidOperationException"/>。</summary>
    public void Import(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            throw new ArgumentException("导入路径不能为空。", nameof(sourcePath));
        }

        if (!File.Exists(sourcePath))
        {
            throw new InvalidOperationException($"导入失败，文件不存在：{sourcePath}");
        }

        AppConfig? imported;
        try
        {
            var json = File.ReadAllText(sourcePath, Encoding.UTF8);
            imported = JsonSerializer.Deserialize<AppConfig>(json, AppServices.JsonOptions);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"导入失败，文件不是有效的配置文件：{ex.Message}", ex);
        }

        if (imported is null)
        {
            throw new InvalidOperationException("导入失败，配置文件内容为空。");
        }

        var sanitized = Sanitize(imported);
        lock (_gate)
        {
            _current = sanitized;
        }

        SaveNow();
        RaiseChanged();
    }

    /// <summary>恢复出厂配置并落盘。</summary>
    public void ResetToDefault()
    {
        lock (_gate)
        {
            // 与首启同一套规则：直接是当前架构版本的清洗结果，重建后不会再被迁移逻辑改写。
            _current = Sanitize(new AppConfig { SchemaVersion = CurrentSchemaVersion });
        }

        SaveNow();
        RaiseChanged();
    }

    /// <summary>停止防抖定时器，若有未落盘的改动则冲刷。</summary>
    public void Dispose()
    {
        bool needFlush;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            needFlush = _pendingSave;
            _pendingSave = false;
        }

        _debounceTimer.Dispose();

        if (needFlush)
        {
            SaveNow();
        }
    }

    /// <summary>定时器回调：真正执行防抖后的写盘。</summary>
    private void OnDebounceElapsed(object? state)
    {
        string? error;

        lock (_gate)
        {
            if (_disposed || !_pendingSave)
            {
                return;
            }

            _pendingSave = false;
            error = WriteConfigFileLocked();
        }

        LogSaveError(error);
    }

    /// <summary>在已持有 _gate 的前提下写盘：tmp 文件 + 原子替换。</summary>
    /// <returns>成功返回 null，失败返回错误描述。</returns>
    private string? WriteConfigFileLocked()
    {
        var tempPath = ConfigPath + ".tmp";

        try
        {
            EnsureDirectoryQuietly();

            var json = JsonSerializer.Serialize(_current, AppServices.JsonOptions);
            File.WriteAllText(tempPath, json, Utf8NoBom);

            if (File.Exists(ConfigPath))
            {
                // 原子替换：写一半断电也不会留下半个 config.json。
                File.Replace(tempPath, ConfigPath, destinationBackupFileName: null,
                    ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, ConfigPath);
            }

            return null;
        }
        catch (Exception ex)
        {
            TryDelete(tempPath);
            return ex.Message;
        }
    }

    /// <summary>把损坏的配置改名保留，便于事后排查；失败返回 null。</summary>
    private string? QuarantineBrokenConfig()
    {
        try
        {
            var target = Path.Combine(
                ConfigDirectory,
                $"config.broken.{DateTime.Now:yyyyMMddHHmmss}.json");

            if (File.Exists(target))
            {
                // 同一秒内多次损坏时补足毫秒，避免覆盖上一份证据。
                target = Path.Combine(
                    ConfigDirectory,
                    $"config.broken.{DateTime.Now:yyyyMMddHHmmssfff}.json");
            }

            File.Move(ConfigPath, target, overwrite: true);
            return target;
        }
        catch (Exception)
        {
            // 连改名都失败时只能放弃隔离，默认配置照常生效。
            return null;
        }
    }

    /// <summary>数据清洗：把反序列化后可能为空或非法的字段修正成可用值。</summary>
    private static AppConfig Sanitize(AppConfig config)
    {
        config.Folders ??= new List<WatchedFolder>();

        if (config.SupportedExtensions is null || config.SupportedExtensions.Count == 0)
        {
            config.SupportedExtensions = new List<string>(DefaultExtensions);
        }
        else
        {
            config.SupportedExtensions = NormalizeExtensions(config.SupportedExtensions);
        }

        // v1 → v2：旧默认 120MP 对喷绘大图偏小，未自定义过的配置跟着升级。
        // v2 → v3：默认引擎切到 libvips，老的「物理旋转」跟随升级（可在设置里改回内置引擎）。
        if (config.SchemaVersion is 2 && config.RotationMode == RotationMode.Physical)
        {
            config.RotationMode = RotationMode.LibVips;
        }

        if (config.SchemaVersion < 2)
        {
            if (config.MaxPixels == LegacyDefaultMaxPixels)
            {
                config.MaxPixels = DefaultMaxPixels;
            }
        }

        // v3 → v4：新增「更新与网络」。老配置升级后默认开启自动检查与系统级 GitHub 分流，
        // 这两项是新版本的核心能力；用户想关掉可以在设置页关。
        // 注意：版本号只在最后一个迁移块里推进，否则 v1 配置会在第一块就被写成 v4，
        // 后面的迁移条件全部落空，新增字段拿不到默认值。
        if (config.SchemaVersion < 4)
        {
            config.AutoUpdateCheck = true;
            config.UpdateCheckIntervalHours = UpdateDefaults.DefaultIntervalHours;
            config.LastCheckUtc = null;
            config.LastCheckOutcome = UpdateCheckOutcome.Unknown;
            config.SkippedVersion = string.Empty;
            config.ProxyMode = ProxyMode.SystemWide;
            config.SchemaVersion = 4;
        }

        // v4 → v5：新增「JPEG 兼容模式」（连续 JPEG 转基线），**按目录**生效。
        // 旧配置里是全局开关，迁移时把它的值复制给每个目录，避免用户已有的偏好被静默丢掉。
        // 新目录的默认值由 WatchedFolder.ConvertProgressiveJpeg = true 提供。
        if (config.SchemaVersion < 5)
        {
            foreach (var folder in config.Folders)
            {
                folder.ConvertProgressiveJpeg = config.ConvertProgressiveJpeg;
            }

            config.SchemaVersion = CurrentSchemaVersion;
        }

        config.UpdateCheckIntervalHours = Math.Clamp(
            config.UpdateCheckIntervalHours, MinCheckIntervalHours, MaxCheckIntervalHours);

        config.SkippedVersion = NormalizeVersionText(config.SkippedVersion);

        // 枚举反序列化可能拿到越界值（手改配置 / 未来降级），落到默认档。
        if (!Enum.IsDefined(config.ProxyMode))
        {
            config.ProxyMode = ProxyMode.SystemWide;
        }

        if (!Enum.IsDefined(config.LastCheckOutcome))
        {
            config.LastCheckOutcome = UpdateCheckOutcome.Unknown;
        }

        if (config.MaxPixels <= 0)
        {
            config.MaxPixels = DefaultMaxPixels;
        }

        if (config.MaxPixels > MaxAllowedPixels)
        {
            config.MaxPixels = MaxAllowedPixels;
        }

        config.Suffix = NormalizeSuffix(config.Suffix);

        // JSON 数组里可能出现 null 项，逐项过一遍并重建列表。
        var folders = new List<WatchedFolder>(config.Folders.Count);
        foreach (var folder in config.Folders)
        {
            if (folder is null)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(folder.Id))
            {
                folder.Id = Guid.NewGuid().ToString("N");
            }

            folder.Path ??= string.Empty;

            if (folder.Threshold <= 0)
            {
                folder.Threshold = DefaultThreshold;
            }

            folder.IgnoredFolders = NormalizeIgnoredFolders(folder.IgnoredFolders);
            folder.SizeRules = NormalizeSizeRules(folder.SizeRules);

            if (double.IsNaN(folder.MaxSideCm) || folder.MaxSideCm <= 0)
            {
                folder.MaxSideCm = DefaultMaxSideCm;
            }
            else if (folder.MaxSideCm > 10_000)
            {
                folder.MaxSideCm = 10_000;
            }

            folders.Add(folder);
        }

        config.Folders = folders;
        return config;
    }

    /// <summary>幅面规则：关键词去空白、去重（不区分大小写），上限夹在 1–10000 之间。</summary>
    private static List<SizeRule> NormalizeSizeRules(IEnumerable<SizeRule>? source)
    {
        var result = new List<SizeRule>();

        if (source is null)
        {
            return result;
        }

        foreach (var rule in source)
        {
            if (rule is null)
            {
                continue;
            }

            var keyword = (rule.Keyword ?? string.Empty).Trim();
            if (keyword.Length == 0)
            {
                continue;
            }

            if (keyword.Length > 32)
            {
                keyword = keyword.Substring(0, 32);
            }

            if (double.IsNaN(rule.MaxSideCm) || rule.MaxSideCm <= 0)
            {
                rule.MaxSideCm = DefaultMaxSideCm;
            }
            else if (rule.MaxSideCm > 10_000)
            {
                rule.MaxSideCm = 10_000;
            }

            if (result.Any(existing => string.Equals(existing.Keyword, keyword, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            rule.Keyword = keyword;
            result.Add(rule);

            if (result.Count >= 20)
            {
                break;
            }
        }

        return result;
    }

    /// <summary>忽略目录名：去空白、剔除非法字符、去重（不区分大小写），最多 20 个。</summary>
    private static List<string> NormalizeIgnoredFolders(IEnumerable<string>? source)
    {
        var result = new List<string>();

        if (source is null)
        {
            return result;
        }

        foreach (var raw in source)
        {
            var name = (raw ?? string.Empty).Trim();
            if (name.Length == 0)
            {
                continue;
            }

            foreach (var invalid in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(invalid.ToString(), string.Empty);
            }

            name = name.Trim();
            if (name.Length == 0 || name == "." || name == "..")
            {
                continue;
            }

            if (name.Length > 64)
            {
                name = name.Substring(0, 64);
            }

            if (result.Any(existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            result.Add(name);

            if (result.Count >= 20)
            {
                break;
            }
        }

        return result;
    }

    /// <summary>
    /// 版本号文本清洗：只保留 <c>数字.数字.数字</c>。
    /// 配置文件可能被手工改成 v1.3.0 或 1.3，统一成三段后再参与比较，
    /// 否则「稍后提醒」会因写法不同而反复弹窗。
    /// </summary>
    private static string NormalizeVersionText(string? raw)
    {
        var parsed = UpdateDefaults.ParseTag(raw);
        return parsed is null ? string.Empty : UpdateDefaults.FormatVersion(parsed);
    }

    /// <summary>后缀：剔除文件名非法字符，最长 24 字符；为空时回退默认值。</summary>
    private static string NormalizeSuffix(string? raw)
    {
        var text = (raw ?? string.Empty).Trim();

        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            text = text.Replace(invalid.ToString(), string.Empty);
        }

        text = text.Trim();

        if (text.Length == 0)
        {
            return DefaultSuffix;
        }

        return text.Length > 24 ? text.Substring(0, 24) : text;
    }

    /// <summary>扩展名统一为「小写 + 前导点」，保证后续比较不用再关心写法。</summary>
    private static List<string> NormalizeExtensions(IEnumerable<string> source)
    {
        var result = new List<string>(source is ICollection<string> collection ? collection.Count : 8);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in source)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var extension = raw.Trim().ToLowerInvariant();
            if (!extension.StartsWith('.'))
            {
                extension = "." + extension;
            }

            if (seen.Add(extension))
            {
                result.Add(extension);
            }
        }

        // 清洗后可能一条都不剩（全是空白项），此时回退到默认值。
        return result.Count > 0 ? result : new List<string>(DefaultExtensions);
    }

    /// <summary>创建配置目录，失败不抛（写盘时还会再试一次）。</summary>
    private void EnsureDirectoryQuietly()
    {
        try
        {
            Directory.CreateDirectory(ConfigDirectory);
        }
        catch (Exception)
        {
            // 目录建不出来时后续写盘会失败并走日志分支，这里无需重复告警。
        }
    }

    /// <summary>删除临时文件，失败忽略。</summary>
    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // 临时文件残留不影响主流程。
        }
    }

    /// <summary>写盘失败统一走 WARN；Log 可能尚未初始化，用 ?. 判空。</summary>
    private static void LogSaveError(string? error)
    {
        if (error is null)
        {
            return;
        }

        AppServices.Log?.Warn("配置", $"保存配置失败：{error}");
    }

    /// <summary>在锁外触发 Changed。</summary>
    private void RaiseChanged()
    {
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
