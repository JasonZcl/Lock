using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Lock.Core.Models;

namespace Lock.Services;

/// <summary>按日志事件类型给出底色 / 前景色，用于日志列表里的标签。</summary>
public sealed class EventColorConverter : IValueConverter
{
    public bool Foreground { get; set; }

    private static readonly (Brush Bg, Brush Fg) Success = (Brush("#E7F6EC"), Brush("#1E7A45"));
    private static readonly (Brush Bg, Brush Fg) Danger = (Brush("#FDECEC"), Brush("#B83232"));
    private static readonly (Brush Bg, Brush Fg) Warn = (Brush("#FFF6E0"), Brush("#9A5B00"));
    private static readonly (Brush Bg, Brush Fg) Info = (Brush("#EAF1FF"), Brush("#2455B8"));
    private static readonly (Brush Bg, Brush Fg) Neutral = (Brush("#EEF0F4"), Brush("#4B5363"));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var pair = (value as string) switch
        {
            LogEvents.Unlocked or LogEvents.FolderLocked or LogEvents.FolderRelocked => Success,
            LogEvents.WrongPassword or LogEvents.AttemptsExceeded or LogEvents.LoginFailed or LogEvents.FolderUnlockFailed or LogEvents.FolderError => Danger,
            LogEvents.Cancelled or LogEvents.NoAgent or LogEvents.AgentLost or LogEvents.SuspendFailed or LogEvents.FolderUnlocked => Warn,
            LogEvents.GracePass or LogEvents.ChildPass or LogEvents.FolderAdded or LogEvents.FolderRemoved => Info,
            _ => Neutral,
        };
        return Foreground ? pair.Fg : pair.Bg;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();

    private static Brush Brush(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
