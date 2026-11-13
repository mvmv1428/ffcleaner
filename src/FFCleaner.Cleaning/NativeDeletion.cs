using System.Runtime.InteropServices;
using FFCleaner.Core;
using Microsoft.Win32.SafeHandles;

namespace FFCleaner.Cleaning;

internal enum DeleteOutcome
{
    Deleted,
    Locked,
    AccessDenied,
    Missing,
    Changed,
    UnsafeAttributes,
    MultipleLinks,
    TooRecent,
    Unconfirmed,
    Failed,
}

/// <summary>
/// Eliminación de un archivo a través de su handle. Todas las comprobaciones (atributos, enlaces,
/// identidad, tamaño, fecha) se hacen sobre el mismo handle que después marca el borrado, así que no hay
/// ventana entre comprobar y actuar. El handle se abre sin compartir: un archivo en uso no se toca.
/// </summary>
internal static class NativeDeletion
{
    private const uint ReadAttributes = 0x80, ListDirectory = 0x0001, DeleteAccess = 0x10000, OpenExisting = 3;
    private const uint OpenReparsePoint = 0x00200000, BackupSemantics = 0x02000000;

    /// <summary>ReadOnly | System | Directory | Reparse | Offline | Encrypted | RecallOnOpen | RecallOnDataAccess.</summary>
    private const uint UnsafeFile = 0x00000001 | 0x00000004 | 0x00000010 | 0x00000400 | 0x00001000 | 0x00004000 | 0x00040000 | 0x00400000;

    /// <summary>Reparse | Offline | Encrypted | RecallOnOpen | RecallOnDataAccess.</summary>
    private const uint UnsafeDirectory = 0x00000400 | 0x00001000 | 0x00004000 | 0x00040000 | 0x00400000;

    private const int FileIdInfo = 18, FileDispositionInfo = 4;

    internal static DeleteOutcome Delete(string path, in FileEntry expected, VolumeIdentity volume, TimeSpan minimumAge,
        DateTime nowUtc, out int win32Error)
    {
        win32Error = 0;
        var literal = PathSafety.ForApi(path);
        using (var handle = CreateFile(literal, DeleteAccess | ReadAttributes, 0, IntPtr.Zero, OpenExisting, OpenReparsePoint, IntPtr.Zero))
        {
            if (handle.IsInvalid)
            {
                win32Error = Marshal.GetLastWin32Error();
                return win32Error switch
                {
                    32 or 33 => DeleteOutcome.Locked,
                    5 => DeleteOutcome.AccessDenied,
                    2 or 3 => DeleteOutcome.Missing,
                    _ => DeleteOutcome.Failed,
                };
            }
            if (!GetFileInformationByHandle(handle, out var info))
            {
                win32Error = Marshal.GetLastWin32Error();
                return DeleteOutcome.Failed;
            }
            if ((info.Attributes & UnsafeFile) != 0) return DeleteOutcome.UnsafeAttributes;
            if (info.NumberOfLinks != 1) return DeleteOutcome.MultipleLinks;

            if (volume.Extended)
            {
                if (!GetId(handle, FileIdInfo, out var id, (uint)Marshal.SizeOf<IdInfo>()))
                {
                    win32Error = Marshal.GetLastWin32Error();
                    return DeleteOutcome.Failed;
                }
                if (id.Volume != volume.Serial || id.IdLow != expected.IdLow || id.IdHigh != expected.IdHigh) return DeleteOutcome.Changed;
            }
            else if (info.VolumeSerialNumber != volume.Serial || expected.IdHigh != 0 ||
                     (((ulong)info.FileIndexHigh << 32) | info.FileIndexLow) != expected.IdLow)
            {
                return DeleteOutcome.Changed;
            }

            var length = ((long)info.SizeHigh << 32) | info.SizeLow;
            var written = ((long)info.WriteHigh << 32) | info.WriteLow;
            if (length != expected.Length || written != expected.LastWriteFileTime) return DeleteOutcome.Changed;
            if (minimumAge > TimeSpan.Zero && FileEntry.ToUtc(written) > nowUtc - minimumAge) return DeleteOutcome.TooRecent;

            // FILE_DISPOSITION_INFO.DeleteFile es BOOLEAN de un byte.
            byte disposition = 1;
            if (!SetFileInformationByHandle(handle, FileDispositionInfo, ref disposition, sizeof(byte)))
            {
                win32Error = Marshal.GetLastWin32Error();
                return win32Error == 5 ? DeleteOutcome.AccessDenied : DeleteOutcome.Failed;
            }
        }

        // A partir de aquí el cambio ya se solicitó: si el nombre persiste se informa como «no confirmado».
        if (GetFileAttributes(literal) == uint.MaxValue)
        {
            var error = Marshal.GetLastWin32Error();
            if (error is 2 or 3) return DeleteOutcome.Deleted;
            win32Error = error;
        }
        return DeleteOutcome.Unconfirmed;
    }

