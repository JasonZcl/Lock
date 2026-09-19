using Lock.Core.Ipc;
using Lock.Core.Models;
using Lock.Core.Services;

namespace Lock.Service;

/// <summary>
/// 文件夹锁：维护列表与锁定状态、执行 ACL 操作、到期自动重新锁定。
/// 所有 ACL 操作串行执行（_opLock），避免同一文件夹并发锁/解锁。
/// </summary>
public sealed class FolderManager : IDisposable
{
    private readonly ConfigManager _config;
    private readonly AgentHub _agents;
    private readonly object _opLock = new();
    private readonly Timer _relockTimer;

    public event Action? Changed;

    public FolderManager(ConfigManager config, AgentHub agents)
    {
        _config = config;
        _agents = agents;
        _relockTimer = new Timer(_ => RelockExpired(), null, Timeout.Infinite, Timeout.Infinite);
        _agents.Disconnected += OnAgentDisconnected;
    }

    /// <summary>服务启动：所有纳管文件夹一律锁上（fail-closed），并启动定时器。</summary>
    public void Start()
    {
        if (!FolderLocker.EnsurePrivileges())
            ServiceLog.Error("无法启用 SeRestorePrivilege，文件夹锁不可用（服务未以 SYSTEM 运行？）");

        foreach (var f in _config.GetFoldersSnapshot())
        {
            var err = DoLock(f.Path, LogEvents.FolderLocked, "服务启动");
            if (err != null) ServiceLog.Error($"启动时锁定失败 {f.Path}: {err}");
        }
        _relockTimer.Change(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
    }

    // ---- 查询 ----

    public FolderStatusData Status(string path)
    {
        path = SafeNormalize(path) ?? path;
        var relock = _config.GetSettings().FolderRelockMinutes;
        var f = _config.GetFoldersSnapshot().FirstOrDefault(x => PathEquals(x.Path, path));
        return new FolderStatusData
        {
            Managed = f != null,
            Folder = f == null ? null : ToInfo(f, relock),
            ForbiddenReason = f == null ? FolderLocker.WhyForbidden(path) : null,
            RelockMinutes = relock,
        };
    }

    public FolderListData List()
    {
        var relock = _config.GetSettings().FolderRelockMinutes;
        return new FolderListData
        {
            Folders = _config.GetFoldersSnapshot()
                .OrderBy(f => f.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .Select(f => ToInfo(f, relock)).ToList(),
        };
    }

    private static FolderInfo ToInfo(LockedFolder f, int relockMinutes)
    {
        int? relockIn = null;
        if (!f.Locked && relockMinutes > 0 && f.UnlockedAtUtc != null)
            relockIn = Math.Max(0, (int)(f.UnlockedAtUtc.Value.AddMinutes(relockMinutes) - DateTime.UtcNow).TotalSeconds);
        return new FolderInfo { Path = f.Path, DisplayName = f.DisplayName, Locked = f.Locked, RelockInSeconds = relockIn };
    }

    // ---- 操作（返回 null 表示成功，否则为错误信息）----

    public string? Add(string path, string? user)
    {
        var norm = SafeNormalize(path);
        if (norm == null) return "路径无效";
        if (FolderLocker.WhyForbidden(norm) is { } why) return why;

        var exists = _config.UpdateFolders(list =>
        {
            if (list.Any(f => PathEquals(f.Path, norm))) return (false, true);
            // 已纳管文件夹的上级或下级也不允许，嵌套锁会让权限恢复顺序变得不可靠
            if (list.Any(f => IsNested(f.Path, norm))) return (false, true);
            list.Add(new LockedFolder { Path = norm, DisplayName = Path.GetFileName(norm) is { Length: > 0 } n ? n : norm, Locked = false });
            return (true, false);
        });
        if (exists) return "该文件夹（或其上级/下级）已在列表中";

        Log(LogEvents.FolderAdded, norm, user);
        var err = DoLock(norm, LogEvents.FolderLocked, user);
        if (err != null)
        {
            // 锁不上就别留在列表里
            _config.UpdateFolders(list => (list.RemoveAll(f => PathEquals(f.Path, norm)) > 0, 0));
            return err;
        }
        Changed?.Invoke();
        return null;
    }

    public string? Remove(string path, string? user)
    {
        var norm = SafeNormalize(path) ?? path;
        var err = DoUnlock(norm, LogEvents.FolderUnlocked, user, removing: true);
        if (err != null) return err;

        _config.UpdateFolders(list => (list.RemoveAll(f => PathEquals(f.Path, norm)) > 0, 0));
        Log(LogEvents.FolderRemoved, norm, user);
        Changed?.Invoke();
        return null;
    }

    public string? Lock(string path, string? user)
    {
        var err = DoLock(SafeNormalize(path) ?? path, LogEvents.FolderLocked, user);
        if (err == null) Changed?.Invoke();
        return err;
    }

    public string? Unlock(string path, string? user)
    {
        var err = DoUnlock(SafeNormalize(path) ?? path, LogEvents.FolderUnlocked, user, removing: false);
        if (err == null) Changed?.Invoke();
        return err;
    }

    /// <summary>注销或托盘退出时把该会话已解锁的文件夹全部锁回去。这里简单处理：没有任何托盘在线就全锁。</summary>
    private void OnAgentDisconnected(ClientSession _)
    {
        if (_agents.All().Any(c => c.IsConnected)) return;
        var any = false;
        foreach (var f in _config.GetFoldersSnapshot().Where(f => !f.Locked))
            any |= DoLock(f.Path, LogEvents.FolderRelocked, "托盘程序断开") == null;
        if (any) Changed?.Invoke();
    }

    private void RelockExpired()
    {
        var minutes = _config.GetSettings().FolderRelockMinutes;
        if (minutes <= 0) return;

        var deadline = DateTime.UtcNow.AddMinutes(-minutes);
        var any = false;
        foreach (var f in _config.GetFoldersSnapshot().Where(f => !f.Locked && f.UnlockedAtUtc != null && f.UnlockedAtUtc <= deadline))
            any |= DoLock(f.Path, LogEvents.FolderRelocked, "到期") == null;
        if (any) Changed?.Invoke();
    }

    // ---- 底层 ----

    private string? DoLock(string path, string evt, string? user)
    {
        lock (_opLock)
        {
            var folder = _config.GetFoldersSnapshot().FirstOrDefault(f => PathEquals(f.Path, path));
            if (folder == null) return "该文件夹不在列表中";
            if (!Directory.Exists(path))
            {
                Log(LogEvents.FolderError, path, user, "文件夹不存在");
                return "文件夹不存在（已被移动或删除？）";
            }

            try
            {
                var original = FolderLocker.Lock(path);
                _config.UpdateFolders(list =>
                {
                    var f = list.First(x => PathEquals(x.Path, path));
                    // 磁盘上已是锁定状态时 Lock 返回 null，此时保留之前记录的原始权限
                    if (original != null) f.OriginalSddl = original;
                    f.Locked = true;
                    f.UnlockedAtUtc = null;
                    return (true, 0);
                });
                if (original != null || !folder.Locked) Log(evt, path, user);
                return null;
            }
            catch (Exception ex)
            {
                ServiceLog.Error($"锁定失败 {path}", ex);
                Log(LogEvents.FolderError, path, user, "锁定失败：" + ex.Message);
                return "锁定失败：" + ex.Message;
            }
        }
    }

    private string? DoUnlock(string path, string evt, string? user, bool removing)
    {
        lock (_opLock)
        {
            var folder = _config.GetFoldersSnapshot().FirstOrDefault(f => PathEquals(f.Path, path));
            if (folder == null) return "该文件夹不在列表中";

            // 移除时即使目录已不存在也允许（只是从列表删掉）
            if (!Directory.Exists(path)) return removing ? null : "文件夹不存在（已被移动或删除？）";

            try
            {
                if (FolderLocker.IsLockedOnDisk(path))
                    FolderLocker.Unlock(path, folder.OriginalSddl);

                _config.UpdateFolders(list =>
                {
                    var f = list.First(x => PathEquals(x.Path, path));
                    f.Locked = false;
                    f.UnlockedAtUtc = DateTime.UtcNow;
                    return (true, 0);
                });
                if (!removing) Log(evt, path, user);
                return null;
            }
            catch (Exception ex)
            {
                ServiceLog.Error($"解锁失败 {path}", ex);
                Log(LogEvents.FolderError, path, user, "解锁失败：" + ex.Message);
                return "解锁失败：" + ex.Message;
            }
        }
    }

    private static string? SafeNormalize(string path)
    {
        try { return string.IsNullOrWhiteSpace(path) ? null : FolderLocker.Normalize(path); }
        catch { return null; }
    }

    private static bool PathEquals(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool IsNested(string a, string b)
        => a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
           || b.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static void Log(string evt, string path, string? user, string? detail = null)
    {
        LogStore.Append(new LogEntry
        {
            Event = evt,
            DisplayName = Path.GetFileName(path) is { Length: > 0 } n ? n : path,
            FullPath = path,
            User = user,
            Detail = detail,
        });
    }

    public void Dispose()
    {
        _agents.Disconnected -= OnAgentDisconnected;
        _relockTimer.Dispose();
    }
}
