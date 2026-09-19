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
    public static string LogPath { get; } = Path.Combine(DataDirectory, "unlock.log");
    public static string ServiceLogPath { get; } = Path.Combine(DataDirectory, "service.log");

    public static AppConfig Load()
    {
        lock (Sync)
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), JsonOptions);
                    if (cfg != null)
                    {
                        foreach (var app in cfg.Settings.LockedApps)
                            app.ExeName = app.ExeName.ToLowerInvariant();
                        return cfg;
                    }
                }
            }
            catch
            {
                // 配置损坏时备份原文件后回退到默认配置，不阻塞启动
                try { File.Copy(ConfigPath, ConfigPath + ".corrupt.bak", overwrite: true); } catch { /* ignore */ }
            }

            return new AppConfig();
        }
    }

    public static void Save(AppConfig config)
    {
        lock (Sync)
        {
            Directory.CreateDirectory(DataDirectory);
            var tmp = ConfigPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(config, JsonOptions));
            File.Move(tmp, ConfigPath, overwrite: true);
        }
    }

    /// <summary>
    /// 创建数据目录并收紧 ACL：仅 SYSTEM 与 Administrators 可访问，普通用户无法读取密码哈希。
    /// 需要管理员权限。
    /// </summary>
    public static void EnsureSecuredDirectory()
    {
        var dir = new DirectoryInfo(DataDirectory);
        if (!dir.Exists) dir.Create();

        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

        security.SetOwner(admins);
        security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));

        dir.SetAccessControl(security);
    }
}
