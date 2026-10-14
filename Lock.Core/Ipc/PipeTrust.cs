using System.IO.Pipes;
using System.Security.Principal;
using Lock.Core.Native;
using Lock.Core.Services;

namespace Lock.Core.Ipc;

public static class PipeTrust
{
    public static bool IsTrustedServer(NamedPipeClientStream pipe, string expectedPath)
    {
        try
        {
            if (NativeMethods.GetPipeServerProcessId(pipe.SafePipeHandle) is not { } pid) return false;
            var owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            if (Protocol.AllowDebugClients)
            {
                using var identity = WindowsIdentity.GetCurrent();
                var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
                if (owner == identity.User || (owner == admins && ServiceInstaller.IsAdministrator())) return true;
            }
            if (owner != system) return false;
            using var guard = PathGuard.Pin(expectedPath);
            var actualPath = NativeMethods.GetProcessImagePath(pid);
            return actualPath != null && string.Equals(actualPath, Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
