using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Lock.Core.Native;

internal static partial class ServiceIdentity
{
    public static bool Matches(string serviceName, uint processId, string expectedPath, out string? error)
    {
        using var manager = OpenSCManagerW(null, null, 1);
        if (manager.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
        using var service = OpenServiceW(manager, serviceName, 5);
        if (service.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
        if (!QueryServiceStatusEx(service, 0, out var status, (uint)Marshal.SizeOf<ServiceStatus>(), out _))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        if (status.CurrentState != 4 || status.ProcessId != processId || status.ServiceType != 0x10)
        {
            error = "管道进程与正在运行的独立后台服务不一致";
            return false;
        }
        QueryServiceConfigW(service, 0, 0, out var required);
        var queryError = Marshal.GetLastPInvokeError();
        if (queryError != 122 || required == 0 || required > 65536) throw new Win32Exception(queryError);
        var buffer = Marshal.AllocHGlobal((int)required);
        try
        {
            if (!QueryServiceConfigW(service, buffer, required, out _)) throw new Win32Exception(Marshal.GetLastPInvokeError());
            var configuration = Marshal.PtrToStructure<ServiceConfiguration>(buffer);
            var account = Marshal.PtrToStringUni(configuration.ServiceStartName);
            if (configuration.ServiceType != 0x10 || !string.Equals(account, "LocalSystem", StringComparison.OrdinalIgnoreCase))
            {
                error = "后台服务没有注册为 LocalSystem 独立服务";
                return false;
            }
            var binaryPath = Marshal.PtrToStringUni(configuration.BinaryPathName)?.Trim();
            if (!MatchesBinaryPath(binaryPath, expectedPath))
            {
                error = "后台服务的注册路径与托盘程序所在目录不一致";
                return false;
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        if (!QueryServiceStatusEx(service, 0, out status, (uint)Marshal.SizeOf<ServiceStatus>(), out _))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        if (status.CurrentState != 4 || status.ProcessId != processId)
        {
            error = "后台服务在连接校验期间已停止或重启";
            return false;
        }
        error = null;
        return true;
    }

    public static bool MatchesBinaryPath(string? binaryPath, string expectedPath)
    {
        var fullPath = Path.GetFullPath(expectedPath);
        binaryPath = binaryPath?.Trim();
        return string.Equals(binaryPath, fullPath, StringComparison.OrdinalIgnoreCase)
            || string.Equals(binaryPath, "\"" + fullPath + "\"", StringComparison.OrdinalIgnoreCase);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceConfiguration
    {
        public uint ServiceType;
        public uint StartType;
        public uint ErrorControl;
        public nint BinaryPathName;
        public nint LoadOrderGroup;
        public uint TagId;
        public nint Dependencies;
        public nint ServiceStartName;
        public nint DisplayName;
    }

    private sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public ServiceHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial ServiceHandle OpenSCManagerW(string? machineName, string? databaseName, uint access);
    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial ServiceHandle OpenServiceW(ServiceHandle manager, string name, uint access);
    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceStatusEx(ServiceHandle service, int level, out ServiceStatus status, uint size, out uint required);
    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceConfigW(ServiceHandle service, nint configuration, uint size, out uint required);
    [LibraryImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(nint handle);
}
