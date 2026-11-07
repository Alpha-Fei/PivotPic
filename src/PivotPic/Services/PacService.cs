using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace PivotPic.Services;

/// <summary>
/// 系统代理（PAC）快照与还原。
/// <para>
/// 加速组件以系统分流模式运行时会把当前用户的 <c>AutoConfigURL</c> 改成本机 PAC。
/// 本服务负责在启动前留快照、启动后核对、退出时还原。
/// </para>
/// <para>
/// 还原采用「条件还原」：只有当 <c>AutoConfigURL</c> 仍然指向本机加速端口时才写回快照。
/// 用户在运行期间改用公司 VPN 或别的代理软件时，我们绝不覆盖他的设置——
/// 宁可留下一个指向本机的 PAC（用户能自己看到并清掉），也不能破坏别人的网络配置。
/// </para>
/// </summary>
internal sealed class PacService : IPacService
{
    /// <summary>当前用户的 Internet 设置（与系统的「Internet 选项」同一处）。</summary>
    private const string InternetSettingsKey =
        @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    /// <summary>Internet 选项：设置变更 / 立即刷新。</summary>
    private const int InternetOptionSettingsChanged = 39;
    private const int InternetOptionRefresh = 37;

    /// <summary>串行化快照与还原，避免启动与退出并发时交叉写注册表。</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <inheritdoc />
    public bool HasPendingBackup => File.Exists(GitPortPaths.PacBackupPath);

    /// <inheritdoc />
    public async Task<bool> CaptureBaselineAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(CaptureBaselineCore, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> VerifyAsync(int proxyPort, CancellationToken cancellationToken = default)
    {
        if (proxyPort <= 0)
        {
            return false;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => VerifyCore(proxyPort), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(RestoreCore, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ============================================================
    // 实现
    // ============================================================

    /// <summary>把当前的系统代理三项写进快照文件。已有快照时不覆盖（否则会把「已改过的值」当成基线）。</summary>
    private bool CaptureBaselineCore()
    {
        try
        {
            if (File.Exists(GitPortPaths.PacBackupPath))
            {
                // 上一次的快照还在：说明上次还原没成功执行（或正在修复），保留更早的真实基线。
                AppServices.Log?.Info("加速", "已存在系统代理快照，沿用旧快照");
                return true;
            }

            Directory.CreateDirectory(GitPortPaths.WorkingDirectory);

            string? autoConfigUrl = null;
            string? proxyServer = null;
            int? proxyEnable = null;

            using (var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, writable: false))
            {
                if (key is not null)
                {
                    autoConfigUrl = key.GetValue("AutoConfigURL") as string;
                    proxyServer = key.GetValue("ProxyServer") as string;
                    if (key.GetValue("ProxyEnable") is int enable)
                    {
                        proxyEnable = enable;
                    }
                }
            }

            // 当前 PAC 已经指向本机加速端口，而我们手上没有更早的基线：
            // 说明上一次异常退出没还干净，或用户自己把 PAC 指到了我们的端口。
            // 此时唯一安全的基线是「没有 PAC」——把它记成基线，退出时就能清掉，
            // 而不是把一个指向死端口的 PAC 原样写回去。
            if (Shared.ProxyCleanup.LooksLikeOurPac(autoConfigUrl))
            {
                AppServices.Log?.Info("加速", "当前 PAC 已指向本机加速端口且无更早基线，基线按「无 PAC」记录");
                autoConfigUrl = null;
            }

            var snapshot = new JsonObject
            {
                ["AutoConfigUrl"] = autoConfigUrl is null ? null : JsonValue.Create(autoConfigUrl),
                ["ProxyServer"] = proxyServer is null ? null : JsonValue.Create(proxyServer),
                ["ProxyEnable"] = proxyEnable.HasValue ? JsonValue.Create(proxyEnable.Value) : null,
                ["CapturedUtc"] = JsonValue.Create(DateTime.UtcNow.ToString("O"))
            };

            var temp = GitPortPaths.PacBackupPath + ".tmp";
            File.WriteAllText(temp, snapshot.ToJsonString(AppServices.JsonOptions));
            File.Move(temp, GitPortPaths.PacBackupPath, overwrite: true);

            AppServices.Log?.Info("加速", "已记录系统代理快照，退出时按它还原");
            return true;
        }
        catch (Exception ex)
        {
            AppServices.Log?.Warn("加速", $"记录系统代理快照失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 核对 PAC 是否已指向本机加速端口。
    /// <para>
    /// 实测：加速组件的 PAC 由它随附的本地 Web 服务提供，地址形如
    /// <c>http://127.0.0.1:{port+1}/proxy.pac</c>（代理端口 +1），
    /// 所以这里必须同时接受 +1，否则会误判成「PAC 没生效」而白降一档。
    /// </para>
    /// <para>
    /// 判据必须精确到端口：早期版本用「含 127.0.0.1 且含 proxy.pac」这种子串判定，
    /// Clash 的 <c>127.0.0.1:7890/proxy.pac</c> 也会被判成我们的 PAC。
    /// </para>
    /// </summary>
    private static bool VerifyCore(int proxyPort)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, writable: false);
            var current = key?.GetValue("AutoConfigURL") as string;

            if (string.IsNullOrWhiteSpace(current))
            {
                return false;
            }

            if (!Uri.TryCreate(current.Trim(), UriKind.Absolute, out var uri))
            {
                return false;
            }

            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            {
                return false;
            }

            var host = uri.Host;
            var isLoopback = string.Equals(host, "127.0.0.1", StringComparison.Ordinal)
                             || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(host, "::1", StringComparison.Ordinal);

            if (!isLoopback)
            {
                return false;
            }

            // 端口必须精确命中代理端口或它的 PAC 端口，其余一律不认。
            return uri.Port == proxyPort || uri.Port == proxyPort + 1;
        }
        catch (Exception ex)
        {
            AppServices.Log?.Warn("加速", $"核对系统代理失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 按快照还原。核心逻辑与卸载程序共用 <see cref="Shared.ProxyCleanup.RestoreSystemProxy"/>，
    /// 保证「运行时还原」与「卸载时还原」的判定完全一致。
    /// </summary>
    private void RestoreCore()
    {
        try
        {
            var restored = Shared.ProxyCleanup.RestoreSystemProxy(AppPaths.DataRoot);

            if (restored)
            {
                RefreshInternetSettings();
                AppServices.Log?.Info("加速", "系统代理已还原");
            }
            else if (HasPendingBackup)
            {
                // 快照还在但没写回：几乎总是「用户中途改了代理设置」，我们按设计不覆盖。
                AppServices.Log?.Info("加速", "系统代理已被其它程序改动，跳过还原");
            }
        }
        catch (Exception ex)
        {
            AppServices.Log?.Warn("加速", $"还原系统代理失败：{ex.Message}");
        }
    }

    /// <summary>通知 WinINet 代理设置已变更。</summary>
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

    [System.Runtime.InteropServices.DllImport("wininet.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode,
        SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool InternetSetOptionW(
        IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);
}
