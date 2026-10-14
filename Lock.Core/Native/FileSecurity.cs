using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Microsoft.Win32.SafeHandles;

namespace Lock.Core.Native;

internal sealed partial class FileSecurity : IDisposable
{
    private readonly SafeFileHandle _handle;
    public SafeFileHandle Handle => _handle;
    public string Path { get; }
    public string Identity { get; }
    public bool IsDirectory { get; }
    public uint LinkCount { get; }

    private FileSecurity(SafeFileHandle handle, string path, FileInformation information, FileIdentity identity)
    {
        _handle = handle;
        Path = path;
        Identity = $"{identity.VolumeSerial:X16}:{identity.IdentifierHigh:X16}{identity.IdentifierLow:X16}";
        IsDirectory = (information.Attributes & 0x10) != 0;
        LinkCount = information.NumberOfLinks;
    }

    public static FileSecurity Open(string path, bool writable = false, uint additionalAccess = 0, bool createNew = false)
    {
        var handle = CreateFileW(path, 0x80u | additionalAccess | (writable ? 0x000E0000u : 0), 3, 0, createNew ? 1u : 3u, 0x02200000, 0);
        try
        {
            if (handle.IsInvalid) throw Error(path);
            if (!GetFileInformationByHandle(handle, out var information)) throw Error(path);
            if ((information.Attributes & 0x400) != 0) throw new IOException("不允许重解析点：" + path);
            if (!GetFileInformationByHandleEx(handle, 18, out var identity, (uint)Marshal.SizeOf<FileIdentity>())) throw Error(path);
            var buffer = new char[32768];
            var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
            if (length == 0 || length >= buffer.Length) throw Error(path);
            var finalPath = new string(buffer, 0, (int)length);
            if (finalPath.StartsWith(@"\\?\", StringComparison.Ordinal)) finalPath = finalPath[4..];
            if (!string.Equals(System.IO.Path.TrimEndingDirectorySeparator(finalPath), System.IO.Path.TrimEndingDirectorySeparator(path), StringComparison.OrdinalIgnoreCase))
                throw new IOException("路径目标发生变化：" + path);
            return new FileSecurity(handle, path, information, identity);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public string ReadSddl()
    {
        var result = GetSecurityInfo(_handle, 1, 7, out _, out _, out _, out _, out var descriptor);
        if (result != 0) throw new Win32Exception((int)result, Path);
        try
        {
            var binary = new byte[GetSecurityDescriptorLength(descriptor)];
            Marshal.Copy(descriptor, binary, 0, binary.Length);
            return new RawSecurityDescriptor(binary, 0).GetSddlForm(AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access);
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    public unsafe void WriteSddl(string sddl)
    {
        var descriptor = new RawSecurityDescriptor(sddl);
        if (descriptor.Owner == null || descriptor.Group == null) throw new InvalidDataException("安全描述符缺少所有者或组");
        var owner = new byte[descriptor.Owner.BinaryLength];
        var group = new byte[descriptor.Group.BinaryLength];
        descriptor.Owner.GetBinaryForm(owner, 0);
        descriptor.Group.GetBinaryForm(group, 0);
        var dacl = new byte[descriptor.DiscretionaryAcl?.BinaryLength ?? 0];
        descriptor.DiscretionaryAcl?.GetBinaryForm(dacl, 0);
        var flags = 7u | ((descriptor.ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0 ? 0x80000000u : 0x20000000u);
        fixed (byte* ownerPointer = owner, groupPointer = group, daclPointer = dacl)
        {
            var result = SetSecurityInfo(_handle, 1, flags, (nint)ownerPointer, (nint)groupPointer, (nint)daclPointer, 0);
            if (result != 0) throw new Win32Exception((int)result, Path);
        }
    }

    private static IOException Error(string path)
        => new("无法安全访问路径：" + path, new Win32Exception(Marshal.GetLastPInvokeError()));

    public void Dispose() => _handle.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint AccessTimeLow;
        public uint AccessTimeHigh;
        public uint WriteTimeLow;
        public uint WriteTimeHigh;
        public uint VolumeSerial;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(string path, uint access, uint sharing, nint security, uint creation, uint flags, nint template);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdentity
    {
        public ulong VolumeSerial;
        public ulong IdentifierLow;
        public ulong IdentifierHigh;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass, out FileIdentity information, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetFinalPathNameByHandleW(SafeFileHandle handle, [Out] char[] path, uint size, uint flags);

    [LibraryImport("advapi32.dll")]
    private static partial uint GetSecurityInfo(SafeFileHandle handle, int objectType, uint flags, out nint owner, out nint group, out nint dacl, out nint sacl, out nint descriptor);

    [LibraryImport("advapi32.dll")]
    private static partial uint SetSecurityInfo(SafeFileHandle handle, int objectType, uint flags, nint owner, nint group, nint dacl, nint sacl);

    [LibraryImport("advapi32.dll")]
    private static partial uint GetSecurityDescriptorLength(nint descriptor);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint pointer);
}
