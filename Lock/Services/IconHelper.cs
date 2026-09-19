using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Lock.Services;

/// <summary>
/// 从可执行文件提取图标并转换为 WPF ImageSource。
/// </summary>
public static class IconHelper
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? GetIcon(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return null;

        lock (Cache)
        {
            if (Cache.TryGetValue(exePath, out var cached)) return cached;
        }

        ImageSource? result = null;
        try
        {
            using var icon = Icon.ExtractAssociatedIcon(exePath);
            if (icon != null)
            {
                result = Imaging.CreateBitmapSourceFromHIcon(
                    icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                result.Freeze();
            }
        }
        catch
        {
            // 图标提取失败不影响功能
        }

        lock (Cache) Cache[exePath] = result;
        return result;
    }
}
