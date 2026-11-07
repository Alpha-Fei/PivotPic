using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PivotPic.Services;

namespace PivotPic.Interop;

/// <summary>
/// 托盘图标工厂：把矢量几何在运行时按多尺寸渲染成 ICO。
/// 工程内因此不需要任何 .ico 资源文件，图标与界面同源、永远一致。
/// </summary>
internal static class TrayIconFactory
{
    private static readonly int[] FrameSizes = { 16, 20, 24, 32, 48, 256 };

    /// <summary>按托盘状态生成多帧图标：空闲=次级色描边，监控中=强调色，告警=强调色 + 红点。</summary>
    public static System.Drawing.Icon Create(TrayState state)
    {
        var frames = new List<byte[]>(FrameSizes.Length);
        foreach (var size in FrameSizes)
        {
            frames.Add(RenderPng(size, state));
        }

        return BuildIco(frames);
    }

    /// <summary>pivopiv 的 P 字形（128×128 设计坐标系，FillRule 为 EvenOdd 才有镂空）。</summary>
    private static readonly Geometry GlyphP = Geometry.Parse(
        "M 34 99 L 34 46 C 34 30 46 20 64 20 C 83 20 96 31 96 48 C 96 65 83 76 65 76 " +
        "L 54 76 L 54 91 Q 54 99 46 99 Z " +
        "M 54 57 L 65 57 C 73 57 77 54 77 48 C 77 42 73 39 65 39 C 58 39 54 42 54 48 Z");

    /// <summary>
    /// 在 128×128 设计视框里绘制品牌标识，再整体缩放到目标像素。
    /// 三种状态只差状态点颜色，方块始终是品牌紫（空闲态整体转灰，避免在托盘里过于跳）。
    /// </summary>
    private static byte[] RenderPng(int size, TrayState state)
    {
        var baseColor = state == TrayState.Idle
            ? Color.FromRgb(0x4A, 0x4F, 0x57)
            : Color.FromRgb(0x55, 0x46, 0xE8);

        var dotColor = state switch
        {
            TrayState.Monitoring => Color.FromRgb(0xBD, 0xF9, 0xCF),
            TrayState.Alert => Color.FromRgb(0xFF, 0x6B, 0x5E),
            _ => Color.FromRgb(0x8A, 0x8F, 0x98)
        };

        var background = new SolidColorBrush(baseColor);
        background.Freeze();

        var glyph = new SolidColorBrush(Colors.White);
        glyph.Freeze();

        var dot = new SolidColorBrush(dotColor);
        dot.Freeze();

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(size / 128.0, size / 128.0));

            // 128 设计稿的圆角是 29，等比缩放即可；小尺寸下略收一点更清晰。
            var radius = size <= 20 ? 26.0 : 29.0;
            dc.DrawRoundedRectangle(background, null, new Rect(0, 0, 128, 128), radius, radius);

            dc.DrawGeometry(glyph, null, GlyphP);

            // 状态点在右下；16px 下缩小半径避免糊成一团。
            var dotRadius = size <= 20 ? 11.0 : 10.0;
            dc.DrawEllipse(dot, null, new Point(86, 90), dotRadius, dotRadius);

            // 告警态在右上角再叠一个红点，小尺寸也能一眼看出。
            if (state == TrayState.Alert)
            {
                var alertDot = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x5E));
                alertDot.Freeze();
                dc.DrawEllipse(alertDot, new Pen(new SolidColorBrush(baseColor), 6), new Point(108, 20), 18, 18);
            }

            dc.Pop();
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    /// <summary>按 ICO 容器格式拼接多帧 PNG（Vista 起支持 PNG 帧，体积远小于 DIB）。</summary>
    private static System.Drawing.Icon BuildIco(IReadOnlyList<byte[]> frames)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write((ushort)0);            // reserved
        writer.Write((ushort)1);            // type = icon
        writer.Write((ushort)frames.Count); // frame count

        var offset = 6 + 16 * frames.Count;
        for (var i = 0; i < frames.Count; i++)
        {
            var size = FrameSizes[i];
            // 256 在目录项里必须写成 0，这是 ICO 格式的历史约定。
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0);          // palette count
            writer.Write((byte)0);          // reserved
            writer.Write((ushort)1);        // color planes
            writer.Write((ushort)32);       // bits per pixel
            writer.Write(frames[i].Length);
            writer.Write(offset);
            offset += frames[i].Length;
        }

        foreach (var frame in frames)
        {
            writer.Write(frame);
        }

        writer.Flush();
        ms.Position = 0;
        return new System.Drawing.Icon(ms);
    }
}
