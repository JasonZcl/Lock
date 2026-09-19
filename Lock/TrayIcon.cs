using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Lock;

/// <summary>
/// 托盘图标（基于 WinForms NotifyIcon），图标在运行时绘制，无需资源文件。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Icon _drawnIcon;

    public event Action? OpenRequested;
    public event Action? ExitRequested;

    public TrayIcon()
    {
        _drawnIcon = DrawLockIcon();

        var menu = new ContextMenuStrip();
        menu.Items.Add("打开管理界面", null, (_, _) => OpenRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitRequested?.Invoke());

        _icon = new NotifyIcon
        {
            Icon = _drawnIcon,
            Text = "应用锁 - 正在保护",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke();
    }

    public void ShowBalloon(string title, string text)
        => _icon.ShowBalloonTip(3000, title, text, ToolTipIcon.Info);

    /// <summary>更新托盘提示：lockCount 为 -1 表示已暂停。</summary>
    public void SetStatus(bool connected, int lockCount)
    {
        var text = !connected ? "应用锁 - 服务未连接"
            : lockCount < 0 ? "应用锁 - 已暂停"
            : $"应用锁 - 正在保护 {lockCount} 个程序";
        // NotifyIcon.Text 最长 127 字符
        _icon.Text = text.Length > 127 ? text[..127] : text;
    }

    private static Icon DrawLockIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            // 锁环
            using var pen = new Pen(Color.FromArgb(60, 60, 60), 4);
            g.DrawArc(pen, 9, 3, 14, 16, 180, 180);

            // 锁体
            using var body = new SolidBrush(Color.FromArgb(230, 160, 30));
            g.FillRectangle(body, 5, 13, 22, 16);

            // 锁孔
            using var hole = new SolidBrush(Color.FromArgb(60, 60, 60));
            g.FillEllipse(hole, 13, 17, 6, 6);
            g.FillRectangle(hole, 15, 20, 2, 5);
        }

        var handle = bmp.GetHicon();
        try
        {
            // FromHandle 的图标不拥有句柄，Clone 一份后释放原句柄
            using var tmp = Icon.FromHandle(handle);
            return (Icon)tmp.Clone();
        }
        finally
        {
            Lock.Core.Native.NativeMethods.DestroyIcon(handle);
        }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _drawnIcon.Dispose();
    }
}
