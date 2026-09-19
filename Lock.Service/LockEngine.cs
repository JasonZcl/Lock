using System.Diagnostics;
using Lock.Core.Ipc;
using Lock.Core.Models;
using Lock.Core.Native;
using Lock.Core.Services;

namespace Lock.Service;

/// <summary>
/// 应用锁核心：匹配新进程、挂起、向对应会话的托盘程序发起解锁请求、根据结果恢复或结束。
/// </summary>
public sealed class LockEngine : IDisposable
{
    private sealed class Pending
    {
        public required string RequestId { get; init; }
        public required string Key { get; init; }
        public required LockedApp App { get; init; }
        public required string ExeName { get; init; }
        public string? FullPath { get; init; }
        public required uint SessionId { get; init; }
        public required ClientSession Agent { get; init; }
        /// <summary>被挂起的进程：pid -> 创建时间。PID 可能被复用，恢复/结束前必须校验。</summary>
        public Dictionary<uint, long> Pids { get; } = new();
        public int Attempts { get; set; }

        public void Add(uint pid)
        {
            var created = NativeMethods.GetProcessCreationTime(pid);
            if (created != null) Pids[pid] = created.Value;
        }
    }

    /// <summary>“跳板进程”判定窗口：父进程已退出、且同一程序在此时间内刚解锁过，则视为同一次启动。</summary>
    private static readonly TimeSpan LauncherWindow = TimeSpan.FromSeconds(5);

    private readonly object _sync = new();
    private readonly ConfigManager _config;
    private readonly AgentHub _agents;
    private readonly ProcessMonitor _monitor;

    // key -> 已解锁的进程：pid -> 创建时间（PID 会被复用，必须连同创建时间一起校验）
    private readonly Dictionary<string, Dictionary<uint, long>> _unlocked = new();
    // requestId -> 等待解锁
    private readonly Dictionary<string, Pending> _pending = new();
    // key -> 最近一次解锁时间（宽限期）
    private readonly Dictionary<string, DateTime> _lastUnlock = new();

    public LockEngine(ConfigManager config, AgentHub agents)
    {
        _config = config;
        _agents = agents;
        _monitor = new ProcessMonitor();
        _monitor.ProcessStarted += OnProcessStarted;
        _agents.Disconnected += OnAgentDisconnected;
    }

    public void Start()
    {
        AdoptRunningProcesses();
        _monitor.Start();
    }

    public int PendingCount { get { lock (_sync) return _pending.Count; } }

    /// <summary>
    /// 把当前已在运行的、与锁定规则匹配的进程登记为“已解锁”。
    /// 否则像企业微信这类多进程程序，在添加锁之前就已运行的实例后续派生子进程时会被误拦截。
    /// 服务启动和设置变更时都要调用。
    /// </summary>
    public void AdoptRunningProcesses()
    {
        var settings = _config.GetSettings();
        if (settings.LockedApps.Count == 0) return;

        var buffer = new uint[1024];
        var count = NativeMethods.EnumProcessIds(ref buffer);
        var adopted = 0;

        lock (_sync)
        {
            for (var i = 0; i < count; i++)
            {
                var pid = buffer[i];
                if (pid == 0 || pid == (uint)Environment.ProcessId) continue;

                var path = NativeMethods.GetProcessImagePath(pid);
                if (path == null) continue;

                var exe = Path.GetFileName(path).ToLowerInvariant();
                var app = settings.LockedApps.FirstOrDefault(a => a.Matches(exe, path));
                if (app == null) continue;

                // 已经在等待解锁的不动
                if (_pending.Values.Any(p => p.Pids.ContainsKey(pid))) continue;

                MarkUnlocked(app.Key, pid);
                adopted++;
            }
        }

        if (adopted > 0)
            ServiceLog.Info($"已将 {adopted} 个运行中的匹配进程登记为已解锁（添加锁之前已在运行）");
    }

    // ---- 新进程 ----

