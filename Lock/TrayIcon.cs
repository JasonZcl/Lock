using System.Drawing;
using System.Windows.Forms;

namespace Lock;

/// <summary>
/// 托盘图标（基于 WinForms NotifyIcon）。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Icon _drawnIcon;

    public event Action? OpenRequested;
    public event Action? ExitRequested;

    public TrayIcon()
    {
        _drawnIcon = LoadAppIcon();

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

    /// <summary>从 exe 自身嵌入的图标资源取托盘图标，与资源管理器/右键菜单里的图标一致。</summary>
    private static Icon LoadAppIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (path != null && Icon.ExtractAssociatedIcon(path) is { } icon) return icon;
        }
        catch
        {
            // 走下面的兜底
        }
        return SystemIcons.Shield;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _drawnIcon.Dispose();
    }
}
