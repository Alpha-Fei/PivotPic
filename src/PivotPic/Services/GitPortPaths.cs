using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace PivotPic.Services;

/// <summary>
/// 内置加速组件的落盘位置与配置文件读写。
/// <para>
/// 加速组件是随包分发的第三方可执行文件（Apache-2.0），用户界面上不出现它的名字，
/// 这里刻意用「加速组件」称呼，避免把技术名词漏进界面文案。
/// </para>
/// </summary>
internal static class GitPortPaths
{
    /// <summary>随包分发的二进制所在目录（安装目录下）。</summary>
    public static string PackageDirectory => Path.Combine(AppContext.BaseDirectory, "proxy");

    /// <summary>随包分发的二进制路径。</summary>
    public static string BinarySourcePath => Path.Combine(PackageDirectory, "gitport-github.exe");

    /// <summary>随包分发的 Apache-2.0 许可证全文。</summary>
    public static string LicenseSourcePath => Path.Combine(PackageDirectory, "LICENSE-APACHE-2.0.txt");

    /// <summary>随包分发的 NOTICE / 来源声明。</summary>
    public static string NoticeSourcePath => Path.Combine(PackageDirectory, "NOTICE.txt");

    /// <summary>随包分发的版本与校验清单。</summary>
    public static string VersionManifestPath => Path.Combine(PackageDirectory, "version.json");

    /// <summary>加速组件的工作目录（数据根下的 proxy 子目录）。</summary>
    public static string WorkingDirectory => Path.Combine(AppPaths.DataRoot, "proxy");

    /// <summary>运行用的二进制副本。</summary>
    public static string BinaryPath => Path.Combine(WorkingDirectory, "gitport-github.exe");

    /// <summary>加速组件的设置文件。</summary>
    public static string SettingsPath => Path.Combine(WorkingDirectory, "settings.json");

    /// <summary>系统代理快照。退出或关闭加速时据此还原。</summary>
    public static string PacBackupPath => Path.Combine(WorkingDirectory, "pac-backup.json");

    /// <summary>加速组件自己的日志（失败排查用）。</summary>
    public static string ProxyLogPath => Path.Combine(WorkingDirectory, "proxy.log");

    /// <summary>更新元数据目录：latest.json / latest.etag / pending.json 与安装包本体。</summary>
    public static string UpdateRoot => Path.Combine(AppPaths.DataRoot, "update");

