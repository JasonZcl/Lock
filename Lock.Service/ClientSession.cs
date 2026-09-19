using System.IO.Pipes;
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
    public uint SessionId { get; }
    public string? UserName { get; }

    private static int _nextId;

    public ClientSession(NamedPipeServerStream pipe)
    {
        _pipe = pipe;
        _channel = new JsonLineChannel(pipe);
        Id = Interlocked.Increment(ref _nextId);

        ProcessId = NativeMethods.GetPipeClientProcessId(pipe.SafePipeHandle);
        SessionId = ProcessId.HasValue ? NativeMethods.GetProcessSessionId(ProcessId.Value) ?? 0 : 0;

        try { UserName = pipe.GetImpersonationUserName(); }
        catch { UserName = null; }
    }

    public bool IsConnected => _pipe.IsConnected && !_cts.IsCancellationRequested;

    /// <summary>循环读取请求，直到对方断开。</summary>
    public async Task RunAsync(Func<ClientSession, JsonObject, Task<JsonObject>> handler, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);

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

            // 每个请求独立处理，避免慢请求阻塞后续读取
            _ = Task.Run(async () =>
            {
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

                response["id"] = request["id"]?.DeepClone();
                await SafeSendAsync(response).ConfigureAwait(false);
            }, linked.Token);
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
            await _channel.SendAsync(message, _cts.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 对方已断开，读循环会自行退出
            _cts.Cancel();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _channel.Dispose(); } catch { /* ignore */ }
        _cts.Dispose();
    }
}