    private void OnProcessStarted(ProcessMonitor.ProcessInfo info)
    {
        var settings = _config.GetSettings();
        if (settings.Paused || !_config.HasPassword) return;

        var app = settings.LockedApps.FirstOrDefault(a => a.Matches(info.ExeName, info.FullPath));
        if (app == null)
        {
            // 同名但路径不同的程序不拦截，记一条诊断日志方便排查“为什么没锁住”
            if (settings.LockedApps.Any(a => a.Enabled && a.ExeName == info.ExeName))
                ServiceLog.Info($"路径不匹配，未拦截：pid={info.Pid} {info.FullPath}");
            return;
        }

        // 不拦截自己和托盘程序
        if (info.Pid == (uint)Environment.ProcessId) return;
        if (_agents.All().Any(c => c.ProcessId == info.Pid)) return;

        var key = app.Key;
        var sessionId = NativeMethods.GetProcessSessionId(info.Pid) ?? 0;
        Pending? created = null;

        lock (_sync)
        {
            // 1. 宽限期
            if (settings.UnlockGraceSeconds > 0
                && _lastUnlock.TryGetValue(key, out var last)
                && (DateTime.UtcNow - last).TotalSeconds < settings.UnlockGraceSeconds)
            {
                MarkUnlocked(key, info.Pid);
                Log(LogEvents.GracePass, app, info, sessionId, null);
                return;
            }

            // 2. 父进程（或祖先）是已解锁的同一程序 → 这是它自己派生的子进程，放行。
            //    从桌面/启动器点开的新实例父进程是 explorer 之类，不会命中，必须输密码。
            var (ancestor, parentGone) = FindUnlockedAncestor(key, info.Pid);
            if (ancestor != null)
            {
                MarkUnlocked(key, info.Pid);
                Log(LogEvents.ChildPass, app, info, sessionId, null, $"父进程 {ancestor}");
                return;
            }

            // 2b. “跳板”模式：A 解锁后启动 B 并立刻退出（Electron/Squirrel、各种 Launcher 常见）。
            //     B 的父进程已经不存在无法校验，但该程序几秒内刚解锁过，视为同一次启动，避免连弹两次密码框。
            if (parentGone
                && _lastUnlock.TryGetValue(key, out var recent)
                && (DateTime.UtcNow - recent) < LauncherWindow)
            {
                MarkUnlocked(key, info.Pid);
                Log(LogEvents.ChildPass, app, info, sessionId, null, "父进程已退出，解锁后数秒内启动");
                return;
            }

            // 3. 挂起
            if (!NativeMethods.SuspendProcess(info.Pid))
            {
                Log(LogEvents.SuspendFailed, app, info, sessionId, null);
                return;
            }

            // 4. 已有同一程序在等待解锁 → 并入同一批
            var existing = _pending.Values.FirstOrDefault(p => p.Key == key && p.SessionId == sessionId);
            if (existing != null)
            {
                existing.Add(info.Pid);
                return;
            }

            // 5. 找该会话的托盘程序；没有则直接结束
            var agent = _agents.Find(sessionId);
            if (agent == null)
            {
                Kill(info.Pid);
                Log(LogEvents.NoAgent, app, info, sessionId, null);
                return;
            }

            created = new Pending
            {
                RequestId = Guid.NewGuid().ToString("N"),
                Key = key,
                App = app,
                ExeName = info.ExeName,
                FullPath = info.FullPath,
                SessionId = sessionId,
                Agent = agent,
            };
            created.Add(info.Pid);
            _pending[created.RequestId] = created;
        }

        _ = created.Agent.SendEventAsync(Protocol.EvUnlockRequest, new UnlockRequestEvent
        {
            RequestId = created.RequestId,
            DisplayName = DisplayNameOf(created.App),
            ExeName = created.ExeName,
            FullPath = created.FullPath ?? created.App.FullPath,
            MaxAttempts = settings.MaxAttempts,
        });
    }

    // ---- 托盘程序的回应 ----

    public UnlockResultData Attempt(ClientSession from, string requestId, string password)
    {
        Pending? pending;
        bool success;
        int remaining;
        Dictionary<uint, long> pids;

        lock (_sync)
        {
            if (!_pending.TryGetValue(requestId, out pending) || pending.Agent != from)
                return new UnlockResultData { Success = false, Remaining = 0 };

            success = _config.VerifyPassword(password);
            var max = Math.Max(1, _config.GetSettings().MaxAttempts);

            if (success)
            {
                _pending.Remove(requestId);
                _lastUnlock[pending.Key] = DateTime.UtcNow;
                foreach (var pid in pending.Pids.Keys) MarkUnlocked(pending.Key, pid);
                remaining = 0;
            }
            else
            {
                pending.Attempts++;
                remaining = Math.Max(0, max - pending.Attempts);
                if (remaining == 0) _pending.Remove(requestId);
            }

            pids = new Dictionary<uint, long>(pending.Pids);
        }

        if (success)
        {
            foreach (var (pid, created) in pids) ResumeIfSame(pid, created);
            Log(LogEvents.Unlocked, pending, from.UserName);
        }
        else
        {
            Log(LogEvents.WrongPassword, pending, from.UserName, $"剩余 {remaining} 次");
            if (remaining == 0)
            {
                KillAll(pids);
                Log(LogEvents.AttemptsExceeded, pending, from.UserName);
                _ = from.SendEventAsync(Protocol.EvUnlockClosed, new UnlockClosedEvent { RequestId = requestId, Reason = LogEvents.AttemptsExceeded });
            }
        }

        return new UnlockResultData { Success = success, Remaining = remaining };
    }

    public void Cancel(ClientSession from, string requestId)
    {
        Pending? pending;
        lock (_sync)
        {
            if (!_pending.TryGetValue(requestId, out pending) || pending.Agent != from) return;
            _pending.Remove(requestId);
        }

        KillAll(pending.Pids);
        Log(LogEvents.Cancelled, pending, from.UserName);
    }

