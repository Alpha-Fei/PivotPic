using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Threading;
using Microsoft.Win32;

namespace PivotPic.Shared;

/// <summary>
/// 卸载与退出前的「加速组件」善后逻辑。
/// <para>
/// 这个文件被三个项目共享编译（主程序 / 安装程序 / 卸载程序），
/// 因此只用 BCL 与 Win32，不引用任何 PivotPic 类型，也不依赖主程序的服务定位器。
/// </para>
/// <para>
/// 处理顺序是硬契约，不可调换：先结束进程，再还原系统代理（此时快照还在），
/// 最后才能删除工作目录（快照文件就在里面）。顺序错了会永久留下一个指向空端口的 PAC，
/// 表现为「卸载后所有 GitHub 访问都不通」。
/// </para>
/// </summary>
internal static class ProxyCleanup
{
    /// <summary>加速组件的可执行文件名（不含扩展名），用于结束进程。</summary>
    private static readonly string[] ProxyProcessNames = { "gitport-github", "gitport" };

    /// <summary>Internet 设置所在注册表路径（当前用户，不走 HKLM）。</summary>
    private const string InternetSettingsKey =
        @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    /// <summary>
    /// 加速组件的代理端口。与主程序 <c>UpdateDefaults.ProxyPort</c> 保持一致；
    /// 本文件被三个项目共享编译，不能引用主程序的类型，因此这里单独声明一份。
    /// </summary>
    private const int ProxyPort = 7810;

    /// <summary>加速组件的 PAC / 状态接口端口（代理端口 +1），PAC 地址就挂在这里。</summary>
    private const int ProxyWebUiPort = ProxyPort + 1;

    /// <summary>Internet 选项：设置变更 / 立即刷新，写注册表后必须调用才会即时生效。</summary>
    private const int InternetOptionSettingsChanged = 39;
    private const int InternetOptionRefresh = 37;

    /// <summary>
    /// 执行完整的加速组件善后：结束进程 → 还原系统代理 → 删除工作目录 → 清理下载缓存。
    /// 任何一步失败都不影响后续步骤，整体不抛异常。
    /// </summary>
    /// <param name="installDir">安装目录。用于推导数据根（与主程序 <c>AppPaths</c> 的规则一致）。</param>
    /// <param name="dataRootOverride">已知的数据根；主程序退出时直接传入，省去重复探测。</param>
    public static void CleanupAll(string installDir, string? dataRootOverride = null)
    {
        var dataRoot = string.IsNullOrWhiteSpace(dataRootOverride)
            ? ResolveDataRoot(installDir)
            : dataRootOverride!;

        try
        {
            KillProxyProcesses();
        }
        catch (Exception)
        {
            // 结束进程失败也要继续还原 PAC。
        }

        try
        {
            RestoreSystemProxy(dataRoot);
        }
        catch (Exception)
        {
            // 还原失败继续清理目录，pac-backup.json 会被保留以便下次再试。
        }

        try
        {
            DeleteDirectoryQuietly(Path.Combine(dataRoot, "proxy"), keepPacBackupWhenRestoreFailed: true);
        }
        catch (Exception)
        {
            // 忽略。
        }

        try
        {
            DeleteDirectoryQuietly(Path.Combine(dataRoot, "update"), keepPacBackupWhenRestoreFailed: false);
            DeleteDirectoryQuietly(Path.Combine(dataRoot, "update-tmp"), keepPacBackupWhenRestoreFailed: false);
        }
        catch (Exception)
        {
            // 忽略。
        }
    }

