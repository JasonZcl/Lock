using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Lock.Core.Native;

public static partial class NativeMethods
{
    public const uint PROCESS_SUSPEND_RESUME = 0x0800;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial SafeProcessHandle OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageNameW(SafeProcessHandle hProcess, uint dwFlags, [Out] char[] lpExeName, ref uint lpdwSize);

    [LibraryImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumProcesses([Out] uint[] lpidProcess, uint cb, out uint lpcbNeeded);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ProcessIdToSessionId(uint dwProcessId, out uint pSessionId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(SafePipeHandle hPipe, out uint clientProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(SafeProcessHandle hProcess, out long lpCreationTime, out long lpExitTime, out long lpKernelTime, out long lpUserTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public nint ExitStatus;
        public nint PebBaseAddress;
        public nint AffinityMask;
        public nint BasePriority;
        public nint UniqueProcessId;
        public nint InheritedFromUniqueProcessId;
    }

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationProcess(SafeProcessHandle hProcess, int processInformationClass, ref PROCESS_BASIC_INFORMATION processInformation, int processInformationLength, out int returnLength);

    [LibraryImport("ntdll.dll")]
    private static partial int NtSuspendProcess(SafeProcessHandle hProcess);

    [LibraryImport("ntdll.dll")]
    private static partial int NtResumeProcess(SafeProcessHandle hProcess);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyIcon(nint hIcon);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    // ---- 令牌特权（改文件夹所有者需要 SeRestorePrivilege）----

    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const uint TOKEN_QUERY = 0x0008;
    private const uint SE_PRIVILEGE_ENABLED = 0x0002;

    // 原生布局：DWORD PrivilegeCount; LUID{DWORD,LONG} Luid; DWORD Attributes —— 全部 4 字节对齐，共 16 字节。
    // 必须 Pack=4：否则 long 会被对齐到偏移 8，整个结构错位，AdjustTokenPrivileges 收到垃圾 LUID 报“特权未分配”。
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public long Luid;
        public uint Attributes;
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LookupPrivilegeValueW(string? systemName, string name, out long luid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AdjustTokenPrivileges(nint tokenHandle, [MarshalAs(UnmanagedType.Bool)] bool disableAll, ref TOKEN_PRIVILEGES newState, uint bufferLength, nint previousState, nint returnLength);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    /// <summary>在当前进程令牌上启用一个特权。返回是否成功（特权不存在或无权时为 false）。</summary>
    public static bool EnablePrivilege(string name)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token)) return false;
        try
        {
            if (!LookupPrivilegeValueW(null, name, out var luid)) return false;
            var tp = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
            if (!AdjustTokenPrivileges(token, false, ref tp, 0, 0, 0)) return false;
            // AdjustTokenPrivileges 对“未分配的特权”也返回 true，要看 LastError 是否 ERROR_NOT_ALL_ASSIGNED(1300)
            return Marshal.GetLastPInvokeError() == 0;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    // ---- 通知资源管理器刷新 ----

    private const uint SHCNE_UPDATEDIR = 0x00001000;
    private const uint SHCNE_UPDATEITEM = 0x00002000;
    private const uint SHCNF_PATHW = 0x0005;
    private const uint SHCNF_FLUSH = 0x1000;

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial void SHChangeNotify(uint eventId, uint flags, string? item1, string? item2);

    /// <summary>让资源管理器刷新某个文件夹（及其父目录中的显示）。</summary>
    public static void NotifyFolderChanged(string path)
    {
        try
        {
            SHChangeNotify(SHCNE_UPDATEITEM, SHCNF_PATHW | SHCNF_FLUSH, path, null);
            SHChangeNotify(SHCNE_UPDATEDIR, SHCNF_PATHW | SHCNF_FLUSH, path, null);
        }
        catch
        {
            // 仅用于刷新显示，失败无妨
        }
    }

    /// <summary>
    /// 枚举当前所有进程 ID。比 Process.GetProcesses() 轻量得多，适合高频轮询。
    /// </summary>
    public static int EnumProcessIds(ref uint[] buffer)
    {
        while (true)
        {
            if (!EnumProcesses(buffer, (uint)(buffer.Length * sizeof(uint)), out var needed))
                return 0;

            var count = (int)(needed / sizeof(uint));
            if (count < buffer.Length) return count;

            // 缓冲区被填满，可能有遗漏，扩容重试
            buffer = new uint[buffer.Length * 2];
        }
    }

    /// <summary>获取进程可执行文件完整路径；失败（例如权限不足）返回 null。</summary>
    public static string? GetProcessImagePath(uint pid)
    {
        using var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h.IsInvalid) return null;

        var buffer = new char[1024];
        uint size = (uint)buffer.Length;
        return QueryFullProcessImageNameW(h, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
    }

    /// <summary>
    /// 获取进程创建时间（FILETIME）。PID 会被系统复用，"PID + 创建时间"才能唯一标识一个进程。
    /// 进程不存在或无权访问返回 null。
    /// </summary>
    public static long? GetProcessCreationTime(uint pid)
    {
        using var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h.IsInvalid) return null;
        return GetProcessTimes(h, out var creation, out _, out _, out _) ? creation : null;
    }

    /// <summary>获取父进程 ID；失败返回 null。注意父进程可能已退出且 PID 被复用，调用方需用创建时间校验。</summary>
    public static uint? GetParentProcessId(uint pid)
    {
        using var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h.IsInvalid) return null;

        var info = new PROCESS_BASIC_INFORMATION();
        var status = NtQueryInformationProcess(h, 0 /* ProcessBasicInformation */, ref info, Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _);
        return status == 0 ? (uint)info.InheritedFromUniqueProcessId : null;
    }

    /// <summary>获取进程所在的终端会话 ID；失败返回 null。</summary>
    public static uint? GetProcessSessionId(uint pid)
        => ProcessIdToSessionId(pid, out var sid) ? sid : null;

    /// <summary>获取命名管道客户端的进程 ID；失败返回 null。</summary>
    public static uint? GetPipeClientProcessId(SafePipeHandle handle)
        => GetNamedPipeClientProcessId(handle, out var pid) ? pid : null;

    /// <summary>挂起整个进程。返回 true 表示成功。</summary>
    public static bool SuspendProcess(uint pid)
    {
        using var h = OpenProcess(PROCESS_SUSPEND_RESUME, false, pid);
        return !h.IsInvalid && NtSuspendProcess(h) == 0;
    }

    /// <summary>恢复整个进程。返回 true 表示成功。</summary>
    public static bool ResumeProcess(uint pid)
    {
        using var h = OpenProcess(PROCESS_SUSPEND_RESUME, false, pid);
        return !h.IsInvalid && NtResumeProcess(h) == 0;
    }

    /// <summary>强制把窗口拉到前台（绕过 Windows 对 SetForegroundWindow 的限制）。</summary>
    public static void ForceForeground(nint hWnd)
    {
        var fg = GetForegroundWindow();
        var fgThread = GetWindowThreadProcessId(fg, out _);
        var cur = GetCurrentThreadId();

        if (fgThread != cur && fgThread != 0)
        {
            AttachThreadInput(cur, fgThread, true);
            SetForegroundWindow(hWnd);
            AttachThreadInput(cur, fgThread, false);
        }
        else
        {
            SetForegroundWindow(hWnd);
        }
    }
}
