using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PivotPic.Models;
using PivotPic.Services;

namespace PivotPic.ImageSmoke;

/// <summary>
/// 核心旋转逻辑的真实图片冒烟验证。
/// 每张图都是「蓝底 + 左上角红色方块」，因此除了尺寸之外，还能用像素采样判断有没有转错方向。
/// </summary>
internal static class Program
{
    /// <summary>1.2 亿像素上限，等同配置默认值。</summary>
    private const int DefaultMaxPixels = 120_000_000;

    private static readonly List<(string Id, string Name, bool Pass, string Detail)> Table = new();

    private static string _root = string.Empty;

    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;

        _root = Path.Combine(Path.GetTempPath(), "pivotpic_smoke");
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        Directory.CreateDirectory(_root);
        Console.WriteLine($"测试目录: {_root}");
        Console.WriteLine();

        // 按任务要求手工装配服务（不依赖 UI 层）。
        // 按任务要求装配服务，但刻意不调用 Config.Load()：
        // Load() 有个破坏性分支——config.json 存在却反序列化失败时，会把用户的 config.json
        // 用 File.Move 改名成 config.broken.<时间戳>.json（见 ConfigService.Load 的 QuarantineBrokenConfig）。
        // 测试进程没有任何理由搬走真实用户配置，而且 ImageService 全程只依赖 IgnoreSet、从不读 Config，
        // 所以这里保留 new（构造函数零 IO）但不 Load。
        AppServices.Log = new LogService();
        AppServices.Config = new ConfigService();
        AppServices.IgnoreSet = new IgnoreSet();
        AppServices.Image = new ImageService();

        var image = AppServices.Image;

        Run("1", "h_800x600 横版被旋转为 600x800", () => Case1(image));
        Run("2", "同一文件再次处理：幂等跳过，不转回去", () => Case2(image));
        Run("3", "v_600x800 竖版跳过且文件字节不变", () => Case3(image));
        Run("4", "s_500x500 正方形跳过且文件字节不变", () => Case4(image));
        Run("5", "exif6_800x600：EXIF 说竖版，绝不能转", () => Case5(image));
        Run("6", "big_6000x5000 超像素上限跳过", () => Case6(image));
        Run("7", "empty.jpg 零字节跳过", () => Case7(image));
        Run("8", "readonly 只读文件跳过", () => Case8(image));
        Run("9", "h_800x600 处理后哈希改变（确实重写了）", () => Case9());
        Run("10", "IgnoreSet 登记于写之前，处理后 Consume 命中", () => Case10(image));
        Run("11", "jpg 内容改名 .webp：不抛异常穿透", () => Case11(image));
        Run("12", "目录下无任何 *.tmp 残留", () => Case12());
        Run("13", "exif6 前后探测值：Orientation=6 / OrientedWidth=600", () => Case13(image));
        Run("14", "顺时针后标记块应在右上（方向正确）", () => Case14());
        Run("15", "逆时针后标记块应在左下（方向正确）", () => Case15());
        Run("16", "高性能引擎同样遵守 MaxPixels（3MP 图 + 1MP 上限 → 跳过）", () => Case16(image));

