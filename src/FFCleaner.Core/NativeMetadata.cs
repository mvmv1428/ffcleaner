using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FFCleaner.Core;

internal sealed record FileMetadata(long Length, long? Allocated, string Identity, uint Links,
    DateTime LastWriteUtc, DateTime LastAccessUtc, uint Attributes);

internal static class NativeMetadata
{
    // Metadata-only access (0); OPEN_EXISTING; OPEN_REPARSE_POINT. Never requests data access.
    internal static FileMetadata Read(string path)
    {
        using var handle = CreateFile(PathSafety.ForApi(path), 0, 7, IntPtr.Zero, 3, 0x00200000 | 0x02000000, IntPtr.Zero);
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

    /// <summary>Número de enlaces físicos del archivo. Solo metadatos: no pide acceso a datos ni bloquea a nadie.</summary>
    internal static uint LinkCount(string path)
    {
        using var handle = CreateFile(PathSafety.ForApi(path), 0, 7, IntPtr.Zero, 3, 0x00200000 | 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "No se puede consultar " + path);
        if (!GetFileInformationByHandle(handle, out var basic))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Metadatos no disponibles: " + path);
        return basic.NumberOfLinks;
    }

    /// <summary>
    /// Borra un archivo normal a través de su propio handle, como el motor de limpieza: sin compartirlo (un archivo en
    /// uso no se toca), sin seguir enlaces y solo si no es de solo lectura, de sistema, un enlace, un archivo en la nube
    /// o un archivo con varios enlaces físicos. Devuelve null si se borró (o ya no existía) o el motivo por el que no.
    /// </summary>
    internal static string? DeleteRegularFile(string path)
    {
        using var handle = CreateFile(PathSafety.ForApi(path), 0x10000 | 0x80, 0, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            return error switch { 2 or 3 => null, 32 or 33 => "en uso por otro programa", 5 => "acceso denegado", _ => "error de Windows " + error };
        }
        if (!GetFileInformationByHandle(handle, out var info)) return "error de Windows " + Marshal.GetLastWin32Error();
        // ReadOnly | System | Directory | Reparse | Offline | Encrypted | RecallOnOpen | RecallOnDataAccess.
        if ((info.Attributes & 0x00445415) != 0) return "atributos protegidos (solo lectura, sistema, enlace o nube)";
        if (info.NumberOfLinks != 1) return "compartido mediante enlaces físicos";
        byte disposition = 1;
        return SetDisposition(handle, 4, ref disposition, sizeof(byte)) ? null : "error de Windows " + Marshal.GetLastWin32Error();
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
    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDisposition(SafeFileHandle handle, int infoClass, ref byte disposition, uint size);
}
