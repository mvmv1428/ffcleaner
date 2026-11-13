namespace FFCleaner.RegistryAnalysis;

using System.Security.Principal;
using Microsoft.Win32;

/// <summary>
/// Ámbito sobre el que opera todo el motor de Registro. En producción son los hives reales; en sandbox,
/// todo (HKCU y HKLM, vistas 32 y 64) se redirige bajo HKCU\Software\CleanerApp_TestSandbox\&lt;id&gt;,
/// de modo que las pruebas ejecutan el mismo código sin tocar ninguna clave real.
/// </summary>
public sealed class RegistryScope
{
    public const string SandboxRoot = @"Software\CleanerApp_TestSandbox";
    private readonly string? _sandboxId;
    private readonly HashSet<RegHive> _readOnlyHives;

    private RegistryScope(string? sandboxId, IEnumerable<RegHive> readOnlyHives)
    {
        _sandboxId = sandboxId;
        _readOnlyHives = [.. readOnlyHives];
    }

    public static RegistryScope Production { get; } = new(null, []);

    /// <param name="id">Identificador de la ejecución de pruebas (letras, dígitos y guiones).</param>
    /// <param name="readOnlyHives">Hives que se comportan como no modificables, para simular falta de permisos.</param>
    public static RegistryScope Sandbox(string id, params RegHive[] readOnlyHives)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 64 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            throw new ArgumentException("Identificador de sandbox inválido.", nameof(id));
        return new(id, readOnlyHives);
    }

    public bool IsSandbox => _sandboxId is not null;
    public string Id => _sandboxId is null ? "production" : "sandbox:" + _sandboxId;
    public string Description => _sandboxId is null
        ? "Registro de Windows (HKCU y HKLM, vistas de 32 y 64 bits)"
        : @"SANDBOX · HKCU\" + SandboxRoot + "\\" + _sandboxId;

    public IReadOnlyList<RegView> Views { get; } =
        Environment.Is64BitOperatingSystem ? [RegView.Registry64, RegView.Registry32] : [RegView.Registry64];

    /// <summary>¿El proceso actual tiene privilegios de administrador?</summary>
    public static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>Abre una clave existente. Devuelve null si no existe.</summary>
    internal RegistryKey? Open(RegLocation location, bool writable)
    {
        if (writable && _readOnlyHives.Contains(location.Hive))
            throw new UnauthorizedAccessException("Hive de solo lectura en este ámbito.");
        using var root = OpenRoot(location, out var prefix);
        return root.OpenSubKey(prefix + location.KeyPath, writable);
    }

    /// <summary>Abre o crea una clave para escritura.</summary>
    internal RegistryKey Create(RegLocation location)
    {
        if (_readOnlyHives.Contains(location.Hive))
            throw new UnauthorizedAccessException("Hive de solo lectura en este ámbito.");
        using var root = OpenRoot(location, out var prefix);
        return root.CreateSubKey(prefix + location.KeyPath, writable: true)
               ?? throw new IOException("No se pudo crear la clave " + location.Display);
    }

    /// <summary>Elimina por completo las claves de este sandbox. No hace nada en producción.</summary>
    public void DeleteSandbox()
    {
        if (_sandboxId is null) return;
        using var software = Registry.CurrentUser.OpenSubKey("Software", writable: true);
        using var root = software?.OpenSubKey("CleanerApp_TestSandbox", writable: true);
        if (root is null) return;
        root.DeleteSubKeyTree(_sandboxId, throwOnMissingSubKey: false);
        if (root.SubKeyCount == 0 && root.ValueCount == 0)
        {
            root.Dispose();
            software!.DeleteSubKey("CleanerApp_TestSandbox", throwOnMissingSubKey: false);
        }
    }

    private RegistryKey OpenRoot(RegLocation location, out string prefix)
    {
        if (string.IsNullOrWhiteSpace(location.KeyPath) || location.KeyPath.StartsWith('\\') || location.KeyPath.EndsWith('\\'))
            throw new ArgumentException("Ruta de clave inválida.", nameof(location));
        if (_sandboxId is null)
        {
            prefix = string.Empty;
            return RegistryKey.OpenBaseKey(
                location.Hive == RegHive.CurrentUser ? RegistryHive.CurrentUser : RegistryHive.LocalMachine,
                location.View == RegView.Registry64 ? RegistryView.Registry64 : RegistryView.Registry32);
        }
        prefix = SandboxRoot + "\\" + _sandboxId + "\\" + location.View + "\\" + location.HiveName + "\\";
        return RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
    }
}
