using System.Text.Json;

namespace FFCleaner.Core;

/// <summary>
/// Ajustes en un JSON propio de la app. Acepta el formato de la versión 0.1 (SelectedRootIds + Exclusions):
/// conserva las exclusiones como rutas protegidas y guarda una copia del archivo antiguo antes de reescribirlo.
/// </summary>
public sealed class SettingsStore
{
    private readonly string path;

    public SettingsStore(string path)
    {
        this.path = PathSafety.Normalize(path);
        if (!Path.GetExtension(this.path).Equals(".json", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("SettingsStore solo acepta el JSON propio de la app.", nameof(path));
    }

    /// <summary>Copia del archivo anterior a la migración, junto a los ajustes.</summary>
    public string LegacyBackupPath => Path.ChangeExtension(path, null) + ".v1.bak.json";

    /// <summary>Carga los ajustes. Si el archivo existe pero no es legible, lanza: no se sobrescribirá a ciegas.</summary>
    public UserSettings Load() => File.Exists(path) ? ReadStrict().Settings : UserSettings.Empty;

    private (UserSettings Settings, bool Legacy) ReadStrict()
    {
        PathSafety.EnsureSafe(path);
        if (NativeMetadata.Read(path).Links != 1) throw new IOException("No se usa JSON de ajustes con enlaces múltiples/desconocidos.");
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("JSON de ajustes no válido; se conserva sin cambios.");
            var legacy = root.TryGetProperty("SelectedRootIds", out _) && !root.TryGetProperty("SelectedTargets", out _);
            var settings = new UserSettings(Strings(root, "SelectedTargets"), Strings(root, "Exclusions"), Strings(root, "RegistryFamilies"))
            {
                Theme = Choice(root, "Theme", ["system", "light", "dark"], "system"),
                Language = Choice(root, "Language", ["es", "en"], "es"),
                AutoUpdate = !root.TryGetProperty("AutoUpdate", out var auto) || auto.ValueKind != JsonValueKind.False,
            };
            return (settings, legacy);
        }
        catch (JsonException ex) { throw new InvalidDataException("JSON de ajustes corrupto; se conserva sin cambios.", ex); }
    }

    /// <summary>Valor de una lista cerrada; uno desconocido o ausente vuelve al predeterminado sin invalidar el resto.</summary>
    private static string Choice(JsonElement root, string name, string[] allowed, string fallback) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && allowed.Contains(value.GetString())
            ? value.GetString()! : fallback;

    private static List<string> Strings(JsonElement root, string name)
    {
        var result = new List<string>();
        if (!root.TryGetProperty(name, out var array) || array.ValueKind == JsonValueKind.Null) return result;
        if (array.ValueKind != JsonValueKind.Array) throw new InvalidDataException("JSON de ajustes incompleto/no válido; se conserva sin cambios.");
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) throw new InvalidDataException("JSON de ajustes incompleto/no válido; se conserva sin cambios.");
            result.Add(item.GetString()!);
        }
        return result;
    }

    public void Save(UserSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.SelectedTargets is null || settings.Exclusions is null || settings.RegistryFamilies is null ||
            settings.SelectedTargets.Any(s => s is null) || settings.Exclusions.Any(s => s is null) ||
            settings.RegistryFamilies.Any(s => s is null))
            throw new ArgumentException("Listas de ajustes no válidas.", nameof(settings));
        var parent = Path.GetDirectoryName(path)!;
        var existingAncestor = parent;
        while (true)
        {
            try { PathSafety.EnsureSafe(existingAncestor); break; }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            { existingAncestor = Path.GetDirectoryName(existingAncestor) ?? throw new IOException("No existe una raíz accesible para los ajustes."); }
        }
        Directory.CreateDirectory(parent);
        PathSafety.EnsureSafe(parent);
        if (File.Exists(path) && ReadStrict().Legacy && !File.Exists(LegacyBackupPath))
            File.Copy(path, LegacyBackupPath, overwrite: false);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(settings);
        var temporary = path + ".writing-" + Guid.NewGuid().ToString("N");
        using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            output.Write(bytes);
            output.Flush(true);
        }
        PathSafety.EnsureSafe(parent);
        File.Move(temporary, path, true);
    }
}
