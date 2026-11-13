using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FFCleaner.Core;

/// <summary>Identidad de volumen con la que se formaron las identidades de archivo de una raíz.</summary>
public readonly record struct VolumeIdentity(ulong Serial, bool Extended);

/// <summary>
/// Enumeración de directorios por lotes. Una sola llamada al sistema devuelve nombre, atributos, tamaño
/// lógico y asignado, fecha de escritura e identidad de decenas de entradas: no se abre ni se consulta
/// cada archivo por separado. Nunca solicita acceso a datos y nunca atraviesa puntos de reanálisis.
/// </summary>
internal sealed class NativeDirectory : IDisposable
{
    internal const int BufferSize = 64 * 1024;
    private const int FileIdBothDirectoryInfo = 10, FileIdBothDirectoryRestartInfo = 11;
    private const int FileIdInfo = 18, FileIdExtdDirectoryInfo = 19, FileIdExtdDirectoryRestartInfo = 20;
    private const int ErrorNoMoreFiles = 18, ErrorFileNotFound = 2;

    private readonly SafeFileHandle _handle;
    private readonly byte[] _buffer;
    private readonly bool _extended;
    private bool _first = true;
    private int _offset = -1;

    private NativeDirectory(SafeFileHandle handle, byte[] buffer, bool extended, uint attributes)
    {
        _handle = handle;
        _buffer = buffer;
        _extended = extended;
        Attributes = attributes;
    }

    /// <summary>Atributos del propio directorio, leídos del handle abierto (no de la ruta).</summary>
    internal uint Attributes { get; }

    /// <summary>
    /// Abre un directorio sin seguir reanálisis. Con <paramref name="extended"/> las identidades son de
    /// 128 bits (NTFS/ReFS); sin él, de 64 bits.
    /// </summary>
    internal static NativeDirectory Open(string path, byte[] buffer, bool extended)
    {
        // FILE_LIST_DIRECTORY; compartir todo; OPEN_EXISTING; BACKUP_SEMANTICS | OPEN_REPARSE_POINT.
        var handle = CreateFile(PathSafety.ForApi(path), 0x0001, 7, IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, "No se puede abrir el directorio " + path);
        }
        if (!GetFileInformationByHandle(handle, out var info))
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, "Atributos no disponibles: " + path);
        }
        return new NativeDirectory(handle, buffer, extended, info.Attributes);
    }

    /// <summary>Identidad de volumen del directorio abierto y si admite identidades de 128 bits.</summary>
    internal static VolumeIdentity QueryVolume(string path)
    {
        using var handle = CreateFile(PathSafety.ForApi(path), 0x0001, 7, IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "No se puede abrir " + path);
        var probe = new byte[4096];
        if (GetIdInfo(handle, FileIdInfo, out var id, (uint)Marshal.SizeOf<IdInfo>()) &&
            (GetDirectoryInfo(handle, FileIdExtdDirectoryRestartInfo, probe, (uint)probe.Length) ||
             Marshal.GetLastWin32Error() is ErrorNoMoreFiles or ErrorFileNotFound))
            return new(id.Volume, true);
        if (!GetFileInformationByHandle(handle, out var info))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Volumen no identificable: " + path);
        return new(info.VolumeSerialNumber, false);
    }

    /// <summary>Avanza a la siguiente entrada. Devuelve false al terminar el directorio.</summary>
    internal bool MoveNext(out Entry entry)
    {
        while (true)
        {
            if (_offset < 0)
            {
                var infoClass = _extended
                    ? (_first ? FileIdExtdDirectoryRestartInfo : FileIdExtdDirectoryInfo)
                    : (_first ? FileIdBothDirectoryRestartInfo : FileIdBothDirectoryInfo);
                _first = false;
                if (!GetDirectoryInfo(_handle, infoClass, _buffer, (uint)_buffer.Length))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error is ErrorNoMoreFiles or ErrorFileNotFound) { entry = default; return false; }
                    throw new Win32Exception(error, "Enumeración interrumpida");
                }
                _offset = 0;
            }

            var span = _buffer.AsSpan(_offset);
            var next = BinaryPrimitives.ReadInt32LittleEndian(span);
            var nameBytes = BinaryPrimitives.ReadInt32LittleEndian(span[60..]);
            var nameOffset = _extended ? 88 : 104;
            // Estructura corrupta o truncada: se corta la enumeración en lugar de leer fuera del búfer o repetirla.
            if (nameBytes < 0 || nameOffset + nameBytes > span.Length)
            {
                _offset = -1;
                entry = default;
                return false;
            }
            var name = MemoryMarshal.Cast<byte, char>(span.Slice(nameOffset, nameBytes));
            if (next == 0) _offset = -1;
            else if (next < 0 || _offset + next >= _buffer.Length)
            {
                _offset = -1;
                entry = default;
                return false;
            }
            else _offset += next;
            if (name is "." or "..") continue;

            entry = new Entry
            {
                Name = name,
                LastWriteFileTime = BinaryPrimitives.ReadInt64LittleEndian(span[24..]),
                Length = BinaryPrimitives.ReadInt64LittleEndian(span[40..]),
                Allocated = BinaryPrimitives.ReadInt64LittleEndian(span[48..]),
                Attributes = BinaryPrimitives.ReadUInt32LittleEndian(span[56..]),
                IdLow = BinaryPrimitives.ReadUInt64LittleEndian(span[(_extended ? 72 : 96)..]),
                IdHigh = _extended ? BinaryPrimitives.ReadUInt64LittleEndian(span[80..]) : 0,
            };
            return true;
        }
    }

    public void Dispose() => _handle.Dispose();

    internal ref struct Entry
    {
        public ReadOnlySpan<char> Name;
        public long LastWriteFileTime, Length, Allocated;
        public uint Attributes;
        public ulong IdLow, IdHigh;
        public readonly bool IsDirectory => (Attributes & 0x10) != 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HandleInfo
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint VolumeSerialNumber, SizeHigh, SizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
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
    private static extern bool GetIdInfo(SafeFileHandle handle, int infoClass, out IdInfo info, uint size);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDirectoryInfo(SafeFileHandle handle, int infoClass, [Out] byte[] buffer, uint size);
}
