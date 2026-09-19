namespace Lock.Core.Models;

/// <summary>解锁日志中的一条记录。</summary>
public sealed class LogEntry
{
    public DateTime Time { get; set; } = DateTime.Now;

    /// <summary>事件类型，见 <see cref="LogEvents"/>。</summary>
    public string Event { get; set; } = "";

    public string DisplayName { get; set; } = "";
    public string ExeName { get; set; } = "";
    public string? FullPath { get; set; }
    public uint Pid { get; set; }
    public uint SessionId { get; set; }

    /// <summary>发起操作的用户名（DOMAIN\user），可能为空。</summary>
    public string? User { get; set; }

    public string? Detail { get; set; }
}

public static class LogEvents
{
    public const string Unlocked = "解锁成功";
    public const string WrongPassword = "密码错误";
    public const string Cancelled = "用户取消";
    public const string AttemptsExceeded = "超出尝试次数";
    public const string NoAgent = "无托盘程序，已结束";
    public const string AgentLost = "托盘程序断开，已结束";
    public const string GracePass = "宽限期放行";
    public const string ChildPass = "子进程放行";
    public const string SuspendFailed = "挂起失败";
    public const string PasswordChanged = "密码已修改";
    public const string PasswordReset = "密码已通过恢复密钥重置";
    public const string SettingsChanged = "设置已修改";
    public const string LoginFailed = "管理登录密码错误";
}
