using System.Globalization;

namespace FFCleaner.Desktop;

/// <summary>Opciones de arranque de la aplicación.</summary>
public sealed record LaunchOptions(string? SandboxDirectory, string? SandboxRegistry, Uri? UpdateFeed, string? UpdatedFrom);

/// <summary>Formato de texto para la interfaz. Sin dependencias de WPF para poder probarlo aparte.</summary>
public static class UiPolicy
{
    /// <summary>Formato de números de la interfaz; cambia con el idioma.</summary>
    public static CultureInfo Culture { get; set; } = CultureInfo.GetCultureInfo("es");

    /// <summary>Hace visibles los caracteres de control o invisibles de un texto procedente del Registro o del disco.</summary>
    public static string VisibleText(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var text = new System.Text.StringBuilder(value.Length);
        foreach (var ch in value)
        {
            var category = char.GetUnicodeCategory(ch);
            if (char.IsControl(ch) || char.IsSurrogate(ch) || category is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                text.Append("\\u").Append(((int)ch).ToString("X4", CultureInfo.InvariantCulture));
            else text.Append(ch);
        }
        return text.ToString();
    }

    /// <summary>Tamaño en unidades binarias («1,50 MiB»).</summary>
    public static string Human(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double value = bytes;
        var index = 0;
        while (value >= 1024 && index < units.Length - 1) { value /= 1024; index++; }
        return value.ToString(index == 0 ? "N0" : "N2", Culture) + " " + units[index];
    }

    public static string Count(long value) => value.ToString("N0", Culture);

    /// <summary>Argumentos reconocidos al arrancar. El modo sandbox exige ambos parámetros a la vez.</summary>
    public static (string? SandboxDirectory, string? SandboxRegistry) ParseArguments(IReadOnlyList<string> args)
    {
        var o = ParseOptions(args);
        return (o.SandboxDirectory, o.SandboxRegistry);
    }

    /// <summary>
    /// Todos los argumentos: sandbox, la fuente de actualizaciones de prueba (solo en sandbox y solo en este equipo)
    /// y la versión desde la que se acaba de actualizar (para el aviso «se actualizó a la versión X»).
    /// </summary>
    public static LaunchOptions ParseOptions(IReadOnlyList<string> args)
    {
        string? directory = null, registry = null, feed = null, updatedFrom = null;
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] is not ("--sandbox" or "--sandbox-registry" or "--update-feed" or "--updated-from"))
                throw new ArgumentException("Argumento no reconocido: " + args[i]);
            if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("Falta el valor de " + args[i]);
            var value = args[++i];
            switch (args[i - 1])
            {
                case "--sandbox": directory = value; break;
                case "--sandbox-registry": registry = value; break;
                case "--update-feed": feed = value; break;
                default: updatedFrom = value; break;
            }
        }
        if ((directory is null) != (registry is null))
            throw new ArgumentException("El modo sandbox requiere --sandbox y --sandbox-registry a la vez; no se mezclan datos de prueba con datos reales.");
        Uri? feedUri = null;
        if (feed is not null)
        {
            if (directory is null) throw new ArgumentException("--update-feed solo se admite en modo sandbox.");
            if (!Uri.TryCreate(feed, UriKind.Absolute, out feedUri) || !feedUri.IsLoopback)
                throw new ArgumentException("--update-feed solo admite una dirección de este equipo (127.0.0.1).");
        }
        if (updatedFrom is not null && !Version.TryParse(updatedFrom, out _)) throw new ArgumentException("Versión no válida en --updated-from.");
        return new(directory, registry, feedUri, updatedFrom);
    }

    /// <summary>Argumentos para volver a abrir la aplicación tras actualizarse: sin la fuente de prueba ni el aviso anterior.</summary>
    public static IReadOnlyList<string> RelaunchArguments(IReadOnlyList<string> args)
    {
        var kept = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] is "--update-feed" or "--updated-from") { i++; continue; }
            kept.Add(args[i]);
        }
        return kept;
    }
}
