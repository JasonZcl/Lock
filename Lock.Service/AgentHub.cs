using Lock.Core.Native;

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
        Path.Combine(Path.GetDirectoryName(Environment.ProcessPath!) ?? "", "AppLock.exe");

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
    /// 找到指定会话中的托盘程序。优先返回可执行文件是正版 AppLock.exe 的客户端，
    /// 防止同会话里别的程序连上管道后截走解锁请求（它拿不到密码，但能让被锁程序一直卡住）。
    /// 找不到正版时退回任意客户端，方便前台调试。
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
            return fallback;
        }
    }

    public static bool IsGenuine(ClientSession c)
    {
        if (c.ProcessId is not { } pid) return false;
        var path = NativeMethods.GetProcessImagePath(pid);
        return path != null && string.Equals(path, ExpectedAgentPath, StringComparison.OrdinalIgnoreCase);
    }

    public List<ClientSession> All()
    {
        lock (_sync) return [.. _clients];
    }
}
