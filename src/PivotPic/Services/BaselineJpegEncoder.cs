using System;
using System.Collections.Generic;
using System.IO;
using BitMiracle.LibJpeg.Classic;

namespace PivotPic.Services;

/// <summary>
/// 用 LibJpeg（纯托管 libjpeg 移植）把图重编码成「打印 RIP 认得」的基线 JPEG。
/// <para>
/// 为什么不用 libvips 的 jpegsave：它能出基线，但结构上跟 Photoshop 存的差三处——
/// 只写 1 张量化表、用标准霍夫曼表、APP14 写 transform=0(CMYK)。
/// 用户那台老 RIP 只认 PS 那套结构（2 张量化表 / optimize 的 DHT / YCCK transform=2），
/// 所以我们自己控制编码器参数，逐项对齐。
/// </para>
/// <para>
/// 像素仍然由 libvips 解码（CMYK、大图、ICC 都靠它），这里只负责「编码」这一段。
/// </para>
/// </summary>
public static class BaselineJpegEncoder
{
    /// <summary>质量 95：与 libvips 路径保持一致，避免二次压缩肉眼可见。</summary>
    private const int Quality = 95;

    /// <summary>
    /// libjpeg 的标准色度量化表（jcparam.c 的 std_chroma_quant）。
    /// 有了它才能产出「2 张量化表」——PS 存出来的就是这样。
    /// </summary>
    private static readonly int[] StandardChromaQuant =
    {
        17, 18, 24, 47, 99, 99, 99, 99,
        18, 21, 26, 66, 99, 99, 99, 99,
        24, 26, 56, 99, 99, 99, 99, 99,
        47, 66, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99
    };

