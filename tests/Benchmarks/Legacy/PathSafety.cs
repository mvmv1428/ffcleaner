namespace FFCleaner.Benchmarks.Legacy;

internal sealed class UnsafePathException(string path) : IOException("Ruta reparse/nube/dispositivo no recorrida: " + path);

internal static class PathSafety
{
    internal static bool IsIoFailure(Exception ex) => ex is IOException or UnauthorizedAccessException
        or System.ComponentModel.Win32Exception or System.Security.SecurityException;

    internal static bool UnsafeAttributes(FileAttributes attributes) =>
        (attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline | FileAttributes.Device | (FileAttributes)0x00440000)) != 0;

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
}
