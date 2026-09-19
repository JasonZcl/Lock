using System.Security.AccessControl;
using System.Security.Principal;
using Lock.Core.Native;

namespace Lock.Core.Services;

/// <summary>
/// 用 NTFS 权限锁定/恢复文件夹。必须以 SYSTEM（或至少具备 SeRestorePrivilege 的管理员）运行。
///
/// 锁定 = 所有者改为 SYSTEM、DACL 改为“仅 SYSTEM 完全控制”并禁用继承，Windows 会把新的可继承 ACE
/// 传播到整个子树（替换子项原本的继承项）。所有者改掉是关键：所有者天然拥有 READ_CONTROL/WRITE_DAC，
/// 不改所有者的话用户在“属性 → 安全”里就能把权限改回来。
///
/// 解锁 = 恢复原所有者，DACL 恢复为原始的显式 ACE 并重新启用继承，继承项由父目录自动补回。
/// </summary>
public static class FolderLocker
{
    // 仅 SYSTEM 完全控制，禁用继承，可继承到子文件夹与文件
    private const string LockedSddl = "O:SYG:SYD:PAI(A;OICI;FA;;;SY)";

    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier AdminsSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    private static bool _privilegesEnabled;

    /// <summary>启用改所有者 / 读写任意 ACL 所需的特权。返回 false 表示当前进程没有这些特权（非 SYSTEM / 非管理员）。</summary>
    public static bool EnsurePrivileges()
    {
        if (_privilegesEnabled) return true;
        var restore = NativeMethods.EnablePrivilege("SeRestorePrivilege");
        var backup = NativeMethods.EnablePrivilege("SeBackupPrivilege");
        NativeMethods.EnablePrivilege("SeTakeOwnershipPrivilege");
        NativeMethods.EnablePrivilege("SeSecurityPrivilege");
        _privilegesEnabled = restore && backup;
        return _privilegesEnabled;
    }

    /// <summary>规范化路径：绝对路径、去结尾分隔符。</summary>
    public static string Normalize(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>
    /// 检查该文件夹能否被锁。返回 null 表示可以，否则返回原因。
    /// 系统目录、用户配置文件根、磁盘根、应用锁自身目录一律拒绝——锁错了会让系统或应用锁自己无法工作。
    /// </summary>
    public static string? WhyForbidden(string path)
    {
        path = Normalize(path);
        if (!Directory.Exists(path)) return "文件夹不存在";
        if (Path.GetPathRoot(path)?.TrimEnd('\\') == path) return "不能锁定整个磁盘";

        var drive = new DriveInfo(Path.GetPathRoot(path)!);
        if (!string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(drive.DriveFormat, "ReFS", StringComparison.OrdinalIgnoreCase))
            return $"该磁盘是 {drive.DriveFormat} 格式，不支持权限控制（仅 NTFS/ReFS 可用）";

        string Dir(Environment.SpecialFolder f) => Normalize(Environment.GetFolderPath(f));

        var forbiddenTrees = new[]
        {
            Dir(Environment.SpecialFolder.Windows),
            Dir(Environment.SpecialFolder.ProgramFiles),
            Dir(Environment.SpecialFolder.ProgramFilesX86),
            Dir(Environment.SpecialFolder.CommonApplicationData),
            Normalize(ConfigStore.DataDirectory),
            Normalize(Path.GetDirectoryName(Environment.ProcessPath!)!),
        };
        foreach (var t in forbiddenTrees)
        {
            if (t.Length > 0 && (IsSameOrUnder(path, t) || IsSameOrUnder(t, path)))
                return "不能锁定系统目录、程序目录或应用锁自身所在目录（及其上级目录）";
        }

        // C:\Users 与任何用户配置文件根目录（C:\Users\xxx）
        var usersRoot = Path.GetDirectoryName(Dir(Environment.SpecialFolder.UserProfile));
        if (usersRoot != null)
        {
            usersRoot = Normalize(usersRoot);
            if (string.Equals(path, usersRoot, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetDirectoryName(path), usersRoot, StringComparison.OrdinalIgnoreCase))
                return "不能锁定整个用户目录，请选择其中的子文件夹";
        }

        return null;
    }

    private static bool IsSameOrUnder(string path, string root)
        => string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
           || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>读取当前安全描述符（所有者 + 组 + DACL）的 SDDL。</summary>
    public static string ReadSddl(string path)
    {
        EnsurePrivileges();
        var ds = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access);
        return ds.GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access);
    }

    /// <summary>该文件夹当前是否处于本工具的锁定状态（所有者为 SYSTEM 且 DACL 受保护）。</summary>
    public static bool IsLockedOnDisk(string path)
    {
        try
        {
            var ds = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
            var owner = ds.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            return owner == SystemSid && ds.AreAccessRulesProtected;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>锁定。返回锁定前的 SDDL（调用方必须保存）。已经锁定时返回 null 且不做任何事。</summary>
    public static string? Lock(string path)
    {
        if (!EnsurePrivileges()) throw new UnauthorizedAccessException("当前进程没有修改文件夹所有者的特权");
        if (IsLockedOnDisk(path)) return null;

        var original = ReadSddl(path);

        var ds = new DirectorySecurity();
        ds.SetSecurityDescriptorSddlForm(LockedSddl, AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access);
        // SetAccessControl 底层是 SetNamedSecurityInfo，会自动把可继承 ACE 传播到整个子树
        new DirectoryInfo(path).SetAccessControl(ds);
        return original;
    }

    /// <summary>
    /// 恢复。originalSddl 为空时（历史数据缺失）退回到“所有者 = Administrators、启用继承、无显式 ACE”，
    /// 这样至少能让管理员和从父目录继承的用户重新访问。
    /// </summary>
    public static void Unlock(string path, string? originalSddl)
    {
        if (!EnsurePrivileges()) throw new UnauthorizedAccessException("当前进程没有修改文件夹所有者的特权");

        var restored = new DirectorySecurity();

        if (!string.IsNullOrEmpty(originalSddl))
        {
            var original = new DirectorySecurity();
            original.SetSecurityDescriptorSddlForm(originalSddl, AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access);

            restored.SetOwner(original.GetOwner(typeof(SecurityIdentifier))!);
            if (original.GetGroup(typeof(SecurityIdentifier)) is { } group) restored.SetGroup(group);

            // 只放回原本的显式 ACE；继承项在取消保护后由父目录重新传播下来
            restored.SetAccessRuleProtection(original.AreAccessRulesProtected, preserveInheritance: false);
            foreach (FileSystemAccessRule rule in original.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier)))
                restored.AddAccessRule(rule);
        }
        else
        {
            restored.SetOwner(AdminsSid);
            restored.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
        }

        new DirectoryInfo(path).SetAccessControl(restored);
    }
}