    /// <summary>
    /// 按主程序的规则推导数据根：安装目录可写就用它，否则退回 <c>%AppData%\PivotPic</c>。
    /// </summary>
    public static string ResolveDataRoot(string installDir)
    {
        if (IsWritable(installDir))
        {
            return installDir;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PivotPic");
    }

    /// <summary>结束所有加速组件进程（含子进程）。返回成功结束的进程数。</summary>
    public static int KillProxyProcesses()
    {
        var killed = 0;

        foreach (var name in ProxyProcessNames)
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(name);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var process in processes)
            {
                try
                {
                    if (process.HasExited)
                    {
                        continue;
                    }

                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(3000);
                    killed++;
                }
                catch (Exception)
                {
                    // 已退出或无权限时忽略。
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        return killed;
    }

    /// <summary>
    /// 按快照还原系统代理。
    /// <para>
    /// 只有当当前 PAC 仍然指向本机加速端口时才写回快照——用户中途改用公司 VPN 或
    /// 其它代理软件时不能被我们覆盖掉。这一条是安全边界，不要为了「干净」而放宽。
    /// </para>
    /// </summary>
    /// <returns>确实执行了还原返回 true。</returns>
    public static bool RestoreSystemProxy(string dataRoot)
    {
        var backupPath = Path.Combine(dataRoot, "proxy", "pac-backup.json");
        if (!File.Exists(backupPath))
        {
            return false;
        }

        JsonObject? backup;
        try
        {
            backup = JsonNode.Parse(File.ReadAllText(backupPath)) as JsonObject;
        }
        catch (Exception)
        {
            backup = null;
        }

        if (backup is null)
        {
            return false;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, writable: true);
            if (key is null)
            {
                return false;
            }

            var current = key.GetValue("AutoConfigURL") as string;
            if (!LooksLikeOurPac(current))
            {
                // 已经不是我们的 PAC：用户自己改过，保持原样并删掉过期快照。
                TryDeleteFile(backupPath);
                return false;
            }

            ApplySnapshot(key, backup);
            RefreshInternetSettings();
            TryDeleteFile(backupPath);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 判断一个 PAC 地址是否确实指向**本机加速端口**。
    /// <para>
    /// 这里是安全边界，判据必须精确。早期版本用三个子串（<c>:78</c> / <c>proxy.pac</c> / <c>gitport</c>）
    /// 任一命中就认作自己的——这会把用户自己的代理软件误判成我们写的：
    /// 例如 Clash 的 <c>http://127.0.0.1:7890/proxy.pac</c> 同时命中 <c>:78</c> 与 <c>proxy.pac</c>，
    /// 我们退出时就会把旧快照盖回去、还顺手删掉快照，用户再也回不到自己的配置。
    /// </para>
    /// <para>
    /// 现在的判据：必须是合法 URL、主机是回环地址、<b>端口精确命中</b>加速端口或其 PAC 端口，
    /// 且路径带 PAC 特征。宁可留下一个指向死端口的 PAC（下次启动会自愈），也绝不覆盖别人的设置。
    /// </para>
    /// </summary>
    public static bool LooksLikeOurPac(string? pacUrl)
    {
        if (string.IsNullOrWhiteSpace(pacUrl))
        {
            return false;
        }

        if (!Uri.TryCreate(pacUrl.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        if (!IsLoopbackHost(uri.Host))
        {
            return false;
        }

        // 端口精确匹配：代理端口 7810 或它的 PAC 端口 7811。7890/8080 之类绝不放过。
        if (uri.Port != ProxyPort && uri.Port != ProxyWebUiPort)
        {
            return false;
        }

        // 端口对上了再看路径特征，进一步降低误判概率（对未来改路径留一点余量）。
        return uri.AbsolutePath.Contains("pac", StringComparison.OrdinalIgnoreCase)
               || uri.AbsolutePath.Length <= 1;
    }

    /// <summary>回环主机判定（127.0.0.1 / localhost / ::1）。</summary>
    private static bool IsLoopbackHost(string host)
    {
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }

        return string.Equals(host, "127.0.0.1", StringComparison.Ordinal)
               || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
               || string.Equals(host, "::1", StringComparison.Ordinal)
               || string.Equals(host, "[::1]", StringComparison.Ordinal);
    }

    /// <summary>把快照里的三项写回注册表。缺失的键按「原本没有」处理。</summary>
    private static void ApplySnapshot(RegistryKey key, JsonObject snapshot)
    {
        var autoConfig = snapshot["AutoConfigUrl"]?.GetValue<string?>();

        if (string.IsNullOrWhiteSpace(autoConfig))
        {
            key.DeleteValue("AutoConfigURL", throwOnMissingValue: false);
        }
        else
        {
            key.SetValue("AutoConfigURL", autoConfig, RegistryValueKind.String);
        }

        var proxyServer = snapshot["ProxyServer"]?.GetValue<string?>();
        if (string.IsNullOrWhiteSpace(proxyServer))
        {
            key.DeleteValue("ProxyServer", throwOnMissingValue: false);
        }
        else
        {
            key.SetValue("ProxyServer", proxyServer, RegistryValueKind.String);
        }

        var proxyEnable = ReadInt(snapshot, "ProxyEnable");
        if (proxyEnable.HasValue)
        {
            key.SetValue("ProxyEnable", proxyEnable.Value, RegistryValueKind.DWord);
        }
        else
        {
            key.DeleteValue("ProxyEnable", throwOnMissingValue: false);
        }
    }

    /// <summary>宽容地从 JSON 对象里读一个整数（可能被写成字符串或布尔）。</summary>
    private static int? ReadInt(JsonObject node, string name)
    {
        var value = node[name];
        if (value is null)
        {
            return null;
        }

        try
        {
            return value.GetValue<int>();
        }
        catch (Exception)
        {
            // 类型不符时退回字符串解析。
        }

        try
        {
            if (int.TryParse(value.ToJsonString().Trim('"'), out var parsed))
            {
                return parsed;
            }
        }
        catch (Exception)
        {
            // 忽略。
        }

        return null;
    }

    /// <summary>通知 WinINet 代理设置已变更，避免要等下一次网络请求才生效。</summary>
    private static void RefreshInternetSettings()
    {
        try
        {
            InternetSetOptionW(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
            InternetSetOptionW(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
        }
        catch (Exception)
        {
            // 通知失败时系统会在下次请求时自行重读。
        }
    }

    /// <summary>
    /// 删除目录。还原 PAC 失败时保留 <c>pac-backup.json</c>，让下次启动还能补救。
    /// </summary>
    private static void DeleteDirectoryQuietly(string directory, bool keepPacBackupWhenRestoreFailed)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        var keepPac = keepPacBackupWhenRestoreFailed
                      && File.Exists(Path.Combine(directory, "pac-backup.json"));

        foreach (var file in SafeEnumerateFiles(directory))
        {
            if (keepPac && string.Equals(Path.GetFileName(file), "pac-backup.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            TryDeleteFile(file);
        }

        if (keepPac && File.Exists(Path.Combine(directory, "pac-backup.json")))
        {
            return;
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (Exception)
            {
                Thread.Sleep(200);
            }
        }
    }

    /// <summary>枚举文件，目录被占用时返回已枚举到的部分而不是抛异常。</summary>
    private static IEnumerable<string> SafeEnumerateFiles(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories);
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
        catch (Exception)
        {
            // 被占用时忽略。
        }
    }

    /// <summary>通过建一个临时文件判断目录是否可写。</summary>
    private static bool IsWritable(string directory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return false;
            }

            var probe = Path.Combine(directory, ".cleanup-probe-" + Guid.NewGuid().ToString("N"));
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.WriteByte(0);
            }

            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    [DllImport("wininet.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InternetSetOptionW(
        IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);
}