        PrintTable();
        return Table.TrueForAll(item => item.Pass) ? 0 : 1;
    }

    // ---------- 用例 ----------

    private static void Case1(IImageService image)
    {
        var path = Path.Combine(_root, "h_800x600.jpg");
        MakeImage(path, 800, 600, PixelFormats.Bgra32, new JpegBitmapEncoder { QualityLevel = 95 });

        _hashBefore = Sha256(path);

        var result = image.Process(path, RotationMode.Physical, RotateDirection.Clockwise, DefaultMaxPixels);
        var probe = image.Probe(path);

        var leftovers = Directory.GetFiles(_root, "h_800x600.jpg.*.tmp");
        var ok = result.Success && result.Rotated
                 && probe is { PixelWidth: 600, PixelHeight: 800 }
                 && leftovers.Length == 0;

        Check("1", "h_800x600 横版被旋转为 600x800", ok,
            $"Rotated={result.Rotated} Action={result.Action} 尺寸={probe?.PixelWidth}x{probe?.PixelHeight} tmp残留={leftovers.Length}");
    }

    private static void Case2(IImageService image)
    {
        var path = Path.Combine(_root, "h_800x600.jpg");
        var result = image.Process(path, RotationMode.Physical, RotateDirection.Clockwise, DefaultMaxPixels);
        var probe = image.Probe(path);

        var ok = !result.Rotated && result.Action == "SkippedVertical"
                 && probe is { PixelWidth: 600, PixelHeight: 800 };

        Check("2", "同一文件再次处理：幂等跳过，不转回去", ok,
            $"Rotated={result.Rotated} Action={result.Action} 尺寸={probe?.PixelWidth}x{probe?.PixelHeight}");
    }

    private static void Case3(IImageService image)
    {
        var path = Path.Combine(_root, "v_600x800.jpg");
        MakeImage(path, 600, 800, PixelFormats.Bgra32, new JpegBitmapEncoder { QualityLevel = 95 });
        var before = Sha256(path);

        var result = image.Process(path, RotationMode.Physical, RotateDirection.Clockwise, DefaultMaxPixels);
        var after = Sha256(path);

        Check("3", "v_600x800 竖版跳过且文件字节不变",
            !result.Rotated && result.Action == "SkippedVertical" && before == after,
            $"Action={result.Action} 哈希一致={before == after}");
    }

    private static void Case4(IImageService image)
    {
        var path = Path.Combine(_root, "s_500x500.png");
        MakeImage(path, 500, 500, PixelFormats.Bgra32, new PngBitmapEncoder());
        var before = Sha256(path);

        var result = image.Process(path, RotationMode.Physical, RotateDirection.Clockwise, DefaultMaxPixels);
        var after = Sha256(path);

        Check("4", "s_500x500 正方形跳过且文件字节不变",
            !result.Rotated && result.Action == "SkippedSquare" && before == after,
            $"Action={result.Action} 哈希一致={before == after}");
    }

    private static void Case5(IImageService image)
    {
        var path = Path.Combine(_root, "exif6_800x600.jpg");
        MakeImage(path, 800, 600, PixelFormats.Bgra32, new JpegBitmapEncoder { QualityLevel = 95 });
        InjectExifOrientation(path, 6);
        var before = Sha256(path);

        var probe = image.Probe(path);
        var result = image.Process(path, RotationMode.Physical, RotateDirection.Clockwise, DefaultMaxPixels);
        var after = Sha256(path);

        // 像素 800x600 是横的，但 EXIF=6 表示逻辑上要顺时针转 90°，即 600x800 竖版 → 必须跳过。
        var ok = !result.Rotated && result.Action == "SkippedVertical" && before == after;

        Check("5", "exif6_800x600：EXIF 说竖版，绝不能转", ok,
            $"Orientation={probe?.Orientation} 像素={probe?.PixelWidth}x{probe?.PixelHeight} "
            + $"逻辑={probe?.OrientedWidth}x{probe?.OrientedHeight} Action={result.Action} 哈希一致={before == after}");
    }

    private static void Case6(IImageService image)
    {
        var path = Path.Combine(_root, "big_6000x5000.jpg");
        MakeImage(path, 6000, 5000, PixelFormats.Bgr24, new JpegBitmapEncoder { QualityLevel = 80 });
        var before = Sha256(path);

        // 3000 万像素，上限故意给 1000 万。
        var result = image.Process(path, RotationMode.Physical, RotateDirection.Clockwise, 10_000_000);
        var after = Sha256(path);

        Check("6", "big_6000x5000 超像素上限跳过",
            result.Action == "SkippedTooLarge" && before == after,
            $"Action={result.Action} 哈希一致={before == after} Message={result.Message}");
    }

    private static void Case7(IImageService image)
    {
        var path = Path.Combine(_root, "empty.jpg");
        File.WriteAllBytes(path, Array.Empty<byte>());

        var result = image.Process(path, RotationMode.Physical, RotateDirection.Clockwise, DefaultMaxPixels);

        // 零字节文件：应从「解码失败」改判为明确的「跳过」。
        Check("7", "empty.jpg 零字节跳过",
            !result.Rotated && result.Action == "SkippedEmpty",
            $"Action={result.Action} Message={result.Message}");
    }

    private static void Case8(IImageService image)
    {
        var path = Path.Combine(_root, "readonly_800x600.jpg");
        MakeImage(path, 800, 600, PixelFormats.Bgra32, new JpegBitmapEncoder { QualityLevel = 95 });
        File.SetAttributes(path, FileAttributes.ReadOnly);
        var before = Sha256(path);

        var result = image.Process(path, RotationMode.Physical, RotateDirection.Clockwise, DefaultMaxPixels);
        var after = Sha256(path);

        // 收尾时恢复属性，否则后面清理临时目录会失败。
        File.SetAttributes(path, FileAttributes.Normal);

        Check("8", "readonly 只读文件跳过",
            !result.Rotated && result.Action == "SkippedReadOnly" && before == after,
            $"Action={result.Action} 哈希一致={before == after}");
    }

    private static string _hashBefore = string.Empty;

    private static void Case9()
    {
        var path = Path.Combine(_root, "h_800x600.jpg");
        var hashAfter = Sha256(path);

        Check("9", "h_800x600 处理后哈希改变（确实重写了）",
            _hashBefore.Length > 0 && hashAfter != _hashBefore,
            $"{_hashBefore[..16]}… -> {hashAfter[..16]}…");
    }

    private static void Case10(IImageService image)
    {
        var path = Path.Combine(_root, "ignore_800x600.jpg");
        MakeImage(path, 800, 600, PixelFormats.Bgra32, new JpegBitmapEncoder { QualityLevel = 95 });

        // 模拟 MonitorService 的正确用法：写之前登记。
        AppServices.IgnoreSet.Remember(path);
        var result = image.Process(path, RotationMode.Physical, RotateDirection.Clockwise, DefaultMaxPixels);

        // Process 内部也会 Remember，所以这里必然能 Consume 到。
        var consumed = AppServices.IgnoreSet.Consume(path);

        Check("10", "IgnoreSet 登记于写之前，处理后 Consume 命中",
            result.Rotated && consumed,
            $"Rotated={result.Rotated} Consumed={consumed}");
    }

    private static void Case11(IImageService image)
    {
        var path = Path.Combine(_root, "pretend_webp.webp");
        MakeImage(path, 800, 600, PixelFormats.Bgra32, new JpegBitmapEncoder { QualityLevel = 95 });

        RotationResult? result = null;
        Exception? thrown = null;

        try
        {
            result = image.Process(path, RotationMode.Physical, RotateDirection.Clockwise, DefaultMaxPixels);
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        Check("11", "jpg 内容改名 .webp：不抛异常穿透",
            thrown is null && result is not null && result.Action is "SkippedUnsupportedEncoder" or "Failed",
            thrown is not null
                ? $"抛异常 {thrown.GetType().Name}: {thrown.Message}"
                : $"Action={result!.Action} Message={result.Message}");
    }

    private static void Case12()
    {
        var leftovers = Directory.GetFiles(_root, "*.tmp", SearchOption.AllDirectories);
        var detail = leftovers.Length == 0 ? "0 个" : string.Join(", ", Array.ConvertAll(leftovers, Path.GetFileName));

        Check("12", "目录下无任何 *.tmp 残留", leftovers.Length == 0, detail);
    }

    private static void Case13(IImageService image)
    {
        var path = Path.Combine(_root, "exif6_800x600.jpg");
        var probe = image.Probe(path);

        var ok = probe is not null
                 && probe.Orientation == 6
                 && probe.PixelWidth == 800
                 && probe.PixelHeight == 600
                 && probe.OrientedWidth == 600
                 && probe.OrientedHeight == 800;

        Check("13", "exif6 探测值：Orientation=6 / OrientedWidth=600", ok,
            probe is null
                ? "Probe 返回 null"
                : $"Orientation={probe.Orientation} 像素={probe.PixelWidth}x{probe.PixelHeight} "
                  + $"逻辑={probe.OrientedWidth}x{probe.OrientedHeight}");
    }

    private static void Case14()
    {
        // 顺时针：左上角的红色标记应移动到右上角。
        var path = Path.Combine(_root, "dir_cw.jpg");
        MakeImage(path, 800, 600, PixelFormats.Bgra32, new JpegBitmapEncoder { QualityLevel = 95 });
        AppServices.Image.Process(path, RotationMode.Physical, RotateDirection.Clockwise, DefaultMaxPixels);

        var topRight = SamplePixel(path, 600 - 40, 40);
        var topLeft = SamplePixel(path, 40, 40);

        Check("14", "顺时针后标记块应在右上（方向正确）",
            IsRed(topRight) && !IsRed(topLeft),
            $"右上={Describe(topRight)} 左上={Describe(topLeft)}");
    }

    private static void Case15()
    {
        // 逆时针：左上角的红色标记应移动到左下角。
        var path = Path.Combine(_root, "dir_ccw.jpg");
        MakeImage(path, 800, 600, PixelFormats.Bgra32, new JpegBitmapEncoder { QualityLevel = 95 });
        AppServices.Image.Process(path, RotationMode.Physical, RotateDirection.CounterClockwise, DefaultMaxPixels);

        var bottomLeft = SamplePixel(path, 40, 800 - 40);
        var topLeft = SamplePixel(path, 40, 40);

        Check("15", "逆时针后标记块应在左下（方向正确）",
            IsRed(bottomLeft) && !IsRed(topLeft),
            $"左下={Describe(bottomLeft)} 左上={Describe(topLeft)}");
    }

    /// <summary>
    /// 高性能引擎（libvips）必须同样遵守用户的 MaxPixels。
    /// 这条断言针对的是一个真实缺陷：vips 分支曾把上限直接置 0，等于用户设置被静默忽略。
    /// 本机没有 libvips 时该分支根本走不到，跳过（内置引擎的上限已由用例 6 覆盖）。
    /// </summary>
    private static void Case16(IImageService image)
    {
        if (!VipsNative.IsAvailable)
        {
            Check("16", "高性能引擎同样遵守 MaxPixels", true, "跳过：本机 libvips 不可用（该分支走不到）");
            return;
        }

        var path = Path.Combine(_root, "vips_cap_2000x1500.jpg");
        MakeImage(path, 2000, 1500, PixelFormats.Bgr24, new JpegBitmapEncoder { QualityLevel = 90 });
        var before = Sha256(path);

        // 3 MP 的图 + 1 MP 上限：必须跳过，且文件一个字节都不能动。
        var result = image.Process(path, RotationMode.LibVips, RotateDirection.Clockwise, 1_000_000);
        var after = Sha256(path);

        var ok = !result.Rotated
                 && result.Action == "SkippedTooLarge"
                 && before == after
                 && result.Message.Contains("可在设置里调大", StringComparison.Ordinal);

        Check("16", "高性能引擎同样遵守 MaxPixels（3MP + 1MP → 跳过）", ok,
            $"Action={result.Action} 哈希一致={before == after} Message={result.Message}");
    }

    // ---------- 基础设施 ----------

    private static void Run(string id, string name, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Check(id, name, false, $"抛出异常 {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void Check(string id, string name, bool pass, string detail)
    {
        Table.Add((id, name, pass, detail));
        Console.WriteLine($"{(pass ? "PASS" : "FAIL")}  [{id}] {name}  {detail}");
    }

    private static void PrintTable()
    {
        Console.WriteLine();
        Console.WriteLine("| #  | 用例 | 结果 | 实测 |");
        Console.WriteLine("|----|------|------|------|");

        foreach (var item in Table)
        {
            Console.WriteLine($"| {item.Id} | {item.Name} | {(item.Pass ? "PASS" : "FAIL")} | {item.Detail} |");
        }

        var failed = 0;
        foreach (var item in Table)
        {
            if (!item.Pass)
            {
                failed++;
            }
        }

        Console.WriteLine();
        Console.WriteLine(failed == 0
            ? $"=== 全部 {Table.Count} 条 PASS ==="
            : $"=== {Table.Count} 条中 {failed} 条 FAIL ===");
    }

    /// <summary>生成「蓝底 + 左上角红色方块」的测试图。</summary>
    private static void MakeImage(string path, int width, int height, PixelFormat format, BitmapEncoder encoder)
    {
        var bytesPerPixel = (format.BitsPerPixel + 7) / 8;
        var stride = width * bytesPerPixel;
        var pixels = new byte[(long)stride * height];

        // 背景：蓝
        for (long index = 0; index < pixels.Length; index += bytesPerPixel)
        {
            pixels[index] = 200;     // B
            pixels[index + 1] = 90;  // G
            pixels[index + 2] = 30;  // R
        }

        // 左上角红色实心方块：100x100，用来判断旋转方向
        var block = Math.Min(150, Math.Min(width, height) / 3);
        for (var y = 0; y < block; y++)
        {
            for (var x = 0; x < block; x++)
            {
                var offset = (long)y * stride + (long)x * bytesPerPixel;
                pixels[offset] = 40;      // B
                pixels[offset + 1] = 40;  // G
                pixels[offset + 2] = 220; // R
            }
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, format, null, pixels, stride);
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(stream);
    }

    /// <summary>在 JPEG 的 SOI 之后插入 APP1/EXIF 段，把 Orientation 写成指定值。</summary>
    private static void InjectExifOrientation(string path, ushort orientation)
    {
        var original = File.ReadAllBytes(path);
        if (original.Length < 2 || original[0] != 0xFF || original[1] != 0xD8)
        {
            throw new InvalidOperationException("目标文件不是合法 JPEG（缺少 SOI 标记）");
        }

        // TIFF 头（小端）+ 1 个 IFD 项（tag 0x0112 SHORT 1 = orientation）+ 结束标记
        var payload = new List<byte>();
        payload.AddRange(new byte[] { 0x45, 0x78, 0x69, 0x66, 0x00, 0x00 });                     // "Exif\0\0"
        payload.AddRange(new byte[] { 0x49, 0x49, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00 });         // II + 42 + IFD0 偏移 8
        payload.AddRange(new byte[] { 0x01, 0x00 });                                             // 1 个目录项
        payload.AddRange(new byte[]
        {
            0x12, 0x01,                                     // tag 0x0112
            0x03, 0x00,                                     // type 3 (SHORT)
            0x01, 0x00, 0x00, 0x00,                         // count 1
            (byte)(orientation & 0xFF), (byte)(orientation >> 8), 0x00, 0x00
        });
        payload.AddRange(new byte[] { 0x00, 0x00, 0x00, 0x00 });                                  // 下一个 IFD = 0

        var length = payload.Count + 2;
        var segment = new List<byte> { 0xFF, 0xE1, (byte)(length >> 8), (byte)(length & 0xFF) };
        segment.AddRange(payload);

        var result = new byte[original.Length + segment.Count];
        result[0] = original[0];
        result[1] = original[1];
        segment.CopyTo(result, 2);
        Array.Copy(original, 2, result, 2 + segment.Count, original.Length - 2);

        File.WriteAllBytes(path, result);
    }

    /// <summary>读取单个像素（统一转成 Bgra32 再采样）。</summary>
    private static (byte R, byte G, byte B) SamplePixel(string path, int x, int y)
    {
        var decoder = BitmapDecoder.Create(new Uri(path), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var converted = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
        var cropped = new CroppedBitmap(converted, new Int32Rect(x, y, 1, 1));
        var buffer = new byte[4];
        cropped.CopyPixels(buffer, 4, 0);
        return (buffer[2], buffer[1], buffer[0]);
    }

    private static bool IsRed((byte R, byte G, byte B) pixel)
    {
        return pixel.R > 150 && pixel.G < 110 && pixel.B < 110;
    }

    private static string Describe((byte R, byte G, byte B) pixel)
    {
        return $"RGB({pixel.R},{pixel.G},{pixel.B})";
    }

    /// <summary>文件 SHA256（大写十六进制）。</summary>
    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream));
    }
}
