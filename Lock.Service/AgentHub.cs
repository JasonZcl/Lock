using Lock.Core.Native;
using Lock.Core.Ipc;

namespace Lock.Service;

/// <summary>
/// 记录当前连接的托盘程序，按终端会话查找。
/// </summary>
public sealed class AgentHub
{
    private readonly object _sync = new();
    private readonly List<ClientSession> _clients = [];

    /// <summary>正版托盘程序的路径：与服务同目录的 AppLock.exe。</summary>
    private static readonly string ExpectedAgentPath =
        Path.Combine(AppContext.BaseDirectory, "AppLock.exe");

    public event Action<ClientSession>? Disconnected;

    public void Add(ClientSession client)
    {
        lock (_sync) _clients.Add(client);
    }

    public void Remove(ClientSession client)
    {
        bool removed;
        lock (_sync) removed = _clients.Remove(client);
        if (removed) Disconnected?.Invoke(client);
    }

    /// <summary>
    /// 找到指定会话中的正版托盘程序；仅显式开启的独立调试管道允许测试客户端。
    /// </summary>
    public ClientSession? Find(uint sessionId)
    {
        lock (_sync)
        {
            ClientSession? fallback = null;
            for (var i = _clients.Count - 1; i >= 0; i--)
            {
                var c = _clients[i];
                if (c.SessionId != sessionId || !c.IsConnected) continue;
                if (IsGenuine(c)) return c;
                fallback ??= c;
            }
            return Protocol.AllowDebugClients ? fallback : null;
        }
    }

    public static bool IsGenuine(ClientSession c)
    {
        if (c.ProcessId is not { } pid) return false;
        if (c.ProcessCreatedAt == null || NativeMethods.GetProcessCreationTime(pid) != c.ProcessCreatedAt) return false;
        var path = NativeMethods.GetProcessImagePath(pid);
        return path != null && string.Equals(path, ExpectedAgentPath, StringComparison.OrdinalIgnoreCase);
    }

    public List<ClientSession> All()
    {
        lock (_sync) return [.. _clients];
    }
}
