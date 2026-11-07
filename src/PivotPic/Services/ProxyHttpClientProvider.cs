using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using PivotPic.Models;

namespace PivotPic.Services;

/// <summary>
/// 按加速可用性给出 HttpClient。
/// <para>
/// 关键取舍：<b>不可用时也返回可用客户端</b>（直连），绝不抛异常。
/// 更新链路上任何一环都不该因为「加速没起来」而彻底断掉——
/// 加速是优化，直连才是底线。
/// </para>
/// <para>
/// 域名请求本身由加速组件的 PAC 分流接管，所以这里的客户端只要「不主动设置代理」，
/// 系统分流模式下就自然走了加速；只有进程内加速档才需要显式指定 WebProxy。
/// </para>
/// </summary>
internal sealed class ProxyHttpClientProvider : IProxyHttpClientProvider, IDisposable
{
    /// <summary>GitHub API 强制要求 User-Agent，缺失会直接返回 403。</summary>
    private const string UserAgent = "PivotPic-Updater/1.3 (+https://github.com/Alpha-Fei/PivotPic)";

    private readonly object _gate = new();

    private HttpClient? _proxied;
    private HttpClient? _direct;
    private int _proxiedPort;
    private UpdateDownloadSource _source = UpdateDownloadSource.Direct;
    private bool _disposed;

    /// <inheritdoc />
    public UpdateDownloadSource CurrentSource
    {
        get
        {
            lock (_gate)
            {
                return _source;
            }
        }
    }

    /// <inheritdoc />
    public HttpClient CreateClient()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                // 退出竞态下仍有调用时给个一次性客户端，避免空引用炸到调用方。
                return BuildClient(0);
            }

            var health = AppServices.Proxy?.Current;

            // 先把端口取出来，后面的分支就不必反复对可空对象解引用。
            var proxyPort = health is { IsRunning: true } && health.Port > 0 ? health.Port : 0;

            if (proxyPort > 0)
            {
                // 加速组件重启可能换端口，端口变了就重建客户端。
                if (_proxied is null || _proxiedPort != proxyPort)
                {
                    _proxied?.Dispose();
                    _proxied = BuildClient(proxyPort);
                    _proxiedPort = proxyPort;
                }

                _source = UpdateDownloadSource.BuiltInProxy;
                return _proxied;
            }

            _direct ??= BuildClient(0);
            _source = UpdateDownloadSource.Direct;
            return _direct;
        }
    }

    /// <summary>
    /// 构造客户端。超时统一交给调用方的 CancellationToken 控制：
    /// 下载 143 MB 的安装包要几分钟，写死 HttpClient.Timeout 会把长下载直接掐断。
    /// </summary>
    /// <param name="proxyPort">加速端口；0 表示直连。</param>
    private static HttpClient BuildClient(int proxyPort)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false
        };

        if (proxyPort > 0)
        {
            handler.Proxy = new WebProxy($"http://127.0.0.1:{proxyPort}", true);
            handler.UseProxy = true;
        }
        else
        {
            handler.Proxy = null;
            handler.UseProxy = false;
        }

        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        return client;
    }

    /// <inheritdoc />
    public HttpClient CreateDirectClient()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return BuildClient(0);
            }

            _direct ??= BuildClient(0);
            return _direct;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            try
            {
                _proxied?.Dispose();
            }
            catch (Exception)
            {
                // 忽略。
            }

            try
            {
                _direct?.Dispose();
            }
            catch (Exception)
            {
                // 忽略。
            }

            _proxied = null;
            _direct = null;
        }
    }
}
