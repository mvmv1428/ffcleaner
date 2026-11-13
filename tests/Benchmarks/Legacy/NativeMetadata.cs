using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FFCleaner.Benchmarks.Legacy;

internal sealed record FileMetadata(long Length, long? Allocated, string Identity, uint Links,
    DateTime LastWriteUtc, DateTime LastAccessUtc, uint Attributes);

internal static class NativeMetadata
{
    // Metadata-only access (0); OPEN_EXISTING; OPEN_REPARSE_POINT. Never requests data access.
    internal static FileMetadata Read(string path)
    {
        using var handle = CreateFile(path, 0, 7, IntPtr.Zero, 3, 0x00200000 | 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "No se puede consultar " + path);
        if (!GetFileInformationByHandle(handle, out var basic))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Metadatos no disponibles: " + path);
        var identity = $"win32:{basic.VolumeSerialNumber:X8}:{basic.FileIndexHigh:X8}{basic.FileIndexLow:X8}";
        if (GetIdentity(handle, 18, out var id, (uint)Marshal.SizeOf<IdInfo>()))
            identity = $"win128:{id.Volume:X16}:{id.IdLow:X16}{id.IdHigh:X16}";
        long? allocated = null;
        var links = basic.NumberOfLinks;
        if (GetStandard(handle, 1, out var standard, (uint)Marshal.SizeOf<StandardInfo>()))
        {
            if (standard.AllocationSize >= 0) allocated = standard.AllocationSize;
            links = standard.NumberOfLinks;
        }
        return new(((long)basic.SizeHigh << 32) | basic.SizeLow, allocated, identity, links,
            DateTime.FromFileTimeUtc(((long)basic.WriteHigh << 32) | basic.WriteLow),
            DateTime.FromFileTimeUtc(((long)basic.AccessHigh << 32) | basic.AccessLow), basic.Attributes);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HandleInfo
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint VolumeSerialNumber, SizeHigh, SizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct StandardInfo
    {
        public long AllocationSize, EndOfFile;
        public uint NumberOfLinks;
        public byte DeletePending, Directory;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IdInfo { public ulong Volume, IdLow, IdHigh; }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out HandleInfo info);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetStandard(SafeFileHandle handle, int infoClass, out StandardInfo info, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIdentity(SafeFileHandle handle, int infoClass, out IdInfo info, uint size);
}
