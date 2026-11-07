using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using PivotPic.Models;

namespace PivotPic.Services;

/// <summary>
/// 在线更新：检查 Release、三源下载、SHA256 校验。
/// <para>
/// 设计原则：
/// 1. 静默检查永不打扰用户，失败只写日志；手动检查才抛出带人话的异常。
/// 2. 下载永远有「直连」兜底，加速只是优化，不能让更新彻底不可用。
/// 3. 校验不过的包一定删除并拒绝执行，宁可让用户重下一次。
/// </para>
/// </summary>
internal sealed class UpdateService : IUpdateService
{
    /// <summary>单次 Release 查询的超时。查询很小，卡住一定是有问题。</summary>
    private const int ApiTimeoutSeconds = 20;

    /// <summary>单次下载尝试的超时上限。143 MB 要几分钟，给足余量。</summary>
    private const int DownloadTimeoutMinutes = 30;

    /// <summary>拼装安装包文件名时禁止出现在路径里的字符。</summary>
    private static readonly char[] InvalidNameChars = Path.GetInvalidFileNameChars();

    /// <inheritdoc />
    public ReleaseInfo? Available { get; private set; }

    /// <inheritdoc />
    public UpdateCheckOutcome LastOutcome { get; private set; } = UpdateCheckOutcome.Unknown;

    /// <inheritdoc />
    public event EventHandler<ReleaseInfo>? UpdateAvailable;

    // ============================================================
    // 检查更新
    // ============================================================

    /// <inheritdoc />
    public async Task<ReleaseInfo?> CheckAsync(UpdateCheckTrigger trigger, CancellationToken cancellationToken)
    {
        var manual = trigger == UpdateCheckTrigger.Manual;

        try
        {
            var (release, notModified) = await FetchReleaseAsync(cancellationToken).ConfigureAwait(false);

            if (notModified)
            {
                // 304：说明缓存就是最新的，直接用本地那份判断。
                release ??= ReadCachedRelease();
            }

            if (release is null)
            {
                return Fail(
                    manual,
                    UpdateCheckOutcome.NoRelease,
                    "暂无可用的发布信息",
                    "304 且本地没有缓存，或发布信息缺少必要字段");
            }

            var current = ReadCurrentVersion();
            var hasUpdate = release.Version > current;

            Available = hasUpdate ? release : null;
            LastOutcome = hasUpdate ? UpdateCheckOutcome.UpdateAvailable : UpdateCheckOutcome.UpToDate;

            PersistCheckResult(LastOutcome);
            AppServices.Log?.Info("更新", hasUpdate
                ? $"发现新版本 {release.TagName}（当前 {AppInfo.VersionText}）"
                : $"已是最新版本（{AppInfo.VersionText}）");

            // 只有静默检查才抛事件，避免手动检查弹两次窗。
            if (hasUpdate && !manual && release is not null)
            {
                RaiseUpdateAvailable(release);
            }

            return hasUpdate ? release : null;
        }
        catch (UpdateFailedException ex)
        {
            return Fail(manual, ex.Outcome, ex.UserMessage, ex.Message, ex);
        }
        catch (OperationCanceledException)
        {
            // 用户主动取消（例如退出程序），不算失败，也不写「失败」状态。
            throw;
        }
        catch (Exception ex)
        {
            return Fail(manual, UpdateCheckOutcome.NetworkError, "检查更新失败", ex.Message, ex);
        }
    }

