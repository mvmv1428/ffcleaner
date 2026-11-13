namespace FFCleaner.RegistryAnalysis;

using System.Globalization;
using System.Security;

/// <summary>Comprueba si una ruta existe. Nunca abre ni ejecuta el destino.</summary>
public interface ITargetProbe
{
    TargetState Probe(string path);
}

/// <summary>
/// Sonda real. Solo una unidad fija y lista permite concluir «no existe»; un permiso denegado, una unidad
/// extraíble, de red o desconectada se informan como <see cref="TargetState.Unknown"/>, jamás como ausencia.
/// </summary>
public sealed class FileSystemTargetProbe : ITargetProbe
{
    /// <summary>
    /// «No existe» solo es concluyente si toda la parte existente de la ruta son carpetas normales. Si algún
    /// ancestro es una unión, un enlace o un punto de montaje, el destino puede estar en un volumen
    /// desconectado o bloqueado: se devuelve desconocido.
    /// </summary>
    private static TargetState MissingUnlessBehindLink(string path)
    {
        try
        {
            for (var current = Path.GetDirectoryName(Path.GetFullPath(path)); current is not null; current = Path.GetDirectoryName(current))
            {
                var attributes = new FileInfo(current).Attributes;
                if ((int)attributes == -1) continue;
                if ((attributes & FileAttributes.ReparsePoint) != 0) return TargetState.Unknown;
            }
            return TargetState.Missing;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException
                                       or ArgumentException or NotSupportedException)
        {
            return TargetState.Unknown;
        }
    }

    public TargetState Probe(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root) || root.Length != 3) return TargetState.Unknown;
            var drive = new DriveInfo(root);
            if (!drive.IsReady || drive.DriveType != DriveType.Fixed) return TargetState.Unknown;
            _ = File.GetAttributes(path);
            return TargetState.Present;
        }
        catch (FileNotFoundException) { return MissingUnlessBehindLink(path); }
        catch (DirectoryNotFoundException) { return MissingUnlessBehindLink(path); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException
                                       or ArgumentException or NotSupportedException)
        {
            return TargetState.Unknown;
        }
    }
}

/// <summary>Resultado de interpretar una línea de comandos del Registro.</summary>
internal readonly record struct CommandTarget(TargetState State, string? Path, string Reason, bool Expanded);

/// <summary>
/// Interpreta valores que contienen un ejecutable con posibles argumentos (inicio, desinstaladores,
/// «abrir con»). Solo decide «no existe» cuando la ruta es local, absoluta y todas sus lecturas posibles
/// faltan; cualquier duda se devuelve como desconocida. Nunca ejecuta nada.
/// </summary>
internal static class CommandLine
{
    private static readonly string[] ExecutableExtensions = [".exe", ".com", ".bat", ".cmd", ".scr", ".dll", ".msi", ".vbs", ".ps1", ".lnk"];

    internal static CommandTarget Resolve(string? raw, ITargetProbe probe)
    {
        var text = (raw ?? string.Empty).Trim();
        if (text.Length == 0) return new(TargetState.Unknown, null, "Valor vacío.", false);
        if (text.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format))
            return new(TargetState.Unknown, null, "Contiene caracteres de control o invisibles.", false);

        var expanded = false;
        if (text.Contains('%'))
        {
            var resolved = Environment.ExpandEnvironmentVariables(text);
            // Un «%NOMBRE%» que sobrevive es una variable sin resolver; «%1», «%L» o «%*» son marcadores de argumentos.
            if (System.Text.RegularExpressions.Regex.IsMatch(resolved, "%[^%\\s\"\\\\/]+%"))
                return new(TargetState.Unknown, null, "Variable de entorno sin resolver.", false);
            expanded = !string.Equals(resolved, text, StringComparison.Ordinal);
            text = resolved.Trim();
        }

