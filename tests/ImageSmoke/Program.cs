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
    /// <summary>
    /// 一张 32×24 的「连续（progressive）」JPEG（PIL 生成，SOF=FFC2，DPI=300）。
    /// 内嵌字节而不是用 WPF 现造：WPF 的 JPEG 编码器只会写基线，造不出 progressive。
    /// </summary>
    private const string ProgressiveJpegBase64 =
        "/9j/4AAQSkZJRgABAQEBLAEsAAD/2wBDAAMCAgMCAgMDAwMEAwMEBQgFBQQEBQoHBwYIDAoMDAsKCwsNDhIQDQ4RDgsLEBYQERMU" +
        "FRUVDA8XGBYUGBIUFRT/2wBDAQMEBAUEBQkFBQkUDQsNFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQU" +
        "FBQUFBQUFBT/wgARCAAYACADASIAAhEBAxEB/8QAFwABAAMAAAAAAAAAAAAAAAAAAAQFBv/EABcBAQEBAQAAAAAAAAAAAAAAAAAE" +
        "Bgf/2gAMAwEAAhADEAAAAcjbSGF64E8YAH//xAAXEAADAQAAAAAAAAAAAAAAAAAAAzAz/9oACAEBAAEFAhGUv//EABoRAAAHAAAA" +
        "AAAAAAAAAAAAAAIDBBESEyD/2gAIAQMBAT8BOUWhi2P/xAAYEQACAwAAAAAAAAAAAAAAAAADEQESIP/aAAgBAgEBPwEhbwlj/8QA" +
        "FhAAAwAAAAAAAAAAAAAAAAAAAAEw/9oACAEBAAY/AhT/AP/EABYQAQEBAAAAAAAAAAAAAAAAALEAMP/aAAgBAQABPyGS5//aAAwD" +
        "AQACAAMAAAAQzCCC/8QAGhEBAAEFAAAAAAAAAAAAAAAAEQEgITFRYf/aAAgBAwEBPxA6yS5d8o//xAAXEQADAQAAAAAAAAAAAAAA" +
        "AAARICGB/9oACAECAQE/EA0NT//EABcQAAMBAAAAAAAAAAAAAAAAAAAwYfD/2gAIAQEAAT8QMVL/AP/Z";


    /// <summary>同上的竖版版本（24×32，progressive，DPI=300），用于验证「不旋转也要转基线」。</summary>
    private const string PortraitProgressiveJpegBase64 =
        "/9j/4AAQSkZJRgABAQEBLAEsAAD/2wBDAAMCAgMCAgMDAwMEAwMEBQgFBQQEBQoHBwYIDAoMDAsKCwsNDhIQDQ4RDgsLEBYQERMU" +
        "FRUVDA8XGBYUGBIUFRT/2wBDAQMEBAUEBQkFBQkUDQsNFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQU" +
        "FBQUFBQUFBT/wgARCAAgABgDASIAAhEBAxEB/8QAFwABAAMAAAAAAAAAAAAAAAAAAAQFBv/EABcBAQEBAQAAAAAAAAAAAAAAAAAE" +
        "Bgf/2gAMAwEAAhADEAAAAcjbSGF64E8YAH//xAAXEAADAQAAAAAAAAAAAAAAAAAAAzAz/9oACAEBAAEFAhGUv//EABoRAAAHAAAA" +
        "AAAAAAAAAAAAAAIDBBESEyD/2gAIAQMBAT8BOUWhi2P/xAAYEQACAwAAAAAAAAAAAAAAAAADEQESIP/aAAgBAgEBPwEhbwlj/8QA" +
        "FhAAAwAAAAAAAAAAAAAAAAAAAAEw/9oACAEBAAY/AhT/AP/EABYQAQEBAAAAAAAAAAAAAAAAALEAMP/aAAgBAQABPyGS5//aAAwD" +
        "AQACAAMAAAAQzCCC/8QAGhEBAAEFAAAAAAAAAAAAAAAAEQEgITFRYf/aAAgBAwEBPxA6yS5d8o//xAAXEQADAQAAAAAAAAAAAAAA" +
        "AAARICGB/9oACAECAQE/EA0NT//EABcQAAMBAAAAAAAAAAAAAAAAAAAwYfD/2gAIAQEAAT8QMVL/AP/Z";


    /// <summary>一张 48×40 的 CMYK JPEG（PIL 生成，Adobe APP14 transform=0），用于验证编码器输出结构。</summary>
    private const string CmykJpegBase64 =
        "/9j/7gAOQWRvYmUAZAAAAAAA/9sAQwADAgICAgIDAgICAwMDAwQGBAQEBAQIBgYFBgkICgoJCAkJCgwPDAoLDgsJCQ0RDQ4PEBAR" +
        "EAoMEhMSEBMPEBAQ/8AAFAgAKAAwBEMRAE0RAFkRAEsRAP/EAB8AAAEFAQEBAQEBAAAAAAAAAAABAgMEBQYHCAkKC//EALUQAAIB" +
        "AwMCBAMFBQQEAAABfQECAwAEEQUSITFBBhNRYQcicRQygZGhCCNCscEVUtHwJDNicoIJChYXGBkaJSYnKCkqNDU2Nzg5OkNERUZH" +
        "SElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6g4SFhoeIiYqSk5SVlpeYmZqio6Slpqeoqaqys7S1tre4ubrCw8TFxsfIycrS09TV" +
        "1tfY2drh4uPk5ebn6Onq8fLz9PX29/j5+v/aAA4EQwBNAFkASwAAPwD9OK+PK+cq/VOrum/8tPw/rXtf7N3/ADMX/bp/7WrE8Sf8" +
        "u3/A/wClUdS/5Z/j/Sr1e11iVSooooooooooooqD7Fbf88v/AB41xP8Awpj4a/8AQt/+Tlx/8XV7+2tT/wCfn/xxf8Kn+23P/PX/" +
        "AMdFPihihz5a4z15rb8N+DPDXhH7T/wjum/ZPtezzv30km7bnb99jjG49PWoLm9ubzb9ol37M4+UDGfp9Kjlmlmx5jZx04qStuoK" +
        "ZRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRX//2Q==";

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
        Run("16", "高性能引擎不设像素上限（3MP 图 + 1MP 上限 → 仍然旋转）", () => Case16(image));
        Run("17", "TIFF（带 [unlimited]）仍可正常加载并旋转", () => Case17(image));
        Run("18", "截断的 TIFF：必须判失败且不挂死", () => Case18(image));
        Run("19", "连续 JPEG：旋转 + 转基线（FFC2→FFC0，DPI 保持）", () => Case19(image));
        Run("20", "竖版连续 JPEG：不旋转也必须转基线", () => Case20(image));
        Run("21", "已是基线的图：不重编码，字节不变", () => Case21(image));
        Run("22", "超幅面跳过的连续 JPEG 也要转基线", () => Case22(image));
        Run("26", "幅面等于上限：四舍五入后不超就不该跳过", () => Case26(image));
        Run("23", "转基线输出结构：SOF0 + DQT≥2 + RST>0 + DPI 保留", () => Case23Test(image));
        Run("24", "CMYK 转基线：APP14 transform=2 + 分量 ID 1..4", () => Case24Test(image));
        Run("25", "写回时目标被占用：退避重试后成功且不留临时文件", () => Case25Test(image));
        Run("27", "中转文件不落在业务目录（全程只有原图一个文件）", () => Case27Test(image));
        Run("28", "RGBA TIFF 旋转后 alpha 仍是 0..255 且 ExtraSamples=2", () => Case28Test(image));
        Run("29", "预乘 alpha(ExtraSamples=1) 旋转后必须仍是 1，SampleFormat 不得凭空出现", () => Case29Test(image));
        Run("30", "独立 alpha(ExtraSamples=2) 旋转后必须仍是 2", () => Case30Test(image));

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
    /// 高性能引擎按用户要求**不设像素上限**：即使 MaxPixels 设成 1 MP，3 MP 的图也必须照转。
    /// （同一个行为在上一版是「缺陷」、现在是「产品决定」，所以断言方向反过来了。）
    /// </summary>
    private static void Case16(IImageService image)
    {
        if (!VipsNative.IsAvailable)
        {
            Check("16", "高性能引擎不设像素上限", true, "跳过：本机 libvips 不可用（该分支走不到）");
            return;
        }

        var path = Path.Combine(_root, "vips_nocap_2000x1500.jpg");
        MakeImage(path, 2000, 1500, PixelFormats.Bgr24, new JpegBitmapEncoder { QualityLevel = 90 });

        // 3 MP 的图 + 1 MP 上限：高性能引擎不看这个上限，必须照转。
        var result = image.Process(path, RotationMode.LibVips, RotateDirection.Clockwise, 1_000_000);
        var probe = image.Probe(path);

        var ok = result.Rotated
                 && result.Action == "Rotated"
                 && probe is { PixelWidth: 1500, PixelHeight: 2000 };

        Check("16", "高性能引擎不设像素上限（3MP + 1MP → 仍然旋转）", ok,
            $"Rotated={result.Rotated} Action={result.Action} " +
            $"尺寸={probe?.PixelWidth}x{probe?.PixelHeight} Message={result.Message}");
    }

    /// <summary>
    /// TIFF 加载会带 <c>[unlimited]</c>（关掉 tiffload 那条 50 MB 累积内存保护）。
    /// 这条用例保证「加了选项之后，普通 TIFF 依然能正常加载并旋转」，不会为了大图把常规路径搞坏。
    /// </summary>
    private static void Case17(IImageService image)
    {
        if (!VipsNative.IsAvailable)
        {
            Check("17", "TIFF 仍可正常加载并旋转", true, "跳过：本机 libvips 不可用（该分支走不到）");
            return;
        }

        var path = Path.Combine(_root, "vips_tiff_800x600.tif");
        MakeImage(path, 800, 600, PixelFormats.Bgra32, new TiffBitmapEncoder());

        var result = image.Process(path, RotationMode.LibVips, RotateDirection.Clockwise, 120_000_000);
        var probe = image.Probe(path);

        var ok = result.Rotated
                 && probe is { PixelWidth: 600, PixelHeight: 800 };

        Check("17", "TIFF（带 [unlimited]）仍可正常加载并旋转", ok,
            $"Rotated={result.Rotated} 尺寸={probe?.PixelWidth}x{probe?.PixelHeight} Message={result.Message}");
    }

    /// <summary>
    /// 截断/损坏文件：libvips 在这类失败上**不一定返回 NULL**，可能给一个非 NULL 的坏图像对象，
    /// 错误只写在 <c>vips_error_buffer</c> 里。以前只判 NULL，于是继续 vips_rot 静默失败、
    /// 再回退 WPF —— 大图就在 WPF 上挂死几分钟。
    /// <para>
    /// 断言两层：
    /// 1) 业务层 <c>Process</c> 必须给出 Fail 结论且不挂死（用户可见的保证）；
    /// 2) 底层 <c>VipsNative.TryRotate</c> 必须报 <c>SourceRejected</c>（本次加固的核心）。
    /// 第二层单独断言是因为截断文件往往在 <c>Probe</c> 阶段就被 WPF 拒掉，
    /// 走不到 vips——那样就测不到这条保护。
    /// </para>
    /// </summary>
    private static void Case18(IImageService image)
    {
        if (!VipsNative.IsAvailable)
        {
            Check("18", "截断的 TIFF 必须判失败", true, "跳过：本机 libvips 不可用（该分支走不到）");
            return;
        }

        var full = Path.Combine(_root, "trunc_source_800x600.tif");
        MakeImage(full, 800, 600, PixelFormats.Bgra32, new TiffBitmapEncoder());

        var bytes = File.ReadAllBytes(full);
        var truncated = Path.Combine(_root, "truncated_800x600.tif");

        // 只留前 1/3：头部还在，像素数据已被截断。
        File.WriteAllBytes(truncated, bytes[..Math.Max(1024, bytes.Length / 3)]);

        // 第 1 层：业务层必须快速给出 Fail。
        var started = DateTime.UtcNow;
        var result = image.Process(truncated, RotationMode.LibVips, RotateDirection.Clockwise, 120_000_000);
        var elapsed = (DateTime.UtcNow - started).TotalMilliseconds;

        // 第 2 层：底层必须分类为「源图不可用」，而不是含糊的 RotateFailed。
        var probeTemp = Path.Combine(_root, "truncated_probe_out.vtmp.tif");
        var outcome = VipsNative.TryRotate(truncated, probeTemp, 0, 0, 0, 90, null, out _, out _);
        var leftovers = File.Exists(probeTemp);

        var ok = !result.Success
                 && result.Action == "Failed"
                 && elapsed < 5000
                 && outcome == VipsNative.VipsRotateOutcome.SourceRejected
                 && !leftovers;

        Check("18", "截断的 TIFF：必须判失败、分类为源图不可用、不挂死", ok,
            $"Process={result.Action} {elapsed:0} ms（{result.Message}） · " +
            $"TryRotate={outcome} · 无残留={!leftovers} · LastError={VipsNative.LastError}");
    }

    /// <summary>
    /// JPEG 兼容模式主路径：横版 progressive → 旋转（必然重编码）+ 转基线。
    /// 断言输出确实是 SOF FFC0（基线），且 DPI 没丢。
    /// </summary>
    private static void Case19(IImageService image)
    {
        if (!VipsNative.IsAvailable)
        {
            Check("19", "连续 JPEG 转基线", true, "跳过：本机 libvips 不可用（该分支走不到）");
            return;
        }

        var path = Path.Combine(_root, "progressive_32x24.jpg");
        File.WriteAllBytes(path, Convert.FromBase64String(ProgressiveJpegBase64));

        var before = JpegProbe.Probe(path);

        var result = image.Process(path, RotationMode.LibVips, RotateDirection.Clockwise,
            DefaultMaxPixels, null, 0, convertProgressiveJpeg: true);

        var after = JpegProbe.Probe(path);
        var probe = image.Probe(path);

        var ok = before.Kind == JpegKind.Progressive
                 && result.Rotated
                 && result.Converted
                 && result.Action == "Rotated+BaselineConverted"
                 && after.Kind == JpegKind.Baseline
                 && probe is { PixelWidth: 24, PixelHeight: 32 }
                 && Math.Abs((probe?.DpiX ?? 0) - 300) < 2;

        Check("19", "连续 JPEG：旋转后为基线（FFC2→FFC0，DPI 保持）", ok,
            $"前={JpegProbe.Describe(before.Kind)} 后={JpegProbe.Describe(after.Kind)} " +
            $"Rotated={result.Rotated} Converted={result.Converted} Action={result.Action} " +
            $"尺寸={probe?.PixelWidth}x{probe?.PixelHeight} dpi={probe?.DpiX:0}");
    }

    /// <summary>
    /// 竖版 progressive：不需要旋转，但用户要的是「能打印」，所以仍然必须转基线。
    /// 这条走的是真正的「只重存不旋转」路径。
    /// </summary>
    private static void Case20(IImageService image)
    {
        if (!VipsNative.IsAvailable)
        {
            Check("20", "竖版连续 JPEG 转基线", true, "跳过：本机 libvips 不可用（该分支走不到）");
            return;
        }

        var path = Path.Combine(_root, "progressive_portrait_24x32.jpg");
        File.WriteAllBytes(path, Convert.FromBase64String(PortraitProgressiveJpegBase64));

        var before = JpegProbe.Probe(path);

        var result = image.Process(path, RotationMode.LibVips, RotateDirection.Clockwise,
            DefaultMaxPixels, null, 0, convertProgressiveJpeg: true);

        var after = JpegProbe.Probe(path);

        var ok = before.Kind == JpegKind.Progressive
                 && !result.Rotated
                 && result.Converted
                 && result.Action == "BaselineConverted"
                 && after.Kind == JpegKind.Baseline
                 && after.Width == 24
                 && after.Height == 32;

        Check("20", "竖版连续 JPEG：不旋转也必须转基线", ok,
            $"前={JpegProbe.Describe(before.Kind)} 后={JpegProbe.Describe(after.Kind)} " +
            $"Rotated={result.Rotated} Converted={result.Converted} Action={result.Action} " +
            $"尺寸={after.Width}x{after.Height}");
    }

    /// <summary>
    /// 边界 bug：界面上限显示为 1 位小数（如 150.0），判定却用未取整的原始厘米值。
    /// 于是 150.02 cm 显示成「150」却按「超过」跳过 —— 用户体验就是「等于上限也不旋转」。
    /// 判据：显示精度下没有超过，就必须允许旋转。
    /// </summary>
    private static void Case26(IImageService image)
    {
        if (!VipsNative.IsAvailable)
        {
            Check("26", "幅面等于上限不跳过", true, "跳过：本机 libvips 不可用");
            return;
        }

        var path = Path.Combine(_root, "size_boundary_24x32.jpg");
        File.WriteAllBytes(path, Convert.FromBase64String(PortraitProgressiveJpegBase64));

        // 24×32 @300dpi ≈ 0.203×0.271 cm；上限设 0.2 —— 原始值略超，但按 1 位小数显示就是 0.2（等于上限）。
        var result = image.Process(path, RotationMode.LibVips, RotateDirection.Clockwise,
            DefaultMaxPixels, null, 0.2, convertProgressiveJpeg: false);

        var ok = result.Action != "SkippedTooBigCm";

        Check("26", "幅面等于上限：四舍五入后不超就不该跳过", ok,
            $"上限=0.2cm 实际≈0.203×0.271cm Action={result.Action}");
    }

    /// <summary>
    /// 用户实测暴露的漏洞：文件被「成品幅面上限」跳过时（SkippedTooBigCm），
    /// 转基线也被一起跳过了。跳过旋转 ≠ 跳过处理——超幅面的连续 JPEG 同样要能打印。
    /// </summary>
    private static void Case22(IImageService image)
    {
        if (!VipsNative.IsAvailable)
        {
            Check("22", "超幅面跳过仍转基线", true, "跳过：本机 libvips 不可用（该分支走不到）");
            return;
        }

        var path = Path.Combine(_root, "progressive_toobig_24x32.jpg");
        File.WriteAllBytes(path, Convert.FromBase64String(PortraitProgressiveJpegBase64));

        var before = JpegProbe.Probe(path);

        // maxSideCm=0.1：24×32 像素 @300dpi ≈ 0.20×0.27 cm，两边都超过 0.1 cm → 走 SkippedTooBigCm
        var result = image.Process(path, RotationMode.LibVips, RotateDirection.Clockwise,
            DefaultMaxPixels, null, 0.1, convertProgressiveJpeg: true);

        var after = JpegProbe.Probe(path);

        var ok = before.Kind == JpegKind.Progressive
                 && !result.Rotated
                 && result.Converted
                 && result.Action == "BaselineConverted"
                 && result.Message != null && result.Message.Contains("超过上限")
                 && after.Kind == JpegKind.Baseline;

        Check("22", "超幅面跳过的连续 JPEG 也要转基线", ok,
            $"前={JpegProbe.Describe(before.Kind)} 后={JpegProbe.Describe(after.Kind)} " +
            $"Converted={result.Converted} Action={result.Action} Message={result.Message}");
    }

    /// <summary>幂等：已经是基线的图，开关打开也不能重新编码（文件字节一个都不能变）。</summary>
    private static void Case21(IImageService image)
    {
        var path = Path.Combine(_root, "progressive_32x24.jpg");
        if (!File.Exists(path))
        {
            Check("21", "已是基线不重编码", false, "前置文件缺失（用例 19 未执行）");
            return;
        }

        var before = Sha256(path);

        var result = image.Process(path, RotationMode.LibVips, RotateDirection.Clockwise,
            DefaultMaxPixels, null, 0, convertProgressiveJpeg: true);

        var after = Sha256(path);
        var kind = JpegProbe.Probe(path).Kind;

        var ok = !result.Converted && kind == JpegKind.Baseline && before == after;

        Check("21", "已是基线的图：不重编码，字节不变", ok,
            $"Converted={result.Converted} 类型={JpegProbe.Describe(kind)} 哈希一致={before == after}");
    }

    /// <summary>JPEG 结构摘要（只解析头部标记，够断言用）。</summary>
    private sealed class JpegStructure
    {
        public int Sof { get; set; } = -1;

        public int[] Ids { get; set; } = Array.Empty<int>();

        public int QuantTables { get; set; }

        public int DhtSegments { get; set; }

        public int Restarts { get; set; }

        public int AdobeTransform { get; set; } = -1;

        public int Dpi { get; set; }

        public bool HasIcc { get; set; }
    }

    private static JpegStructure ParseJpeg(string path)
    {
        var d = File.ReadAllBytes(path);
        var result = new JpegStructure();
        var i = 2;

        while (i < d.Length - 1)
        {
            if (d[i] != 0xFF)
            {
                i++;
                continue;
            }

            int m = d[i + 1];

            if (m == 0x00)
            {
                i += 2;
                continue;
            }

            if (m == 0xD9 || m == 0xDA)
            {
                break;
            }

            if (m == 0xD8 || m == 0x01 || (m >= 0xD0 && m <= 0xD7))
            {
                i += 2;
                continue;
            }

            if (i + 4 > d.Length)
            {
                break;
            }

            var len = (d[i + 2] << 8) | d[i + 3];
            var p = i + 4;

            if (m >= 0xC0 && m <= 0xCF && m != 0xC4 && m != 0xC8 && m != 0xCC && p + 6 < d.Length)
            {
                result.Sof = m;
                var n = d[p + 5];
                var ids = new int[n];
                for (var k = 0; k < n && p + 6 + k * 3 < d.Length; k++)
                {
                    ids[k] = d[p + 6 + k * 3];
                }

                result.Ids = ids;
            }
            else if (m == 0xDB)
            {
                result.QuantTables += (len - 2) / 65;   // 多段要累加，不能覆盖
            }
            else if (m == 0xC4)
            {
                result.DhtSegments++;
            }
            else if (m == 0xE0 && len >= 14 && p + 12 < d.Length && d[p] == 'J' && d[p + 1] == 'F')
            {
                result.Dpi = (d[p + 8] << 8) | d[p + 9];
            }
            else if (m == 0xE2 && p + 3 < d.Length && d[p] == 'I' && d[p + 1] == 'C' && d[p + 2] == 'C')
            {
                result.HasIcc = true;
            }
            else if (m == 0xEE && p + 11 < d.Length && d[p] == 'A' && d[p + 1] == 'd')
            {
                result.AdobeTransform = d[p + 11];
            }

            i += 2 + len;
        }

        for (var k = 0; k < d.Length - 1; k++)
        {
            if (d[k] == 0xFF && d[k + 1] >= 0xD0 && d[k + 1] <= 0xD7)
            {
                result.Restarts++;
            }
        }

        return result;
    }

    /// <summary>
    /// RGB 连续 JPEG 转换后的结构：必须是 SOF0、至少 2 张量化表、有重启标记、DPI 不丢。
    /// （RGB 没有 APP14，YCCK 的结构由用例 24 覆盖。）
    /// </summary>
    private static void Case23Test(IImageService image)
    {
        if (!VipsNative.IsAvailable)
        {
            Check("23", "转基线输出结构", true, "跳过：本机 libvips 不可用（该分支走不到）");
            return;
        }

        // 直接测编码器本身：不走旋转，避免 libvips 重写文件时把 JFIF 密度丢掉（那属于另一处问题了）。
        var src = Path.Combine(_root, "struct_rgb_32x24.jpg");
        File.WriteAllBytes(src, Convert.FromBase64String(ProgressiveJpegBase64));

        var dst = Path.Combine(_root, "struct_rgb_out.jpg");
        var encoded = BaselineJpegEncoder.TryEncode(src, dst, out var error);

        if (!encoded)
        {
            Check("23", "转基线输出结构", false, $"编码失败：{error}");
            return;
        }

        var s = ParseJpeg(dst);
        var ok = s.Sof == 0xC0 && s.QuantTables >= 2 && s.Restarts > 0 && Math.Abs(s.Dpi - 300) <= 1;

        Check("23", "转基线输出结构：SOF0 + DQT>=2 + RST>0 + DPI 保留", ok,
            $"SOF=FF{s.Sof:X2} DQT={s.QuantTables} DHT段={s.DhtSegments} RST={s.Restarts} DPI={s.Dpi}");
    }

    /// <summary>
    /// CMYK 走 LibJpeg 编码器：必须 SOF0 / 分量 ID 1..4 / 2 张量化表 / 有重启标记 / APP14 transform=2（YCCK）。
    /// 这四项正是用户那台老 RIP 认 PS、不认 libvips 的关键差异。
    /// </summary>
    private static void Case24Test(IImageService image)
    {
        if (!VipsNative.IsAvailable)
        {
            Check("24", "CMYK 转基线结构", true, "跳过：本机 libvips 不可用（该分支走不到）");
            return;
        }

        var src = Path.Combine(_root, "struct_cmyk_48x40.jpg");
        File.WriteAllBytes(src, Convert.FromBase64String(CmykJpegBase64));

        var dst = Path.Combine(_root, "struct_cmyk_out.jpg");
        var encoded = BaselineJpegEncoder.TryEncode(src, dst, out var error);

        if (!encoded)
        {
            Check("24", "CMYK 转基线结构", false, $"编码失败：{error}");
            return;
        }

        var s = ParseJpeg(dst);
        var idsOk = s.Ids.Length == 4 && s.Ids[0] == 1 && s.Ids[1] == 2 && s.Ids[2] == 3 && s.Ids[3] == 4;

        var ok = s.Sof == 0xC0 && idsOk && s.QuantTables >= 2 && s.Restarts > 0 && s.AdobeTransform == 2;

        Check("24", "CMYK 转基线：APP14 transform=2 + 分量 ID 1..4", ok,
            $"SOF=FF{s.Sof:X2} IDs=[{string.Join(",", s.Ids)}] DQT={s.QuantTables} RST={s.Restarts} " +
            $"transform={s.AdobeTransform} DHT段={s.DhtSegments}");
    }

    /// <summary>
    /// 模拟「写回时目标文件正被别的程序占用」（用户事故的直接成因）：
    /// 用 FileShare.Read 持有目标文件 → 本进程写不进去 → 写回必须退避重试；
    /// 900ms 后释放，重试应当成功，且不能留下 .vtmp/.btmp 临时文件。
    /// </summary>
    private static void Case25Test(IImageService image)
    {
        var dir = Path.Combine(_root, "writeback");
        Directory.CreateDirectory(dir);

        var path = Path.Combine(dir, "locked_240x120.jpg");
        MakeImage(path, 240, 120, PixelFormats.Bgr24, new JpegBitmapEncoder { QualityLevel = 90 });

        // FileShare.Read：别人可以读，但写会被拒 → 等价于「文件被别的程序打开着」。
        var holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var released = System.Threading.Tasks.Task.Run(() =>
        {
            System.Threading.Thread.Sleep(900);
            holder.Dispose();
        });

        RotationResult result;
        try
        {
            result = image.Process(path, RotationMode.LibVips, RotateDirection.Clockwise, DefaultMaxPixels);
        }
        finally
        {
            released.Wait();
            holder.Dispose();
        }

        var probe = image.Probe(path);
        var leftovers = 0;
        foreach (var file in Directory.GetFiles(dir))
        {
            var name = Path.GetFileName(file);
            if (name.Contains(".vtmp.", StringComparison.Ordinal) || name.Contains(".btmp.", StringComparison.Ordinal))
            {
                leftovers++;
            }
        }

        var ok = result.Rotated
                 && probe is { PixelWidth: 120, PixelHeight: 240 }
                 && leftovers == 0;

        Check("25", "写回时目标被占用：重试后成功且不留临时文件", ok,
            $"Rotated={result.Rotated} 尺寸={probe?.PixelWidth}x{probe?.PixelHeight} " +
            $"Message={result.Message} 残留={leftovers}");
    }

    /// <summary>
    /// 中转文件必须落在系统临时目录：业务目录里如果短暂出现一张「完整可打开的图」，
    /// 打印软件按目录扫描就会把它当新图收走 → 多印一张，这是真实的经济损失。
    /// </summary>
    private static void Case27Test(IImageService image)
    {
        var dir = Path.Combine(_root, "work_temp");
        Directory.CreateDirectory(dir);

        // 先清空目录，保证断言看到的是本次产生的文件。
        foreach (var stale in Directory.GetFiles(dir))
        {
            TryDeleteFile(stale);
        }

        var path = Path.Combine(dir, "temp_check_320x180.jpg");
        MakeImage(path, 320, 180, PixelFormats.Bgr24, new JpegBitmapEncoder { QualityLevel = 90 });

        var before = SnapshotNames(dir);

        var result = image.Process(path, RotationMode.LibVips, RotateDirection.Clockwise, DefaultMaxPixels);

        var after = SnapshotNames(dir);

        // 业务目录里从头到尾只能有原图一个文件。
        var onlyOriginal = after.Count == 1
                           && after[0] == Path.GetFileName(path);
        var sameAsBefore = before.Count == after.Count;

        // 中转路径必须指向 %TEMP%\PivotPic\work。
        var workTemp = ImageService.GetWorkTempPath(path, ".vtmp", ".jpg");
        var tempRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PivotPic", "work"));
        var inTemp = Path.GetFullPath(workTemp).StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase);

        var ok = result.Rotated && onlyOriginal && sameAsBefore && inTemp;

        Check("27", "中转文件不落在业务目录", ok,
            $"Rotated={result.Rotated} 目录文件=[{string.Join(",", after)}] 中转路径在临时目录={inTemp}");
    }

    /// <summary>
    /// RGBA TIFF（真的带透明像素）旋转后必须仍是 4 通道 + ExtraSamples=2 + alpha 含 0。
    /// alpha 丢了 → 打印出来就没有透明度，属于会直接造成经济损失的错误。
    /// </summary>
    private static void Case28Test(IImageService image)
    {
        var dir = Path.Combine(_root, "tiff_alpha");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "rgba_3000x2000.tif");

        MakeRgbaTiff(path, 3000, 2000);

        var beforeStructure = ImageService.ProbeTiffStructure(path);
        var beforeAlpha = ReadAlphaRange(path);

        var result = image.Process(path, RotationMode.LibVips, RotateDirection.Clockwise, DefaultMaxPixels);

        var afterStructure = ImageService.ProbeTiffStructure(path);
        var afterAlpha = ReadAlphaRange(path);
        var afterProbe = image.Probe(path);

        var structureOk = beforeStructure is not null && afterStructure is not null
                          && afterStructure.Value.SamplesPerPixel == beforeStructure.Value.SamplesPerPixel
                          && afterStructure.Value.Photometric == beforeStructure.Value.Photometric
                          && afterStructure.Value.ExtraSamples.Length > 0
                          && afterStructure.Value.ExtraSamples[0] == 2;

        var alphaOk = beforeAlpha.min == 0 && afterAlpha.min == 0
                      && beforeAlpha.max == 255 && afterAlpha.max == 255;

        var ok = result.Rotated && structureOk && alphaOk
                 && afterProbe is { PixelWidth: 2000, PixelHeight: 3000 };

        Check("28", "RGBA TIFF 旋转后 alpha 仍为 0..255 且 ExtraSamples=2", ok,
            $"Rotated={result.Rotated} 源=SPP{beforeStructure?.SamplesPerPixel}/P{beforeStructure?.Photometric}" +
            $"/Extra[{string.Join(",", beforeStructure?.ExtraSamples ?? Array.Empty<int>())}] alpha={beforeAlpha.min}..{beforeAlpha.max}" +
            $" → 输出=SPP{afterStructure?.SamplesPerPixel}/P{afterStructure?.Photometric}" +
            $"/Extra[{string.Join(",", afterStructure?.ExtraSamples ?? Array.Empty<int>())}] alpha={afterAlpha.min}..{afterAlpha.max}");
    }

    /// <summary>生成一张真正带透明的 RGBA TIFF：中间一块 alpha=0，四周 alpha=255。</summary>
    private static void MakeRgbaTiff(string path, int width, int height)
    {
        const int bytesPerPixel = 4;
        var stride = width * bytesPerPixel;
        var pixels = new byte[stride * height];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = y * stride + x * bytesPerPixel;
                pixels[offset] = 40;      // B
                pixels[offset + 1] = 60;  // G
                pixels[offset + 2] = 200; // R
                pixels[offset + 3] = 255; // A
            }
        }

        // 中间挖一块全透明的洞（1000×1000）。
        for (var y = height / 4; y < height / 4 + Math.Min(1000, height / 2); y++)
        {
            for (var x = width / 3; x < width / 3 + Math.Min(1000, width / 2); x++)
            {
                var offset = y * stride + x * bytesPerPixel;
                pixels[offset] = 0;
                pixels[offset + 1] = 0;
                pixels[offset + 2] = 0;
                pixels[offset + 3] = 0;
            }
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        var encoder = new TiffBitmapEncoder { Compression = TiffCompressOption.Lzw };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var output = File.Create(path);
        encoder.Save(output);
    }

    /// <summary>读图片 alpha 通道的最小/最大值。</summary>
    private static (int min, int max) ReadAlphaRange(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];

        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var buffer = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(buffer, stride, 0);

        var min = 255;
        var max = 0;

        for (var i = 3; i < buffer.Length; i += 4)
        {
            var a = buffer[i];
            if (a < min) min = a;
            if (a > max) max = a;
        }

        return (min, max);
    }

    private static List<string> SnapshotNames(string dir)
    {
        var list = new List<string>();
        foreach (var file in Directory.GetFiles(dir))
        {
            list.Add(Path.GetFileName(file));
        }

        list.Sort(StringComparer.Ordinal);
        return list;
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch (Exception) { }
    }

    /// <summary>
    /// 预乘 alpha（ExtraSamples=1，ASSOCIATED）旋转后必须**仍是 1**。
    /// libvips 会写成 2，而 RIP 按标签解释同一份像素数据：改了标签就等于换了一张图，
    /// 透明区会被当成有颜色的像素印出来——这是用户「打印时没有透明度」的真凶。
    /// </summary>
    private static void Case29Test(IImageService image)
    {
        RunAlphaTagCase("29", image, expectedExtra: 1);
    }

    /// <summary>独立 alpha（ExtraSamples=2，UNASSOCIATED）旋转后必须仍是 2。</summary>
    private static void Case30Test(IImageService image)
    {
        RunAlphaTagCase("30", image, expectedExtra: 2);
    }

    /// <summary>构造指定 ExtraSamples 的 RGBA TIFF，旋转后校验标签逐值不变、SampleFormat 不得凭空出现。</summary>
    private static void RunAlphaTagCase(string caseId, IImageService image, int expectedExtra)
    {
        var dir = Path.Combine(_root, "alpha_tag_" + caseId);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"rgba_extra{expectedExtra}_3000x2000.tif");

        MakeRgbaTiff(path, 3000, 2000);

        // WPF 写出来恒定 ExtraSamples=2 / SampleFormat 全 1，这里改成目标语义。
        if (!TiffTagWriter.TryApply(path, 338, new[] { expectedExtra }, out var applyError))
        {
            Check(caseId, "alpha 标签用例", false, $"构造失败：{applyError}");
            return;
        }

        // 源图就没有 SampleFormat，输出也不许凭空出现。
        TiffTagWriter.TryApply(path, 339, null, out _);

        var before = ImageService.ProbeTiffStructure(path);

        var result = image.Process(path, RotationMode.LibVips, RotateDirection.Clockwise, DefaultMaxPixels);

        var after = ImageService.ProbeTiffStructure(path);
        var afterAlpha = ReadAlphaRange(path);

        var tagsOk = before is not null && after is not null
                     && after.Value.SamplesPerPixel == before.Value.SamplesPerPixel
                     && after.Value.Photometric == before.Value.Photometric
                     && after.Value.ExtraSamples.Length == 1
                     && after.Value.ExtraSamples[0] == expectedExtra
                     && after.Value.ExtraSamples[0] == before.Value.ExtraSamples[0];

        // SampleFormat(339) 源图没有 → 输出也不该有（结构里的 BitsPerSampleCount 只记数量，单独用 ProbeTiffStructure 看不到 339，
        // 所以这里用 TiffTagWriter 直接读）。
        var sampleFormat = TiffTagWriter.ReadShortArray(path, 339);
        var sampleFormatOk = sampleFormat is null;

        var ok = result.Rotated && tagsOk && sampleFormatOk && afterAlpha.min == 0 && afterAlpha.max == 255;

        Check(caseId, $"ExtraSamples={expectedExtra} 旋转后保持不变", ok,
            $"Rotated={result.Rotated} 源Extra=[{string.Join(",", before?.ExtraSamples ?? Array.Empty<int>())}]" +
            $" → 输出Extra=[{string.Join(",", after?.ExtraSamples ?? Array.Empty<int>())}]" +
            $" SampleFormat={(sampleFormat is null ? "无" : string.Join(",", sampleFormat))} alpha={afterAlpha.min}..{afterAlpha.max}");
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
