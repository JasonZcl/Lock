using System.IO;
using Microsoft.Win32;

namespace Lock.Services;

/// <summary>
/// 枚举“控制面板 → 程序和功能”里的已安装程序，并尽量推断出每个程序的主 exe。
/// 数据来源：注册表 Uninstall 项 + 开始菜单快捷方式。
/// </summary>
public static class InstalledAppScanner
{
    public sealed class InstalledApp
    {
        public required string Name { get; init; }
        public string? Publisher { get; init; }
        public string? InstallLocation { get; init; }

        /// <summary>推断出的主程序路径；推断失败为 null。</summary>
        public string? ExePath { get; init; }

        /// <summary>主程序是怎么找到的（显示给用户，便于判断可信度）。</summary>
        public string Source { get; init; } = "";
    }

    private static readonly string[] UninstallKeys =
    [
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
    ];

    // 安装目录里这些名字的 exe 肯定不是主程序
    private static readonly string[] ExcludedExeWords =
    [
        "unins", "uninst", "setup", "install", "update", "updater", "upgrade", "crash", "report",
        "helper", "service", "daemon", "repair", "elevat", "launcher_", "bugreport", "dump",
    ];

    public static List<InstalledApp> Scan()
    {
        var shortcuts = ScanStartMenuShortcuts();
        var result = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase); // key: exe path 或 name
        var usedExes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in ReadUninstallEntries())
        {
            var (exe, source) = ResolveExe(entry, shortcuts);
            var app = new InstalledApp
            {
                Name = entry.Name,
                Publisher = entry.Publisher,
                InstallLocation = entry.InstallLocation,
                ExePath = exe,
                Source = source,
            };

            var key = exe ?? "name:" + entry.Name;
            if (result.ContainsKey(key)) continue;
            result[key] = app;
            if (exe != null) usedExes.Add(exe);
        }

        // 开始菜单里有、但控制面板里没有的（绿色软件、Store 之外的自安装程序等）也列出来
        foreach (var (name, exe) in shortcuts)
        {
            if (usedExes.Contains(exe) || result.ContainsKey(exe)) continue;
            result[exe] = new InstalledApp { Name = name, ExePath = exe, Source = "开始菜单快捷方式" };
            usedExes.Add(exe);
        }

        return result.Values
            .OrderBy(a => a.ExePath == null) // 找到主程序的排前面
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    // ---- 注册表 ----

    private sealed record UninstallEntry(string Name, string? Publisher, string? InstallLocation, string? DisplayIcon, string? UninstallString);

    private static IEnumerable<UninstallEntry> ReadUninstallEntries()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (root, keyPath) in EnumerateUninstallRoots())
        {
            using var key = root.OpenSubKey(keyPath);
            if (key == null) continue;

            foreach (var sub in key.GetSubKeyNames())
            {
                using var k = key.OpenSubKey(sub);
                if (k == null) continue;

                var name = k.GetValue("DisplayName") as string;
                if (string.IsNullOrWhiteSpace(name)) continue;
                name = name.Trim();

                // 系统组件、补丁、更新不显示在控制面板里
                if (k.GetValue("SystemComponent") is int sc && sc == 1) continue;
                if (k.GetValue("ParentKeyName") != null) continue;
                if (name.StartsWith("KB", StringComparison.OrdinalIgnoreCase) && name.Length > 2 && char.IsDigit(name[2])) continue;

                if (!seen.Add(name)) continue;

                yield return new UninstallEntry(
                    name,
                    k.GetValue("Publisher") as string,
                    Clean(k.GetValue("InstallLocation") as string),
                    k.GetValue("DisplayIcon") as string,
                    k.GetValue("UninstallString") as string);
            }
        }
    }

    private static IEnumerable<(RegistryKey Root, string Path)> EnumerateUninstallRoots()
    {
        foreach (var p in UninstallKeys) yield return (Registry.LocalMachine, p);
        foreach (var p in UninstallKeys) yield return (Registry.CurrentUser, p);
    }

    private static string? Clean(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        path = path.Trim().Trim('"');
        return path.Length == 0 ? null : path;
    }

    // ---- 推断主程序 ----

    private static (string? Exe, string Source) ResolveExe(UninstallEntry e, List<(string Name, string Exe)> shortcuts)
    {
        // 1. 开始菜单同名快捷方式——这就是用户平时点开的那个 exe，最可信
        //    （DisplayIcon 有时指向带版本号的子目录副本，如 Edge 的 Application\1xx.x\msedge.exe）
        var byName = shortcuts.FirstOrDefault(s => string.Equals(s.Name, e.Name, StringComparison.OrdinalIgnoreCase));
        if (byName.Exe != null) return (byName.Exe, "开始菜单快捷方式");

        // 2. DisplayIcon 直接指向 exe（形如 "C:\...\App.exe" 或 "C:\...\App.exe,0"）
        var icon = ParseIconPath(e.DisplayIcon);
        if (icon != null && IsCandidateExe(icon) && !SamePath(icon, e.UninstallString))
            return (icon, "注册表 DisplayIcon");

        // 3. 快捷方式目标位于安装目录内
        if (e.InstallLocation != null)
        {
            var dir = e.InstallLocation.TrimEnd('\\') + "\\";
            var inDir = shortcuts
                .Where(s => s.Exe.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(s => Similarity(s.Name, e.Name))
                .FirstOrDefault();
            if (inDir.Exe != null) return (inDir.Exe, "开始菜单快捷方式");
        }

        // 4. 名字包含关系的快捷方式（如 "企业微信" vs "企业微信 5.0"）
        var fuzzy = shortcuts
            .Where(s => s.Name.Contains(e.Name, StringComparison.OrdinalIgnoreCase) || e.Name.Contains(s.Name, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(s => Similarity(s.Name, e.Name))
            .FirstOrDefault();
        if (fuzzy.Exe != null && Similarity(fuzzy.Name, e.Name) > 0.5) return (fuzzy.Exe, "开始菜单快捷方式（名称近似）");

        // 5. 扫描安装目录
        var scanned = ScanInstallDir(e.InstallLocation, e.Name);
        if (scanned != null) return (scanned, "安装目录扫描");

        return (null, "未找到主程序");
    }

    private static string? ParseIconPath(string? displayIcon)
    {
        if (string.IsNullOrWhiteSpace(displayIcon)) return null;
        var s = displayIcon.Trim();

        // 去掉 ",0" 这类图标索引
        var comma = s.LastIndexOf(',');
        if (comma > 0 && int.TryParse(s[(comma + 1)..].Trim(), out _)) s = s[..comma];

        s = Environment.ExpandEnvironmentVariables(s.Trim().Trim('"'));
        return s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(s) ? Path.GetFullPath(s) : null;
    }

    private static bool SamePath(string exe, string? uninstallString)
    {
        if (string.IsNullOrEmpty(uninstallString)) return false;
        return uninstallString.Contains(exe, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly string WindowsDir =
        Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";

    private static bool IsCandidateExe(string path)
    {
        // msiexec / rundll32 之类的系统程序不可能是某个应用的主程序
        if (path.StartsWith(WindowsDir, StringComparison.OrdinalIgnoreCase)) return false;

        var name = Path.GetFileNameWithoutExtension(path);
        return !ExcludedExeWords.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    private static string? ScanInstallDir(string? dir, string appName)
    {
        if (dir == null || !Directory.Exists(dir)) return null;

        try
        {
            var exes = Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly)
                .Concat(Directory.EnumerateDirectories(dir).Take(20)
                    .SelectMany(d => SafeEnumerate(d, "*.exe")))
                .Where(IsCandidateExe)
                .ToList();
            if (exes.Count == 0) return null;

            // 名字最像的优先，其次取体积最大的（主程序通常最大）
            return exes
                .OrderByDescending(p => Similarity(Path.GetFileNameWithoutExtension(p), appName))
                .ThenByDescending(p => new FileInfo(p).Length)
                .First();
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> SafeEnumerate(string dir, string pattern)
    {
        try { return Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly); }
        catch { return []; }
    }

    /// <summary>粗略的名称相似度（0~1）：公共字符占比。</summary>
    private static double Similarity(string a, string b)
    {
        a = Normalize(a);
        b = Normalize(b);
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a == b) return 1;
        if (a.Contains(b) || b.Contains(a)) return 0.8;

        var common = a.Intersect(b).Count();
        return (double)common / Math.Max(a.Length, b.Length);
    }

    private static string Normalize(string s)
        => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    // ---- 开始菜单 ----

    private static List<(string Name, string Exe)> ScanStartMenuShortcuts()
    {
        var result = new List<(string, string)>();
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        };

        foreach (var root in roots.Where(r => !string.IsNullOrEmpty(r) && Directory.Exists(r)))
        {
            IEnumerable<string> links;
            try { links = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories); }
            catch { continue; }

            foreach (var lnk in links)
            {
                var name = Path.GetFileNameWithoutExtension(lnk);
                if (name.Contains("卸载", StringComparison.Ordinal) || name.Contains("uninstall", StringComparison.OrdinalIgnoreCase))
                    continue;

                var target = ShellLink.ResolveTarget(lnk);
                if (target == null
                    || !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    || !File.Exists(target)
                    || !IsCandidateExe(target))
                    continue;

                result.Add((name, Path.GetFullPath(target)));
            }
        }

        return result;
    }
}