    /// <summary>
    /// 请求 GitHub API。返回解析好的 Release（或 304 时的 null）+ 是否 304。
    /// 优先用 ETag 条件请求，把「每小时 60 次」的未认证限额影响降到最低。
    /// <para>
    /// 走「直连优先、失败回落加速」：实测元数据请求直连约 0.59s、经加速约 0.95s，
    /// 加速在这里是负收益；但若直连被网络环境阻断，加速通道就是救命的那一条。
    /// </para>
    /// </summary>
    private async Task<(ReleaseInfo? Release, bool NotModified)> FetchReleaseAsync(
        CancellationToken cancellationToken)
    {
        var etag = ReadEtag();

        using var response = await SendWithFallbackAsync(
            () => CreateReleaseRequest(etag), ApiTimeoutSeconds, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return (null, true);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw ClassifyHttpFailure(response);
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var newEtag = response.Headers.ETag?.ToString();

        var release = ParseRelease(json);
        if (release is null)
        {
            throw new UpdateFailedException(
                "发布信息无法识别",
                "Release JSON 缺少 tag_name 或版本号非法",
                outcome: UpdateCheckOutcome.Unrecognized);
        }

        WriteCache(json, newEtag);
        return (release, false);
    }

    /// <summary>
    /// 把 HTTP 失败翻译成人话 + 归类。
    /// <para>
    /// 原则：界面上不出现状态码，日志里必须留全。
    /// 404 与 403 的含义天差地别——前者是「还没有发布」，后者多半是「你查太快了」，
    /// 混成一句「网络失败」会让排查彻底跑偏。
    /// </para>
    /// </summary>
    private static UpdateFailedException ClassifyHttpFailure(HttpResponseMessage response)
    {
        var technical =
            $"Release API 返回 {(int)response.StatusCode} {response.ReasonPhrase}";

        switch (response.StatusCode)
        {
            case HttpStatusCode.NotFound:
                return new UpdateFailedException(
                    "暂无可用的发布信息", technical, outcome: UpdateCheckOutcome.NoRelease);

            case HttpStatusCode.Forbidden:
                // 403 要区分「配额用尽」和真正的禁止访问：只有前者对用户是「稍后重试」。
                return IsRateLimitExhausted(response)
                    ? new UpdateFailedException(
                        "请求过于频繁，稍后重试", technical, outcome: UpdateCheckOutcome.RateLimited)
                    : new UpdateFailedException(
                        "服务暂时不可用，请稍后重试", technical, outcome: UpdateCheckOutcome.NetworkError);

            case HttpStatusCode.TooManyRequests:
                return new UpdateFailedException(
                    "请求过于频繁，稍后重试", technical, outcome: UpdateCheckOutcome.RateLimited);

            default:
                return new UpdateFailedException(
                    "服务暂时不可用，请稍后重试", technical, outcome: UpdateCheckOutcome.NetworkError);
        }
    }

    /// <summary>判断 403 是否因为未认证配额用尽（GitHub 会回 X-RateLimit-Remaining: 0）。</summary>
    private static bool IsRateLimitExhausted(HttpResponseMessage response)
    {
        try
        {
            if (!response.Headers.TryGetValues("X-RateLimit-Remaining", out var values))
            {
                // 拿不到配额头时按「限流」处理：公共仓库的 403 绝大多数就是限流。
                return true;
            }

            return values.FirstOrDefault()?.Trim() == "0";
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>构造 Release 查询请求（带 ETag 条件头）。每次重试都要新建实例。</summary>
    private static HttpRequestMessage CreateReleaseRequest(string? etag)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, UpdateDefaults.LatestReleaseApiUrl);

        if (!string.IsNullOrWhiteSpace(etag))
        {
            // ETag 值本身带引号，直接原样回填。
            request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        }

        return request;
    }

    /// <summary>
    /// HTTP 客户端工厂。未装配时抛出人话异常：
    /// 正常流程里不会发生（装配失败时整个更新模块都不会被启动），但保持类型安全。
    /// </summary>
    private static IProxyHttpClientProvider HttpProvider =>
        AppServices.ProxyHttp
        ?? throw new UpdateFailedException("更新模块未就绪，请重启程序", "ProxyHttpClientProvider 未装配");

    /// <summary>
    /// 小请求的两级通道：先直连，链路层失败再走内置加速。
    /// <para>
    /// 只对「连不上 / 超时」回落；HTTP 4xx/5xx 说明请求本身有问题，换通道重试没有意义。
    /// </para>
    /// </summary>
    private async Task<HttpResponseMessage> SendWithFallbackAsync(
        Func<HttpRequestMessage> requestFactory, int timeoutSeconds, CancellationToken cancellationToken)
    {
        try
        {
            return await SendOnceAsync(
                HttpProvider.CreateDirectClient(), requestFactory(), timeoutSeconds, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ShouldFallback(ex, cancellationToken))
        {
            if (AppServices.Proxy?.Current.IsRunning != true)
            {
                throw new UpdateFailedException("网络不通", $"直连失败：{ex.Message}", ex);
            }

            AppServices.Log?.Info("更新", $"直连失败，改用内置加速重试：{ex.Message}");
        }

        try
        {
            return await SendOnceAsync(
                HttpProvider.CreateClient(), requestFactory(), timeoutSeconds, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ShouldFallback(ex, cancellationToken))
        {
            throw new UpdateFailedException("网络不通", $"直连与加速通道均失败：{ex.Message}", ex);
        }
    }

    /// <summary>是否值得换通道重试：只认链路层失败，用户主动取消不算。</summary>
    private static bool ShouldFallback(Exception ex, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        return ex is UpdateFailedException or HttpRequestException or OperationCanceledException;
    }

    /// <summary>单次发送。超时用独立 CTS 控制，响应体一次读完后再释放。</summary>
    private static async Task<HttpResponseMessage> SendOnceAsync(
        HttpClient client, HttpRequestMessage request, int timeoutSeconds, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            return await client
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpdateFailedException("网络不通", $"请求超时（{timeoutSeconds}s）");
        }
        catch (HttpRequestException ex)
        {
            // DNS 失败、连接被拒、TLS 被重置都归到这一类：对用户就是「网络不通」。
            throw new UpdateFailedException("网络不通", $"请求失败：{ex.Message}", ex);
        }
        finally
        {
            request.Dispose();
        }
    }

    /// <summary>把 Release JSON 解析成 <see cref="ReleaseInfo"/>；缺少 tag 时返回 null。</summary>
    private static ReleaseInfo? ParseRelease(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (Exception)
        {
            return null;
        }

        if (root is not JsonObject obj)
        {
            return null;
        }

        var tagName = GetString(obj, "tag_name");
        var version = UpdateDefaults.ParseTag(tagName);
        if (version is null)
        {
            return null;
        }

        var publishedAt = DateTime.MinValue;
        var rawPublished = GetString(obj, "published_at");
        if (!string.IsNullOrWhiteSpace(rawPublished)
            && DateTime.TryParse(rawPublished, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
        {
            publishedAt = parsed;
        }

        var assets = ReadAssets(obj);
        var setup = MatchSetupAsset(assets, version);
        var checksums = assets.FirstOrDefault(a => string.Equals(
            a.Name, UpdateDefaults.ChecksumsAssetName, StringComparison.OrdinalIgnoreCase));

        return new ReleaseInfo
        {
            TagName = tagName!,
            Version = version,
            PublishedAt = publishedAt,
            Body = GetString(obj, "body") ?? string.Empty,
            SetupAsset = setup ?? new ReleaseAsset(),
            ChecksumsAsset = checksums
        };
    }

    /// <summary>读取 assets 数组。</summary>
    private static List<ReleaseAsset> ReadAssets(JsonObject obj)
    {
        var result = new List<ReleaseAsset>();

        if (obj["assets"] is not JsonArray array)
        {
            return result;
        }

        foreach (var item in array)
        {
            if (item is not JsonObject asset)
            {
                continue;
            }

            var name = GetString(asset, "name");
            var url = GetString(asset, "browser_download_url");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            long size = 0;
            try
            {
                if (asset["size"] is JsonNode sizeNode)
                {
                    size = sizeNode.GetValue<long>();
                }
            }
            catch (Exception)
            {
                size = 0;
            }

            result.Add(new ReleaseAsset
            {
                Name = name!,
                Size = size,
                BrowserDownloadUrl = url!
            });
        }

        return result;
    }

    /// <summary>
    /// 匹配安装包资产。先按「包含 Setup 且以 .exe 结尾」，再退到「体积够大的 .exe」。
    /// 不写死文件名是有意为之：将来改命名规则不用跟着改代码。
    /// </summary>
    private static ReleaseAsset? MatchSetupAsset(IReadOnlyList<ReleaseAsset> assets, Version version)
    {
        var exeAssets = assets
            .Where(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (exeAssets.Count == 0)
        {
            return null;
        }

        var versionText = UpdateDefaults.FormatVersion(version);

        var byKeyword = exeAssets.FirstOrDefault(a =>
            UpdateDefaults.SetupAssetKeyword.Any(k => a.Name.Contains(k, StringComparison.OrdinalIgnoreCase))
            && a.Name.Contains(versionText, StringComparison.OrdinalIgnoreCase));

        if (byKeyword is not null)
        {
            return byKeyword;
        }

        byKeyword = exeAssets.FirstOrDefault(a =>
            UpdateDefaults.SetupAssetKeyword.Any(k => a.Name.Contains(k, StringComparison.OrdinalIgnoreCase)));

        if (byKeyword is not null)
        {
            return byKeyword;
        }

        // 最后兜底：体积达到安装包量级的 exe（排除掉 updater 之类的小工具）。
        return exeAssets
            .Where(a => a.Size >= UpdateDefaults.MinSetupAssetBytes)
            .OrderByDescending(a => a.Size)
            .FirstOrDefault();
    }

    private static string? GetString(JsonObject obj, string name)
    {
        try
        {
            return obj[name]?.GetValue<string?>();
        }
        catch (Exception)
        {
            try
            {
                return obj[name]?.ToJsonString().Trim('"');
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    // ============================================================
    // 下载 + 校验
    // ============================================================

    /// <inheritdoc />
    public async Task<string> DownloadAndVerifyAsync(
        ReleaseInfo release, IProgress<UpdateProgress> progress, CancellationToken cancellationToken)
    {
        if (!release.HasSetupAsset)
        {
            throw new UpdateFailedException("发布信息里没有可用的安装包", "Release 未匹配到 Setup 资产");
        }

        var targetDirectory = ResolveTargetDirectory(release);
        Directory.CreateDirectory(targetDirectory);

        var fileName = SanitizeFileName(release.SetupAsset.Name);
        var finalPath = Path.Combine(targetDirectory, fileName);

        // 1) 已下载且校验通过的包直接复用，避免重复下载 143 MB。
        if (File.Exists(finalPath) && new FileInfo(finalPath).Length == release.SetupAsset.Size)
        {
            var existingHash = await GitPortPaths.ComputeSha256Async(finalPath, cancellationToken)
                .ConfigureAwait(false);

            if (existingHash is not null
                && string.Equals(existingHash, await TryReadExpectedHashAsync(release, cancellationToken)
                        .ConfigureAwait(false),
                    StringComparison.OrdinalIgnoreCase))
            {
                AppServices.Log?.Info("更新", "复用上次已校验的安装包");
                WritePending(release, finalPath, existingHash);
                return finalPath;
            }
        }

        // 2) 三源依次尝试
        var expectedHash = await TryReadExpectedHashAsync(release, cancellationToken).ConfigureAwait(false);

        // 校验清单是「执行安装程序」的前提：拿不到它就无法证明这个 exe 是我们发布的那一份。
        // 宁可让用户手动下载，也不运行一个来路不明的安装包。
        if (string.IsNullOrEmpty(expectedHash))
        {
            throw new UpdateFailedException(
                "缺少校验文件，已取消更新",
                "本次发布未提供 checksums.txt，或清单拉取/解析失败；详情见上一行日志");
        }

        var sources = BuildSourceOrder();
        Exception? lastError = null;

        foreach (var source in sources)
        {
            var maxAttempts = 1 + UpdateDefaults.RetriesPerSource;

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    progress?.Report(new UpdateProgress
                    {
                        ReceivedBytes = 0,
                        TotalBytes = release.SetupAsset.Size,
                        Source = source,
                        Percent = 0
                    });

                    await DownloadOnceAsync(source, release, finalPath, progress, cancellationToken)
                        .ConfigureAwait(false);

                    var actualHash = await GitPortPaths.ComputeSha256Async(finalPath, cancellationToken)
                        .ConfigureAwait(false);

                    if (!VerifyHash(actualHash, expectedHash, out var verifyError))
                    {
                        // 校验不过必须删包，绝不执行一个可能被换掉的安装程序。
                        TryDelete(finalPath);
                        throw new UpdateFailedException(
                            "安装包校验失败，已删除", verifyError ?? "SHA256 不一致");
                    }

                    CleanupStalePackages(targetDirectory, fileName);
                    WritePending(release, finalPath, actualHash!);
                    AppServices.Log?.Info("更新", $"安装包已下载并校验通过（{source}）");
                    return finalPath;
                }
                catch (OperationCanceledException)
                {
                    // 用户取消：把这次下载产生的半截文件都删掉（文件名带来源，可能有多个），不留垃圾。
                    CleanupPartFiles(finalPath);
                    throw;
                }
                catch (UpdateFailedException ex)
                {
                    lastError = ex;
                    AppServices.Log?.Warn("更新", $"下载失败（{source} 第 {attempt} 次）：{ex.Message}");

                    // 校验失败换源也没用，但换源成本很低，仍继续尝试下一源。
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    AppServices.Log?.Warn("更新", $"下载异常（{source} 第 {attempt} 次）：{ex.Message}");
                }

                if (attempt < maxAttempts)
                {
                    await DelayBeforeRetryAsync(attempt, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        var detail = lastError?.Message ?? "所有下载源均不可用";
        throw new UpdateFailedException("下载失败，请稍后重试", detail, lastError);
    }

    /// <summary>
    /// 按可用性排出下载源顺序：内置加速 → 第三方镜像 → 直连。
    /// 加速没起来时直接跳过第一档，不做无意义的等待。
    /// </summary>
    private static List<UpdateDownloadSource> BuildSourceOrder()
    {
        var order = new List<UpdateDownloadSource>(3);

        if (AppServices.Proxy?.Current.IsRunning == true)
        {
            order.Add(UpdateDownloadSource.BuiltInProxy);
        }

        order.Add(UpdateDownloadSource.GhProxy);
        order.Add(UpdateDownloadSource.Direct);
        return order;
    }

    /// <summary>按来源拼出真实下载地址。</summary>
    private static string BuildDownloadUrl(UpdateDownloadSource source, ReleaseInfo release)
    {
        var origin = release.SetupAsset.BrowserDownloadUrl;

        return source switch
        {
            UpdateDownloadSource.GhProxy => UpdateDefaults.GhProxyPrefix + origin,
            _ => origin
        };
    }

    /// <summary>
    /// 下载一次。支持 <c>.part</c> 续传：断网重试只补剩下的字节，不用从头再来。
    /// <para>
    /// <c>.part</c> 文件名里带来源名：不同来源拿到的是不同字节流（镜像会改写/分块），
    /// 跨源续传会把两份内容拼在一起，校验必然失败又说不清原因。换源就从零开始。
    /// </para>
    /// </summary>
    private async Task DownloadOnceAsync(
        UpdateDownloadSource source,
        ReleaseInfo release,
        string finalPath,
        IProgress<UpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        var partPath = $"{finalPath}.{source}.part";
        long resumeFrom = 0;

        if (File.Exists(partPath))
        {
            resumeFrom = new FileInfo(partPath).Length;

            // ≥ 而不是 >：等于时再发 Range: bytes=size- 会被回 416，且永不恢复。
            if (release.SetupAsset.Size > 0 && resumeFrom >= release.SetupAsset.Size)
            {
                TryDelete(partPath);
                resumeFrom = 0;
            }
        }

        var url = BuildDownloadUrl(source, release);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(DownloadTimeoutMinutes));

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Accept", "application/octet-stream");

        if (resumeFrom > 0)
        {
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(resumeFrom, null);
        }

        // 按来源选客户端：只有「内置加速」走本机代理，第三方镜像与直连都必须真直连。
        // 否则加速挂了三个源会一起废，而且弹窗显示的「直连」是假信息。
        var client = source == UpdateDownloadSource.BuiltInProxy
            ? HttpProvider.CreateClient()
            : HttpProvider.CreateDirectClient();

        using var response = await client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);

        // 服务器不支持 Range 时会回 200，此时必须从 0 开始写。
        var append = resumeFrom > 0 && response.StatusCode == HttpStatusCode.PartialContent;

        if (!response.IsSuccessStatusCode)
        {
            throw new UpdateFailedException(
                "下载失败，请稍后重试",
                $"{source} 返回 {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        var totalBytes = release.SetupAsset.Size;
        var contentLength = response.Content.Headers.ContentLength ?? 0;
        if (totalBytes <= 0 && contentLength > 0)
        {
            totalBytes = contentLength + (append ? resumeFrom : 0);
        }

        var fileMode = append ? FileMode.Append : FileMode.Create;
        var startBytes = append ? resumeFrom : 0;
        long received = startBytes;

        var stopwatch = Stopwatch.StartNew();
        var lastReport = 0L;
        var lastSampleTicks = 0L;
        var lastSampleBytes = startBytes;

        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        await using var file = new FileStream(
            partPath, fileMode, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);

        var buffer = new byte[64 * 1024];

        while (true)
        {
            var read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            await file.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
            received += read;

            var now = stopwatch.ElapsedMilliseconds;
            if (now - lastReport >= UpdateDefaults.ProgressReportIntervalMs)
            {
                lastReport = now;

                var speed = 0d;
                var deltaMs = now - lastSampleTicks;
                if (deltaMs > 0 && lastSampleTicks > 0)
                {
                    speed = (received - lastSampleBytes) * 1000d / deltaMs;
                }

                lastSampleTicks = now;
                lastSampleBytes = received;

                progress?.Report(new UpdateProgress
                {
                    ReceivedBytes = received,
                    TotalBytes = totalBytes,
                    BytesPerSecond = speed,
                    Source = source,
                    Percent = totalBytes > 0 ? Math.Clamp((double)received / totalBytes, 0, 1) : 0
                });
            }
        }

        await file.FlushAsync(timeout.Token).ConfigureAwait(false);
        file.Close();

        progress?.Report(new UpdateProgress
        {
            ReceivedBytes = received,
            TotalBytes = totalBytes > 0 ? totalBytes : received,
            BytesPerSecond = stopwatch.ElapsedMilliseconds > 0
                ? received * 1000d / Math.Max(1, stopwatch.ElapsedMilliseconds)
                : 0,
            Source = source,
            Percent = 1
        });

        if (totalBytes > 0 && received != totalBytes)
        {
            throw new UpdateFailedException(
                "下载不完整，正在重试",
                $"{source} 收到 {received} 字节，期望 {totalBytes} 字节");
        }

        // 下载完整后才改名成正式文件：中途失败不会留下一个「看起来能用」的半个 exe。
        File.Move(partPath, finalPath, overwrite: true);
    }

    // ============================================================
    // 目标目录与空间预检
    // ============================================================

    /// <summary>
    /// 选下载目录：默认数据根的 <c>update</c> 目录。
    /// <para>
    /// 空间预检要求「包大小 × 2」（下载完还要留给安装程序解压），
    /// 同一块盘上确实放不下时回退到 <c>%LOCALAPPDATA%\PivotPic\update</c>。
    /// </para>
    /// </summary>
    private static string ResolveTargetDirectory(ReleaseInfo release)
    {
        var needed = Math.Max(release.SetupAsset.Size, 0) * UpdateDefaults.DiskHeadroomFactor;
        var primary = GitPortPaths.UpdateRoot;

        try
        {
            if (needed <= 0 || HasFreeSpace(primary, needed))
            {
                return primary;
            }
        }
        catch (Exception ex)
        {
            AppServices.Log?.Warn("更新", $"检查磁盘空间失败：{ex.Message}");
        }

        var fallback = GitPortPaths.UpdateFallbackRoot;

        try
        {
            // 不论是否同盘都要查：兜底目录可能落在另一块更小的盘上。
            if (!HasFreeSpace(fallback, needed))
            {
                throw new UpdateFailedException(
                    "磁盘空间不足，至少需要 " + UpdateProgress.Format(needed) + " 可用空间",
                    $"主目录与兜底目录（{fallback}）均不足：需要 {needed} 字节");
            }
        }
        catch (UpdateFailedException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppServices.Log?.Warn("更新", $"检查兜底目录空间失败：{ex.Message}");
        }

        AppServices.Log?.Info("更新", $"安装目录所在盘空间不足，改用 {fallback}");
        return fallback;
    }

    /// <summary>判断目录所在卷是否有足够可用空间。目录还不存在时顺着父目录找。</summary>
    private static bool HasFreeSpace(string directory, long neededBytes)
    {
        if (neededBytes <= 0)
        {
            return true;
        }

        var root = Path.GetPathRoot(Path.GetFullPath(directory));
        if (string.IsNullOrEmpty(root))
        {
            // 拿不到卷信息时不做拦截，交给写入失败去报错。
            return true;
        }

        var drive = new DriveInfo(root);
        return drive.AvailableFreeSpace >= neededBytes;
    }

    // ============================================================
    // 校验
    // ============================================================

    /// <summary>
    /// 读取校验清单里本安装包的期望 SHA256。
    /// 清单缺失或下载失败时返回 null——此时退化为「只比体积」，并在日志里留痕。
    /// </summary>
    private async Task<string?> TryReadExpectedHashAsync(
        ReleaseInfo release, CancellationToken cancellationToken)
    {
        if (release.ChecksumsAsset is null || string.IsNullOrWhiteSpace(release.ChecksumsAsset.BrowserDownloadUrl))
        {
            return null;
        }

        var cachePath = Path.Combine(GitPortPaths.UpdateRoot, UpdateDefaults.ChecksumsAssetName);

        try
        {
            Directory.CreateDirectory(GitPortPaths.UpdateRoot);

            // 清单只有几百字节，直接异步取回；不要在这里同步阻塞——调用点可能在 UI 线程上。
            var content = await DownloadTextAsync(release.ChecksumsAsset.BrowserDownloadUrl, cancellationToken)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(content))
            {
                return null;
            }

            File.WriteAllText(cachePath, content);
            return ParseChecksum(content, release.SetupAsset.Name);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppServices.Log?.Warn("更新", $"下载校验清单失败：{ex.Message}");

            // 网络失败时用上次缓存的清单兜底（同一版本的清单不会变）。
            try
            {
                if (File.Exists(cachePath))
                {
                    return ParseChecksum(File.ReadAllText(cachePath), release.SetupAsset.Name);
                }
            }
            catch (Exception)
            {
                // 忽略。
            }

            return null;
        }
    }

    /// <summary>下载一份小文本（校验清单）。与 Release 查询同一套「直连优先、回落加速」策略。</summary>
    private async Task<string?> DownloadTextAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await SendWithFallbackAsync(
                () => new HttpRequestMessage(HttpMethod.Get, url), ApiTimeoutSeconds, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 从 <c>checksums.txt</c> 里找指定文件的哈希。
    /// 兼容 <c>sha256sum</c> 的两种写法：<c>hash  name</c> 与 <c>hash *name</c>。
    /// </summary>
    private static string? ParseChecksum(string content, string assetName)
    {
        var expectedName = Path.GetFileName(assetName);

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim().TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                continue;
            }

            var hash = parts[0].Trim();
            var name = string.Join(' ', parts.Skip(1)).Trim().TrimStart('*');

            if (!string.Equals(Path.GetFileName(name), expectedName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return hash.Length == 64 ? hash.ToLowerInvariant() : null;
        }

        return null;
    }

    /// <summary>
    /// 严格比对 SHA256。没有「只核体积」的退路——体积一致不代表内容一致，
    /// 而我们要执行的是一个会以管理员权限运行的安装程序。
    /// </summary>
    private static bool VerifyHash(string? actualHash, string expectedHash, out string? error)
    {
        error = null;

        if (actualHash is null)
        {
            error = "无法读取已下载的文件";
            return false;
        }

        if (string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var preview = expectedHash[..Math.Min(12, expectedHash.Length)];
        error = $"SHA256 不一致（期望 {preview}…，实际 {actualHash[..Math.Min(12, actualHash.Length)]}…）";
        return false;
    }

    // ============================================================
    // 缓存与状态
    // ============================================================

    /// <inheritdoc />
    public ReleaseInfo? GetCachedRelease() => ReadCachedRelease();

    /// <summary>读本地缓存的 Release JSON（304 时用它判断版本）。</summary>
    private ReleaseInfo? ReadCachedRelease()
    {
        try
        {
            if (!File.Exists(GitPortPaths.LatestManifestPath))
            {
                return null;
            }

            return ParseRelease(File.ReadAllText(GitPortPaths.LatestManifestPath));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? ReadEtag()
    {
        try
        {
            if (!File.Exists(GitPortPaths.LatestEtagPath))
            {
                return null;
            }

            var etag = File.ReadAllText(GitPortPaths.LatestEtagPath).Trim();
            return etag.Length == 0 ? null : etag;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>写 Release 缓存与 ETag。缓存只是省流量，写失败不影响本次结果。</summary>
    private static void WriteCache(string json, string? etag)
    {
        try
        {
            Directory.CreateDirectory(GitPortPaths.UpdateRoot);
            File.WriteAllText(GitPortPaths.LatestManifestPath, json);

            if (!string.IsNullOrWhiteSpace(etag))
            {
                File.WriteAllText(GitPortPaths.LatestEtagPath, etag);
            }
        }
        catch (Exception ex)
        {
            AppServices.Log?.Warn("更新", $"写 Release 缓存失败：{ex.Message}");
        }
    }

    /// <summary>记录「已下载待安装」的包，下次启动可直接复用（断点场景）。</summary>
    private static void WritePending(ReleaseInfo release, string path, string? sha256)
    {
        try
        {
            Directory.CreateDirectory(GitPortPaths.UpdateRoot);

            var node = new JsonObject
            {
                ["version"] = UpdateDefaults.FormatVersion(release.Version),
                ["path"] = path,
                ["sha256"] = sha256,
                ["size"] = release.SetupAsset.Size,
                ["downloadedUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            };

            File.WriteAllText(GitPortPaths.PendingPackagePath, node.ToJsonString(AppServices.JsonOptions));
        }
        catch (Exception ex)
        {
            AppServices.Log?.Warn("更新", $"写待安装记录失败：{ex.Message}");
        }
    }

    /// <summary>把本次检查结果落到配置，供设置页「上次检查」显示。</summary>
    private static void PersistCheckResult(UpdateCheckOutcome outcome)
    {
        var config = AppServices.Config;
        if (config is null)
        {
            return;
        }

        try
        {
            config.Current.LastCheckUtc = DateTime.UtcNow;
            config.Current.LastCheckOutcome = outcome;
            config.SaveDebounced();
        }
        catch (Exception)
        {
            // 配置落盘失败不影响检查结果。
        }
    }

    /// <summary>统一处理失败：静默检查只落状态，手动检查抛人话异常。</summary>
    private ReleaseInfo? Fail(
        bool manual,
        UpdateCheckOutcome outcome,
        string userMessage,
        string technicalMessage,
        Exception? inner = null)
    {
        LastOutcome = outcome;
        Available = null;
        PersistCheckResult(outcome);
        AppServices.Log?.Warn("更新", $"{userMessage}：{technicalMessage}");

        if (manual)
        {
            throw new UpdateFailedException(userMessage, technicalMessage, inner);
        }

        return null;
    }

    /// <summary>安全地抛事件：订阅方异常不得打断检查流程。</summary>
    private void RaiseUpdateAvailable(ReleaseInfo release)
    {
        try
        {
            UpdateAvailable?.Invoke(this, release);
        }
        catch (Exception ex)
        {
            AppServices.Log?.Warn("更新", $"更新事件处理异常：{ex.Message}");
        }
    }

    /// <summary>读当前程序版本。读不到时当作 0.0.0，宁可多提示一次也不漏更新。</summary>
    private static Version ReadCurrentVersion()
    {
        return Version.TryParse(AppInfo.Version, out var version) ? version : new Version(0, 0, 0);
    }

    // ============================================================
    // 杂项
    // ============================================================

    /// <summary>清掉文件名里的非法字符，避免资产名带路径分隔符时把文件写到别处。</summary>
    private static string SanitizeFileName(string name)
    {
        var cleaned = new string(name.Where(ch => !InvalidNameChars.Contains(ch)).ToArray()).Trim();
        return cleaned.Length == 0 ? "PivotPic-Setup.exe" : cleaned;
    }

    /// <summary>删掉旧版本的安装包，避免 update 目录越积越大（每个包 100 MB 级）。</summary>
    private static void CleanupStalePackages(string directory, string keepFileName)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                var name = Path.GetFileName(file);

                if (string.Equals(name, keepFileName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, UpdateDefaults.ChecksumsAssetName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // 只清理更新自己产生的文件，不碰用户可能放进来的东西。
                // 半截文件形如 xxx.exe.BuiltInProxy.part，因此按「含 .part」判断。
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    || name.Contains(".part", StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(file);
                }
            }
        }
        catch (Exception)
        {
            // 清理失败不影响本次更新。
        }
    }

    /// <summary>
    /// 删除某个目标包派生出来的所有半截文件（每个来源一个后缀）。
    /// 取消下载时用，避免 100 MB 级的残片长期占地方。
    /// </summary>
    private static void CleanupPartFiles(string finalPath)
    {
        try
        {
            var directory = Path.GetDirectoryName(finalPath);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                return;
            }

            var prefix = Path.GetFileName(finalPath);

            foreach (var file in Directory.EnumerateFiles(directory, "*.part"))
            {
                if (Path.GetFileName(file).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(file);
                }
            }
        }
        catch (Exception)
        {
            // 清不掉也只是一点垃圾，不影响主流程。
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // 被占用时忽略。
        }
    }

    /// <summary>重试前退避。最后一段之后不再等，避免无意义的 5 秒卡顿。</summary>
    private static Task DelayBeforeRetryAsync(int attempt, CancellationToken cancellationToken)
    {
        var index = Math.Clamp(attempt - 1, 0, UpdateDefaults.RetryBackoffMilliseconds.Length - 1);
        var delay = UpdateDefaults.RetryBackoffMilliseconds[index];

        return Task.Delay(delay, cancellationToken);
    }
}