    /// <summary>
    /// 把 <paramref name="sourcePath"/> 重编码成基线 JPEG 写到 <paramref name="targetPath"/>。
    /// </summary>
    /// <param name="sourcePath">源文件（任意 JPEG）。</param>
    /// <param name="targetPath">输出路径（调用方负责临时文件与写回）。</param>
    /// <param name="error">失败原因。</param>
    public static bool TryEncode(string sourcePath, string targetPath, out string error)
    {
        error = string.Empty;

        if (!VipsNative.TryReadPixels(sourcePath, out var raw, out error) || raw is null)
        {
            return false;
        }

        var metadata = JpegProbe.ReadMetadata(sourcePath);

        try
        {
            using var stream = new FileStream(targetPath, FileMode.Create, FileAccess.Write,
                FileShare.None, 1 << 20, FileOptions.SequentialScan);

            var cinfo = new jpeg_compress_struct();

            cinfo.Image_width = raw.Width;
            cinfo.Image_height = raw.Height;
            cinfo.Input_components = raw.Bands;

            var isCmyk = raw.Bands == 4;

            cinfo.In_color_space = raw.Bands switch
            {
                4 => J_COLOR_SPACE.JCS_CMYK,
                3 => J_COLOR_SPACE.JCS_RGB,
                _ => J_COLOR_SPACE.JCS_GRAYSCALE
            };

            cinfo.jpeg_stdio_dest(stream);
            cinfo.jpeg_set_defaults();

            // CMYK 走 YCCK：输出 APP14 transform=2，这正是 PS 的做法，也是用户 RIP 认的那一种。
            if (isCmyk)
            {
                cinfo.jpeg_set_colorspace(J_COLOR_SPACE.JCS_YCCK);
            }

            cinfo.jpeg_set_quality(Quality, true);

            // 第二张量化表（色度）：PS 输出带 2 张，只写 1 张会被 RIP 拒。
            // 单分量（灰度）没有色度分量，跳过。
            if (raw.Bands >= 3)
            {
                cinfo.jpeg_add_quant_table(1, StandardChromaQuant,
                    jpeg_compress_struct.jpeg_quality_scaling(Quality), true);

                for (var i = 1; i < raw.Bands; i++)
                {
                    cinfo.Component_info[i].Quant_tbl_no = 1;
                }
            }

            // 采样因子全部 1×1：源就是无子采样的 CMYK，PS 存的也是 0x11×4。
            // libjpeg 的 JCS_YCCK 默认会给 Y/K 设 2×2，那样输出会被降采样，画质和结构都对不上。
            for (var i = 0; i < cinfo.Num_components; i++)
            {
                cinfo.Component_info[i].H_samp_factor = 1;
                cinfo.Component_info[i].V_samp_factor = 1;

            }

            // 优化霍夫曼表（按实际图像统计生成），而不是标准表。
            cinfo.Optimize_coding = true;

            // 每 MCU 行一个重启点：DRI = ceil(宽/8)，PS 是 493（3937 宽）。
            cinfo.Restart_interval = (raw.Width + 7) / 8;

            // 4 分量不写 JFIF（PS 也没有），DPI 交给 APP1/APP13 里的 XMP 携带；
            // 3 分量且有 JFIF 密度时按原值写回。
            cinfo.Write_JFIF_header = !isCmyk && metadata.DpiX > 0;

            if (metadata.DpiX > 0 && metadata.DpiY > 0)
            {
                cinfo.Density_unit = DensityUnit.DotsInch;
                cinfo.X_density = (short)Math.Min(metadata.DpiX, short.MaxValue);
                cinfo.Y_density = (short)Math.Min(metadata.DpiY, short.MaxValue);
            }

            cinfo.jpeg_start_compress(true);

            // 原样搬 APP1(XMP/EXIF)、APP2(ICC)、APP13(Photoshop IRB)。
            // APP14 由 libjpeg 自己写（transform=2），照抄反而会把 transform 写错。
            foreach (var segment in metadata.Segments)
            {
                cinfo.jpeg_write_marker(segment.Marker, segment.Payload);
            }

            var stride = raw.Width * raw.Bands;
            var rowBuffer = new byte[stride];
            var rows = new byte[1][];

            for (var y = 0; y < raw.Height; y++)
            {
                Buffer.BlockCopy(raw.Data, y * stride, rowBuffer, 0, stride);
                rows[0] = rowBuffer;

                if (cinfo.jpeg_write_scanlines(rows, 1) != 1)
                {
                    error = "写入扫描行失败";
                    return false;
                }
            }

            cinfo.jpeg_finish_compress();

            // 必须先关掉文件句柄：重打包是「读全文 → 改写 → 覆盖写」，
            // 流还开着的话，方法退出时的 Flush/Dispose 会把我们改好的内容覆盖回去。
            stream.Flush();
            stream.Dispose();

            // libjpeg 会把每张量化表/霍夫曼表各写成一个标记段（2 个 DQT + 4 个 DHT），
            // 而 Photoshop 是把全部表塞进「一个 DQT 段 + 一个 DHT 段」。老打印 RIP 往往只读
            // 第一个 DQT/DHT 段，于是只能拿到 1 张表，整份图直接拒收——这是用户反复失败的关键。
            // 编码完做一次纯字节重打包：合并 DQT/DHT，并把 APP14 的 flags0 对齐成 PS 的 0x4000。
            RepackMarkersForPrint(targetPath);

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 重打包头部标记：把多段 DQT / DHT 各自合并成一段，并把 Adobe APP14 的 flags0 对齐成 0x4000。
    /// <para>
    /// 只动头部字节，熵编码数据一字不改，所以画质与文件大小几乎不变。
    /// 目标结构（= Photoshop 存的基线）：
    /// <c>1 个 DQT 段（2 张表，132 字节）/ 1 个 DHT 段（4 张表，约 418 字节）/ APP14 = 41..64 40 00 00 00 02</c>。
    /// </para>
    /// </summary>
    private static void RepackMarkersForPrint(string path)
    {
        byte[] data;

        try
        {
            data = File.ReadAllBytes(path);
        }
        catch (Exception)
        {
            return;
        }

        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
        {
            return;
        }

        // 1) 扫头部，收集 DQT / DHT 的 payload 并记录首次出现的位置。
        var markers = new List<(int Marker, byte[] Payload)>();
        var scan = 2;
        var sosIndex = data.Length;

        while (scan + 3 < data.Length)
        {
            if (data[scan] != 0xFF)
            {
                break;
            }

            var marker = data[scan + 1];

            if (marker == 0xD9)
            {
                sosIndex = scan;
                break;
            }

            if (marker == 0xDA)
            {
                sosIndex = scan;
                break;
            }

            if (marker == 0xD8 || (marker >= 0xD0 && marker <= 0xD7))
            {
                scan += 2;
                continue;
            }

            var length = (data[scan + 2] << 8) | data[scan + 3];
            if (length < 2 || scan + 2 + length > data.Length)
            {
                break;
            }

            var payload = new byte[length - 2];
            Buffer.BlockCopy(data, scan + 4, payload, 0, payload.Length);
            markers.Add((marker, payload));

            scan += 2 + length;
        }

        var dqt = new List<byte>();
        var dht = new List<byte>();
        var firstDqt = -1;
        var firstDht = -1;

        for (var i = 0; i < markers.Count; i++)
        {
            if (markers[i].Marker == 0xDB)
            {
                if (firstDqt < 0)
                {
                    firstDqt = i;
                }

                dqt.AddRange(markers[i].Payload);
            }
            else if (markers[i].Marker == 0xC4)
            {
                if (firstDht < 0)
                {
                    firstDht = i;
                }

                dht.AddRange(markers[i].Payload);
            }
        }

        // 2) 重建头部。
        var output = new List<byte>(data.Length + 32) { 0xFF, 0xD8 };

        for (var i = 0; i < markers.Count; i++)
        {
            var (marker, payload) = markers[i];

            if (marker == 0xDB)
            {
                if (i == firstDqt && dqt.Count > 0)
                {
                    AppendSegment(output, 0xDB, dqt);
                }

                continue;
            }

            if (marker == 0xC4)
            {
                if (i == firstDht && dht.Count > 0)
                {
                    AppendSegment(output, 0xC4, dht);
                }

                continue;
            }

            if (marker == 0xEE && payload.Length >= 12
                && payload[0] == 'A' && payload[1] == 'd' && payload[2] == 'o')
            {
                // PS 的 APP14：Adobe + version 0x0064 + flags0 0x4000 + flags1 0x0000 + transform 2
                AppendSegment(output, 0xEE, new byte[]
                {
                    0x41, 0x64, 0x6F, 0x62, 0x65, 0x00,
                    0x64, 0x40, 0x00, 0x00, 0x00, 0x02
                });

                continue;
            }

            AppendSegment(output, marker, payload);
        }

        // 3) 原样接上 SOS 之后的熵编码数据。
        for (var i = sosIndex; i < data.Length; i++)
        {
            output.Add(data[i]);
        }

        try
        {
            File.WriteAllBytes(path, output.ToArray());
        }
        catch (Exception)
        {
            // 重打包失败不影响「至少是个合法基线」，保持原文件即可。
        }
    }

    /// <summary>追加一个带长度字段的标记段。</summary>
    private static void AppendSegment(List<byte> output, int marker, IReadOnlyList<byte> payload)
    {
        var length = payload.Count + 2;

        output.Add(0xFF);
        output.Add((byte)marker);
        output.Add((byte)(length >> 8));
        output.Add((byte)(length & 0xFF));

        for (var i = 0; i < payload.Count; i++)
        {
            output.Add(payload[i]);
        }
    }
}
