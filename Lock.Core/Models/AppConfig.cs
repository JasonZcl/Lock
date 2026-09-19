using System.Text.Json.Serialization;

namespace Lock.Core.Models;

/// <summary>
/// 服务端持久化到 %ProgramData%\AppLock\config.json 的全部配置。
/// 只有服务（SYSTEM）能读写；托盘程序通过管道拿到的只有 <see cref="Settings"/> 部分。
/// </summary>
public sealed class AppConfig
{
    /// <summary>解锁密码。为 null 表示尚未初始化。</summary>
    public SecretRecord? Password { get; set; }

    /// <summary>忘记密码时使用的恢复密钥。</summary>
    public SecretRecord? Recovery { get; set; }

    public LockSettings Settings { get; set; } = new();

    [JsonIgnore]
    public bool HasPassword => Password != null;
}

/// <summary>PBKDF2 哈希后的密钥记录。</summary>
public sealed class SecretRecord
{
    public string Hash { get; set; } = "";
    public string Salt { get; set; } = "";
    public int Iterations { get; set; }
}

/// <summary>可以通过管道下发给托盘程序、并允许其（登录后）修改的设置。</summary>
public sealed class LockSettings
{
    public List<LockedApp> LockedApps { get; set; } = [];

    /// <summary>解锁后在多少秒内，同一程序再次启动无需输密码（0 表示每次都要）。</summary>
    public int UnlockGraceSeconds { get; set; } = 0;

    /// <summary>连续输错多少次后自动结束目标进程。</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>是否暂停保护。</summary>
    public bool Paused { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter<MatchMode>))]
public enum MatchMode
{
    /// <summary>只比较可执行文件名（不区分目录）。</summary>
    ExeName,

    /// <summary>比较完整路径（不区分大小写）。</summary>
    FullPath,
}

public sealed class LockedApp
{
    /// <summary>可执行文件名（小写，含 .exe）。</summary>
    public string ExeName { get; set; } = "";

    /// <summary>可执行文件完整路径。MatchMode = FullPath 时必填。</summary>
    public string? FullPath { get; set; }

    public string DisplayName { get; set; } = "";

    public bool Enabled { get; set; } = true;

    public MatchMode MatchMode { get; set; } = MatchMode.FullPath;

    /// <summary>用于区分“同一个被锁程序”的键（多进程放行、宽限期都按此键计算）。</summary>
    [JsonIgnore]
    public string Key => MatchMode == MatchMode.FullPath && !string.IsNullOrEmpty(FullPath)
        ? "path:" + FullPath.ToLowerInvariant()
        : "exe:" + ExeName.ToLowerInvariant();

    public bool Matches(string exeName, string? fullPath)
    {
        if (!Enabled) return false;

        if (MatchMode == MatchMode.FullPath && !string.IsNullOrEmpty(FullPath))
        {
            return fullPath != null && string.Equals(fullPath, FullPath, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(exeName, ExeName, StringComparison.OrdinalIgnoreCase);
    }
}
