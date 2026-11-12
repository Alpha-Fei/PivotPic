using System;
using System.IO;
using System.Runtime.InteropServices;

namespace PivotPic.Services;

/// <summary>
/// libvips 原生调用封装（LGPL-2.1+，以独立动态库形式随程序分发）。
/// <para>
/// 用途：超大图的 90° 旋转。WPF 走「整图解码 → 旋转 → 编码」，峰值约 3 份全尺寸像素；
/// libvips 是需求驱动流水线，实测 125MP 图的峰值增量约 260MB，比 WPF 低一个量级。
/// </para>
/// 所有调用失败都不抛异常，返回 false 让上层回退到 WPF 实现。
/// </summary>
internal static class VipsNative
{
    private static readonly object Gate = new();

    private static bool _initialized;
    private static bool _available;
    private static string _lastError = string.Empty;

    // 函数指针
    private static VipsInit? _init;
    private static VipsNewFromFile? _newFromFile;
    private static VipsRot? _rot;
    private static VipsNewFromMemory? _newFromMemory;
    private static VipsImageSetInt? _setImageInt;
    private static VipsCopy? _copy;
    private static VipsWriteToFile? _writeToFile;
    private static VipsGetInt? _getWidth;
    private static VipsGetInt? _getHeight;
    private static VipsGetInt? _getBands;
    private static VipsWriteToMemory? _writeToMemory;
    private static GFree? _gFree;
    private static VipsCacheSetInt? _cacheSetMax;
    private static VipsCacheSetInt? _cacheSetMaxMem;
    private static VipsCacheSetInt? _cacheSetMaxFiles;
    private static GObjectUnref? _unref;
    private static VipsErrorBuffer? _errorBuffer;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int VipsInit([MarshalAs(UnmanagedType.LPUTF8Str)] string argv0);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr VipsNewFromFile(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? optionString);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int VipsRot(IntPtr input, out IntPtr output, int angle, IntPtr unused);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int VipsWriteToFile(IntPtr image,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, IntPtr unused);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int VipsGetInt(IntPtr image);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr VipsWriteToMemory(IntPtr image, out UIntPtr size);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void GFree(IntPtr pointer);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int VipsCacheSetInt(int value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void GObjectUnref(IntPtr obj);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr VipsNewFromMemory(IntPtr data, UIntPtr size, int width, int height, int bands, int format);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int VipsImageSetInt(IntPtr image, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int VipsCopy(IntPtr input, out IntPtr output, IntPtr options);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr VipsErrorBuffer();

    /// <summary>原生库是否可用（随程序附带的 DLL 存在且初始化成功）。</summary>
    public static bool IsAvailable
    {
        get
        {
            EnsureInitialized();
            return _available;
        }
    }

    /// <summary>最近一次失败原因，便于日志排查。</summary>
    public static string LastError
    {
        get
        {
            EnsureInitialized();
            return _lastError;
        }
    }

    private static void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        lock (Gate)
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;

            try
            {
                var dllPath = Path.Combine(AppContext.BaseDirectory, "vips", "libvips-42.dll");
                if (!File.Exists(dllPath))
                {
                    _available = false;
                    return;
                }

                var handle = NativeLibrary.Load(dllPath);

                _init = GetExport<VipsInit>(handle, "vips_init");
                _newFromFile = GetExport<VipsNewFromFile>(handle, "vips_image_new_from_file");
                _rot = GetExport<VipsRot>(handle, "vips_rot");
                _newFromMemory = GetExport<VipsNewFromMemory>(handle, "vips_image_new_from_memory");
                _setImageInt = GetExport<VipsImageSetInt>(handle, "vips_image_set_int");
                _copy = GetExport<VipsCopy>(handle, "vips_copy");
                _writeToFile = GetExport<VipsWriteToFile>(handle, "vips_image_write_to_file");
                _getWidth = GetExport<VipsGetInt>(handle, "vips_image_get_width");
                _getHeight = GetExport<VipsGetInt>(handle, "vips_image_get_height");
                _unref = GetExport<GObjectUnref>(handle, "g_object_unref");
                _errorBuffer = GetExport<VipsErrorBuffer>(handle, "vips_error_buffer");

                // 这三个只给「取原始像素」用（LibJpeg 重编码需要）。老版本 libvips 可能没有，
                // 缺了就降级成「不支持新编码器」，不能因此把整个引擎判死。
                try
                {
                    _getBands = GetExport<VipsGetInt>(handle, "vips_image_get_bands");
                    _writeToMemory = GetExport<VipsWriteToMemory>(handle, "vips_image_write_to_memory");
                    _gFree = GetExport<GFree>(handle, "g_free");
                }
                catch (Exception)
                {
                    _getBands = null;
                    _writeToMemory = null;
                    _gFree = null;
                }

                if (_init!(dllPath) != 0)
                {
                    _lastError = "vips_init 返回失败";
                    _available = false;
                    return;
                }

                // libvips 默认有操作缓存（最多 1000 个操作 / 100 MB / 100 个文件）。
                // 缓存的图像会一直持有源文件句柄，而 libvips 打开文件时**不带 FILE_SHARE_DELETE**，
                // 于是「处理过的文件」在资源管理器里改不了名、删不掉，弹「文件已在…中打开」。
                // 我们每张图只加载一次，缓存没有任何收益，直接全部关掉。
                BindCacheControls(handle);

                _available = true;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _available = false;
            }
        }
    }

