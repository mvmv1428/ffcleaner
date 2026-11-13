namespace FFCleaner.Core;

/// <summary>
/// Origen de las carpetas base. Toda ruta del producto se deriva de aquí: no hay rutas absolutas
/// ni nombres de usuario en el código.
/// </summary>
public interface IFolderProvider
{
    string LocalAppData { get; }
    string RoamingAppData { get; }
    string Temp { get; }

    /// <summary>Carpeta del perfil del usuario (solo se usan sus subcarpetas de herramientas, «.cache», «.ollama»…).</summary>
    string UserProfile { get; }
    string LocalLowAppData { get; }
    string ProgramData { get; }
    string WindowsDirectory { get; }

    /// <summary>¿Se ejecuta con privilegios de administrador? En sandbox se simula que sí.</summary>
    bool IsElevated { get; }

    /// <summary>Carpeta propia de FFCleaner (ajustes, bitácoras y respaldos).</summary>
    string StateDirectory { get; }

    /// <summary>En sandbox, raíz que debe contener todo objetivo; null en producción.</summary>
    string? ContainmentRoot { get; }

    bool IsSandbox { get; }
}

/// <summary>Carpetas reales del usuario actual, resueltas por Windows en tiempo de ejecución.</summary>
public sealed class SystemFolderProvider : IFolderProvider
{
    public SystemFolderProvider()
    {
        LocalAppData = Known(Environment.SpecialFolder.LocalApplicationData, "LOCALAPPDATA");
        RoamingAppData = Known(Environment.SpecialFolder.ApplicationData, "APPDATA");
        Temp = PathSafety.Normalize(Path.GetTempPath());
        UserProfile = Known(Environment.SpecialFolder.UserProfile, "USERPROFILE");
        LocalLowAppData = Path.Combine(UserProfile, "AppData", "LocalLow");
        ProgramData = Known(Environment.SpecialFolder.CommonApplicationData, "ProgramData");
        WindowsDirectory = Known(Environment.SpecialFolder.Windows, "windir");
        using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
            IsElevated = new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        StateDirectory = Path.Combine(LocalAppData, "FFCleaner");
        MigratedFrom = MigrateLegacyState(Path.Combine(LocalAppData, "Claro"), StateDirectory);
    }

    public string LocalAppData { get; }
    public string RoamingAppData { get; }
    public string Temp { get; }
    public string StateDirectory { get; }
    public string UserProfile { get; }
    public string LocalLowAppData { get; }
    public string ProgramData { get; }
    public string WindowsDirectory { get; }
    public bool IsElevated { get; }
    public string? ContainmentRoot => null;
    public bool IsSandbox => false;

    /// <summary>Carpeta de la versión anterior (Claro) cuyos datos se copiaron en este arranque, o null.</summary>
    public string? MigratedFrom { get; }

    /// <summary>
    /// Primera ejecución con el nombre nuevo: copia ajustes, bitácoras, respaldos del Registro y log de la
    /// carpeta antigua para que sigan disponibles (los respaldos solo se restauran desde la carpeta propia).
    /// Copia, no mueve: la carpeta antigua queda intacta. Si algo falla, se arranca igual con lo copiado.
    /// </summary>
    internal static string? MigrateLegacyState(string legacy, string current)
    {
        try
        {
            if (Directory.Exists(current) || !Directory.Exists(legacy)) return null;
            if ((File.GetAttributes(legacy) & FileAttributes.ReparsePoint) != 0) return null;
            var staging = current + ".migrando-" + Guid.NewGuid().ToString("N")[..8];
            CopyTree(legacy, staging);
            Directory.Move(staging, current);
            var log = Path.Combine(current, "Logs", "claro.log");
            if (File.Exists(log)) File.Move(log, Path.Combine(current, "Logs", "ffcleaner.log"));
            return legacy;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning("Migración de datos de Claro no completada: {0}", ex.Message);
            return null;
        }
    }

    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;   // nunca se siguen enlaces
            var target = Path.Combine(destination, entry.Name);
            if (entry is DirectoryInfo) CopyTree(entry.FullName, target);
            else File.Copy(entry.FullName, target, overwrite: false);
        }
    }

    private static string Known(Environment.SpecialFolder folder, string variable)
    {
        var path = Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);
        if (string.IsNullOrWhiteSpace(path)) path = Environment.GetEnvironmentVariable(variable) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("Windows no informó la carpeta " + variable + ".");
        return PathSafety.Normalize(path);
    }
}

/// <summary>
/// Perfil simulado para pruebas. Solo acepta una carpeta «ffcleaner-sandbox-*» bajo el TEMP real que contenga
/// el marcador <see cref="MarkerFile"/>; así ningún parámetro puede redirigir la limpieza a datos reales.
/// </summary>
public sealed class SandboxFolderProvider : IFolderProvider
{
    public const string DirectoryPrefix = "ffcleaner-sandbox-";
    public const string MarkerFile = ".ffcleaner-sandbox";

    public SandboxFolderProvider(string sandboxRoot)
    {
        var root = PathSafety.Normalize(sandboxRoot);
        var temp = PathSafety.Normalize(Path.GetTempPath());
        if (!PathSafety.IsStrictlyWithin(root, temp))
            throw new ArgumentException("El sandbox debe estar bajo la carpeta temporal del usuario.", nameof(sandboxRoot));
        if (!Path.GetFileName(root).StartsWith(DirectoryPrefix, StringComparison.Ordinal))
            throw new ArgumentException("El sandbox debe llamarse «" + DirectoryPrefix + "*».", nameof(sandboxRoot));
        if (!File.Exists(Path.Combine(root, MarkerFile)))
            throw new ArgumentException("Falta el marcador " + MarkerFile + " en el sandbox.", nameof(sandboxRoot));
        PathSafety.EnsureSafe(root);
        ContainmentRoot = root;
        LocalAppData = Path.Combine(root, "Local");
        RoamingAppData = Path.Combine(root, "Roaming");
        Temp = Path.Combine(root, "Temp");
        StateDirectory = Path.Combine(root, "State");
        UserProfile = Path.Combine(root, "Home");
        LocalLowAppData = Path.Combine(root, "LocalLow");
        ProgramData = Path.Combine(root, "ProgramData");
        WindowsDirectory = Path.Combine(root, "Windows");
    }

    public string UserProfile { get; }
    public string LocalLowAppData { get; }
    public string ProgramData { get; }
    public string WindowsDirectory { get; }
    public bool IsElevated => true;

    public string LocalAppData { get; }
    public string RoamingAppData { get; }
    public string Temp { get; }
    public string StateDirectory { get; }
    public string? ContainmentRoot { get; }
    public bool IsSandbox => true;

    /// <summary>Crea una carpeta sandbox vacía y válida bajo el TEMP del usuario.</summary>
    public static string CreateRoot(string? parent = null)
    {
        var root = Path.Combine(parent ?? Path.GetTempPath(), DirectoryPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, MarkerFile), "Carpeta de pruebas de FFCleaner. Se puede borrar.");
        foreach (var name in new[] { "Local", "Roaming", "Temp", "State", "Home", "LocalLow", "ProgramData", "Windows" }) Directory.CreateDirectory(Path.Combine(root, name));
        return root;
    }
}

/// <summary>Sonda real de procesos por nombre de imagen (sin extensión).</summary>
public sealed class SystemProcessProbe : IProcessProbe
{
    public bool IsRunning(string processName)
    {
        try
        {
            var found = System.Diagnostics.Process.GetProcessesByName(processName);
            foreach (var process in found) process.Dispose();
            return found.Length > 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
