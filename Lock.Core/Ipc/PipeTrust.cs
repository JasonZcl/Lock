using System.IO.Pipes;
using System.Security.Principal;
using Lock.Core.Native;
using Lock.Core.Services;

namespace Lock.Core.Ipc;

public static class PipeTrust
{
    public static bool IsTrustedServer(NamedPipeClientStream pipe, string expectedPath)
        => IsTrustedServer(pipe, expectedPath, out _);

    public static bool IsTrustedServer(NamedPipeClientStream pipe, string expectedPath, out string? error)
    {
        error = null;
        try
        {
            if (NativeMethods.GetPipeServerProcessId(pipe.SafePipeHandle) is not { } pid)
            {
                error = "无法查询管道服务端进程";
                return false;
            }
            var owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            if (Protocol.AllowDebugClients)
            {
                using var identity = WindowsIdentity.GetCurrent();
                if (owner == identity.User || (owner == admins && ServiceInstaller.IsAdministrator())) return true;
            }
            if (owner != system && owner != admins)
            {
                error = "管道所有者不是 SYSTEM 或管理员组：" + owner?.Value;
                return false;
            }
            using var guard = PathGuard.Pin(expectedPath);
            return ServiceIdentity.Matches(ServiceInstaller.ServiceName, pid, expectedPath, out error);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            error = exception.Message;
            return false;
        }
    }
}
