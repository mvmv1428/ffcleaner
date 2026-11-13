namespace FFCleaner.RegistryAnalysis;

using System.IO;

internal enum PathClassification
{
    AbsoluteLocalExe,
    Ambiguous,
}

internal readonly record struct PathAnalysis(PathClassification Classification, string? Resolved, string Reason)
{
    public bool IsUsable => Classification == PathClassification.AbsoluteLocalExe;

    public static PathAnalysis Ambiguous(string reason) => new(PathClassification.Ambiguous, null, reason);
}

/// <summary>
/// Analiza la cadena cruda de App Paths sin tocar el sistema de archivos, sin expandir
/// variables y sin ejecutar nada. Solo una ruta local absoluta terminada en .exe es
/// verificable; todo lo demás se declara ambiguo (nunca seleccionable).
/// </summary>
internal static class RegistryPathSyntax
{
    private static readonly char[] ForbiddenChars = { '<', '>', '|', '*', '?' };

    public static PathAnalysis Analyze(string? raw)
    {
        var text = (raw ?? string.Empty).Trim();
        if (text.Any(ch => char.IsControl(ch) || char.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.Format))
            return PathAnalysis.Ambiguous("Caracteres de control o formato Unicode invisibles: sintaxis ambigua.");
        if (text.Length == 0)
        {
            return PathAnalysis.Ambiguous("Valor vacío: no hay ruta que verificar.");
        }

        if (text.StartsWith('"'))
        {
            var closing = text.IndexOf('"', 1);
            if (closing < 0)
            {
                return PathAnalysis.Ambiguous("Comilla sin cerrar: sintaxis ambigua.");
            }

            var trailing = text[(closing + 1)..].Trim();
            if (trailing.Length > 0)
            {
                return PathAnalysis.Ambiguous("Texto adicional o argumentos tras la ruta entrecomillada: no es una ruta pura.");
            }

            text = text[1..closing].Trim();
            if (text.Length == 0)
            {
                return PathAnalysis.Ambiguous("Valor vacío: no hay ruta que verificar.");
            }
        }
        else if (text.Contains('"'))
        {
            return PathAnalysis.Ambiguous("Comillas internas en la ruta: sintaxis ambigua.");
        }

        if (text.Contains('%'))
        {
            return PathAnalysis.Ambiguous("Contiene una variable de entorno (%...%) sin expandir: la ruta es ambigua.");
        }

        if (text.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return PathAnalysis.Ambiguous("Ruta UNC de red: no es verificable como ruta local.");
        }

        if (text.Contains('"'))
        {
            if (!text.StartsWith('"'))
            {
                return PathAnalysis.Ambiguous("Comillas internas en la ruta: sintaxis ambigua.");
            }

            var closing = text.IndexOf('"', 1);
            if (closing < 0)
            {
                return PathAnalysis.Ambiguous("Comilla sin cerrar: sintaxis ambigua.");
            }

            var trailing = text[(closing + 1)..].Trim();
            if (trailing.Length > 0)
            {
                return PathAnalysis.Ambiguous("Texto adicional o argumentos tras la ruta entrecomillada: no es una ruta pura.");
            }

            return PathAnalysis.Ambiguous("Ruta entrecomillada: requiere normalización explícita antes de verificar.");
        }

        foreach (var ch in text)
        {
            if (char.IsControl(ch) || Array.IndexOf(ForbiddenChars, ch) >= 0 || ch == '"')
            {
                return PathAnalysis.Ambiguous("Caracteres inválidos en la ruta: sintaxis ambigua.");
            }
        }

        if (!Path.IsPathFullyQualified(text))
        {
            return PathAnalysis.Ambiguous("Ruta relativa: no es una ruta local absoluta verificable.");
        }

        var root = Path.GetPathRoot(text);
        if (root is null || root.Length != 3 || root[1] != ':' || !char.IsAsciiLetter(root[0])
            || (root[2] != '\\' && root[2] != '/'))
        {
            return PathAnalysis.Ambiguous("Ruta relativa a la unidad o sin letra de unidad: no es absoluta local.");
        }

        if (text.Length > 2 && text[2..].Contains(':'))
        {
            return PathAnalysis.Ambiguous("Flujo alternativo de datos (ADS) o dos puntos adicionales: no es un ejecutable verificable.");
        }

        if (!text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return PathAnalysis.Ambiguous("El destino no termina en .exe: no es un ejecutable verificable.");
        }

        return new PathAnalysis(PathClassification.AbsoluteLocalExe, text, string.Empty);
    }
}
