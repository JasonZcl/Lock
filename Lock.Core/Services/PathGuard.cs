using Lock.Core.Native;

namespace Lock.Core.Services;

public sealed class PathGuard : IDisposable
{
    private readonly List<FileSecurity> _handles = [];

    public static PathGuard Pin(string path)
    {
        path = FolderLocker.Normalize(path);
        var guard = new PathGuard();
        try
        {
            var root = Path.GetPathRoot(path)!;
            var current = root;
            guard._handles.Add(FileSecurity.Open(current));
            foreach (var component in path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, component);
                guard._handles.Add(FileSecurity.Open(current));
            }
            return guard;
        }
        catch
        {
            guard.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        foreach (var handle in _handles.AsEnumerable().Reverse()) handle.Dispose();
        _handles.Clear();
    }
}
