using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Lock.Core.Models;
using Lock.Core.Native;
using Lock.Services;

namespace Lock.Views;

public partial class ProcessPickerWindow : Window
{
    public sealed record Item
    {
        public required string ExeName { get; init; }
        public required string DisplayName { get; init; }
        public string? FullPath { get; init; }
        public ImageSource? Icon { get; init; }
        public bool HasWindow { get; init; }
        public string WindowTitle { get; init; } = "";
        public string WindowText => HasWindow ? "有" : "无（托盘/后台）";
    }

    public List<LockedApp> Selected { get; } = [];

    public ProcessPickerWindow()
    {
        InitializeComponent();
        Refresh();
    }

    private void Refresh()
    {
        // 按完整路径去重：同一个 exe 的多个进程只显示一行
        var items = new Dictionary<string, Item>(StringComparer.OrdinalIgnoreCase);
        var selfPid = Environment.ProcessId;
        var mySession = Process.GetCurrentProcess().SessionId;
        var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";

        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    // 只看当前用户会话里的进程；系统服务、Windows 自带的后台进程不列
                    if (p.Id == selfPid || p.SessionId != mySession) continue;

                    var path = NativeMethods.GetProcessImagePath((uint)p.Id);
                    if (path == null || path.StartsWith(windowsDir, StringComparison.OrdinalIgnoreCase)) continue;

                    // 不能把托盘图标缩起来的主程序（如企业微信、微信）过滤掉——它们此刻没有可见窗口，
                    // 但恰恰是用户想锁的。窗口状态只作为排序依据。
                    var hasWindow = p.MainWindowHandle != 0;

                    if (items.TryGetValue(path, out var existing))
                    {
                        if (hasWindow && !existing.HasWindow)
                            items[path] = existing with { HasWindow = true, WindowTitle = p.MainWindowTitle };
                        continue;
                    }

                    var exe = Path.GetFileName(path);
                    var display = GetDescription(path);
                    if (string.IsNullOrWhiteSpace(display)) display = hasWindow ? p.MainWindowTitle : null;
                    if (string.IsNullOrWhiteSpace(display)) display = Path.GetFileNameWithoutExtension(exe);

                    items[path] = new Item
                    {
                        ExeName = exe,
                        DisplayName = display,
                        FullPath = path,
                        Icon = IconHelper.GetIcon(path),
                        HasWindow = hasWindow,
                        WindowTitle = hasWindow ? p.MainWindowTitle : "",
                    };
                }
                catch
                {
                    // 无权访问的进程跳过
                }
            }
        }

        ProcessList.ItemsSource = items.Values
            .OrderByDescending(i => i.HasWindow)
            .ThenBy(i => i.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>读取 exe 的文件描述（如“微信”），没有则返回 null。</summary>
    public static string? GetDescription(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            var desc = FileVersionInfo.GetVersionInfo(path).FileDescription;
            return string.IsNullOrWhiteSpace(desc) ? null : desc.Trim();
        }
        catch
        {
            return null;
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void ProcessList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ProcessList.SelectedItem != null) Confirm();
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => Confirm();

    private void Confirm()
    {
        foreach (var item in ProcessList.SelectedItems.OfType<Item>())
        {
            Selected.Add(new LockedApp
            {
                ExeName = item.ExeName.ToLowerInvariant(),
                DisplayName = item.DisplayName,
                FullPath = item.FullPath,
                MatchMode = item.FullPath != null ? MatchMode.FullPath : MatchMode.ExeName,
            });
        }
        DialogResult = Selected.Count > 0;
    }
}