    /// <summary>
    /// Abre un directorio para mantenerlo fijado: sin FILE_SHARE_DELETE nadie puede renombrarlo ni
    /// sustituirlo por un enlace mientras se eliminan sus archivos. Devuelve null y el motivo si no es seguro.
    /// </summary>
    internal static SafeFileHandle? PinDirectory(string path, out string? problem)
    {
        problem = null;
        // Un handle con solo READ_ATTRIBUTES no cuenta para el uso compartido; con LIST_DIRECTORY y sin
        // FILE_SHARE_DELETE, Windows impide renombrar o sustituir la carpeta mientras siga abierto.
        var handle = CreateFile(PathSafety.ForApi(path), ListDirectory | ReadAttributes, 3, IntPtr.Zero, OpenExisting, OpenReparsePoint | BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            problem = "No se pudo fijar la carpeta (error " + error + "): " + path;
            return null;
        }
        if (!GetFileInformationByHandle(handle, out var info))
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            problem = "Carpeta no verificable (error " + error + "): " + path;
            return null;
        }
        if ((info.Attributes & UnsafeDirectory) != 0 || (info.Attributes & 0x10) == 0)
        {
            handle.Dispose();
            problem = "Carpeta insegura (enlace, nube o cifrada): " + path;
            return null;
        }
        return handle;
    }

    /// <summary>
    /// Retira una carpeta solo si está vacía. Todo se decide sobre el handle abierto: debe ser una carpeta
    /// normal (no un enlace, ni de solo lectura o de sistema) y, si la categoría exige antigüedad, haberse
    /// creado antes de ese plazo. Windows rechaza la operación si la carpeta tiene contenido o está en uso.
    /// </summary>
    internal static bool TryRemoveEmptyDirectory(string path, TimeSpan minimumAge, DateTime nowUtc)
    {
        using var handle = CreateFile(PathSafety.ForApi(path), DeleteAccess | ReadAttributes, 3, IntPtr.Zero, OpenExisting,
            OpenReparsePoint | BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info)) return false;
        if ((info.Attributes & 0x10) == 0 || (info.Attributes & (UnsafeDirectory | 0x1 | 0x4)) != 0) return false;
        var created = ((long)info.CreationHigh << 32) | info.CreationLow;
        if (minimumAge > TimeSpan.Zero && FileEntry.ToUtc(created) > nowUtc - minimumAge) return false;
        byte disposition = 1;
        return SetFileInformationByHandle(handle, FileDispositionInfo, ref disposition, sizeof(byte));
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
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out HandleInfo info);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetId(SafeFileHandle handle, int type, out IdInfo id, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int type, ref byte disposition, uint size);

    [DllImport("kernel32.dll", EntryPoint = "GetFileAttributesW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFileAttributes(string path);
}

/// <summary>
/// Cadena de carpetas fijadas desde la raíz de la unidad hasta la carpeta en curso. Reutiliza los handles
/// comunes al pasar de una carpeta a otra, en lugar de reabrir todos los ancestros por cada archivo.
/// </summary>
internal sealed class PinnedDirectoryChain : IDisposable
{
    private readonly List<(string Path, SafeFileHandle Handle)> _pins = [];

    /// <summary>Fija <paramref name="directory"/> y todos sus ancestros. Devuelve null o el motivo del fallo.</summary>
    internal string? Pin(string directory)
    {
        var chain = new List<string>();
        for (string? current = directory; current is not null; current = Path.GetDirectoryName(current)) chain.Add(current);
        chain.Reverse();

        var common = 0;
        while (common < chain.Count && common < _pins.Count &&
               string.Equals(chain[common], _pins[common].Path, StringComparison.OrdinalIgnoreCase)) common++;
        Release(common);
        for (var i = common; i < chain.Count; i++)
        {
            var handle = NativeDeletion.PinDirectory(chain[i], out var problem);
            if (handle is null)
            {
                Release(0);
                return problem;
            }
            _pins.Add((chain[i], handle));
        }
        return null;
    }

    public void Dispose() => Release(0);

    private void Release(int keep)
    {
        for (var i = _pins.Count - 1; i >= keep; i--) _pins[i].Handle.Dispose();
        _pins.RemoveRange(keep, _pins.Count - keep);
    }
}
