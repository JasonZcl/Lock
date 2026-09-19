using Lock.Core.Models;

namespace Lock.Core.Ipc;

/// <summary>
/// 管道协议。每行一个 JSON 对象：
///   请求  { "id": n, "type": "...", "token": "...", "data": {...} }
///   响应  { "id": n, "ok": true/false, "error": "...", "data": {...} }
///   事件  { "event": "...", "data": {...} }  （服务端主动推送）
/// </summary>
public static class Protocol
{
    /// <summary>管道名。开发调试时可用环境变量 APPLOCK_PIPE 覆盖，以便与已安装的服务并存。</summary>
    public static readonly string PipeName = Environment.GetEnvironmentVariable("APPLOCK_PIPE") is { Length: > 0 } p ? p : "AppLock.v1";
    public const int Version = 1;

    // ---- 请求类型 ----
    public const string Hello = "hello";
    public const string Setup = "setup";               // 首次设置密码（无密码时才允许）
    public const string Login = "login";               // 密码 → token
    public const string Logout = "logout";
    public const string GetStatus = "getStatus";
    public const string GetSettings = "getSettings";   // 需 token
    public const string SetSettings = "setSettings";   // 需 token
    public const string ChangePassword = "changePassword"; // 需 token
    public const string ResetPassword = "resetPassword";   // 用恢复密钥
    public const string GetLog = "getLog";             // 需 token
    public const string UnlockAttempt = "unlockAttempt";
    public const string UnlockCancel = "unlockCancel";

    // ---- 事件类型 ----
    public const string EvUnlockRequest = "unlockRequest";
    public const string EvUnlockClosed = "unlockClosed";
    public const string EvSettingsChanged = "settingsChanged";
}

public sealed class HelloData
{
    public int Version { get; set; } = Protocol.Version;
    public bool HasPassword { get; set; }
    public uint SessionId { get; set; }
}

public sealed class PasswordData
{
    public string Password { get; set; } = "";
}

public sealed class LoginData
{
    public string Token { get; set; } = "";
}

public sealed class RecoveryKeyData
{
    public string RecoveryKey { get; set; } = "";
}

public sealed class ResetPasswordData
{
    public string RecoveryKey { get; set; } = "";
    public string NewPassword { get; set; } = "";
}

public sealed class StatusData
{
    public bool HasPassword { get; set; }
    public bool Paused { get; set; }
    public int EnabledLockCount { get; set; }
    public int PendingCount { get; set; }
}

public sealed class GetLogData
{
    /// <summary>跳过最新的多少条（按时间倒序分页）。</summary>
    public int Offset { get; set; }
    public int Count { get; set; } = 50;
}

public sealed class LogListData
{
    public List<LogEntry> Entries { get; set; } = [];
    public int Total { get; set; }
}

public sealed class UnlockAttemptData
{
    public string RequestId { get; set; } = "";
    public string Password { get; set; } = "";
}

public sealed class UnlockResultData
{
    public bool Success { get; set; }
    public int Remaining { get; set; }
}

public sealed class UnlockCancelData
{
    public string RequestId { get; set; } = "";
}

public sealed class UnlockRequestEvent
{
    public string RequestId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string ExeName { get; set; } = "";
    public string? FullPath { get; set; }
    public int MaxAttempts { get; set; }
}

public sealed class UnlockClosedEvent
{
    public string RequestId { get; set; } = "";
    public string Reason { get; set; } = "";
}
