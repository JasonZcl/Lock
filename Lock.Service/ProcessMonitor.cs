using System.Diagnostics;
using Lock.Core.Native;

namespace Lock.Service;

/// <summary>
/// 后台线程轮询系统进程列表，发现新进程时回调 <see cref="ProcessStarted"/>（在监控线程上执行）。
/// </summary>
public sealed class ProcessMonitor : IDisposable
{
    public sealed record ProcessInfo(uint Pid, string ExeName, string? FullPath);

    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _cts = new();
    private Thread? _thread;

    public event Action<ProcessInfo>? ProcessStarted;

    public ProcessMonitor(TimeSpan? interval = null)
    {
        _interval = interval ?? TimeSpan.FromMilliseconds(200);
    }

    public void Start()
    {
        if (_thread != null) return;

        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "AppLock.ProcessMonitor",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    private void Loop()
    {
        var buffer = new uint[1024];
        var known = new HashSet<uint>();

        // 首次快照：已在运行的进程视为已知，不做拦截
        var count = NativeMethods.EnumProcessIds(ref buffer);
        for (var i = 0; i < count; i++) known.Add(buffer[i]);

        var current = new HashSet<uint>();
        var token = _cts.Token;

        while (!token.IsCancellationRequested)
        {
            try
            {
                count = NativeMethods.EnumProcessIds(ref buffer);
                current.Clear();
                for (var i = 0; i < count; i++) current.Add(buffer[i]);

                foreach (var pid in current)
                {
                    if (pid == 0 || known.Contains(pid)) continue;

                    var info = Resolve(pid);
                    if (info != null)
                    {
                        try { ProcessStarted?.Invoke(info); }
                        catch (Exception ex) { ServiceLog.Error("ProcessStarted 处理异常", ex); }
                    }
                }

                // 用当前快照替换已知集合，PID 被复用时也能重新识别
                known.Clear();
                known.UnionWith(current);
            }
            catch (Exception ex)
            {
                ServiceLog.Error("枚举进程失败", ex);
            }

            if (token.WaitHandle.WaitOne(_interval)) break;
        }
    }

    private static ProcessInfo? Resolve(uint pid)
    {
        var path = NativeMethods.GetProcessImagePath(pid);
        string? exe = null;

        if (!string.IsNullOrEmpty(path))
        {
            exe = Path.GetFileName(path);
        }
        else
        {
            try
            {
                using var p = Process.GetProcessById((int)pid);
                exe = p.ProcessName + ".exe";
            }
            catch
            {
                // 进程已退出或无权访问
            }
        }

        return string.IsNullOrEmpty(exe) ? null : new ProcessInfo(pid, exe.ToLowerInvariant(), path);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(2));
        _cts.Dispose();
    }
}
