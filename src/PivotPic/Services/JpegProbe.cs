using System;
using System.IO;

namespace PivotPic.Services;

/// <summary>JPEG 的编码类型（按 SOF 标记判定）。</summary>
public enum JpegKind
{
    /// <summary>不是 JPEG（签名不对）。</summary>
    NotJpeg,

    /// <summary>读不出 SOF（文件残缺/损坏）。</summary>
    Unreadable,

    /// <summary>基线（FFC0/FFC1）——打印 RIP 唯一普遍支持的类型，无需处理。</summary>
    Baseline,

    /// <summary>连续/渐进（FFC2）——需要转基线。</summary>
    Progressive,

    /// <summary>算术编码（FFC9/FFCA）——罕见，同样按「需转换」处理。</summary>
    Arithmetic
}

/// <summary>一次 JPEG 头部探测的结果。</summary>
public sealed class JpegInfo
{
    public JpegKind Kind { get; init; } = JpegKind.Unreadable;

    public int Width { get; init; }

    public int Height { get; init; }

    /// <summary>分量数：1=灰度，3=YCbCr，4=CMYK/YCCK。</summary>
    public int Components { get; init; }

    /// <summary>第一个分量（亮度）的水平采样因子。</summary>
    public int SamplingH { get; init; } = 1;

    /// <summary>第一个分量（亮度）的垂直采样因子。</summary>
    public int SamplingV { get; init; } = 1;

    /// <summary>是否为 CMYK（4 分量）。CMYK JPEG 用 WPF 编码器会出色彩错误，只能走 libvips。</summary>
    public bool IsCmyk => Components == 4;

    /// <summary>是否需要转成基线。</summary>
    public bool NeedsConversion => Kind is JpegKind.Progressive or JpegKind.Arithmetic;
}

/// <summary>
/// 只读 JPEG 头部来判定编码类型：扫描到 SOF 段就停，不解码像素，几毫秒级。
/// <para>
/// 为什么不复用 WPF/vips：两者都要把图读进来才知道，而这里只需要一个标记字节；
/// 而且「是否连续」必须在重编码之前就知道，否则等于白白重压一遍。
/// </para>
/// </summary>
public static class JpegProbe
{
    /// <summary>段长度字段是 2 字节，单段最大 65535；这里给头部扫描一个总上限，避免异常文件把读取拖长。</summary>
    private const int MaxHeaderBytes = 4 * 1024 * 1024;

    /// <summary>探测指定文件；任何异常都落到 <see cref="JpegKind.Unreadable"/>，不上抛。</summary>
    public static JpegInfo Probe(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            if (stream.ReadByte() != 0xFF || stream.ReadByte() != 0xD8)
            {
                return new JpegInfo { Kind = JpegKind.NotJpeg };
            }

            long budget = MaxHeaderBytes;

            while (budget > 0)
            {
                var marker = ReadMarker(stream);
                if (marker < 0)
                {
                    return new JpegInfo { Kind = JpegKind.Unreadable };
                }

                // 无长度字段的独立标记：SOI/EOI/RSTn/TEM。
                if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
                {
                    continue;
                }

                if (marker == 0xD9)
                {
                    return new JpegInfo { Kind = JpegKind.Unreadable };
                }

                var length = ReadBigEndianInt16(stream);
                if (length < 2)
                {
                    return new JpegInfo { Kind = JpegKind.Unreadable };
                }

                var payload = length - 2;

                if (IsStartOfFrame(marker))
                {
                    return ParseFrame(stream, marker);
                }

                // SOS 之前没有 SOF，说明头部不完整。
                if (marker == 0xDA)
                {
                    return new JpegInfo { Kind = JpegKind.Unreadable };
                }

                stream.Seek(payload, SeekOrigin.Current);

                if (stream.Position >= stream.Length)
                {
                    return new JpegInfo { Kind = JpegKind.Unreadable };
                }

                budget -= payload + 4;
            }

            return new JpegInfo { Kind = JpegKind.Unreadable };
        }
        catch (Exception)
        {
            return new JpegInfo { Kind = JpegKind.Unreadable };
        }
    }

    /// <summary>把类型翻译成日志/界面可读的说明。</summary>
    public static string Describe(JpegKind kind) => kind switch
    {
        JpegKind.Baseline => "基线",
        JpegKind.Progressive => "连续（progressive）",
        JpegKind.Arithmetic => "连续（算术编码）",
        JpegKind.NotJpeg => "非 JPEG",
        _ => "无法识别"
    };

    /// <summary>读下一个标记字节（自动跳过 0xFF 填充）。返回 -1 表示到达文件尾。</summary>
    private static int ReadMarker(Stream stream)
    {
        int value;

        do
        {
            value = stream.ReadByte();
            if (value < 0)
            {
                return -1;
            }
        }
        while (value != 0xFF);

        do
        {
            value = stream.ReadByte();
            if (value < 0)
            {
                return -1;
            }
        }
        while (value == 0xFF);

        return value;
    }

    private static int ReadBigEndianInt16(Stream stream)
    {
        var hi = stream.ReadByte();
        var lo = stream.ReadByte();
        return hi < 0 || lo < 0 ? -1 : (hi << 8) | lo;
    }

    /// <summary>SOF 段：C0–CF，排除 C4(DHT)/C8(JPG)/CC(DAC)。</summary>
    private static bool IsStartOfFrame(int marker)
    {
        return marker >= 0xC0 && marker <= 0xCF
               && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
    }

    private static JpegInfo ParseFrame(Stream stream, int marker)
    {
        var precision = stream.ReadByte();
        if (precision < 0)
        {
            return new JpegInfo { Kind = JpegKind.Unreadable };
        }

        var height = ReadBigEndianInt16(stream);
        var width = ReadBigEndianInt16(stream);
        var components = stream.ReadByte();

        if (height < 0 || width < 0 || components <= 0)
        {
            return new JpegInfo { Kind = JpegKind.Unreadable };
        }

        var samplingH = 1;
        var samplingV = 1;

        for (var i = 0; i < components; i++)
        {
            var id = stream.ReadByte();
            var sampling = stream.ReadByte();
            var quant = stream.ReadByte();

            if (id < 0 || sampling < 0 || quant < 0)
            {
                return new JpegInfo { Kind = JpegKind.Unreadable };
            }

            if (i == 0)
            {
                samplingH = (sampling >> 4) & 0x0F;
                samplingV = sampling & 0x0F;
            }
        }

        return new JpegInfo
        {
            Kind = marker switch
            {
                0xC0 or 0xC1 => JpegKind.Baseline,
                0xC2 => JpegKind.Progressive,
                0xC9 or 0xCA => JpegKind.Arithmetic,

                // 无损(C3)/差分(C5-C7)/算术差分(CB/CD-CF)：都属「非基线」，一律按需转换处理。
                _ => JpegKind.Progressive
            },
            Width = width,
            Height = height,
            Components = components,
            SamplingH = samplingH,
            SamplingV = samplingV
        };
    }
}
