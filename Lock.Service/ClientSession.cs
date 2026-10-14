using System.IO.Pipes;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Lock.Core.Ipc;
using Lock.Core.Native;

namespace Lock.Service;

/// <summary>
/// 一个已连接的托盘程序。负责读请求、写响应/事件。
/// </summary>
public sealed class ClientSession : IDisposable
{
    private readonly NamedPipeServerStream _pipe;
    private readonly JsonLineChannel _channel;
    private readonly CancellationTokenSource _cts = new();

    public int Id { get; }
    public uint? ProcessId { get; }
    public long? ProcessCreatedAt { get; }
    public uint SessionId { get; }
    public string? UserName { get; }

    private static int _nextId;

    public ClientSession(NamedPipeServerStream pipe)
    {
        _pipe = pipe;
        _channel = new JsonLineChannel(pipe, leaveOpen: true);
        Id = Interlocked.Increment(ref _nextId);

        ProcessId = NativeMethods.GetPipeClientProcessId(pipe.SafePipeHandle);
        ProcessCreatedAt = ProcessId.HasValue ? NativeMethods.GetProcessCreationTime(ProcessId.Value) : null;
        SessionId = ProcessId.HasValue ? NativeMethods.GetProcessSessionId(ProcessId.Value) ?? 0 : 0;

        try { UserName = pipe.GetImpersonationUserName(); }
        catch { UserName = null; }
    }

    public bool IsConnected => _pipe.IsConnected && !_cts.IsCancellationRequested;

    /// <summary>循环读取请求，直到对方断开。</summary>
    public async Task RunAsync(Func<ClientSession, JsonObject, Task<JsonObject>> handler, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        var rateWindow = Stopwatch.StartNew();
        var requestCount = 0;

        while (!linked.IsCancellationRequested)
        {
            JsonObject? request;
            try
            {
                request = await _channel.ReadAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (IOException) { break; }

            if (request == null) break;
            if (rateWindow.Elapsed >= TimeSpan.FromSeconds(1))
            {
                requestCount = 0;
                rateWindow.Restart();
            }
            if (++requestCount > 30) throw new InvalidDataException("请求过于频繁");
            if (request["id"] is not JsonValue requestId || !requestId.TryGetValue<int>(out _))
                throw new InvalidDataException("请求编号无效");
            JsonObject response;
            try
            {
                response = await handler(this, request).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ServiceLog.Error("处理请求异常", ex);
                response = new JsonObject { ["ok"] = false, ["error"] = "内部错误" };
            }
            response["id"] = requestId.DeepClone();
            await SafeSendAsync(response).ConfigureAwait(false);
        }
    }

    public Task SendEventAsync<T>(string name, T data)
    {
        return SafeSendAsync(new JsonObject
        {
            ["event"] = name,
            ["data"] = JsonLineChannel.ToNode(data),
        });
    }

    private async Task SafeSendAsync(JsonObject message)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await _channel.SendAsync(message, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 对方已断开，读循环会自行退出
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _channel.Dispose(); } catch { /* ignore */ }
        _cts.Dispose();
    }
}
