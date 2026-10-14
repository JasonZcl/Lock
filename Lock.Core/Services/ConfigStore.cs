using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lock.Core.Models;

namespace Lock.Core.Services;

/// <summary>
/// 配置文件与日志的存放位置及读写。目录位于 ProgramData，仅 SYSTEM 与管理员可访问。
/// </summary>
public static class ConfigStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly object Sync = new();

    /// <summary>数据目录。开发调试时可用环境变量 APPLOCK_DATA 覆盖。</summary>
    public static string DataDirectory { get; } =
        Environment.GetEnvironmentVariable("APPLOCK_DATA") is { Length: > 0 } d
            ? Path.GetFullPath(d)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AppLock");

    public static string ConfigPath { get; } = Path.Combine(DataDirectory, "config.json");
    public static string BackupPath { get; } = ConfigPath + ".bak";
    private static string InitializedPath { get; } = Path.Combine(DataDirectory, "initialized");
    public static string LogPath { get; } = Path.Combine(DataDirectory, "unlock.log");
    public static string ServiceLogPath { get; } = Path.Combine(DataDirectory, "service.log");

    public static AppConfig Load()
    {
        lock (Sync)
        {
            if (!File.Exists(ConfigPath) && !File.Exists(BackupPath))
            {
                if (File.Exists(InitializedPath)) throw new InvalidDataException("配置文件丢失，已停止保护初始化，请恢复配置备份。");
                return new AppConfig();
            }
            try
            {
                return ReadValidated(ConfigPath);
            }
            catch (Exception original) when (original is IOException or InvalidDataException or JsonException or ArgumentException or FormatException)
            {
                try
                {
                    var backup = ReadValidated(BackupPath);
                    if (File.Exists(InitializedPath) && !backup.HasPassword)
                        throw new InvalidDataException("备份中缺少已初始化的密码");
                    if (File.Exists(ConfigPath)) File.Copy(ConfigPath, ConfigPath + ".corrupt.bak", overwrite: true);
                    WriteDurably(ConfigPath, JsonSerializer.SerializeToUtf8Bytes(backup, JsonOptions));
                    return backup;
                }
                catch (Exception recovery) when (recovery is IOException or InvalidDataException or JsonException or ArgumentException or FormatException)
                {
                    throw new InvalidDataException("配置和备份均不可用，请恢复配置；禁止重新初始化密码。", new AggregateException(original, recovery));
                }
            }
        }
    }

    private static AppConfig ReadValidated(string path)
    {
        if (new FileInfo(path).Length > 64 * 1024 * 1024) throw new InvalidDataException("配置文件过大");
        var content = File.ReadAllText(path);
        using var document = JsonDocument.Parse(content);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("Settings", out var settings)
            || settings.ValueKind != JsonValueKind.Object || !settings.TryGetProperty("LockedApps", out _)
            || !document.RootElement.TryGetProperty("Folders", out _))
            throw new InvalidDataException("配置缺少关键字段");
        var config = JsonSerializer.Deserialize<AppConfig>(content, JsonOptions)
            ?? throw new InvalidDataException("配置为空");
        Validate(config);
        foreach (var app in config.Settings.LockedApps) app.ExeName = app.ExeName.ToLowerInvariant();
        return config;
    }

    public static AppConfig Clone(AppConfig config)
        => JsonSerializer.Deserialize<AppConfig>(JsonSerializer.SerializeToUtf8Bytes(config, JsonOptions), JsonOptions)!;

    public static void AppendLog(string path, string text)
    {
        using var guard = PathGuard.Pin(DataDirectory);
        using var file = Lock.Core.Native.FileSecurity.Open(path, additionalAccess: 4, createNew: !File.Exists(path));
        if (file.IsDirectory || file.LinkCount != 1) throw new IOException("日志目标不安全");
        using var stream = new FileStream(file.Handle, FileAccess.Write);
        stream.Write(System.Text.Encoding.UTF8.GetBytes(text));
    }

    private static void Validate(AppConfig config)
    {
        if (config.Settings == null || config.Settings.LockedApps == null || config.Folders == null
            || config.Settings.LockedApps.Count > 512 || config.Folders.Count > 128)
            throw new InvalidDataException("配置结构无效");
        if (config.Password == null && (config.Recovery != null || config.Folders.Count > 0 || config.Settings.LockedApps.Count > 0))
            throw new InvalidDataException("配置缺少密码");
        if (File.Exists(InitializedPath) && !config.HasPassword) throw new InvalidDataException("已初始化配置缺少密码");
        foreach (var secret in new[] { config.Password, config.Recovery }.OfType<SecretRecord>())
            if (secret.Hash == null || secret.Salt == null || secret.Iterations < 10_000 || secret.Iterations > 10_000_000
                || Convert.FromBase64String(secret.Salt).Length != 16 || Convert.FromBase64String(secret.Hash).Length != 32)
                throw new InvalidDataException("密码记录无效");
        if (config.Settings.MaxAttempts is < 1 or > 100 || config.Settings.UnlockGraceSeconds is < 0 or > 604800
            || config.Settings.FolderRelockMinutes is < 0 or > 10080)
            throw new InvalidDataException("配置数值越界");
        foreach (var app in config.Settings.LockedApps)
        {
            if (app == null || string.IsNullOrWhiteSpace(app.ExeName) || !Enum.IsDefined(app.MatchMode)
                || (app.MatchMode == MatchMode.FullPath && string.IsNullOrWhiteSpace(app.FullPath)))
                throw new InvalidDataException("应用规则无效");
            if (app.MatchMode == MatchMode.FullPath) FolderLocker.Normalize(app.FullPath!);
        }
        foreach (var folder in config.Folders)
        {
            if (folder == null || string.IsNullOrWhiteSpace(folder.Path) || folder.AclEntries == null || folder.AclEntries.Count > 10000
                || folder.AclEntries.Any(entry => entry == null || entry.RelativePath == null || string.IsNullOrEmpty(entry.Sddl) || string.IsNullOrEmpty(entry.Identity)))
                throw new InvalidDataException("文件夹恢复记录无效");
            FolderLocker.Normalize(folder.Path);
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in folder.AclEntries)
            {
                if (!paths.Add(entry.RelativePath) || (entry.RelativePath.Length > 0
                    && (Path.IsPathRooted(entry.RelativePath) || entry.RelativePath.Split('\\', '/').Any(part => part is "" or "." or ".." || part.Contains(':')))))
                    throw new InvalidDataException("权限恢复路径无效");
                var descriptor = new RawSecurityDescriptor(entry.Sddl);
                if (descriptor.Owner == null || descriptor.Group == null) throw new InvalidDataException("恢复权限缺少所有者或组");
            }
            if (folder.AclEntries.Count > 0 && !paths.Contains("")) throw new InvalidDataException("权限备份缺少根目录");
        }
    }

    public static void Save(AppConfig config)
    {
        lock (Sync)
        {
            Directory.CreateDirectory(DataDirectory);
            Validate(config);
            var content = JsonSerializer.SerializeToUtf8Bytes(config, JsonOptions);
            if (content.Length > 64 * 1024 * 1024) throw new InvalidDataException("配置文件超过 64 MB，已取消修改");
            byte[]? previousBackup = null;
            if (File.Exists(BackupPath))
            {
                if (new FileInfo(BackupPath).Length > 64 * 1024 * 1024) throw new InvalidDataException("配置备份过大，已取消修改");
                previousBackup = File.ReadAllBytes(BackupPath);
            }
            WriteDurably(BackupPath, content);
            try { WriteDurably(ConfigPath, content); }
            catch (Exception original)
            {
                try
                {
                    if (previousBackup == null) File.Delete(BackupPath);
                    else WriteDurably(BackupPath, previousBackup);
                }
                catch (Exception rollback)
                {
                    throw new IOException("主配置保存和备份回滚均失败，请核查配置文件后再启动服务。", new AggregateException(original, rollback));
                }
                throw;
            }
            if (config.HasPassword && !File.Exists(InitializedPath)) WriteDurably(InitializedPath, "initialized"u8.ToArray());
        }
    }

    private static void WriteDurably(string path, byte[] content)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>
    /// 创建数据目录并收紧 ACL：仅 SYSTEM 与 Administrators 可访问，普通用户无法读取密码哈希。
    /// 需要管理员权限。
    /// </summary>
    public static void EnsureSecuredDirectory()
    {
        if (!FolderLocker.EnsurePrivileges()) throw new UnauthorizedAccessException("缺少保护配置目录的管理员特权");
        var dir = new DirectoryInfo(DataDirectory);
        var existing = dir.Exists;
        if (!existing) dir.Create();
        using var guard = PathGuard.Pin(DataDirectory);

        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        if (existing)
        {
            var owner = dir.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (owner != system && owner != admins) throw new UnauthorizedAccessException("现有配置目录的所有者不可信；请由管理员核查后再安装。");
        }
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

        security.SetOwner(admins);
        security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));

        dir.SetAccessControl(security);
        foreach (var path in Directory.EnumerateFileSystemEntries(DataDirectory))
        {
            using var fileGuard = PathGuard.Pin(path);
            using var file = Lock.Core.Native.FileSecurity.Open(path, writable: true);
            if (file.IsDirectory || file.LinkCount != 1) throw new IOException("配置目录中存在不安全的子目录或硬链接：" + path);
            file.WriteSddl("O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)");
        }
    }
}
