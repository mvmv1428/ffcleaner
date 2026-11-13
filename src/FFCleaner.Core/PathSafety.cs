using System.Runtime.InteropServices;

namespace FFCleaner.Core;

internal sealed class UnsafePathException(string path) : IOException("Ruta reparse/nube/dispositivo no recorrida: " + path);

internal static class PathSafety
{
    /// <summary>ReparsePoint | Offline | Device | RECALL_ON_OPEN (0x40000) | RECALL_ON_DATA_ACCESS (0x400000).</summary>
    internal const uint UnsafeMask = 0x00000400 | 0x00001000 | 0x00000040 | 0x00040000 | 0x00400000;

    internal static bool IsIoFailure(Exception ex) => ex is IOException or UnauthorizedAccessException
        or System.ComponentModel.Win32Exception or System.Security.SecurityException;

    internal static bool UnsafeAttributes(FileAttributes attributes) => ((uint)attributes & UnsafeMask) != 0;

    internal static bool UnsafeAttributes(uint attributes) => (attributes & UnsafeMask) != 0;

    /// <summary>Comprueba la cadena completa de ancestros. Úsese una vez por raíz, nunca por archivo.</summary>
    internal static void EnsureSafe(string path)
    {
        var ancestors = new Stack<string>();
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            ancestors.Push(current);
        foreach (var current in ancestors)
            if (UnsafeAttributes(File.GetAttributes(current))) throw new UnsafePathException(current);
    }

    internal static bool IsWithin(string path, string ancestor) =>
        path.Equals(ancestor, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.EndsInDirectorySeparator(ancestor) ? ancestor : ancestor + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    /// <summary>Estrictamente dentro: descendiente, no la propia carpeta.</summary>
    internal static bool IsStrictlyWithin(string path, string ancestor) =>
        !path.Equals(ancestor, StringComparison.OrdinalIgnoreCase) && IsWithin(path, ancestor);

    /// <summary>Número de componentes bajo la raíz de unidad («C:\a\b» → 2).</summary>
    internal static int Depth(string normalizedPath) =>
        normalizedPath.Length <= 3 ? 0 : normalizedPath.AsSpan(3).Count('\\') + 1;

    internal static bool IsFixedDrive(string normalizedPath)
    {
        var root = Path.GetPathRoot(normalizedPath);
        return !string.IsNullOrEmpty(root) && GetDriveType(root) == 3;
    }

    internal static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length < 3 || !char.IsAsciiLetter(path[0]) ||
            path[1] != ':' || (path[2] != '\\' && path[2] != '/') ||
            path.AsSpan(2).Contains(':') || path.IndexOfAny(['\0', '*', '?', '"', '<', '>', '|']) >= 0)
            throw new ArgumentException("Se requiere una ruta local absoluta de unidad, no UNC/dispositivo/ADS.", nameof(path));
        foreach (var part in path[3..].Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if ((part.EndsWith('.') && part != "." && part != "..") || part.EndsWith(' '))
                throw new ArgumentException("Componente de ruta ambiguo.", nameof(path));
            var stem = part.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" or "CLOCK$" ||
                ((stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
                    stem.Length == 4 && "123456789¹²³".Contains(stem[3])))
                throw new ArgumentException("Nombre de dispositivo DOS no permitido.", nameof(path));
        }
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    /// <summary>
    /// Forma literal para las API nativas: admite rutas de más de 260 caracteres y evita que Windows
    /// reinterprete el nombre (puntos o espacios finales, nombres de dispositivo).
    /// </summary>
    internal static string ForApi(string fullPath) =>
        fullPath.Length >= 3 && fullPath[1] == ':' && fullPath[2] == '\\' ? @"\\?\" + fullPath : fullPath;

    [DllImport("kernel32.dll", EntryPoint = "GetDriveTypeW", CharSet = CharSet.Unicode)]
    private static extern uint GetDriveType(string root);
}
