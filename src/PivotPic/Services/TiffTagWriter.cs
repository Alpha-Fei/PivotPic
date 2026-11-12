using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PivotPic.Services;

/// <summary>
/// TIFF 标签字节级读写（只处理第一个 IFD）。
/// <para>
/// 为什么需要它：libvips 写 TIFF 时会把 <c>ExtraSamples</c> 恒定写成 <c>2</c>（独立 alpha），
/// 而打印厂的预乘 alpha 源图是 <c>1</c>（ASSOCIATED）。像素数据没变、Photoshop 看起来也一样，
/// 但 RIP 按标签解释同一份像素——标签一改，透明区就被当成有颜色的像素印出来。
/// 所以 alpha 语义标签必须**原样保留**，写完要改回去。
/// </para>
/// <para>只改标签字节，绝不碰像素数据；预乘数据保持预乘。</para>
/// </summary>
public static class TiffTagWriter
{
    private const ushort TypeShort = 3;
    private const ushort TypeLong = 4;

    /// <summary>读第一个 IFD 里指定 SHORT 型标签的全部值；标签不存在返回 null。</summary>
    /// <param name="path">TIFF 文件。</param>
    /// <param name="tag">标签号（338=ExtraSamples，339=SampleFormat）。</param>
    public static IReadOnlyList<int>? ReadShortArray(string path, int tag)
    {
        try
        {
            if (!TryReadIfd(path, out var header, out var entries))
            {
                return null;
            }

            var entry = entries.FirstOrDefault(e => e.Tag == tag);
            return entry.Tag == 0 ? null : entry.ShortValues;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 把第一个 IFD 里的某个标签改成指定值；<paramref name="values"/> 为 null 表示删除该标签。
    /// 改完的 IFD 追加到文件末尾并把头部 IFD0 指针指过去，这样原 IFD 区与像素数据一律不动。
    /// </summary>
    /// <param name="path">要修改的 TIFF 文件。</param>
    /// <param name="tag">标签号。</param>
    /// <param name="values">新值；null 表示删除。</param>
    /// <param name="error">失败原因。</param>
    public static bool TryApply(string path, int tag, IReadOnlyList<int>? values, out string error)
    {
        error = string.Empty;

        try
        {
            if (!TryReadIfd(path, out var header, out var entries))
            {
                error = "无法解析 TIFF 头部/IFD";
                return false;
            }

            var existing = entries.FirstOrDefault(e => e.Tag == tag);
            var already = existing.Tag != 0
                          && existing.ShortValues.Count == values?.Count
                          && (values is null || existing.ShortValues.SequenceEqual(values));

            if (already)
            {
                // 已经一致就不用动文件。
                return true;
            }

            if (values is null)
            {
                // 删除：不再写入该条目即可。
                entries.RemoveAll(e => e.Tag == tag);
            }
            else if (existing.Tag != 0)
            {
                // 改值：类型不变，计数与数据一起更新。
                // RawValueBytes 必须清空——它代表「原 4 字节 value/offset」，留着会被原样抄回去，等于没改。
                entries[entries.FindIndex(e => e.Tag == tag)] =
                    existing with
                    {
                        Count = (uint)values.Count,
                        ShortValues = values.ToList(),
                        RawValueBytes = Array.Empty<byte>()
                    };
            }
            else
            {
                // 新增：只允许 SHORT 类型，够用（338/339 都是 SHORT）。
                entries.Add(new IfdEntry(tag, TypeShort, (uint)values.Count, values.ToList(), Array.Empty<byte>()));
            }

            RewriteIfd(path, header, entries);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 把 <paramref name="outputPath"/> 的 alpha 语义标签（ExtraSamples / SampleFormat）
    /// 改回与 <paramref name="sourcePath"/> 完全一致。
    /// </summary>
    /// <param name="sourcePath">源图（取权威标签值）。</param>
    /// <param name="outputPath">刚被 libvips 写出来的文件，就地修正。</param>
    /// <param name="error">失败原因。</param>
    public static bool TryRestoreAlphaTags(string sourcePath, string outputPath, out string error)
    {
        error = string.Empty;

        // 262=PhotometricInterpretation, 338=ExtraSamples, 339=SampleFormat
        // 三个标签必须与源图逐值一致，否则 RIP 按错误语义解释像素数据（颜色全错）。
        foreach (var tag in new[] { 262, 338, 339 })
        {
            var sourceValues = ReadShortArray(sourcePath, tag);

            if (!TryApply(outputPath, tag, sourceValues, out error))
            {
                return false;
            }
        }

        return true;
    }

    // ==================== 内部实现 ====================

    private readonly record struct IfdEntry(
        int Tag,
        ushort Type,
        uint Count,
        List<int> ShortValues,
        byte[] RawValueBytes);

    private readonly record struct TiffHeader(bool LittleEndian, long IfdOffset);

    private static bool TryReadIfd(string path, out TiffHeader header, out List<IfdEntry> entries)
    {
        header = default;
        entries = new List<IfdEntry>();

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096);

        if (stream.Length < 8)
        {
            return false;
        }

        var order = new byte[2];
        if (stream.Read(order, 0, 2) != 2)
        {
            return false;
        }

        var little = order[0] == 'I' && order[1] == 'I';
        if (!little && !(order[0] == 'M' && order[1] == 'M'))
        {
            return false;
        }

        var magic = ReadU16(stream, little);
        if (magic != 42)
        {
            return false;
        }

        var ifdOffset = ReadU32(stream, little);
        if (ifdOffset <= 0 || ifdOffset >= stream.Length)
        {
            return false;
        }

        header = new TiffHeader(little, ifdOffset);
        stream.Seek(ifdOffset, SeekOrigin.Begin);

        var count = ReadU16(stream, little);
        if (count == 0 || ifdOffset + 2 + count * 12L > stream.Length)
        {
            return false;
        }

        for (var i = 0; i < count; i++)
        {
            var raw = new byte[12];
            if (stream.Read(raw, 0, 12) != 12)
            {
                return false;
            }

            var tag = little ? raw[0] | (raw[1] << 8) : (raw[0] << 8) | raw[1];
            var type = little ? (ushort)(raw[2] | (raw[3] << 8)) : (ushort)((raw[2] << 8) | raw[3]);
            var valueCount = BitConverter.ToUInt32(little ? raw[4..8] : raw[4..8].Reverse().ToArray(), 0);
            var valueBytes = raw[8..12];

            entries.Add(new IfdEntry(tag, type, valueCount, ReadShortValues(stream, little, type, valueCount, valueBytes), valueBytes));
        }

        return true;
    }

    private static List<int> ReadShortValues(Stream stream, bool little, ushort type, uint count, byte[] valueBytes)
    {
        var values = new List<int>();

        if (type != TypeShort && type != TypeLong)
        {
            return values;
        }

        var unit = type == TypeShort ? 2 : 4;
        var total = (long)count * unit;

        if (total <= 4)
        {
            for (var i = 0; i < count; i++)
            {
                values.Add(type == TypeShort
                    ? ReadU16At(valueBytes, i * 2, little)
                    : (int)ReadU32At(valueBytes, i * 4, little));
            }

            return values;
        }

        var offset = ReadU32At(valueBytes, 0, little);
        if (offset + total > stream.Length)
        {
            return values;
        }

        var back = stream.Position;
        stream.Seek(offset, SeekOrigin.Begin);

        for (var i = 0; i < count; i++)
        {
            values.Add(type == TypeShort ? ReadU16(stream, little) : (int)ReadU32(stream, little));
        }

        stream.Seek(back, SeekOrigin.Begin);
        return values;
    }

    /// <summary>把 IFD 重新写到文件末尾并更新头部指针，原 IFD 与像素数据不动。</summary>
    private static void RewriteIfd(string path, TiffHeader header, List<IfdEntry> entries)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var little = header.LittleEndian;
        var newIfdOffset = stream.Length;

        // 1) 先写「需要外置」的值（计数超过 4 字节的 SHORT 数组）。
        var externalValues = new List<(IfdEntry Entry, long Offset)>();

        foreach (var entry in entries)
        {
            // 只有「值被改动且放不进 4 字节」的条目才需要外置；其余保留原指针。
            if (entry.RawValueBytes.Length == 4 || entry.Type != TypeShort || entry.Count * 2 <= 4)
            {
                continue;
            }

            var offset = stream.Length;
            stream.Seek(offset, SeekOrigin.Begin);

            foreach (var v in entry.ShortValues)
            {
                WriteU16(stream, little, (ushort)v);
            }

            externalValues.Add((entry, offset));
        }

        // 2) 写新 IFD。
        //    **关键**：没改动过的条目必须把原来的 4 字节 value/offset 原样抄过去。
        //    StripOffsets / StripByteCounts / XMP 这类条目是「指针到外部数据」，
        //    一旦重写成 0 就等于把整个文件写坏（踩过一次）。
        stream.Seek(newIfdOffset, SeekOrigin.Begin);
        WriteU16(stream, little, (ushort)entries.Count);

        foreach (var entry in entries)
        {
            var raw = new byte[12];

            WriteU16Raw(raw, 0, little, (ushort)entry.Tag);
            WriteU16Raw(raw, 2, little, entry.Type);
            WriteU32Raw(raw, 4, little, entry.Count);

            if (entry.RawValueBytes.Length == 4)
            {
                // 未改动（或改动时沿用原值区）：字节级照抄，指针保持有效。
                Buffer.BlockCopy(entry.RawValueBytes, 0, raw, 8, 4);
            }
            else if (entry.Type == TypeShort && entry.Count * 2 <= 4 && entry.ShortValues.Count >= entry.Count)
            {
                for (var i = 0; i < entry.Count && i < 2; i++)
                {
                    WriteU16Raw(raw, 8 + i * 2, little, (ushort)entry.ShortValues[i]);
                }
            }
            else if (entry.RawValueBytes.Length > 0)
            {
                // 有原始字节就用原始字节（未修改的外部值条目）。
                var copyLen = Math.Min(4, entry.RawValueBytes.Length);
                Buffer.BlockCopy(entry.RawValueBytes, 0, raw, 8, copyLen);
            }
            else
            {
                var match = externalValues.FirstOrDefault(x => x.Entry == entry);
                WriteU32Raw(raw, 8, little, (uint)match.Offset);
            }

            stream.Write(raw, 0, 12);
        }

        // next-IFD = 0（单页图；多页 TIFF 的后续 IFD 不在本工具职责内）。
        WriteU32(stream, little, 0);

        // 3) 更新头部 IFD0 指针。
        stream.Seek(4, SeekOrigin.Begin);
        WriteU32(stream, little, (uint)newIfdOffset);
        stream.Flush();
    }

    // ==================== 小工具 ====================

    private static ushort ReadU16(Stream stream, bool little)
    {
        var b = new byte[2];
        stream.Read(b, 0, 2);
        return ReadU16At(b, 0, little);
    }

    private static uint ReadU32(Stream stream, bool little)
    {
        var b = new byte[4];
        stream.Read(b, 0, 4);
        return ReadU32At(b, 0, little);
    }

    private static ushort ReadU16At(byte[] data, int offset, bool little) =>
        little ? (ushort)(data[offset] | (data[offset + 1] << 8)) : (ushort)((data[offset] << 8) | data[offset + 1]);

    private static uint ReadU32At(byte[] data, int offset, bool little) =>
        little
            ? (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24))
            : (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);

    private static void WriteU16(Stream stream, bool little, ushort value)
    {
        if (little)
        {
            stream.WriteByte((byte)(value & 0xFF));
            stream.WriteByte((byte)(value >> 8));
        }
        else
        {
            stream.WriteByte((byte)(value >> 8));
            stream.WriteByte((byte)(value & 0xFF));
        }
    }

    private static void WriteU32(Stream stream, bool little, uint value)
    {
        if (little)
        {
            stream.WriteByte((byte)(value & 0xFF));
            stream.WriteByte((byte)((value >> 8) & 0xFF));
            stream.WriteByte((byte)((value >> 16) & 0xFF));
            stream.WriteByte((byte)((value >> 24) & 0xFF));
        }
        else
        {
            stream.WriteByte((byte)((value >> 24) & 0xFF));
            stream.WriteByte((byte)((value >> 16) & 0xFF));
            stream.WriteByte((byte)((value >> 8) & 0xFF));
            stream.WriteByte((byte)(value & 0xFF));
        }
    }

    private static void WriteU16Raw(byte[] buffer, int offset, bool little, ushort value)
    {
        if (little)
        {
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)(value >> 8);
        }
        else
        {
            buffer[offset] = (byte)(value >> 8);
            buffer[offset + 1] = (byte)(value & 0xFF);
        }
    }

    private static void WriteU32Raw(byte[] buffer, int offset, bool little, uint value)
    {
        if (little)
        {
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
            buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
            buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
        }
        else
        {
            buffer[offset] = (byte)((value >> 24) & 0xFF);
            buffer[offset + 1] = (byte)((value >> 16) & 0xFF);
            buffer[offset + 2] = (byte)((value >> 8) & 0xFF);
            buffer[offset + 3] = (byte)(value & 0xFF);
        }
    }
}
