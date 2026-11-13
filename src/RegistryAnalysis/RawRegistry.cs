namespace FFCleaner.RegistryAnalysis;

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

/// <summary>Valor tal y como está almacenado: tipo numérico y bytes exactos, sin expansión ni conversión.</summary>
internal readonly record struct RawValue(int Kind, byte[] Data)
{
    public const int Sz = 1, ExpandSz = 2, Binary = 3, Dword = 4, MultiSz = 7, Qword = 11;

    /// <summary>Texto de un REG_SZ o REG_EXPAND_SZ (sin expandir). Null para cualquier otro tipo.</summary>
    public string? AsString()
    {
        if (Kind is not (Sz or ExpandSz)) return null;
        var text = Encoding.Unicode.GetString(Data, 0, Data.Length & ~1);
        var end = text.IndexOf('\0');
        return end >= 0 ? text[..end] : text;
    }

    public int? AsDword() => Kind == Dword && Data.Length == 4 ? BitConverter.ToInt32(Data, 0) : null;
}

/// <summary>
/// Acceso crudo al Registro. Las lecturas preservan tipo y bytes exactos (necesario para respaldar y
/// restaurar sin pérdida); las escrituras solo las usan el reparador y el restaurador.
/// </summary>
internal static class RawRegistry
{
    private const int ErrorSuccess = 0, ErrorFileNotFound = 2, ErrorAccessDenied = 5, ErrorMoreData = 234;

    /// <summary>Lee un valor. Devuelve null si no existe. Cadena vacía = valor predeterminado.</summary>
    internal static RawValue? Query(RegistryKey key, string name)
    {
        var size = 0;
        var status = RegQueryValueEx(key.Handle, name, IntPtr.Zero, out var kind, null, ref size);
        if (status == ErrorFileNotFound) return null;
        while (status is ErrorSuccess or ErrorMoreData)
        {
            var data = new byte[size];
            status = RegQueryValueEx(key.Handle, name, IntPtr.Zero, out kind, data, ref size);
            if (status == ErrorSuccess) return new(kind, size == data.Length ? data : data[..size]);
            if (status == ErrorFileNotFound) return null;
        }
        throw Failure(status, "leer");
    }

    internal static void Set(RegistryKey key, string name, RawValue value)
    {
        var status = RegSetValueEx(key.Handle, name, 0, value.Kind, value.Data, value.Data.Length);
        if (status != ErrorSuccess) throw Failure(status, "escribir");
    }

    /// <summary>Elimina un valor. Devuelve false si ya no existía.</summary>
    internal static bool DeleteValue(RegistryKey key, string name)
    {
        var status = RegDeleteValue(key.Handle, name);
        if (status == ErrorFileNotFound) return false;
        if (status != ErrorSuccess) throw Failure(status, "eliminar");
        return true;
    }

    /// <summary>
    /// Ruta física de la clave en formato .reg (HKEY_...), tal y como la resuelve Windows después de
    /// aplicar la redirección de 32 bits. Null si no se puede determinar.
    /// </summary>
    internal static string? PhysicalPath(RegistryKey key)
    {
        var buffer = new byte[1024];
        var status = NtQueryKey(key.Handle, 3, buffer, buffer.Length, out var needed);
        if (status is unchecked((int)0xC0000023) or unchecked((int)0x80000005))
        {
            buffer = new byte[needed];
            status = NtQueryKey(key.Handle, 3, buffer, buffer.Length, out needed);
        }
        if (status != 0) return null;
        var length = BitConverter.ToInt32(buffer, 0);
        var native = Encoding.Unicode.GetString(buffer, 4, length);

        const string machine = @"\REGISTRY\MACHINE\", user = @"\REGISTRY\USER\";
        if (native.StartsWith(machine, StringComparison.OrdinalIgnoreCase))
            return @"HKEY_LOCAL_MACHINE\" + native[machine.Length..];
        if (!native.StartsWith(user, StringComparison.OrdinalIgnoreCase)) return null;
        var rest = native[user.Length..];
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value;
        if (sid is not null)
        {
            if (rest.StartsWith(sid + @"_Classes\", StringComparison.OrdinalIgnoreCase))
                return @"HKEY_CURRENT_USER\Software\Classes\" + rest[(sid.Length + 9)..];
            if (rest.StartsWith(sid + @"\", StringComparison.OrdinalIgnoreCase))
                return @"HKEY_CURRENT_USER\" + rest[(sid.Length + 1)..];
        }
        return @"HKEY_USERS\" + rest;
    }

    private static Exception Failure(int status, string action) => status == ErrorAccessDenied
        ? new UnauthorizedAccessException("Acceso denegado al " + action + " en el Registro.")
        : new Win32Exception(status, "No se pudo " + action + " en el Registro");

    [DllImport("advapi32.dll", EntryPoint = "RegQueryValueExW", CharSet = CharSet.Unicode)]
    private static extern int RegQueryValueEx(SafeRegistryHandle key, string name, IntPtr reserved, out int kind, byte[]? data, ref int size);

    [DllImport("advapi32.dll", EntryPoint = "RegSetValueExW", CharSet = CharSet.Unicode)]
    private static extern int RegSetValueEx(SafeRegistryHandle key, string name, int reserved, int kind, byte[] data, int size);

    [DllImport("advapi32.dll", EntryPoint = "RegDeleteValueW", CharSet = CharSet.Unicode)]
    private static extern int RegDeleteValue(SafeRegistryHandle key, string name);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryKey(SafeRegistryHandle key, int informationClass, byte[] information, int length, out int resultLength);
}