        if (text.StartsWith('"'))
        {
            var closing = text.IndexOf('"', 1);
            if (closing < 0) return new(TargetState.Unknown, null, "Comilla sin cerrar.", expanded);
            var quoted = text[1..closing].Trim();
            if (!IsAbsoluteLocal(quoted)) return Bare(quoted, probe, expanded);
            return Candidate(quoted, probe, expanded);
        }

        if (text.StartsWith(@"\\", StringComparison.Ordinal))
            return new(TargetState.Unknown, null, "Ruta de red: no verificable.", expanded);
        if (!IsDriveRooted(text)) return Bare(text.Split(' ', 2)[0], probe, expanded);

        // Sin comillas: Windows prueba cada corte en un espacio («C:\Program Files\App\app.exe /arg»).
        var candidates = new List<string>();
        for (var i = text.IndexOf(' '); i >= 0; i = text.IndexOf(' ', i + 1)) candidates.Add(text[..i]);
        candidates.Add(text);
        var unknown = false;
        string? best = null;
        foreach (var candidate in candidates)
        {
            // Un corte con caracteres imposibles en un nombre de archivo solo descarta la conclusión si
            // todavía no se identificó el ejecutable (p. ej. un argumento «http://…» tras «app.exe»).
            if (!IsAbsoluteLocal(candidate)) { if (best is null) unknown = true; continue; }
            var result = Candidate(candidate, probe, expanded);
            if (result.State == TargetState.Present) return result;
            if (result.State == TargetState.Unknown) unknown = true;
            if (best is null && ExecutableExtensions.Contains(Path.GetExtension(candidate), StringComparer.OrdinalIgnoreCase)) best = candidate;
        }
        if (unknown) return new(TargetState.Unknown, best, "La ruta admite varias lecturas y alguna no es verificable.", expanded);
        return new(TargetState.Missing, best ?? text, "El destino no existe.", expanded);
    }

    /// <summary>Ruta local absoluta sin flujos alternativos ni caracteres inválidos.</summary>
    internal static bool IsAbsoluteLocal(string path) =>
        IsDriveRooted(path) && path.IndexOf(':', 2) < 0 && path.IndexOfAny(['<', '>', '|', '*', '?', '"']) < 0 &&
        !path.Any(char.IsControl);

    private static bool IsDriveRooted(string path) =>
        path.Length >= 4 && char.IsAsciiLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/');

    private static CommandTarget Candidate(string path, ITargetProbe probe, bool expanded)
    {
        var state = probe.Probe(path);
        if (state == TargetState.Missing && Path.GetExtension(path).Length == 0)
        {
            // Windows añade «.exe» a un nombre sin extensión.
            var withExtension = probe.Probe(path + ".exe");
            if (withExtension != TargetState.Missing) state = withExtension;
        }
        return new(state, path, state == TargetState.Missing ? "El destino no existe." : state == TargetState.Unknown ? "Destino no verificable (unidad no disponible o sin permiso)." : string.Empty, expanded);
    }

    /// <summary>Comando sin ruta («rundll32.exe …»): se busca en las carpetas del sistema y en PATH.</summary>
    private static CommandTarget Bare(string token, ITargetProbe probe, bool expanded)
    {
        if (token.Length == 0 || token.IndexOfAny(['\\', '/', ':', '<', '>', '|', '*', '?', '"']) >= 0)
            return new(TargetState.Unknown, null, "Ruta relativa: no verificable.", expanded);
        var names = Path.GetExtension(token).Length == 0 ? new[] { token + ".exe", token + ".com", token + ".bat", token + ".cmd" } : [token];
        var folders = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        };
        folders.AddRange((Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        foreach (var folder in folders)
        {
            if (!IsDriveRooted(folder)) continue;
            foreach (var name in names)
            {
                var path = Path.Combine(folder, name);
                if (probe.Probe(path) == TargetState.Present) return new(TargetState.Present, path, string.Empty, expanded);
            }
        }
        return new(TargetState.Unknown, null, "Comando sin ruta que no se encontró en las carpetas del sistema.", expanded);
    }
}