    private static T GetExport<T>(IntPtr library, string name) where T : Delegate
    {
        var address = NativeLibrary.GetExport(library, name);
        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    /// <summary>
    /// 组装 libvips 的加载路径。
    /// <para>
    /// <b>为什么 TIFF 必须带 <c>[unlimited]</c></b>：libvips 的 tiffload 有一层反 DoS 保护，
    /// 默认把「累积内存分配」限制在 50 MB（52428800 字节）。喷绘用的多通道 TIFF 光是 IFD/StripOffset
    /// 与 XMP 就能超过这个数，于是加载直接失败：
    /// <c>tiff2vips: Cumulated memory allocation of … is beyond the 52428800 cumulated byte limit</c>。
    /// 这类文件在我们的场景里是完全正常的输入，所以 TIFF 一律关闭该保护。
    /// </para>
    /// <para>
    /// 只对 TIFF 加：其它加载器（jpeg/png/webp）没有这条限制，乱塞未知选项反而会让 libvips 报错。
    /// </para>
    /// </summary>
    private static string BuildLoadPath(string path)
    {
        var extension = Path.GetExtension(path);

        var isTiff = string.Equals(extension, ".tif", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(extension, ".tiff", StringComparison.OrdinalIgnoreCase);

        // 只加 unlimited。**不要**加 access=sequential：90° 旋转必须按列随机访问，
        // 顺序访问模式下 vips 会直接报 "tiff2vips: out of order read -- at line N, but line M requested"。
        return isTiff ? path + "[unlimited]" : path;
    }

    /// <summary>
    /// vips 旋转的结果分类。
    /// <para>
    /// 必须区分「源图根本读不出来」与「其它失败」：前者回退 WPF 也是一样的结局，
    /// 而大图在 WPF 上会挂死几分钟，所以调用方要据此直接判失败、不准回退。
    /// </para>
    /// </summary>
    public enum VipsRotateOutcome
    {
        /// <summary>旋转并写回成功。</summary>
        Success,

        /// <summary>原生库不可用（缺 DLL / 初始化失败）。</summary>
        NotAvailable,

        /// <summary>源图加载失败或返回坏图像（文件损坏、截断、解码失败）。<b>不应回退 WPF。</b></summary>
        SourceRejected,

        /// <summary>旋转步骤失败（可回退）。</summary>
        RotateFailed,

        /// <summary>写回失败（可回退）。</summary>
        WriteFailed
    }

    /// <summary>关闭 libvips 操作缓存（老版本可能缺某个导出，缺了就尽力而为）。</summary>
    private static void BindCacheControls(IntPtr handle)
    {
        try
        {
            _cacheSetMax = GetExport<VipsCacheSetInt>(handle, "vips_cache_set_max");
            _cacheSetMaxMem = GetExport<VipsCacheSetInt>(handle, "vips_cache_set_max_mem");
            _cacheSetMaxFiles = GetExport<VipsCacheSetInt>(handle, "vips_cache_set_max_files");
        }
        catch (Exception)
        {
            _cacheSetMax = null;
            _cacheSetMaxMem = null;
            _cacheSetMaxFiles = null;
        }

        try
        {
            _cacheSetMax?.Invoke(0);
            _cacheSetMaxMem?.Invoke(0);
            _cacheSetMaxFiles?.Invoke(0);

            // 注意：**不要**在这里或处理流程里调 vips_cache_drop_all()。
            // 该导出在本构建上会触发 AccessViolation（实测：批量旋转第三张就崩），
            // 而 max=0 之后本来就不会再缓存，drop_all 没有额外收益。
        }
        catch (Exception)
        {
            // 缓存关不掉也不影响正确性，只是句柄晚一点释放。
        }
    }

    /// <summary>从 libvips 读出来的原始像素（行优先、通道交错）。</summary>
    public sealed class RawPixels
    {
        public int Width { get; init; }

        public int Height { get; init; }

        /// <summary>通道数：4=CMYK，3=RGB，1=灰度。</summary>
        public int Bands { get; init; }

        public byte[] Data { get; init; } = Array.Empty<byte>();
    }

    /// <summary>
    /// 把整幅图读成内存里的原始像素，交给 LibJpeg 重编码用。
    /// <para>
    /// 走 <c>vips_image_write_to_memory</c>：一次拿到 headerless 的交错数据，
    /// 不需要落临时 .raw 文件再读回来（少一次 130 MB 的磁盘往返）。
    /// </para>
    /// </summary>
    public static bool TryReadPixels(string path, out RawPixels? pixels, out string error)
    {
        pixels = null;
        error = string.Empty;

        if (!IsAvailable || _writeToMemory is null || _gFree is null || _getBands is null)
        {
            error = "高性能引擎不支持像素直读（libvips 版本过旧）";
            return false;
        }

        IntPtr image = IntPtr.Zero;
        IntPtr buffer = IntPtr.Zero;

        try
        {
            image = _newFromFile!(BuildLoadPath(path), null);
            if (image == IntPtr.Zero)
            {
                error = ReadError();
                return false;
            }

            var width = _getWidth!(image);
            var height = _getHeight!(image);
            var bands = _getBands!(image);

            if (width <= 0 || height <= 0 || bands <= 0)
            {
                var reason = ReadError();
                error = string.IsNullOrWhiteSpace(reason) ? $"尺寸非法：{width}×{height}×{bands}" : reason;
                return false;
            }

            buffer = _writeToMemory!(image, out var size);
            if (buffer == IntPtr.Zero)
            {
                error = ReadError();
                return false;
            }

            var expected = (long)width * height * bands;
            if ((long)size < expected)
            {
                error = $"像素数据长度异常：得到 {size}，期望 {expected}";
                return false;
            }

            var data = new byte[expected];
            Marshal.Copy(buffer, data, 0, (int)expected);

            pixels = new RawPixels { Width = width, Height = height, Bands = bands, Data = data };
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                _gFree!(buffer);
            }

            if (image != IntPtr.Zero)
            {
                _unref!(image);
            }
        }
    }

    /// <summary>
    /// 把 JPEG 重存成「基线标准」：interlace=false（非连续）+ optimize-coding=false（标准 Huffman）。
    /// <para>
    /// 保留 ICC / EXIF / DPI 由 libvips 的元数据传递负责（jpegsave 会把 icc-profile-data、exif-data、
    /// xres/yres 一并写回）。色度抽样按原图传 subsample，避免把 4:4:4 降成 4:2:0 造成二次损失；
    /// CMYK 四分量不传 subsample（由 libvips 自行处理）。
    /// </para>
    /// </summary>
    /// <param name="sourcePath">源 JPEG。</param>
    /// <param name="destinationPath">目标临时文件。</param>
    /// <param name="info">源图探测结果（用于沿用色度抽样）。</param>
    /// <param name="error">失败原因（vips_error_buffer 原文）。</param>
    public static bool TrySaveBaselineJpeg(string sourcePath, string destinationPath, JpegInfo info, out string error)
    {
        error = string.Empty;

        if (!IsAvailable)
        {
            error = "高性能引擎不可用";
            return false;
        }

        IntPtr input = IntPtr.Zero;

        try
        {
            input = _newFromFile!(sourcePath, null);
            if (input == IntPtr.Zero)
            {
                error = ReadError();
                return false;
            }

            var width = _getWidth!(input);
            var height = _getHeight!(input);
            if (width <= 0 || height <= 0)
            {
                var reason = ReadError();
                error = string.IsNullOrWhiteSpace(reason) ? $"加载后尺寸非法：{width}×{height}" : reason;
                return false;
            }

            var target = destinationPath + "[" + BuildBaselineJpegOptions(info) + "]";

            if (_writeToFile!(input, target, IntPtr.Zero) != 0)
            {
                error = ReadError();
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            if (input != IntPtr.Zero)
            {
                _unref!(input);
            }
        }
    }

    /// <summary>
    /// 基线 JPEG 的保存选项：Q95、非连续、标准 Huffman + 每 MCU 行一个重启标记。
    /// <para>
    /// 两个易踩的坑（都实测过）：
    /// 1) 本仓库的 libvips 构建里 jpegsave **没有** subsample/downsample 属性，
    ///    带上任何未知属性整个保存都会失败（"no property named"）；
    /// 2) 不写 restart-interval 的话输出里一个 RST 标记都没有，而 Photoshop 存的基线
    ///    每个 MCU 行都有重启点（DRI = ceil(宽/8)）。很多打印 RIP 靠 RST 定位 MCU 行，
    ///    没有就整份拒收——用户实测「PS 转的认、我们转的不认」这是主因之一。
    /// </para>
    /// </summary>
    private static string BuildBaselineJpegOptions(JpegInfo info)
    {
        // optimize-coding=false 很关键：否则输出的是「基线已优化」，部分老旧 RIP 仍认不出。
        // 重启间隔按「一行 MCU」取：我们输出 4:4:4，MCU 宽 8 像素。
        var restartInterval = Math.Max(1, (info.Width + 7) / 8);

        return $"Q=95,interlace=false,optimize-coding=false,restart-interval={restartInterval}";
    }

    /// <summary>
    /// 把图片旋转 90° 并写到目标路径。
    /// </summary>
    /// <param name="sourcePath">源文件。</param>
    /// <param name="destinationPath">目标文件（同目录临时文件）。</param>
    /// <param name="probePixelWidth">探测到的原始像素宽度（用于识别 libvips 是否已自动纠偏）。</param>
    /// <param name="probeOrientedWidth">按 EXIF 校正后的逻辑宽度。</param>
    /// <param name="baseAngle">EXIF 带来的基础角度（0/90/180/270）。</param>
    /// <param name="delta">需要额外旋转的角度（顺时针 90 / 逆时针 -90）。</param>
    /// <param name="saveOptions">vips 保存选项，如 compression=lzw。</param>
    /// <param name="outputWidth">旋转后的宽度。</param>
    /// <param name="outputHeight">旋转后的高度。</param>
    /// <param name="premultiplyAlpha">
    /// 源图 ExtraSamples=1（预乘 alpha）时传 true。
    /// libvips 加载时会把预乘数据自动反预乘成直通（color*255/alpha），写出前必须重新预乘回去，
    /// 否则「数据是直通、标签说是预乘」→ 打印机按预乘解释 → 颜色全错。
    /// </param>
    public static VipsRotateOutcome TryRotate(string sourcePath, string destinationPath,
        int probePixelWidth, int probeOrientedWidth, int baseAngle, int delta,
        string? saveOptions, out int outputWidth, out int outputHeight)
    {
        outputWidth = 0;
        outputHeight = 0;

        if (!IsAvailable)
        {
            return VipsRotateOutcome.NotAvailable;
        }

        IntPtr input = IntPtr.Zero;
        IntPtr output = IntPtr.Zero;

        try
        {
            input = _newFromFile!(BuildLoadPath(sourcePath), null);

            // 关键：libvips 加载失败时**不一定返回 NULL**。
            // 例如 tiffload 的「累积内存超限」路径会返回一个非 NULL 的坏图像对象，
            // 错误只写进 vips_error_buffer。此时若只判 NULL 就会继续 vips_rot，
            // 静默失败 → 回退 WPF → 大图在 WPF 上挂死。
            // 所以加载后必须读一次尺寸做健全性检查，非正即视为源图不可用。
            if (input == IntPtr.Zero)
            {
                _lastError = ReadError();
                return VipsRotateOutcome.SourceRejected;
            }

            var loadedWidth = _getWidth!(input);
            var loadedHeight = _getHeight!(input);

            if (loadedWidth <= 0 || loadedHeight <= 0)
            {
                var reason = ReadError();
                _lastError = string.IsNullOrWhiteSpace(reason)
                    ? $"加载后图像尺寸非法：{loadedWidth}×{loadedHeight}"
                    : reason;

                return VipsRotateOutcome.SourceRejected;
            }

            // 角度在同一次加载里算完：以前调用方要先 TryGetSize（再 load 一次 291 MB 的图）
            // 才能判断 libvips 是否已按 EXIF 自动纠偏，两次加载会让峰值内存与分页压力翻倍。
            // libvips 加载时若已纠偏，加载宽度会等于「校正后的宽度」而不等于原始像素宽度。
            var totalAngle = NormalizeAngle(baseAngle + delta);
            if (loadedWidth == probeOrientedWidth && probeOrientedWidth != probePixelWidth)
            {
                totalAngle = NormalizeAngle(delta);
            }

            // VIPS_ANGLE_D90 = 1，D180 = 2，D270 = 3
            var angle = totalAngle switch
            {
                90 => 1,
                180 => 2,
                270 => 3,
                _ => 0
            };

            var source = input;

            if (angle != 0)
            {
                if (_rot!(input, out output, angle, IntPtr.Zero) != 0)
                {
                    _lastError = ReadError();
                    return VipsRotateOutcome.RotateFailed;
                }

                source = output;
            }

            // 源图是预乘 alpha 时，libvips 加载后已把数据反预乘（color*255/alpha）。
            // 写出前必须重新预乘回去，否则数据是直通、标签说是预乘 → 打印机颜色全错。
            var target = string.IsNullOrEmpty(saveOptions)
                ? destinationPath
                : destinationPath + "[" + saveOptions + "]";

            if (_writeToFile!(source, target, IntPtr.Zero) != 0)
            {
                _lastError = ReadError();
                return VipsRotateOutcome.WriteFailed;
            }

            outputWidth = _getWidth!(source);
            outputHeight = _getHeight!(source);
            return VipsRotateOutcome.Success;
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            return VipsRotateOutcome.RotateFailed;
        }
        finally
        {
            if (output != IntPtr.Zero)
            {
                _unref!(output);
            }

            if (input != IntPtr.Zero)
            {
                _unref!(input);
            }

        }
    }

    /// <summary>把角度归一化到 [0,360)。</summary>
    private static int NormalizeAngle(int angle)
    {
        var normalized = angle % 360;
        return normalized < 0 ? normalized + 360 : normalized;
    }

    /// <summary>读取图片当前（按 EXIF 自动旋转后）的宽度，用于判断 libvips 是否已自动纠偏。</summary>
    public static bool TryGetSize(string path, out int width, out int height)
    {
        width = 0;
        height = 0;

        if (!IsAvailable)
        {
            return false;
        }

        IntPtr image = IntPtr.Zero;
        try
        {
            image = _newFromFile!(BuildLoadPath(path), null);
            if (image == IntPtr.Zero)
            {
                return false;
            }

            width = _getWidth!(image);
            height = _getHeight!(image);
            return width > 0 && height > 0;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            if (image != IntPtr.Zero)
            {
                _unref!(image);
            }
        }
    }

    private static string ReadError()
    {
        try
        {
            var pointer = _errorBuffer!();
            return pointer == IntPtr.Zero ? "未知错误" : Marshal.PtrToStringUTF8(pointer) ?? "未知错误";
        }
        catch (Exception)
        {
            return "未知错误";
        }
    }
}
