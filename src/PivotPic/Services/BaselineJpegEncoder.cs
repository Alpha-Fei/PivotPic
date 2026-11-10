using System;
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

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
