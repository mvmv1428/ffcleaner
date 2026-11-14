using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace FFCleaner.Setup;

/// <summary>
/// Ruta del ejecutable de otro proceso. Usa QueryFullProcessImageName con el permiso mínimo de consulta, que también
/// funciona con procesos abiertos como administrador (Process.MainModule falla con ellos). Null si no se puede leer.
/// </summary>
internal static class ProcessImage
{
    public static string? PathOf(Process process)
    {
        IntPtr handle;
        try { handle = OpenProcess(0x1000, false, process.Id); }   // PROCESS_QUERY_LIMITED_INFORMATION
        catch (InvalidOperationException) { return null; }
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
        }
        finally { CloseHandle(handle); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
