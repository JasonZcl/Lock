using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json.Nodes;
using Lock.Core.Ipc;
using Lock.Core.Models;
using Lock.Core.Services;

namespace Lock.Service;

/// <summary>
/// 命名管道服务端：接受托盘程序连接并分发请求。
/// </summary>
public sealed class PipeServer
{
    private readonly ConfigManager _config;
    private readonly AgentHub _agents;
    private readonly LockEngine _engine;
    private readonly FolderManager _folders;

    public PipeServer(ConfigManager config, AgentHub agents, LockEngine engine, FolderManager folders)
    {
        _config = config;
        _agents = agents;
        _engine = engine;
        _folders = folders;
        _folders.Changed += () =>
        {
            foreach (var c in _agents.All()) _ = c.SendEventAsync(Protocol.EvFoldersChanged, new { });
        };
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                ServiceLog.Error("创建管道失败", ex);
                await Task.Delay(1000, ct).ConfigureAwait(false);
                continue;
            }

            _ = Task.Run(() => ServeAsync(pipe, ct), ct);
        }
    }

    private static NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        // 任何已登录用户都可以连接；安全性由密码校验保证，而不是管道 ACL
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        // 创建后续实例需要 CreateNewInstance 权限；前台调试（非 SYSTEM）运行时也要能建第二个实例
        using (var me = WindowsIdentity.GetCurrent())
        {
            if (me.User != null)
                security.AddAccessRule(new PipeAccessRule(me.User, PipeAccessRights.FullControl, AccessControlType.Allow));
        }

        return NamedPipeServerStreamAcl.Create(
            Protocol.PipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            security);
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using var client = new ClientSession(pipe);
        ServiceLog.Info($"客户端连接：#{client.Id} pid={client.ProcessId} session={client.SessionId} user={client.UserName}");
        _agents.Add(client);
        try
        {
            await client.RunAsync(HandleAsync, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ServiceLog.Error($"客户端 #{client.Id} 异常", ex);
        }
        finally
        {
            _agents.Remove(client);
            ServiceLog.Info($"客户端断开：#{client.Id}");
        }
    }

    // ---- 请求分发 ----

    private Task<JsonObject> HandleAsync(ClientSession client, JsonObject request)
    {
        var type = request["type"]?.GetValue<string>() ?? "";
        var token = request["token"]?.GetValue<string>();
        var data = request["data"];

        JsonObject result = type switch
        {
            Protocol.Hello => Ok(new HelloData { HasPassword = _config.HasPassword, SessionId = client.SessionId }),
            Protocol.GetStatus => Ok(Status()),
            Protocol.Setup => HandleSetup(Get<PasswordData>(data)),
            Protocol.Login => HandleLogin(client, Get<PasswordData>(data)),
            Protocol.Logout => Do(() => _config.Logout(token)),
            Protocol.ResetPassword => HandleReset(client, Get<ResetPasswordData>(data)),
            Protocol.UnlockAttempt => HandleAttempt(client, Get<UnlockAttemptData>(data)),
            Protocol.UnlockCancel => Do(() => _engine.Cancel(client, Get<UnlockCancelData>(data).RequestId)),

            // 文件夹锁
            Protocol.FolderStatus => Ok(_folders.Status(Get<FolderPathData>(data).Path)),
            Protocol.FolderLock => Result(_folders.Lock(Get<FolderPathData>(data).Path, client.UserName)),
            Protocol.FolderUnlock => HandleFolderUnlock(client, token, Get<FolderUnlockData>(data)),
            Protocol.FolderList => Auth(token, () => Ok(_folders.List())),
            Protocol.FolderAdd => Auth(token, () => Result(_folders.Add(Get<FolderPathData>(data).Path, client.UserName))),
            Protocol.FolderRemove => Auth(token, () => Result(_folders.Remove(Get<FolderPathData>(data).Path, client.UserName))),

            // 以下需要登录
            Protocol.GetSettings => Auth(token, () => Ok(_config.GetSettings())),
            Protocol.SetSettings => Auth(token, () => HandleSetSettings(client, Get<LockSettings>(data))),
            Protocol.ChangePassword => Auth(token, () => HandleChangePassword(client, Get<PasswordData>(data))),
            Protocol.GetLog => Auth(token, () => HandleGetLog(Get<GetLogData>(data))),

            _ => Fail("未知请求类型：" + type),
        };

        return Task.FromResult(result);
    }

    private StatusData Status()
    {
        var s = _config.GetSettings();
        return new StatusData
        {
            HasPassword = _config.HasPassword,
            Paused = s.Paused,
            EnabledLockCount = s.LockedApps.Count(a => a.Enabled),
            PendingCount = _engine.PendingCount,
        };
    }

    private JsonObject HandleSetup(PasswordData d)
    {
        if (d.Password.Length < 4) return Fail("密码长度至少 4 位");
        var key = _config.Setup(d.Password);
        return key == null ? Fail("密码已设置，不能重复初始化") : Ok(new RecoveryKeyData { RecoveryKey = key });
    }

    private JsonObject HandleLogin(ClientSession client, PasswordData d)
    {
        if (_config.LockoutRemainingSeconds is { } locked)
            return Fail($"错误次数过多，请 {locked} 秒后再试", "lockout");

        var token = _config.Login(d.Password);
        if (token != null) return Ok(new LoginData { Token = token });

        LogStore.Append(new LogEntry { Event = LogEvents.LoginFailed, SessionId = client.SessionId, User = client.UserName });
        return Fail(_config.LockoutRemainingSeconds is { } l2 ? $"密码错误，错误次数过多，已锁定 {l2} 秒" : "密码错误");
    }

    private JsonObject HandleReset(ClientSession client, ResetPasswordData d)
    {
        if (d.NewPassword.Length < 4) return Fail("密码长度至少 4 位");
        if (_config.LockoutRemainingSeconds is { } locked)
            return Fail($"错误次数过多，请 {locked} 秒后再试", "lockout");
        var key = _config.ResetPassword(d.RecoveryKey, d.NewPassword);
        if (key == null) return Fail("恢复密钥不正确");

        LogStore.Append(new LogEntry { Event = LogEvents.PasswordReset, SessionId = client.SessionId, User = client.UserName });
        return Ok(new RecoveryKeyData { RecoveryKey = key });
    }

    private JsonObject HandleChangePassword(ClientSession client, PasswordData d)
    {
        if (d.Password.Length < 4) return Fail("密码长度至少 4 位");
        var key = _config.ChangePassword(d.Password);
        LogStore.Append(new LogEntry { Event = LogEvents.PasswordChanged, SessionId = client.SessionId, User = client.UserName });
        return Ok(new RecoveryKeyData { RecoveryKey = key });
    }

    private JsonObject HandleSetSettings(ClientSession client, LockSettings settings)
    {
        _config.SetSettings(settings);
        // 新加的锁对应的程序如果正在运行，它的子进程不应被拦截
        _engine.AdoptRunningProcesses();
        LogStore.Append(new LogEntry
        {
            Event = LogEvents.SettingsChanged,
            SessionId = client.SessionId,
            User = client.UserName,
            Detail = $"{settings.LockedApps.Count(a => a.Enabled)} 个程序启用，暂停={settings.Paused}",
        });

        // 通知其他托盘程序刷新
        foreach (var other in _agents.All().Where(c => c != client))
            _ = other.SendEventAsync(Protocol.EvSettingsChanged, new { });

        return Ok(new { });
    }

    private static JsonObject HandleGetLog(GetLogData d)
    {
        var (entries, total) = LogStore.ReadPage(Math.Max(0, d.Offset), Math.Clamp(d.Count, 1, 500));
        return Ok(new LogListData { Entries = entries, Total = total });
    }

    private JsonObject HandleFolderUnlock(ClientSession client, string? token, FolderUnlockData d)
    {
        // 管理界面已登录 → 免密；右键菜单 → 必须给密码（同样受暴力破解锁定约束）
        if (!_config.ValidateToken(token))
        {
            if (_config.LockoutRemainingSeconds is { } locked)
                return Fail($"错误次数过多，请 {locked} 秒后再试", "lockout");
            if (string.IsNullOrEmpty(d.Password) || !_config.VerifyPassword(d.Password))
            {
                LogStore.Append(new LogEntry { Event = LogEvents.FolderUnlockFailed, FullPath = d.Path, DisplayName = Path.GetFileName(d.Path), SessionId = client.SessionId, User = client.UserName });
                return Fail(_config.LockoutRemainingSeconds is { } l2 ? $"密码错误，错误次数过多，已锁定 {l2} 秒" : "密码错误");
            }
        }
        return Result(_folders.Unlock(d.Path, client.UserName));
    }

    private static JsonObject Result(string? error) => error == null ? Ok(new { }) : Fail(error);

    private JsonObject HandleAttempt(ClientSession client, UnlockAttemptData d)
        => Ok(_engine.Attempt(client, d.RequestId, d.Password));

    // ---- 小工具 ----

    private JsonObject Auth(string? token, Func<JsonObject> action)
        => _config.ValidateToken(token) ? action() : Fail("未登录或登录已过期", "unauthorized");

    private static T Get<T>(JsonNode? node) where T : new()
        => JsonLineChannel.FromNode<T>(node) ?? new T();

    private static JsonObject Ok<T>(T data) => new() { ["ok"] = true, ["data"] = JsonLineChannel.ToNode(data) };

    private static JsonObject Do(Action action)
    {
        action();
        return Ok(new { });
    }

    private static JsonObject Fail(string message, string? code = null)
    {
        var o = new JsonObject { ["ok"] = false, ["error"] = message };
        if (code != null) o["code"] = code;
        return o;
    }
}
