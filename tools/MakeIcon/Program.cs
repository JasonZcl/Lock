using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// 与 Themes/Theme.xaml 里的 AppIconImage 保持一致：圆角蓝底 + 白色描边锁
var output = args.Length > 0 ? args[0] : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Lock", "Assets", "applock.ico"));
Directory.CreateDirectory(Path.GetDirectoryName(output)!);

int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
var frames = new List<byte[]>();

foreach (var size in sizes)
{
    var visual = new DrawingVisual();
    using (var dc = visual.RenderOpen())
    {
        // 以 32x32 为设计坐标系，按尺寸缩放
        var s = size / 32.0;
        dc.PushTransform(new ScaleTransform(s, s));

        var bg = new SolidColorBrush(Color.FromRgb(0x2F, 0x6F, 0xED));
        dc.DrawRoundedRectangle(bg, null, new Rect(0, 0, 32, 32), 7, 7);

        // 小尺寸线条略粗一点，否则 16px 下看不清
        var thickness = size <= 20 ? 3.0 : 2.4;
        var pen = new Pen(Brushes.White, thickness) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawGeometry(null, pen, Geometry.Parse("M11,14 V11 a5,5 0 0 1 10,0 V14 M8,14 h16 v12 h-16 z M16,18.5 v3.5"));
        dc.Pop();
    }

    var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
    bmp.Render(visual);
    var enc = new PngBitmapEncoder();
    enc.Frames.Add(BitmapFrame.Create(bmp));
    using var ms = new MemoryStream();
    enc.Save(ms);
    frames.Add(ms.ToArray());
}

// ICO 容器：ICONDIR + N × ICONDIRENTRY + 数据（PNG 帧，Vista+ 支持）
using var fs = new FileStream(output, FileMode.Create);
using var w = new BinaryWriter(fs);
w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)sizes.Length);
var offset = 6 + 16 * sizes.Length;
for (var i = 0; i < sizes.Length; i++)
{
    var sz = sizes[i];
    w.Write((byte)(sz >= 256 ? 0 : sz)); w.Write((byte)(sz >= 256 ? 0 : sz));
    w.Write((byte)0); w.Write((byte)0);
    w.Write((ushort)1); w.Write((ushort)32);
    w.Write(frames[i].Length); w.Write(offset);
    offset += frames[i].Length;
}
foreach (var f in frames) w.Write(f);

Console.WriteLine($"written {output} ({fs.Length} bytes, {sizes.Length} sizes)");