    /// <summary>更新包下载的兜底目录（安装目录所在盘空间不足时启用）。</summary>
    public static string UpdateFallbackRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PivotPic",
        "update");

    /// <summary>最近一次 Release 元数据缓存。</summary>
    public static string LatestManifestPath => Path.Combine(UpdateRoot, "latest.json");

    /// <summary>最近一次 Release 请求的 ETag，用于条件请求避开限流。</summary>
    public static string LatestEtagPath => Path.Combine(UpdateRoot, "latest.etag");

    /// <summary>下载完成但尚未安装的包记录（下次启动可复用）。</summary>
    public static string PendingPackagePath => Path.Combine(UpdateRoot, "pending.json");

    /// <summary>按加速组件的工作目录与端口拼出可用的代理地址。</summary>
    public static string ProxyUrl(int port) => $"http://127.0.0.1:{port}";

    /// <summary>
    /// 确保工作目录里有一份可执行的加速组件副本。
    /// 源与副本都做 SHA256 比对，命中就直接复用，避免每次启动都复制 7 MB。
    /// </summary>
    /// <returns>可用返回 true；源缺失或复制失败返回 false。</returns>
    public static async Task<bool> EnsureBinaryAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(BinarySourcePath))
            {
                AppServices.Log?.Warn("加速", $"未找到加速组件：{BinarySourcePath}");
                return false;
            }

            Directory.CreateDirectory(WorkingDirectory);

            // 许可证与来源声明随副本一起落地，满足 Apache-2.0 的分发要求。
            CopyQuietly(LicenseSourcePath, Path.Combine(WorkingDirectory, "LICENSE-APACHE-2.0.txt"));
            CopyQuietly(NoticeSourcePath, Path.Combine(WorkingDirectory, "NOTICE.txt"));

            var sourceHash = await ComputeSha256Async(BinarySourcePath, cancellationToken).ConfigureAwait(false);
            if (sourceHash is null)
            {
                return false;
            }

            if (File.Exists(BinaryPath))
            {
                var currentHash = await ComputeSha256Async(BinaryPath, cancellationToken).ConfigureAwait(false);
                if (string.Equals(currentHash, sourceHash, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            // 副本被占用时先尝试改名（正在运行的进程也能被改名），再重新复制。
            var staging = BinaryPath + ".new";
            File.Copy(BinarySourcePath, staging, overwrite: true);

            try
            {
                File.Move(staging, BinaryPath, overwrite: true);
            }
            catch (IOException)
            {
                File.Delete(BinaryPath);
                File.Move(staging, BinaryPath, overwrite: true);
            }

            var installedHash = await ComputeSha256Async(BinaryPath, cancellationToken).ConfigureAwait(false);
            var ok = string.Equals(installedHash, sourceHash, StringComparison.OrdinalIgnoreCase);

            if (ok)
            {
                AppServices.Log?.Info("加速", "加速组件已就位");
            }
            else
            {
                AppServices.Log?.Warn("加速", "加速组件复制后校验不一致");
            }

            return ok;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppServices.Log?.Warn("加速", $"准备加速组件失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 改写加速组件的设置文件：始终关闭它的解密能力（纯透传），
    /// 并声明是否接管 GitHub 域名（系统分流）。
    /// <para>文件里其它键原样保留，避免把用户在组件界面里调过的设置清掉。</para>
    /// </summary>
    /// <param name="enableSystemPac">true 表示请求组件写入系统 PAC（只接管 GitHub 域）。</param>
    public static bool PatchSettings(bool enableSystemPac)
    {
        try
        {
            Directory.CreateDirectory(WorkingDirectory);

            JsonObject root = new();
            if (File.Exists(SettingsPath))
            {
                try
                {
                    var text = File.ReadAllText(SettingsPath);
                    if (!string.IsNullOrWhiteSpace(text) && JsonNode.Parse(text) is JsonObject existing)
                    {
                        root = existing;
                    }
                }
                catch (Exception)
                {
                    // 设置文件损坏时用空对象重建，不影响启动。
                }
            }

            // 纯透传模式的核心约定：绝不解密 TLS，因此也永远不需要证书。
            root["mitm"] = false;
            root["githubPAC"] = enableSystemPac;

            // 组件自带的开机自启与托盘行为一律关闭，避免出现第二个托盘图标。
            root["autostart"] = false;
            root["closeToTray"] = false;

            var json = root.ToJsonString(AppServices.JsonOptions);
            var temp = SettingsPath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, SettingsPath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            AppServices.Log?.Warn("加速", $"写入加速组件设置失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>读回加速组件的当前设置，仅用于诊断。</summary>
    public static IReadOnlyDictionary<string, string> ReadSettingsSnapshot()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (!File.Exists(SettingsPath))
            {
                return result;
            }

            if (JsonNode.Parse(File.ReadAllText(SettingsPath)) is not JsonObject root)
            {
                return result;
            }

            foreach (var pair in root)
            {
                result[pair.Key] = pair.Value?.ToJsonString() ?? "null";
            }
        }
        catch (Exception)
        {
            // 诊断用途，失败返回已读到的部分。
        }

        return result;
    }

    /// <summary>计算文件 SHA256（小写十六进制）；文件不存在或读取失败返回 null。</summary>
    public static async Task<string?> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);

            using var sha = SHA256.Create();
            var hash = await sha.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>复制小文件，失败忽略（许可证缺失不影响运行）。</summary>
    private static void CopyQuietly(string source, string destination)
    {
        try
        {
            if (!File.Exists(source))
            {
                return;
            }

            if (File.Exists(destination))
            {
                var sourceInfo = new FileInfo(source);
                var destInfo = new FileInfo(destination);
                if (sourceInfo.Length == destInfo.Length)
                {
                    return;
                }
            }

            File.Copy(source, destination, overwrite: true);
        }
        catch (Exception)
        {
            // 忽略。
        }
    }
}
