using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using Lock.Core.Ipc;
using Lock.Core.Models;

namespace Lock.Services;

public sealed record ApiResult<T>(bool Ok, T? Data, string? Error, string? Code)
{
    public static ApiResult<T> Fail(string error, string? code = null) => new(false, default, error, code);
}

/// <summary>
/// 与服务通信的管道客户端：自动重连、请求/响应配对、事件分发（事件在 UI 线程触发）。
/// </summary>
public sealed class ServiceClient : IDisposable
{
    private static readonly TimeSpan ReconnectInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _sync = new();
    private readonly Dictionary<int, TaskCompletionSource<JsonObject>> _inflight = new();

    private JsonLineChannel? _channel;
    private int _nextId;

    public bool IsConnected { get; private set; }
    public HelloData? Hello { get; private set; }

    /// <summary>管理登录后拿到的 token；未登录为 null。</summary>
    public string? Token { get; private set; }

    public event Action? Connected;
    public event Action? Disconnected;
    public event Action<UnlockRequestEvent>? UnlockRequested;
    public event Action<UnlockClosedEvent>? UnlockClosed;
    public event Action? SettingsChanged;

    public ServiceClient(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    /// <summary>后台持续维持连接。</summary>
    public void Start() => _ = Task.Run(() => MaintainAsync(_cts.Token));

    private async Task MaintainAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeClientStream? pipe = null;
            try
            {
                pipe = new NamedPipeClientStream(".", Protocol.PipeName, PipeDirection.InOut,
                    PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
                await pipe.ConnectAsync(1000, ct).ConfigureAwait(false);

                var channel = new JsonLineChannel(pipe);
                lock (_sync) _channel = channel;

                // 先起读循环，握手响应才有人收
                var readTask = ReadLoopAsync(channel, ct);

                var hello = await RequestAsync<HelloData>(Protocol.Hello, null, ct).ConfigureAwait(false);
                if (!hello.Ok || hello.Data == null) throw new IOException("握手失败：" + hello.Error);

                Hello = hello.Data;
                IsConnected = true;
                Raise(Connected);

                await readTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                // 服务未运行或连接中断，稍后重试
            }
            finally
            {
                var wasConnected = IsConnected;
                IsConnected = false;
                Token = null;

                JsonLineChannel? old;
                lock (_sync) { old = _channel; _channel = null; }
                old?.Dispose();
                pipe?.Dispose();
                FailAllInflight("连接已断开");

                if (wasConnected) Raise(Disconnected);
            }

            try { await Task.Delay(ReconnectInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ReadLoopAsync(JsonLineChannel channel, CancellationToken ct)
    {
        while (true)
        {
            var msg = await channel.ReadAsync(ct).ConfigureAwait(false);
            if (msg == null) return;

            if (msg["id"] is { } idNode)
            {
                TaskCompletionSource<JsonObject>? tcs;
                lock (_sync) _inflight.Remove(idNode.GetValue<int>(), out tcs);
                tcs?.TrySetResult(msg);
                continue;
            }

            var evt = msg["event"]?.GetValue<string>();
            var data = msg["data"];
            switch (evt)
            {
                case Protocol.EvUnlockRequest:
                    var req = JsonLineChannel.FromNode<UnlockRequestEvent>(data);
                    if (req != null) Raise(() => UnlockRequested?.Invoke(req));
                    break;
                case Protocol.EvUnlockClosed:
                    var closed = JsonLineChannel.FromNode<UnlockClosedEvent>(data);
                    if (closed != null) Raise(() => UnlockClosed?.Invoke(closed));
                    break;
                case Protocol.EvSettingsChanged:
                    Raise(SettingsChanged);
                    break;
            }
        }
    }

    private void Raise(Action? action)
    {
        if (action == null) return;
        _dispatcher.BeginInvoke(action);
    }

    private void FailAllInflight(string reason)
    {
        List<TaskCompletionSource<JsonObject>> pending;
        lock (_sync)
        {
            pending = [.. _inflight.Values];
            _inflight.Clear();
        }
        foreach (var tcs in pending)
            tcs.TrySetResult(new JsonObject { ["ok"] = false, ["error"] = reason });
    }

    // ---- 请求 ----

    private async Task<ApiResult<T>> RequestAsync<T>(string type, object? data, CancellationToken ct = default, bool withToken = false)
    {
        JsonLineChannel? channel;
        lock (_sync) channel = _channel;
        if (channel == null) return ApiResult<T>.Fail("服务未连接", "disconnected");

        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync) _inflight[id] = tcs;

        var request = new JsonObject { ["id"] = id, ["type"] = type };
        if (withToken) request["token"] = Token;
        if (data != null) request["data"] = JsonLineChannel.ToNode(data);

        try
        {
            await channel.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            lock (_sync) _inflight.Remove(id);
            return ApiResult<T>.Fail("发送失败：" + ex.Message, "disconnected");
        }

        using var timeout = new CancellationTokenSource(RequestTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token, _cts.Token);
        JsonObject response;
        try
        {
            response = await tcs.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            lock (_sync) _inflight.Remove(id);
            return ApiResult<T>.Fail("请求超时", "timeout");
        }

        var ok = response["ok"]?.GetValue<bool>() == true;
        if (!ok)
        {
            var code = response["code"]?.GetValue<string>();
            // 服务端 token 30 分钟过期；被拒后清掉本地 token，UI 才会重新要求登录
            if (code == "unauthorized") Token = null;
            return ApiResult<T>.Fail(response["error"]?.GetValue<string>() ?? "未知错误", code);
        }

        return new ApiResult<T>(true, JsonLineChannel.FromNode<T>(response["data"]), null, null);
    }

    public sealed class Empty { }

    public Task<ApiResult<StatusData>> GetStatusAsync() => RequestAsync<StatusData>(Protocol.GetStatus, null);

    public Task<ApiResult<RecoveryKeyData>> SetupAsync(string password)
        => RequestAsync<RecoveryKeyData>(Protocol.Setup, new PasswordData { Password = password });

    public async Task<ApiResult<LoginData>> LoginAsync(string password)
    {
        var r = await RequestAsync<LoginData>(Protocol.Login, new PasswordData { Password = password });
        if (r.Ok) Token = r.Data?.Token;
        return r;
    }

    public async Task LogoutAsync()
    {
        if (Token == null) return;
        await RequestAsync<Empty>(Protocol.Logout, null, withToken: true);
        Token = null;
    }

    public Task<ApiResult<LockSettings>> GetSettingsAsync()
        => RequestAsync<LockSettings>(Protocol.GetSettings, null, withToken: true);

    public Task<ApiResult<Empty>> SetSettingsAsync(LockSettings settings)
        => RequestAsync<Empty>(Protocol.SetSettings, settings, withToken: true);

    public Task<ApiResult<RecoveryKeyData>> ChangePasswordAsync(string newPassword)
        => RequestAsync<RecoveryKeyData>(Protocol.ChangePassword, new PasswordData { Password = newPassword }, withToken: true);

    public Task<ApiResult<RecoveryKeyData>> ResetPasswordAsync(string recoveryKey, string newPassword)
        => RequestAsync<RecoveryKeyData>(Protocol.ResetPassword, new ResetPasswordData { RecoveryKey = recoveryKey, NewPassword = newPassword });

    public Task<ApiResult<LogListData>> GetLogAsync(int offset, int count)
        => RequestAsync<LogListData>(Protocol.GetLog, new GetLogData { Offset = offset, Count = count }, withToken: true);

    public Task<ApiResult<UnlockResultData>> UnlockAttemptAsync(string requestId, string password)
        => RequestAsync<UnlockResultData>(Protocol.UnlockAttempt, new UnlockAttemptData { RequestId = requestId, Password = password });

    public Task UnlockCancelAsync(string requestId)
        => RequestAsync<Empty>(Protocol.UnlockCancel, new UnlockCancelData { RequestId = requestId });

    public void Dispose()
    {
        _cts.Cancel();
        JsonLineChannel? ch;
        lock (_sync) { ch = _channel; _channel = null; }
        ch?.Dispose();
        _cts.Dispose();
    }
}
