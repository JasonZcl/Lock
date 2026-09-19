using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Lock.Services;

/// <summary>没有程序图标时用的占位锁图标（矢量绘制）。</summary>
public static class LockGlyph
{
    private static ImageSource? _cached;

    public static ImageSource Create()
    {
        if (_cached != null) return _cached;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x2F, 0x6F, 0xED)), 3.2)
            {
                LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round,
            };
            var geo = Geometry.Parse("M14,20 V13 a10,10 0 0 1 20,0 V20 M8,20 h32 v22 h-32 z M24,28 v6");
            dc.DrawGeometry(null, pen, geo);
        }

        var bmp = new RenderTargetBitmap(48, 48, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        return _cached = bmp;
    }
}
