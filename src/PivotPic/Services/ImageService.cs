using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PivotPic.Models;

namespace PivotPic.Services;

/// <summary>图片探测与方向归一化：WIC 读头探测尺寸/EXIF，物理旋转或改写 EXIF Orientation 两种模式。</summary>
public sealed class ImageService : IImageService
{
    /// <summary>EXIF Orientation 的标准查询路径（app1/ifd 下的 274 号 tag）。</summary>
    private const string OrientationQuery = "/app1/ifd/{ushort=274}";

    /// <summary>JPEG 重编码质量，95 在体积与画质间比较平衡。</summary>
    private const int JpegQuality = 95;

    /// <summary>探测图片头部信息（不解码全图）；任何异常返回 null，绝不上抛。</summary>
    public ImageProbe? Probe(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
            {
                return null;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            // DelayCreation + None：只为读头部，不把整幅像素读进内存。
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.None);

            if (decoder.Frames.Count == 0)
            {
                return null;
            }

            var frame = decoder.Frames[0];

            // 必须在 stream 释放之前把需要的值全部取完（DelayCreation 帧仍持有流）。
            var width = frame.PixelWidth;
            var height = frame.PixelHeight;
            var orientation = ReadOrientation(frame);
            var decoderName = ReadDecoderName(decoder);

            return new ImageProbe
            {
                PixelWidth = width,
                PixelHeight = height,
                Orientation = orientation,
                FileSize = file.Length,
                DpiX = SafeDpi(frame.DpiX),
                DpiY = SafeDpi(frame.DpiY),
                DecoderName = decoderName
            };
        }
        catch (Exception)
        {
            // 损坏文件、无解码器、权限不足——统一按「无法探测」处理。
            return null;
        }
    }

    /// <summary>内置（WPF）引擎能安全处理的最大文件体积；超过就不试了，直接失败返回。</summary>
    private const long BuiltinEngineMaxFileBytes = 50L * 1024 * 1024;

    /// <summary>内置（WPF）引擎能安全处理的最大像素数；超过就不试了，直接失败返回。</summary>
    private const long BuiltinEngineMaxPixels = 100_000_000;

    /// <summary>处理单个文件：横版则旋转为竖版，失败只返回 Fail 结果而不抛异常。</summary>
    public RotationResult Process(string path, RotationMode mode, RotateDirection direction, int maxPixels,
        string? renameSuffix = null, double maxSideCm = 0, bool convertProgressiveJpeg = false)
    {
        var stopwatch = Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return Fail("文件不存在或路径为空", stopwatch, null);
        }

        // 零字节文件解码必然失败，先判长度，让它落到「跳过」而不是「失败」——空文件不是错误。
        if (ReadFileLength(path) == 0)
        {
            return Skip("SkippedEmpty", "文件长度为 0", null, stopwatch);
        }

        var probe = Probe(path);
        if (probe is null)
        {
            return Fail("无法读取图片信息（格式不受支持或文件已损坏）", stopwatch, null);
        }

        if (probe.FileSize <= 0)
        {
            return Skip("SkippedEmpty", "文件长度为 0", probe, stopwatch);
        }

        // 源图是不是「连续 JPEG」，必须在**任何重编码之前**判定——旋转/写回都会改变文件编码。
        var sourceJpegKind = JpegKind.NotJpeg;
        if (convertProgressiveJpeg && IsJpegPath(path))
        {
            sourceJpegKind = JpegProbe.Probe(path).Kind;
        }

        // 「不旋转」的各类跳过，但「连续 JPEG 转基线」照样要做：
        // 用户要的是「这张图能打印」，跟跳过原因（竖版/正方形/超幅面/超像素）无关。
        // 这些路径只是「不旋转」，不等于「不处理」。
        RotationResult FinishWithoutRotation(string action, string? message)
        {
            var skipped = Skip(action, message, probe, stopwatch);

            if (convertProgressiveJpeg && sourceJpegKind is JpegKind.Progressive or JpegKind.Arithmetic)
            {
                // 接下来要写回文件，必须先登记，否则监控会把自己写回的文件当成新文件。
                AppServices.IgnoreSet?.Remember(path);
                return EnsureBaselineJpeg(path, skipped, sourceWasProgressive: true, stopwatch);
            }

            return skipped;
        }

        // 高性能引擎按用户要求**不设像素上限**（它逐块处理，内存可控；
        // 以前的 1.5 Gpx「绝对天花板」也随之取消，用户怎么设都不影响它）。
        // 内置引擎沿用用户配置的 MaxPixels。
        var usingVips = mode == RotationMode.LibVips && VipsNative.IsAvailable;
        var effectiveMaxPixels = usingVips ? 0 : maxPixels;

        if (effectiveMaxPixels > 0 && probe.PixelCount > effectiveMaxPixels)
        {
            return FinishWithoutRotation("SkippedTooLarge",
                $"像素数 {probe.PixelCount:N0} 超过上限 {effectiveMaxPixels:N0}（可在设置里调大）");
        }

        // 成品幅面检查：按像素与 DPI 换算成厘米，宽高“都”超过上限才跳过（0 表示未启用）。
        // 边界容差：界面按 1 位小数显示（150.0），判定也用同一精度。
        // 否则 150.02 会显示成「150」却按「超过上限」跳过，用户看到的就是「等于 150 也不旋转」。
        const double cmTolerance = 0.05;
        if (maxSideCm > 0
            && probe.WidthCm > maxSideCm + cmTolerance
            && probe.HeightCm > maxSideCm + cmTolerance)
        {
            return FinishWithoutRotation("SkippedTooBigCm",
                $"成品 {probe.WidthCm:0.#}×{probe.HeightCm:0.#} cm 两边均超过上限 {maxSideCm:0.#} cm");
        }

        // 横竖判定用「按 EXIF 校正后」的逻辑尺寸：Orientation 5~8 时宽高互换。
        if (probe.OrientedWidth < probe.OrientedHeight)
        {
            return FinishWithoutRotation("SkippedVertical", null);
        }

        if (probe.OrientedWidth == probe.OrientedHeight)
        {
            return FinishWithoutRotation("SkippedSquare", "正方形不旋转");
        }

        if (IsReadOnly(path))
        {
            return Skip("SkippedReadOnly", "文件为只读，跳过以避免破坏原文件", probe, stopwatch);
        }

        // 关键：登记必须发生在任何写流打开之前，否则监控会拾到自己写回的文件。
        AppServices.IgnoreSet?.Remember(path);

        try
        {
            var result = mode == RotationMode.ExifOnly
                ? RewriteExif(path, probe, direction, stopwatch)
                : RotatePhysical(path, probe, direction, stopwatch, mode);

            // JPEG 兼容模式：**旋转优先，转基线在后**。
            // 旋转失败就不做格式转换（保留失败结果，避免把「没转成功」包装成「转成功了」）。
            // 竖版/正方形这类「跳过旋转」的 JPEG 同样要转——用户的目的只是能打印，与横竖无关。
            //
            // 注意：源图的类型必须在**旋转之前**读。旋转会重编码文件，
            // 转完之后再去判断，看到的已经是新文件了。
            if (convertProgressiveJpeg && result.Success && AllowsBaselineConversion(result)
                && sourceJpegKind is JpegKind.Progressive or JpegKind.Arithmetic)
            {
                result = EnsureBaselineJpeg(path, result, sourceWasProgressive: true, stopwatch);
            }

            // 改名必须放在最后：转基线也写同一个文件，先改名会让路径对不上。
            return result.Rotated ? ApplySuffix(path, renameSuffix, result) : result;
        }
        catch (Exception ex)
        {
            return Fail(ex.Message, stopwatch, probe);
        }
    }

    /// <summary>
    /// 把 JPEG 的分量 ID 归一成标准的 1..N。
    /// <para>
    /// libvips 的 jpegsave 会把图像通道名当成 SOF/SOS 里的分量 ID：CMYK 写成 67,77,89,75
    /// （ASCII 的 C/M/Y/K）。标准 JPEG 分量 ID 应为 1..4，很多打印 RIP 的解析器遇到非标准 ID
    /// 会直接把文件判为非法（用户实测：PS 存的认、我们转的不认，差别就在这）。
    /// 只改头里的 ID 字节，不动熵编码数据，风险极低。
    /// </para>
    /// </summary>
    private static void NormalizeJpegComponentIds(string path)
    {
        try
        {
            var data = File.ReadAllBytes(path);
            var changed = false;
            var map = new Dictionary<int, int>();
            var next = 1;

            var i = 2;
            while (i < data.Length - 9)
            {
                if (data[i] != 0xFF)
                {
                    i++;
                    continue;
                }

                var marker = data[i + 1];
                if (marker == 0xD8 || marker == 0xD9 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
                {
                    i += 2;
                    continue;
                }

                if (marker == 0xDA)
                {
                    // SOS：分量表里同样存了 ID，必须一起改。
                    var sosCount = data[i + 4];
                    for (var c = 0; c < sosCount; c++)
                    {
                        var pos = i + 5 + c * 2;
                        if (map.TryGetValue(data[pos], out var normalized))
                        {
                            data[pos] = (byte)normalized;
                            changed = true;
                        }
                    }

                    break;
                }

                var seglen = (data[i + 2] << 8) | data[i + 3];

                // SOF 段：C0–CF，排除 C4(DHT)/C8(JPG)/CC(DAC)
                if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
                {
                    var comp = data[i + 9];
                    for (var c = 0; c < comp; c++)
                    {
                        var pos = i + 10 + c * 3;
                        var old = (int)data[pos];
                        if (!map.TryGetValue(old, out var normalized))
                        {
                            normalized = next++;
                            map[old] = normalized;
                        }

                        if (old != normalized)
                        {
                            data[pos] = (byte)normalized;
                            changed = true;
                        }
                    }
                }

                i += 2 + seglen;
            }

            if (changed)
            {
                // 必须走 Win32 写回：File.WriteAllBytes 在文件被内存映射时会抛
                // 「请求的操作无法在使用用户映射区域打开的文件上执行」。
                var temp = GetWorkTempPath(path, ".idfix", ".tmp");
                File.WriteAllBytes(temp, data);

                if (TryWriteBackNative(temp, path, out var writeError))
                {
                    AppServices.Log?.Info("处理", "已把分量 ID 归一为标准 1..N（打印软件兼容）",
                        Path.GetDirectoryName(path) ?? string.Empty, Path.GetFileName(path));
                }
                else
                {
                    AppServices.Log?.Warn("处理", $"分量 ID 归一写回失败：{writeError}",
                        Path.GetDirectoryName(path) ?? string.Empty, Path.GetFileName(path));
                }

                TryDelete(temp);
            }
        }
        catch (Exception ex)
        {
            // 归一失败不影响主流程：文件仍是合法 JPEG，只是部分 RIP 可能不认。
            AppServices.Log?.Warn("处理", $"分量 ID 归一失败：{ex.Message}",
                Path.GetDirectoryName(path) ?? string.Empty, Path.GetFileName(path));
        }
    }

    /// <summary>
    /// 写回文件，遇到占用/共享冲突时退避重试。
    /// <para>
    /// 为什么不用 File.Replace / File.Move / FileStream：**网络盘（SMB）上它们的共享语义与 Win32 不一致**。
    /// 实测（A: 共享）：libvips 读过该文件后 File.Delete/File.Replace 必失败，托管 FileStream 打开也会被拒；
    /// 但用 CreateFile(GENERIC_READ|GENERIC_WRITE, FILE_SHARE_ALL) 就能写进去。
    /// 所以这里直接走 Win32，参数就是被验证可用的那一组。
    /// </para>
    /// </summary>
    private static bool TryWriteBackWithRetry(string tempPath, string targetPath, out string error)
    {
        error = string.Empty;
        Exception? last = null;

        // 先让托管层残留的句柄（WPF 解码器、libvips 缓存）释放掉。
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (TryWriteBackNative(tempPath, targetPath, out error))
            {
                return true;
            }

            try
            {
                File.Replace(tempPath, targetPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
                return true;
            }
            catch (Exception ex)
            {
                last = ex;
            }

            Thread.Sleep(600 * (1 << attempt));
        }

        if (string.IsNullOrEmpty(error))
        {
            error = last?.Message ?? "未知原因";
        }

        return false;
    }

    // ---------------- Win32 写回 ----------------

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareAll = 0x00000007;
    private const uint OpenExisting = 3;

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr CreateFileNative(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "WriteFile", SetLastError = true)]
    private static extern bool WriteFileNative(IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite,
        out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "SetEndOfFile", SetLastError = true)]
    private static extern bool SetEndOfFileNative(IntPtr hFile);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "FlushFileBuffers", SetLastError = true)]
    private static extern bool FlushFileBuffersNative(IntPtr hFile);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
    private static extern bool CloseHandleNative(IntPtr hObject);

    private static readonly IntPtr InvalidHandleValue = new(-1);

    /// <summary>
    /// 用 CreateFile(GENERIC_READ|GENERIC_WRITE, FILE_SHARE_ALL) 就地覆写目标文件。
    /// 这组参数在网络盘上被实测验证可用（见方法注释）。
    /// </summary>
    private static bool TryWriteBackNative(string tempPath, string targetPath, out string error)
    {
        error = string.Empty;
        var data = File.ReadAllBytes(tempPath);
        var handle = CreateFileNative(targetPath, GenericRead | GenericWrite, FileShareAll,
            IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);

        if (handle == IntPtr.Zero || handle == InvalidHandleValue)
        {
            error = "CreateFile 失败（错误码 " + System.Runtime.InteropServices.Marshal.GetLastWin32Error() + "）";
            return false;
        }

        try
        {
            if (!WriteFileNative(handle, data, (uint)data.Length, out var written, IntPtr.Zero) || written != data.Length)
            {
                error = "WriteFile 失败（错误码 " + System.Runtime.InteropServices.Marshal.GetLastWin32Error() + "）";
                return false;
            }

            SetEndOfFileNative(handle);
            FlushFileBuffersNative(handle);
            return true;
        }
        finally
        {
            CloseHandleNative(handle);
        }
    }

    /// <summary>哪些结果之后还要做「转基线」：旋转成功，或本来就无需旋转（竖版/正方形）。</summary>
    private static bool AllowsBaselineConversion(RotationResult result)
    {
        return result.Action is "Rotated" or "SkippedVertical" or "SkippedSquare";
    }

    /// <summary>是否为 JPEG（.jpg/.jpeg）。</summary>
    private static bool IsJpegPath(string path)
    {
        var extension = Path.GetExtension(path);
        return string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase)
               || string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// JPEG 兼容模式：确保文件最终是基线。
    /// <para>
    /// 先看旋转后的实际状态：libvips 的 jpegsave 默认就是非连续，所以「旋转」这一步往往
    /// 已经顺带把连续 JPEG 变成了基线——这种情况只记日志、不再重编码一次（避免白掉一次画质）。
    /// 只有旋转没改变编码（竖版/正方形，或走了 EXIF 模式）时才真正执行转换。
    /// </para>
    /// </summary>
    private static RotationResult EnsureBaselineJpeg(
        string path, RotationResult result, bool sourceWasProgressive, Stopwatch stopwatch)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty;
        var fileName = Path.GetFileName(path);

        var current = JpegProbe.Probe(path);

        if (!current.NeedsConversion)
        {
            if (current.Kind != JpegKind.Baseline)
            {
                return result;
            }

            // 文件现在是基线：若源本来就是基线，说明没什么可做（幂等，字节不动）。
            // 但**源是连续**时不能就这么收工——旋转那一步是 libvips 写的，
            // 它的结构（1 张量化表 / 标准霍夫曼 / CMYK transform=0）正是老 RIP 不认的那种。
            // 所以「源连续 + 刚旋转过」必须再走一遍我们的编码器。
            if (!sourceWasProgressive || !result.Rotated)
            {
                return result;
            }

            AppServices.Log?.Info("处理", "旋转输出需要按打印结构重编码（2 张量化表 / optimize / YCCK）",
                directory, fileName);
        }

        if (!VipsNative.IsAvailable)
        {
            AppServices.Log?.Warn("处理",
                "连续 JPEG 未转换（高性能引擎不可用，内置引擎处理 CMYK 会产生色彩错误）", directory, fileName);

            return result.WithExtraMessage("连续 JPEG 未转换：高性能引擎不可用");
        }

        if (string.IsNullOrEmpty(directory))
        {
            return result.WithExtraMessage("连续 JPEG 未转换：目录无效");
        }

        var extension = Path.GetExtension(path);
        var tempPath = GetWorkTempPath(path, ".btmp", extension);

        try
        {
            // 先从「刚被改写过的文件」复制一份快照再解码。
            // 原因：libvips 有操作缓存，同一个路径在同一进程里二次加载可能拿到改动前的旧像素
            // （实测：旋转完再读，读回来还是旋转前的尺寸）；换个从没出现过的文件名就绝不会命中。
            // 快照用 .btmp 后缀，扫描与清理逻辑已经把它排除在外。
            var snapshotPath = GetWorkTempPath(path, ".btmp", extension);

            var encodeSource = path;
            try
            {
                File.Copy(path, snapshotPath, overwrite: true);
                encodeSource = snapshotPath;
            }
            catch (Exception)
            {
                encodeSource = path;   // 复制失败就直接读原文件，最多是命中旧缓存，不影响正确性以外的部分
            }

            // 首选 LibJpeg 编码器：结构与 Photoshop 一致（2 张量化表 / optimize DHT / APP14 transform=2 YCCK）。
            // libvips 的 jpegsave 只作兜底：它写的结构对不上老 RIP，但至少是个合法基线。
            var encoded = BaselineJpegEncoder.TryEncode(encodeSource, tempPath, out var error);
            TryDelete(snapshotPath);

            if (!encoded)
            {
                AppServices.Log?.Warn("处理", $"LibJpeg 编码失败，回退 libvips：{error}", directory, fileName);

                if (!VipsNative.TrySaveBaselineJpeg(path, tempPath, current, out var vipsError))
                {
                    TryDelete(tempPath);
                    AppServices.Log?.Warn("处理", $"连续 JPEG 转基线失败：{vipsError}", directory, fileName);
                    return result.WithExtraMessage("连续 JPEG 转基线失败");
                }
            }

            // 写回前文件很可能正被别的程序占用（Photoshop 之类会持锁，哪怕是"只打开看看"）。
            // 这类锁通常是暂时的，退避重试几次比一次失败就放弃更符合实际使用习惯。
            if (!TryWriteBackWithRetry(tempPath, path, out var writeError))
            {
                TryDelete(tempPath);
                AppServices.Log?.Warn("处理",
                    $"连续 JPEG 写回失败：{writeError}（若文件正在 Photoshop 等程序中打开，请先关闭再试）",
                    directory, fileName);
                return result.WithExtraMessage("连续 JPEG 转基线失败：文件被占用");
            }

            // libvips 会把通道名当分量 ID 写进 SOF/SOS（CMYK → 67,77,89,75 = 'C','M','Y','K'）。
            // 打印软件普遍只认标准的 1..4，看到字母编码会直接拒收——用户实测「PS 转的认、我们转的不认」就是这个原因。
            NormalizeJpegComponentIds(path);

            AppServices.Log?.Info("处理", "连续 JPEG 已转基线（Q95）", directory, fileName);
            TryDelete(tempPath);   // 就地覆写不会消耗临时文件，成功后必须自己清掉

            return result.WithConversion(true,
                $"连续 JPEG 已转基线（Q95，{JpegProbe.Describe(current.Kind)}，{current.Width}×{current.Height}）");
        }
        catch (Exception ex)
        {
            TryDelete(tempPath);
            AppServices.Log?.Warn("处理", $"连续 JPEG 转基线异常：{ex.Message}", directory, fileName);
            return result.WithExtraMessage("连续 JPEG 转基线异常");
        }
    }

    /// <summary>TIFF 的关键结构标签（只读第一个 IFD，不做解码）。</summary>
    private readonly record struct TiffStructure(int SamplesPerPixel, int Photometric, int[] ExtraSamples, int BitsPerSampleCount);

    /// <summary>
    /// 对比旋转前后的 TIFF 结构，确认透明通道没有被写丢。
    /// <para>
    /// 源图有 alpha（SamplesPerPixel 大于 Photometric 对应的基色数，或带 ExtraSamples），
    /// 输出就必须同样带 alpha：样本数、Photometric、ExtraSamples 三项都要对得上。
    /// </para>
    /// </summary>
    /// <param name="sourcePath">源图（尚未被覆盖）。</param>
    /// <param name="outputPath">旋转后的中转文件。</param>
    /// <param name="error">不一致的原因。</param>
    private static bool VerifyTiffAlphaPreserved(string sourcePath, string outputPath, out string error)
    {
        error = string.Empty;

        try
        {
            var source = ReadTiffStructure(sourcePath);
            if (source is null)
            {
                // 源图读不出结构就无从校验，交给后续流程。
                return true;
            }

            var output = ReadTiffStructure(outputPath);
            if (output is null)
            {
                error = "输出文件不是可解析的 TIFF";
                return false;
            }

            var sourceHasAlpha = source.Value.SamplesPerPixel > BaseChannels(source.Value.Photometric)
                                 || source.Value.ExtraSamples.Length > 0;

            if (!sourceHasAlpha)
            {
                return true;
            }

            var outputHasAlpha = output.Value.SamplesPerPixel > BaseChannels(output.Value.Photometric)
                                 || output.Value.ExtraSamples.Length > 0;

            if (!outputHasAlpha)
            {
                error = $"源图 {source.Value.SamplesPerPixel} 通道(Photometric={source.Value.Photometric})，"
                        + $"输出只有 {output.Value.SamplesPerPixel} 通道(Photometric={output.Value.Photometric})";
                return false;
            }

            // ExtraSamples 必须**逐值相等**：1=预乘 alpha、2=独立 alpha，语义不同，
            // RIP 按这个值决定怎么解释同一份像素数据，改了就等于换了一张图。
            if (!source.Value.ExtraSamples.SequenceEqual(output.Value.ExtraSamples))
            {
                error = $"ExtraSamples 被改写：源 [{string.Join(",", source.Value.ExtraSamples)}]"
                        + $" → 输出 [{string.Join(",", output.Value.ExtraSamples)}]";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Photometric 对应的基色通道数（2=RGB→3，5=CMYK→4，0/1=灰度→1）。</summary>
    private static int BaseChannels(int photometric) => photometric switch
    {
        2 => 3,
        5 => 4,
        0 or 1 => 1,
        _ => 3
    };

    /// <summary>
    /// 读 TIFF 关键结构（SamplesPerPixel / Photometric / ExtraSamples / BitsPerSample 个数）。
    /// 只解析第一个 IFD，不做像素解码，毫秒级。给测试与排障用。
    /// </summary>
    public static (int SamplesPerPixel, int Photometric, int[] ExtraSamples, int BitsPerSampleCount)? ProbeTiffStructure(string path)
    {
        var structure = ReadTiffStructure(path);
        return structure is null
            ? null
            : (structure.Value.SamplesPerPixel, structure.Value.Photometric,
               structure.Value.ExtraSamples, structure.Value.BitsPerSampleCount);
    }

    /// <summary>读第一个 IFD 的 277/262/338/258 标签；解析失败返回 null。</summary>
    private static TiffStructure? ReadTiffStructure(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096);
        using var reader = new BinaryReader(stream);

        if (stream.Length < 8)
        {
            return null;
        }

        var order = reader.ReadBytes(2);
        var little = order[0] == 'I' && order[1] == 'I';

        if (!little && !(order[0] == 'M' && order[1] == 'M'))
        {
            return null;
        }

        if (ReadUInt16(reader, little) != 42)
        {
            return null;
        }

        var ifdOffset = ReadUInt32(reader, little);
        if (ifdOffset <= 0 || ifdOffset >= stream.Length)
        {
            return null;
        }

        stream.Seek(ifdOffset, SeekOrigin.Begin);

        var count = ReadUInt16(reader, little);
        var spp = 0;
        var photometric = -1;
        var bitsCount = 0;
        var extras = Array.Empty<int>();

        for (var i = 0; i < count; i++)
        {
            var tag = ReadUInt16(reader, little);
            var type = ReadUInt16(reader, little);
            var valueCount = ReadUInt32(reader, little);
            var valueBytes = reader.ReadBytes(4);

            switch (tag)
            {
                case 277 when type == 3:
                    spp = ReadUInt16From(valueBytes, 0, little);
                    break;

                case 262 when type == 3:
                    photometric = ReadUInt16From(valueBytes, 0, little);
                    break;

                case 258:
                    bitsCount = (int)valueCount;
                    break;

                case 338 when type == 3:
                    extras = new int[valueCount];
                    if (valueCount <= 2)
                    {
                        for (var k = 0; k < valueCount; k++)
                        {
                            extras[k] = ReadUInt16From(valueBytes, k * 2, little);
                        }
                    }
                    else
                    {
                        var back = stream.Position;
                        var offset = BitConverter.ToUInt32(little ? valueBytes : valueBytes.Reverse().ToArray(), 0);
                        stream.Seek(offset, SeekOrigin.Begin);
                        for (var k = 0; k < valueCount; k++)
                        {
                            extras[k] = ReadUInt16(reader, little);
                        }

                        stream.Seek(back, SeekOrigin.Begin);
                    }

                    break;
            }
        }

        return new TiffStructure(spp, photometric, extras, bitsCount);
    }

    private static ushort ReadUInt16(BinaryReader reader, bool little)
    {
        var bytes = reader.ReadBytes(2);
        return ReadUInt16From(bytes, 0, little);
    }

    private static ushort ReadUInt16From(byte[] bytes, int offset, bool little)
    {
        return little
            ? (ushort)(bytes[offset] | (bytes[offset + 1] << 8))
            : (ushort)((bytes[offset] << 8) | bytes[offset + 1]);
    }

    private static uint ReadUInt32(BinaryReader reader, bool little)
    {
        var bytes = reader.ReadBytes(4);
        if (!little)
        {
            Array.Reverse(bytes);
        }

        return BitConverter.ToUInt32(bytes, 0);
    }

    /// <summary>
    /// 对未压缩 TIFF 的像素字节做预乘：color = color * alpha / 255，alpha 不动。
    /// 支持多 strip（StripOffsets count>1），逐 strip 处理。
    /// </summary>
    private static bool PremultiplyTiffPixels(string path, out string error)
    {
        error = string.Empty;
        try
        {
            var data = File.ReadAllBytes(path);
            var little = data[0] == 'I';
            int rd(int off) => little
                ? data[off] | (data[off + 1] << 8) | (data[off + 2] << 16) | (data[off + 3] << 24)
                : (data[off] << 24) | (data[off + 1] << 16) | (data[off + 2] << 8) | data[off + 3];
            int rd16(int off) => little ? data[off] | (data[off + 1] << 8) : (data[off] << 8) | data[off + 1];

            var ifdOff = rd(4);
            var entryCount = rd16(ifdOff);
            int spp = 0, width = 0, height = 0, bitsPerSample = 8;
            var stripOffsets = new List<int>();
            var stripByteCounts = new List<int>();

            for (var i = 0; i < entryCount; i++)
            {
                var o = ifdOff + 2 + i * 12;
                var tag = rd16(o);
                var cnt = rd(o + 4);

                if (tag == 273) // StripOffsets
                {
                    if (cnt == 1) stripOffsets.Add(rd(o + 8));
                    else { var ptr = rd(o + 8); for (var k = 0; k < cnt; k++) stripOffsets.Add(rd(ptr + k * 4)); }
                }
                else if (tag == 279) // StripByteCounts
                {
                    if (cnt == 1) stripByteCounts.Add(rd(o + 8));
                    else { var ptr = rd(o + 8); for (var k = 0; k < cnt; k++) stripByteCounts.Add(rd(ptr + k * 4)); }
                }
                else if (tag == 277) spp = rd16(o + 8);
                else if (tag == 256) width = rd16(o + 8);
                else if (tag == 257) height = rd16(o + 8);
                else if (tag == 258) // BitsPerSample
                {
                    if (cnt == 1) bitsPerSample = rd16(o + 8);
                    else { var ptr = rd(o + 8); bitsPerSample = rd16(ptr); }
                }
            }

            if (spp < 2 || width <= 0 || height <= 0 || stripOffsets.Count == 0)
            {
                error = $"TIFF 参数异常：spp={spp} {width}x{height} strips={stripOffsets.Count} bps={bitsPerSample}";
                return false;
            }
            if (bitsPerSample != 8) { error = $"仅支持 8bit，实际 bps={bitsPerSample}"; return false; }

            var ai = spp - 1;
            for (var s = 0; s < stripOffsets.Count; s++)
            {
                var off = stripOffsets[s];
                var len = s < stripByteCounts.Count ? stripByteCounts[s] : data.Length - off;
                var pixels = len / spp;
                for (var px = 0; px < pixels; px++)
                {
                    var o = off + px * spp;
                    var a = data[o + ai];
                    if (a is 255 or 0) continue;
                    for (var c = 0; c < ai; c++)
                        data[o + c] = (byte)((data[o + c] * a + 127) / 255);
                }
            }

            File.WriteAllBytes(path, data);
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    /// <summary>中转文件目录：**系统临时目录**，绝不放业务目录。</summary>
    private static readonly string WorkTempDirectory =
        Path.Combine(Path.GetTempPath(), "PivotPic", "work");

    /// <summary>
    /// 申请一个中转文件路径（旋转中转 / 编码快照 / 分量 ID 修正都走这里）。
    /// <para>
    /// **为什么必须离开业务目录**：中转文件在 libvips 写完后是「一张完整可打开的图」，
    /// 大图要写 2–5 秒，这段时间打印软件按目录扫描就会把它当成新图收走 → 多印一张，
    /// 这是真实的经济损失。业务目录里从头到尾只能出现原图一个文件。
    /// </para>
    /// <para>
    /// 保留原文件名前缀，排查时能一眼对上是哪张图。
    /// </para>
    /// </summary>
    /// <param name="sourcePath">原图路径（只取文件名做前缀）。</param>
    /// <param name="kind">中转类型标记，如 <c>.vtmp</c> / <c>.btmp</c> / <c>.idfix</c>。</param>
    /// <param name="extension">扩展名（含点），保持与源图一致，编码器才能正确推断格式。</param>
    public static string GetWorkTempPath(string sourcePath, string kind, string extension)
    {
        try
        {
            Directory.CreateDirectory(WorkTempDirectory);
        }
        catch (Exception)
        {
            // 建不出来就退回系统临时目录根，功能优先。
            return Path.Combine(Path.GetTempPath(),
                Path.GetFileName(sourcePath) + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + kind + extension);
        }

        var prefix = Path.GetFileNameWithoutExtension(sourcePath);

        return Path.Combine(
            WorkTempDirectory,
            prefix + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + kind + extension);
    }

    /// <summary>
    /// 启动时清理中转目录：只删超过 24 小时的文件（正在跑的进程可能还在用新的）。
    /// </summary>
    public static void CleanupWorkTemp()
    {
        try
        {
            if (!Directory.Exists(WorkTempDirectory))
            {
                return;
            }

            var cutoff = DateTime.UtcNow.AddHours(-24);
            var removed = 0;

            foreach (var file in Directory.EnumerateFiles(WorkTempDirectory))
            {
                try
                {
                    var info = new FileInfo(file);
                    if (info.LastWriteTimeUtc > cutoff)
                    {
                        continue;
                    }

                    info.Delete();
                    removed++;
                }
                catch (Exception)
                {
                    // 单个删不掉不影响其它。
                }
            }

            if (removed > 0)
            {
                AppServices.Log?.Info("清理", $"已清理中转目录 {removed} 个超过 24 小时的文件", WorkTempDirectory);
            }
        }
        catch (Exception ex)
        {
            AppServices.Log?.Warn("清理", $"清理中转目录失败：{ex.Message}");
        }
    }

    /// <summary>物理旋转像素后重编码写回（临时文件 + 原子替换）。</summary>
    private static RotationResult RotatePhysical(string path, ImageProbe probe, RotateDirection direction,
        Stopwatch stopwatch, RotationMode mode)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var encoder = CreateEncoder(extension);
        if (encoder is null)
        {
            // webp 等格式 WPF 没有内置编码器，只能交回人工处理。
            return Skip("SkippedUnsupportedEncoder", $"WPF 无 {extension} 编码器，无法写回", probe, stopwatch);
        }

        var notes = new List<string>();
        if (IsMirrored(probe.Orientation))
        {
            // 2/4/5/7 含镜像，本工具只处理「横转竖」，镜像按未镜像处理并在消息里留痕。
            notes.Add("含镜像翻转，已按未翻转处理");
        }

        // 用户选了高性能引擎就走 libvips（需求驱动流水线，峰值内存比 WPF 低一个量级）。
        if (mode == RotationMode.LibVips)
        {
            var outcome = TryRotateWithVips(path, probe, direction, stopwatch, out var vipsResult);

            if (outcome == VipsNative.VipsRotateOutcome.Success)
            {
                return vipsResult;
            }

            if (outcome == VipsNative.VipsRotateOutcome.SourceRejected)
            {
                // 源图根本读不出来（损坏 / 截断 / 编码异常 / 加载出坏图像对象）：
                // 回退 WPF 也是同样的结局，而大图在 WPF 上会挂死几分钟。
                // 这里直接判失败，让进度正常走完，并在日志里留下 vips_error_buffer 原文。
                AppServices.Log?.Error("处理",
                    $"vips 无法解析该图片，已判失败（不再回退 WPF）：{VipsNative.LastError}",
                    Path.GetDirectoryName(path) ?? string.Empty,
                    Path.GetFileName(path));

                return Fail("图片无法读取（文件可能已损坏）", stopwatch, probe);
            }

            AppServices.Log?.Warn("处理",
                $"libvips 旋转失败，回退 WPF：{VipsNative.LastError}",
                Path.GetDirectoryName(path) ?? string.Empty,
                Path.GetFileName(path));

            // TIFF 绝不回退 WPF：WPF 解码喷绘级 TIFF 会挂死数分钟且没有任何输出
            // （实测 291 MB CMYK TIFF 挂 5 分钟以上），这是上一版最伤用户的地方。
            // 宁可给一个明确的失败结论，也不能让界面「卡住 + 进程消失」。
            if (IsTiff(path))
            {
                AppServices.Log?.Error("处理",
                    $"这张 TIFF 无法处理，已判失败（不回退内置引擎）：{VipsNative.LastError}",
                    Path.GetDirectoryName(path) ?? string.Empty,
                    Path.GetFileName(path));

                return Fail($"这张 TIFF 无法处理：{VipsNative.LastError}", stopwatch, probe);
            }
        }

        // 走到这里说明要靠内置（WPF）引擎解码整幅像素。
        // 它扛不住喷绘级的大图：WPF 会把整幅读进内存，291 MB 的 CMYK TIFF 实测能挂死 5 分钟以上且毫无输出，
        // 用户看到的就是「进度条卡住、图没转」。所以先设底线，扛不住就直接失败返回——
        // 回退路径必须有一个明确结果，绝不允许「回退之后再也没有日志」的悬停状态。
        if (probe.FileSize >= BuiltinEngineMaxFileBytes || probe.PixelCount >= BuiltinEngineMaxPixels)
        {
            var detail = $"{probe.FileSize / 1024d / 1024:0} MB，{probe.PixelCount:N0} px";

            AppServices.Log?.Error("处理",
                $"文件过大，内置引擎无法处理（{detail}）",
                Path.GetDirectoryName(path) ?? string.Empty,
                Path.GetFileName(path));

            return Fail("文件过大，内置引擎无法处理", stopwatch, probe);
        }

        BitmapFrame? frame;
        using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            // OnLoad：立刻把像素读进内存，随后即可关闭输入流。
            var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);

            if (decoder.Frames.Count == 0)
            {
                return Fail("解码结果为空帧", stopwatch, probe);
            }

            frame = decoder.Frames[0];
        }

        var baseAngle = OrientationToAngle(probe.Orientation);
        var delta = direction == RotateDirection.Clockwise ? 90 : -90;
        var totalAngle = NormalizeAngle(baseAngle + delta);

        BitmapSource rotated = totalAngle == 0
            ? frame
            : new TransformedBitmap(frame, new RotateTransform(totalAngle));

        // EXIF 的 app1/ifd 只有 JPEG / TIFF 编码器认；PNG 是 tEXt/iTXt 块、BMP 无元数据段，
        // 硬塞 BitmapMetadata 会让 encoder.Save 抛「找不到图像处理组件」，所以按格式取舍。
        var metadata = SupportsExifMetadata(extension) ? BuildNormalizedMetadata(frame) : null;
        var outputFrame = CreateOutputFrame(rotated, frame, metadata);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(directory))
        {
            return Fail("无法确定输出目录", stopwatch, probe);
        }

        var tempPath = Path.Combine(
            directory,
            Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp");

        try
        {
            try
            {
                SaveFrame(encoder, outputFrame, tempPath);
            }
            catch (Exception) when (metadata is not null)
            {
                // 兜底：万一元数据与编码器仍不兼容，丢掉元数据重试一次，绝不因为元数据丢图。
                notes.Add("元数据与编码器不兼容，已丢弃元数据写回");
                var retryEncoder = CreateEncoder(extension);
                if (retryEncoder is null)
                {
                    throw;
                }

                SaveFrame(retryEncoder, BitmapFrame.Create(rotated), tempPath);
            }

            // 与 libvips 路径同一套：SMB 上必须用 Win32 就地覆写，
            // File.Replace / 删除+移动 在网络盘上会被共享冲突挡住。
            if (!TryWriteBackWithRetry(tempPath, path, out var wpWriteError))
            {
                TryDelete(tempPath);
                return Fail($"写回失败：{wpWriteError}", stopwatch, probe);
            }

            // 就地覆写不消耗临时文件，必须清理。
            TryDelete(tempPath);

            return new RotationResult
            {
                Success = true,
                Rotated = true,
                Action = "Rotated",
                Message = notes.Count == 0 ? null : string.Join("；", notes),
                BeforeWidth = probe.PixelWidth,
                BeforeHeight = probe.PixelHeight,
                AfterWidth = rotated.PixelWidth,
                AfterHeight = rotated.PixelHeight,
                ElapsedMs = stopwatch.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            // 任何一步失败：删临时文件，原文件保持不动。
            TryDelete(tempPath);
            return Fail(ex.Message, stopwatch, probe);
        }
    }

    /// <summary>只改写 EXIF Orientation，不重编码像素。</summary>
    private static RotationResult RewriteExif(string path, ImageProbe probe, RotateDirection direction,
        Stopwatch stopwatch)
    {
        var delta = direction == RotateDirection.Clockwise ? 90 : -90;
        var totalAngle = NormalizeAngle(OrientationToAngle(probe.Orientation) + delta);
        var newOrientation = AngleToOrientation(totalAngle);

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            // 原地元数据写入要求帧不被缓存，流也必须保持打开。
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.None);

            if (decoder.Frames.Count == 0)
            {
                return Fail("解码结果为空帧", stopwatch, probe);
            }

            var frame = decoder.Frames[0];
            var writer = frame.CreateInPlaceBitmapMetadataWriter();
            if (writer is null)
            {
                return Fail("当前格式不支持原地写入元数据", stopwatch, probe);
            }

            writer.SetQuery(OrientationQuery, newOrientation);
            if (!writer.TrySave())
            {
                // 文件里没有预留 EXIF 空间时 TrySave 会失败（手机相机拍的多半有，二次编辑过的常常没有）。
                return Fail("原地写入 EXIF 失败（文件没有元数据预留空间），建议改用「物理旋转」模式",
                    stopwatch, probe);
            }
        }
        catch (Exception ex)
        {
            return Fail(ex.Message, stopwatch, probe);
        }

        return new RotationResult
        {
            Success = true,
            Rotated = true,
            Action = "ExifRewritten",
            Message = $"仅改写 EXIF Orientation 为 {newOrientation}",
            BeforeWidth = probe.PixelWidth,
            BeforeHeight = probe.PixelHeight,
            // 逻辑尺寸整体转了 90°，宽高互换。
            AfterWidth = probe.OrientedHeight,
            AfterHeight = probe.OrientedWidth,
            ElapsedMs = stopwatch.ElapsedMilliseconds
        };
    }

    /// <summary>读取 EXIF Orientation（274 号 tag），任何异常或非法值都回落为 1。</summary>
    private static int ReadOrientation(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is not BitmapMetadata metadata)
            {
                return 1;
            }

            var value = metadata.GetQuery(OrientationQuery);
            var orientation = value switch
            {
                ushort ushortValue => ushortValue,
                byte byteValue => byteValue,
                short shortValue => shortValue,
                int intValue => intValue,
                _ => 1
            };

            // 合法范围是 1~8，越界一律视为正常方向。
            return orientation is >= 1 and <= 8 ? orientation : 1;
        }
        catch (Exception)
        {
            // PNG 等格式没有 app1/ifd，GetQuery 会抛异常或返回 null。
            return 1;
        }
    }

    /// <summary>读取解码器名，失败返回空串。</summary>
    private static string ReadDecoderName(BitmapDecoder decoder)
    {
        try
        {
            return decoder.CodecInfo?.FriendlyName ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>按扩展名挑选 WPF 内置编码器；不支持的返回 null。</summary>
    private static BitmapEncoder? CreateEncoder(string extension)
    {
        switch (extension)
        {
            case ".jpg":
            case ".jpeg":
                return new JpegBitmapEncoder { QualityLevel = JpegQuality };
            case ".png":
                return new PngBitmapEncoder();
            case ".bmp":
                return new BmpBitmapEncoder();
            case ".tif":
            case ".tiff":
                return new TiffBitmapEncoder();
            default:
                // .webp 等：WPF 无内置编码器；.gif 不列出，因为没有无损旋转且会丢掉动画帧。
                return null;
        }
    }

    /// <summary>该扩展名的编码器是否支持 EXIF app1/ifd 元数据。</summary>
    private static bool SupportsExifMetadata(string extension)
    {
        return extension is ".jpg" or ".jpeg" or ".tif" or ".tiff";
    }

    /// <summary>是否为 TIFF（.tif/.tiff）。TIFF 是唯一「绝不回退内置引擎」的格式。</summary>
    private static bool IsTiff(string path)
    {
        var extension = Path.GetExtension(path);
        return string.Equals(extension, ".tif", StringComparison.OrdinalIgnoreCase)
               || string.Equals(extension, ".tiff", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 用 libvips 旋转。返回值区分「源图读不出来」与「其它失败」，
    /// 让调用方能决定要不要回退 WPF（源图坏了回退也没用，只会让大图挂死）。
    /// </summary>
    private static VipsNative.VipsRotateOutcome TryRotateWithVips(
        string path, ImageProbe probe, RotateDirection direction,
        Stopwatch stopwatch, out RotationResult result)
    {
        result = null!;

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(directory))
        {
            return VipsNative.VipsRotateOutcome.RotateFailed;
        }

        var extension = Path.GetExtension(path);

        // 中转文件放系统临时目录，业务目录里全程只有原图一个文件。
        var tempPath = GetWorkTempPath(path, ".vtmp", extension);

        try
        {
            var delta = direction == RotateDirection.Clockwise ? 90 : -90;
            var baseAngle = OrientationToAngle(probe.Orientation);

            // 源图 ExtraSamples=1（预乘 alpha）时，vips 加载会自动反预乘。
            // 方案 E：加载前把副本的 ExtraSamples 改成 2，骗过 vips 不做反预乘，
            // vips 原样搬运预乘字节（旋转只是位置置换），全程零像素运算。
            // 写盘后再用 TryRestoreAlphaTags 把标签改回源图值。
            var srcExtra = TiffTagWriter.ReadShortArray(path, 338);
            var needsPremultiply = srcExtra is { Count: > 0 } && srcExtra[0] == 1;
            var rotateSource = path;
            string? sourceCopy = null;

            if (needsPremultiply)
            {
                sourceCopy = GetWorkTempPath(path, ".src", extension);
                File.Copy(path, sourceCopy, overwrite: true);
                if (!TiffTagWriter.TryApply(sourceCopy, 338, new[] { 2 }, out var patchError))
                {
                    TryDelete(sourceCopy);
                    AppServices.Log?.Error("处理", $"修改副本 ExtraSamples 失败：{patchError}",
                        directory, Path.GetFileName(path));
                    return VipsNative.VipsRotateOutcome.WriteFailed;
                }
                rotateSource = sourceCopy;
            }

            var options = BuildVipsSaveOptions(path, extension);

            var outcome = VipsNative.TryRotate(
                rotateSource, tempPath, probe.PixelWidth, probe.OrientedWidth, baseAngle, delta,
                options, out var outWidth, out var outHeight);


            if (outcome != VipsNative.VipsRotateOutcome.Success)
            {
                TryDelete(tempPath);
                return outcome;
            }

            if (sourceCopy is not null) { TryDelete(sourceCopy); }

            // 写回必须走 Win32 就地覆写：SMB 上 File.Replace / File.Delete 在「文件被本进程读过」之后
            // 必然返回共享冲突，网络盘上每一张需要旋转的图都会因此失败（v1.3.24 的真实事故）。
            // TIFF 的 alpha 语义标签必须原样保留。
            // libvips 写出的 ExtraSamples 恒为 2（独立 alpha），而预乘 alpha 源图是 1（ASSOCIATED）——
            // 像素数据没变，但 RIP 按标签解释同一份像素，透明区就会被当成有颜色像素印出来
            // （Photoshop 两种语义都能显示，肉眼看不出，打印才露馅）。
            if (IsTiff(path))
            {
                // 先把 ExtraSamples / SampleFormat 改回源图的值，再严格校验。
                if (!TiffTagWriter.TryRestoreAlphaTags(path, tempPath, out var restoreError))
                {
                    TryDelete(tempPath);
                    AppServices.Log?.Error("处理", $"TIFF alpha 标签还原失败，已中止写回：{restoreError}",
                        directory, Path.GetFileName(path));
                    return VipsNative.VipsRotateOutcome.WriteFailed;
                }
                if (!VerifyTiffAlphaPreserved(path, tempPath, out var alphaError))
                {
                    TryDelete(tempPath);
                    AppServices.Log?.Error("处理", $"TIFF 旋转会破坏透明通道，已中止写回：{alphaError}",
                        directory, Path.GetFileName(path));

                    return VipsNative.VipsRotateOutcome.WriteFailed;
                }
            }
            if (!TryWriteBackWithRetry(tempPath, path, out var vipsWriteError))
            {
                TryDelete(tempPath);
                AppServices.Log?.Warn("处理", $"libvips 写回失败：{vipsWriteError}", directory, Path.GetFileName(path));
                return VipsNative.VipsRotateOutcome.WriteFailed;
            }

            // 就地覆写不会消耗临时文件，必须自己删，否则目录里会留下 .vtmp.jpg 残骸。
            TryDelete(tempPath);

            result = new RotationResult
            {
                Success = true,
                Rotated = true,
                Action = "Rotated",
                Message = "libvips",
                BeforeWidth = probe.PixelWidth,
                BeforeHeight = probe.PixelHeight,
                AfterWidth = outWidth,
                AfterHeight = outHeight,
                ElapsedMs = stopwatch.ElapsedMilliseconds
            };

            return VipsNative.VipsRotateOutcome.Success;
        }
        catch (Exception ex)
        {
            TryDelete(tempPath);
            AppServices.Log?.Warn("处理", $"libvips 异常，回退 WPF：{ex.Message}", directory, Path.GetFileName(path));
            return VipsNative.VipsRotateOutcome.RotateFailed;
        }
    }

    /// <summary>
    /// DPI 兜底：只有明显异常的值（0、负数、离谱的大数）才退回 96。
    /// 喷绘/UV 常有 20–40 DPI 的图，不能按"小于 30 就不可信"处理。
    /// </summary>
    private static double SafeDpi(double dpi) => dpi is >= 5 and <= 10_000 ? dpi : 96;

    /// <summary>按格式给出保存参数：TIFF 尽量沿用源压缩方式，避免体积暴涨。</summary>
    private static string? BuildVipsSaveOptions(string path, string extension)
    {
        switch (extension.ToLowerInvariant())
        {
            case ".tif":
            case ".tiff":
                var compression = ReadTiffCompression(path);
                return compression switch
                {
                    5 => "compression=lzw",
                    8 or 32946 => "compression=deflate",
                    32773 => "compression=packbits",
                    1 => "compression=none",
                    _ => "compression=lzw"
                };

            case ".jpg":
            case ".jpeg":
                return "Q=95";

            case ".png":
                return "compression=6";

            default:
                return null;
        }
    }

    /// <summary>读 TIFF 第一层 IFD 的 Compression 标记（259）。读不到时返回 0。</summary>
    private static int ReadTiffCompression(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var header = new byte[8];
            if (stream.Read(header, 0, 8) != 8)
            {
                return 0;
            }

            var little = header[0] == 'I' && header[1] == 'I';
            if (!little && !(header[0] == 'M' && header[1] == 'M'))
            {
                return 0;
            }

            var ifdOffset = ReadUInt32(header, 4, little);
            if (ifdOffset == 0 || ifdOffset > stream.Length - 2)
            {
                return 0;
            }

            stream.Seek(ifdOffset, SeekOrigin.Begin);
            var countBytes = new byte[2];
            if (stream.Read(countBytes, 0, 2) != 2)
            {
                return 0;
            }

            var count = little ? countBytes[0] | (countBytes[1] << 8) : (countBytes[0] << 8) | countBytes[1];
            var entry = new byte[12];

            for (var i = 0; i < count; i++)
            {
                if (stream.Read(entry, 0, 12) != 12)
                {
                    return 0;
                }

                var tag = little ? entry[0] | (entry[1] << 8) : (entry[0] << 8) | entry[1];
                if (tag != 259)
                {
                    continue;
                }

                // SHORT 类型的值内联在偏移字段的前两个字节。
                return little ? entry[8] | (entry[9] << 8) : (entry[8] << 8) | entry[9];
            }
        }
        catch (Exception)
        {
            // 解析失败按未知处理，交给调用方的默认值。
        }

        return 0;
    }

    private static uint ReadUInt32(byte[] buffer, int offset, bool little) => little
        ? (uint)(buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16) | (buffer[offset + 3] << 24))
        : (uint)((buffer[offset] << 24) | (buffer[offset + 1] << 16) | (buffer[offset + 2] << 8) | buffer[offset + 3]);

    /// <summary>
    /// 旋转成功后把文件名改成「原名 + 标记 + 扩展名」。
    /// 已带同样标记的不重复添加；重名时自动加序号，绝不覆盖已有文件。
    /// </summary>
    private static RotationResult ApplySuffix(string path, string? suffix, RotationResult result)
    {
        var tag = (suffix ?? string.Empty).Trim();
        if (tag.Length == 0)
        {
            return result;
        }

        try
        {
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory))
            {
                return result;
            }

            var extension = Path.GetExtension(path);
            var name = Path.GetFileNameWithoutExtension(path);

            if (name.EndsWith(tag, StringComparison.OrdinalIgnoreCase))
            {
                return result;
            }

            var candidate = Path.Combine(directory, name + tag + extension);
            var index = 1;

            while (File.Exists(candidate))
            {
                candidate = Path.Combine(directory, $"{name}{tag}({index}){extension}");
                index++;
            }

            File.Move(path, candidate);

            // 新文件名同样是本进程写出的，登记后不会被监控当成新文件再处理一次。
            AppServices.IgnoreSet?.Remember(candidate);

            return result.WithExtraMessage($"已改名为 {Path.GetFileName(candidate)}");
        }
        catch (Exception ex)
        {
            return result.WithExtraMessage($"改名失败：{ex.Message}");
        }
    }

    /// <summary>把编码器的唯一一帧写入指定文件（每次编码器只能 Save 一次，故调用方需新建）。</summary>
    private static void SaveFrame(BitmapEncoder encoder, BitmapFrame frame, string targetPath)
    {
        encoder.Frames.Add(frame);

        using var output = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(output);
    }

    /// <summary>克隆元数据并把 Orientation 归零为 1（像素已经转正，不再需要二次旋转）。</summary>
    private static BitmapMetadata? BuildNormalizedMetadata(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is not BitmapMetadata source)
            {
                return null;
            }

            var clone = source.Clone() as BitmapMetadata;
            if (clone is null)
            {
                return null;
            }

            try
            {
                clone.SetQuery(OrientationQuery, (ushort)1);
            }
            catch (Exception)
            {
                // 该格式不允许写这个查询（PNG/BMP 等），整份元数据放弃，避免编码失败。
                return null;
            }

            return clone;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>构造输出帧；缩略图/颜色上下文与旋转后尺寸不匹配时逐级退化。</summary>
    private static BitmapFrame CreateOutputFrame(BitmapSource rotated, BitmapFrame source, BitmapMetadata? metadata)
    {
        try
        {
            return BitmapFrame.Create(rotated, source.Thumbnail, metadata, source.ColorContexts);
        }
        catch (Exception)
        {
            try
            {
                return BitmapFrame.Create(rotated, null, metadata, null);
            }
            catch (Exception)
            {
                // 连元数据都带不上时，至少把像素写出去。
                return BitmapFrame.Create(rotated);
            }
        }
    }

    /// <summary>EXIF Orientation → 需要顺时针补偿的角度（镜像类 2/4/5/7 按未镜像处理）。</summary>
    private static int OrientationToAngle(int orientation)
    {
        return orientation switch
        {
            3 => 180,
            6 => 90,
            8 => 270,
            _ => 0
        };
    }

    /// <summary>角度 → EXIF Orientation（0→1，90→6，180→3，270→8）。</summary>
    private static int AngleToOrientation(int angle)
    {
        return angle switch
        {
            90 => 6,
            180 => 3,
            270 => 8,
            _ => 1
        };
    }

    /// <summary>判断是否为含镜像的 Orientation。</summary>
    private static bool IsMirrored(int orientation)
    {
        return orientation is 2 or 4 or 5 or 7;
    }

    /// <summary>把角度归一到 [0, 360)。</summary>
    private static int NormalizeAngle(int angle)
    {
        var normalized = angle % 360;
        return normalized < 0 ? normalized + 360 : normalized;
    }

    /// <summary>读取文件长度；异常返回 -1（调用方只在 ==0 时特判）。</summary>
    private static long ReadFileLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    /// <summary>判断文件是否为只读属性。</summary>
    private static bool IsReadOnly(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0;
        }
        catch (Exception)
        {
            return false;
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
            // 临时文件残留比丢失原文件安全得多。
        }
    }

    /// <summary>构造失败结果。</summary>
    private static RotationResult Fail(string message, Stopwatch stopwatch, ImageProbe? probe)
    {
        return new RotationResult
        {
            Success = false,
            Rotated = false,
            Action = "Failed",
            Message = message,
            BeforeWidth = probe?.PixelWidth ?? 0,
            BeforeHeight = probe?.PixelHeight ?? 0,
            ElapsedMs = stopwatch.ElapsedMilliseconds
        };
    }

    /// <summary>构造跳过结果（跳过也属于成功，Rotated 为 false；尺寸未知时传 null 探针）。</summary>
    private static RotationResult Skip(string action, string? message, ImageProbe? probe, Stopwatch stopwatch)
    {
        return new RotationResult
        {
            Success = true,
            Rotated = false,
            Action = action,
            Message = message,
            BeforeWidth = probe?.PixelWidth ?? 0,
            BeforeHeight = probe?.PixelHeight ?? 0,
            ElapsedMs = stopwatch.ElapsedMilliseconds
        };
    }
}
