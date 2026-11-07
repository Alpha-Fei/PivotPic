using System;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace PivotPic.Services;

/// <summary>
/// 网络路径识别与延迟探测。
/// 支持 UNC（\\server\share）和已映射的网络盘符（Z:\），后者通过 WNetGetConnection 反查 UNC。
/// </summary>
internal static class NetworkPath
{
    /// <summary>超过这个延迟就认为连接不稳定。</summary>
    public const int UnstableLatencyMs = 120;

    private const int PingTimeoutMs = 1500;

    /// <summary>判断路径是否位于网络位置，并取出主机名用于 ping。</summary>
    public static bool TryGetHost(string? path, out string host)
    {
        host = string.Empty;

        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        // UNC：\\server\share\...
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return TrySplitUncHost(path, out host);
        }

        // 映射盘：先看 DriveType，再反查 UNC 取主机名
        try
        {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root) || root.Length < 2 || root[1] != ':')
            {
                return false;
            }

            var drive = new DriveInfo(root);
            if (drive.DriveType != DriveType.Network)
            {
                return false;
            }

            var unc = GetUncPath(root.TrimEnd(Path.DirectorySeparatorChar));
            return !string.IsNullOrEmpty(unc) && TrySplitUncHost(unc, out host);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>同步探测一次网络延迟（毫秒）；不可达时返回 null。</summary>
    public static async Task<long?> MeasureAsync(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, PingTimeoutMs).ConfigureAwait(false);
            return reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool TrySplitUncHost(string unc, out string host)
    {
        host = string.Empty;

        var rest = unc.StartsWith(@"\\", StringComparison.Ordinal) ? unc.Substring(2) : unc;
        var index = rest.IndexOf('\\');
        host = (index > 0 ? rest.Substring(0, index) : rest).Trim();

        return host.Length > 0;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetGetConnection(string localName, StringBuilder remoteName, ref int length);

    private static string? GetUncPath(string driveRoot)
    {
        try
        {
            var buffer = new StringBuilder(512);
            var size = buffer.Capacity;
            var result = WNetGetConnection(driveRoot, buffer, ref size);

            // ERROR_MORE_DATA(234) 时会回填所需长度，再试一次。
            if (result == 234)
            {
                buffer = new StringBuilder(size);
                result = WNetGetConnection(driveRoot, buffer, ref size);
            }

            return result == 0 ? buffer.ToString() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
