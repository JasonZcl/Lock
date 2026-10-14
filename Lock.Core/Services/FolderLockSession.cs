using Lock.Core.Models;
using Lock.Core.Native;

namespace Lock.Core.Services;

public sealed class FolderLockSession : IDisposable
{
    private const string LockedSddl = "O:SYG:SYD:P(A;;FA;;;SY)";
    private readonly PathGuard _guard;
    private readonly List<(FileSecurity File, FolderAclEntry Entry)> _objects = [];
    private readonly string _path;
    public List<FolderAclEntry> Entries => _objects.Select(item => new FolderAclEntry
    {
        RelativePath = item.Entry.RelativePath, Identity = item.Entry.Identity, Sddl = item.Entry.Sddl,
    }).ToList();

    private FolderLockSession(string path)
    {
        _path = FolderLocker.Normalize(path);
        if (!FolderLocker.EnsurePrivileges()) throw new UnauthorizedAccessException("当前进程没有修改文件夹所有者的特权");
        _guard = PathGuard.Pin(_path);
        if (FolderLocker.WhyForbidden(_path) is { } reason)
        {
            _guard.Dispose();
            throw new IOException(reason);
        }
    }

    public static FolderLockSession Capture(string path, IReadOnlyList<FolderAclEntry>? previous = null)
    {
        var session = new FolderLockSession(path);
        try
        {
            var saved = previous?.ToDictionary(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase);
            session.Visit(session._path, saved);
            if (saved != null && saved.Count != session._objects.Count) throw new IOException("锁定期间文件夹内容发生变化，请先恢复权限");
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    private void Visit(string path, Dictionary<string, FolderAclEntry>? saved, int depth = 0)
    {
        if (depth > 128) throw new IOException("文件夹嵌套层级超过 128");
        if (_objects.Count >= 10000) throw new IOException("单个文件夹最多保护 10000 个文件和目录");
        var file = FileSecurity.Open(path, writable: true);
        try
        {
            if (!file.IsDirectory && file.LinkCount != 1) throw new IOException("不允许锁定含硬链接的文件：" + path);
            var relative = string.Equals(path, _path, StringComparison.OrdinalIgnoreCase) ? "" : Path.GetRelativePath(_path, path);
            string sddl;
            if (saved != null)
            {
                if (!saved.TryGetValue(relative, out var original) || original.Identity != file.Identity)
                    throw new IOException("文件身份与恢复记录不一致：" + path);
                sddl = original.Sddl;
            }
            else sddl = file.ReadSddl();
            _objects.Add((file, new FolderAclEntry { RelativePath = relative, Identity = file.Identity, Sddl = sddl }));
        }
        catch
        {
            file.Dispose();
            throw;
        }
        if (file.IsDirectory)
            foreach (var child in Directory.EnumerateFileSystemEntries(path).Order(StringComparer.OrdinalIgnoreCase)) Visit(child, saved, depth + 1);
    }

    public void Lock()
    {
        foreach (var item in _objects) item.File.WriteSddl(LockedSddl);
        var expected = _objects.Select(item => item.File.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        VerifyContents(_path, expected);
        foreach (var item in _objects)
            if (!FolderLocker.IsLockDescriptor(item.File.ReadSddl())) throw new IOException("权限锁定验证失败：" + item.File.Path);
    }

    private static void VerifyContents(string path, HashSet<string> expected)
    {
        foreach (var child in Directory.EnumerateFileSystemEntries(path))
        {
            if (!expected.Contains(child)) throw new IOException("锁定时新增了文件，请重试");
            using var file = FileSecurity.Open(child);
            if (file.IsDirectory) VerifyContents(child, expected);
        }
    }

    public void Restore()
    {
        foreach (var item in _objects) item.File.WriteSddl(item.Entry.Sddl);
    }

    public void Dispose()
    {
        foreach (var item in _objects.AsEnumerable().Reverse()) item.File.Dispose();
        _objects.Clear();
        _guard.Dispose();
    }
}
