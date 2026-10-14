using System.Security.AccessControl;
using System.Security.Principal;
using Lock.Core.Models;
using Lock.Core.Native;
using Microsoft.Win32;
using FileSecurity = Lock.Core.Native.FileSecurity;

namespace Lock.Core.Services;

public static class FolderLocker
{
    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly object PrivilegeSync = new();
    private static bool _privilegesEnabled;

    public static bool EnsurePrivileges()
    {
        lock (PrivilegeSync)
        {
            if (_privilegesEnabled) return true;
            var restore = NativeMethods.EnablePrivilege("SeRestorePrivilege");
            var backup = NativeMethods.EnablePrivilege("SeBackupPrivilege");
            NativeMethods.EnablePrivilege("SeTakeOwnershipPrivilege");
            _privilegesEnabled = restore && backup;
            return _privilegesEnabled;
        }
    }

    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)
            || path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\')
            throw new ArgumentException("仅支持本地磁盘的绝对路径");
        foreach (var component in path[3..].Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries))
            if (component.Contains(':') || component.EndsWith('.') || component.EndsWith(' '))
                throw new ArgumentException("不允许设备路径、数据流或含尾随空格/点的路径");
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        foreach (var component in normalized[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
            if (component.Contains(':') || component.EndsWith('.') || component.EndsWith(' '))
                throw new ArgumentException("不允许设备路径、数据流或含尾随空格/点的路径");
        return normalized;
    }

    public static string? WhyForbidden(string path)
    {
        try
        {
            path = Normalize(path);
            if (!Directory.Exists(path)) return "文件夹不存在";
            using var guard = PathGuard.Pin(path);
            if (string.Equals(Path.GetPathRoot(path), path, StringComparison.OrdinalIgnoreCase)) return "不能锁定整个磁盘";
            var drive = new DriveInfo(Path.GetPathRoot(path)!);
            if (!string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(drive.DriveFormat, "ReFS", StringComparison.OrdinalIgnoreCase))
                return "仅支持 NTFS/ReFS 本地磁盘";
            var forbidden = new List<string>();
            foreach (var folder in new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles,
                Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.CommonApplicationData })
            {
                var directory = Environment.GetFolderPath(folder);
                if (!string.IsNullOrWhiteSpace(directory)) forbidden.Add(Normalize(directory));
            }
            forbidden.Add(Normalize(ConfigStore.DataDirectory));
            forbidden.Add(Normalize(AppContext.BaseDirectory));
            if (forbidden.Any(root => IsSameOrUnder(path, root) || IsSameOrUnder(root, path)))
                return "不能锁定系统目录、程序目录或应用锁自身所在目录（及其上级目录）";
            using var profiles = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
            var roots = new List<string> { Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "Users") };
            if (profiles?.GetValue("ProfilesDirectory") is string profilesDirectory) roots.Add(Normalize(Environment.ExpandEnvironmentVariables(profilesDirectory)));
            if (roots.Any(root => IsSameOrUnder(root, path) || string.Equals(Path.GetDirectoryName(path), root, StringComparison.OrdinalIgnoreCase)))
                return "不能锁定整个用户目录，请选择其中的子文件夹";
            if (profiles != null)
                foreach (var profileName in profiles.GetSubKeyNames())
                {
                    using var profile = profiles.OpenSubKey(profileName);
                    if (profile?.GetValue("ProfileImagePath") is string profilePath
                        && IsSameOrUnder(Normalize(Environment.ExpandEnvironmentVariables(profilePath)), path))
                        return "不能锁定整个用户配置目录";
                }
            return null;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return "路径不安全或不可访问：" + exception.Message;
        }
    }

    private static bool IsSameOrUnder(string path, string root)
        => string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(Path.TrimEndingDirectorySeparator(root) + '\\', StringComparison.OrdinalIgnoreCase);

    internal static bool IsLockDescriptor(string sddl)
    {
        var descriptor = new RawSecurityDescriptor(sddl);
        if (descriptor.Owner != SystemSid || (descriptor.ControlFlags & ControlFlags.DiscretionaryAclProtected) == 0
            || descriptor.DiscretionaryAcl == null || descriptor.DiscretionaryAcl.Count != 1) return false;
        return descriptor.DiscretionaryAcl[0] is CommonAce rule && rule.AceQualifier == AceQualifier.AccessAllowed
            && rule.SecurityIdentifier == SystemSid && (rule.AccessMask & 0x1F01FF) == 0x1F01FF;
    }

    public static bool IsLockedOnDisk(string path)
    {
        EnsurePrivileges();
        using var guard = PathGuard.Pin(path);
        using var file = FileSecurity.Open(Normalize(path), writable: true);
        return IsLockDescriptor(file.ReadSddl());
    }

    public static void Restore(LockedFolder folder)
    {
        if (folder.AclEntries.Count > 0)
        {
            using var session = FolderLockSession.Capture(folder.Path, folder.AclEntries);
            session.Restore();
            return;
        }
        if (!IsLockedOnDisk(folder.Path)) return;
        if (string.IsNullOrEmpty(folder.OriginalSddl)) throw new InvalidDataException("缺少原始权限；保留记录，禁止猜测恢复 ACL");
        using var guard = PathGuard.Pin(folder.Path);
        if (WhyForbidden(folder.Path) is { } reason) throw new IOException(reason);
        if (!EnsurePrivileges()) throw new UnauthorizedAccessException("缺少恢复文件夹权限的特权");
        using var file = FileSecurity.Open(Normalize(folder.Path), writable: true);
        file.WriteSddl(folder.OriginalSddl);
    }
}