    private void OnAgentDisconnected(ClientSession client)
    {
        List<Pending> lost;
        lock (_sync)
        {
            lost = _pending.Values.Where(p => p.Agent == client).ToList();
            foreach (var p in lost) _pending.Remove(p.RequestId);
        }

        foreach (var p in lost)
        {
            KillAll(p.Pids);
            Log(LogEvents.AgentLost, p, client.UserName);
        }
    }

    // ---- 辅助（要求已持有 _sync 的标注方法） ----

    private void MarkUnlocked(string key, uint pid)
    {
        // 拿不到创建时间（进程已退出）就没必要记录
        var created = NativeMethods.GetProcessCreationTime(pid);
        if (created == null) return;

        if (!_unlocked.TryGetValue(key, out var map))
            _unlocked[key] = map = new Dictionary<uint, long>();
        map[pid] = created.Value;
    }

    /// <summary>该 pid 是否仍是当初登记的那个已解锁进程（活着且创建时间一致，排除 PID 复用）。</summary>
    private bool IsUnlockedProcess(string key, uint pid)
    {
        if (!_unlocked.TryGetValue(key, out var map) || !map.TryGetValue(pid, out var created)) return false;

        var now = NativeMethods.GetProcessCreationTime(pid);
        if (now == created) return true;

        // 已退出或 PID 被别的进程复用，清理掉
        map.Remove(pid);
        if (map.Count == 0) _unlocked.Remove(key);
        return false;
    }

    /// <summary>
    /// 沿父进程链向上找已解锁的同一程序实例。
    /// 返回 (祖先 pid, 父进程是否已退出)。父进程 PID 可能已被复用，用“父进程创建时间早于子进程”来校验。
    /// </summary>
    private (uint? Ancestor, bool ParentGone) FindUnlockedAncestor(string key, uint pid)
    {
        var childCreated = NativeMethods.GetProcessCreationTime(pid);
        var current = pid;

        for (var depth = 0; depth < 8 && childCreated != null; depth++)
        {
            var parent = NativeMethods.GetParentProcessId(current);
            if (parent is null or 0) return (null, depth == 0);

            var parentCreated = NativeMethods.GetProcessCreationTime(parent.Value);
            // 父进程已退出（或 PID 被更晚创建的进程复用）
            if (parentCreated == null || parentCreated > childCreated) return (null, depth == 0);

            if (_unlocked.ContainsKey(key) && IsUnlockedProcess(key, parent.Value)) return (parent, false);

            current = parent.Value;
            childCreated = parentCreated;
        }
        return (null, false);
    }

    private static void Kill(uint pid)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            p.Kill(entireProcessTree: true);
        }
        catch
        {
            // 已退出或无权限
        }
    }

    /// <summary>只结束仍是“当初那个进程”的 pid（创建时间一致），避免 PID 被复用后误杀无关进程。</summary>
    private static void KillAll(Dictionary<uint, long> pids)
    {
        foreach (var (pid, created) in pids)
        {
            if (NativeMethods.GetProcessCreationTime(pid) == created) Kill(pid);
        }
    }

    private static void ResumeIfSame(uint pid, long created)
    {
        if (NativeMethods.GetProcessCreationTime(pid) == created) NativeMethods.ResumeProcess(pid);
    }

    private static string DisplayNameOf(LockedApp app)
        => string.IsNullOrWhiteSpace(app.DisplayName) ? app.ExeName : app.DisplayName;

    private static void Log(string evt, LockedApp app, ProcessMonitor.ProcessInfo info, uint sessionId, string? user, string? detail = null)
    {
        LogStore.Append(new LogEntry
        {
            Event = evt,
            DisplayName = DisplayNameOf(app),
            ExeName = info.ExeName,
            FullPath = info.FullPath,
            Pid = info.Pid,
            SessionId = sessionId,
            User = user,
            Detail = detail,
        });
    }

    private static void Log(string evt, Pending p, string? user, string? detail = null)
    {
        LogStore.Append(new LogEntry
        {
            Event = evt,
            DisplayName = DisplayNameOf(p.App),
            ExeName = p.ExeName,
            FullPath = p.FullPath,
            Pid = p.Pids.Keys.FirstOrDefault(),
            SessionId = p.SessionId,
            User = user,
            Detail = detail ?? (p.Pids.Count > 1 ? $"共 {p.Pids.Count} 个进程" : null),
        });
    }

    /// <summary>服务停止时结束所有仍挂起的进程，避免留下无法恢复的“僵尸”。</summary>
    public void Dispose()
    {
        _monitor.Dispose();
        _agents.Disconnected -= OnAgentDisconnected;

        List<Pending> all;
        lock (_sync)
        {
            all = [.. _pending.Values];
            _pending.Clear();
        }
        foreach (var p in all) KillAll(p.Pids);
    }
}
