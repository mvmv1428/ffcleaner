using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace FFCleaner.Setup;

/// <summary>Opciones de instalación o desinstalación. Las rutas alternativas existen solo para pruebas en sandbox.</summary>
public sealed record SetupOptions
{
    public const string DefaultRegistryRoot = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    public const string SandboxRegistryPrefix = @"Software\CleanerApp_TestSandbox\";

    public bool Uninstall { get; init; }
    public bool Quiet { get; init; }
    public bool RemoveData { get; init; }
    public bool DesktopShortcut { get; init; } = true;
    public bool Launch { get; init; } = true;
    public bool Relocated { get; init; }
    public string Destination { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "FFCleaner");
    public string StartMenu { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
    public string DesktopDirectory { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    public string RegistryRoot { get; init; } = DefaultRegistryRoot;
    public string DataDirectory { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FFCleaner");
    public string? InstallDirectory { get; init; }

    /// <summary>Actualización automática: conserva las elecciones de la instalación existente (accesos, carpetas).</summary>
    public bool Update { get; init; }
    /// <summary>Proceso de FFCleaner que hay que esperar a que se cierre antes de sustituir sus archivos.</summary>
    public int? WaitPid { get; init; }
    /// <summary>En modo silencioso, muestra una ventana de progreso (actualización automática).</summary>
    public bool ShowProgress { get; init; }
    /// <summary>Argumentos con los que se vuelve a abrir FFCleaner al terminar.</summary>
    public string? LaunchArguments { get; init; }
    /// <summary>Versión publicada que se intenta instalar (la que se anota si falla). Sin ella, la del propio instalador.</summary>
    public string? TargetVersion { get; init; }

    /// <summary>En una actualización, toma de install.json las carpetas y accesos que eligió el usuario al instalar.</summary>
    public SetupOptions WithPreviousChoices()
    {
        if (!Update || !File.Exists(Path.Combine(Destination, "install.json"))) return this;
        var manifest = SetupEngine.ReadManifest(Destination);
        var startMenu = manifest.Shortcuts.Count > 0 ? Path.GetDirectoryName(manifest.Shortcuts[0]) : null;
        var desktop = manifest.Shortcuts.Count > 1 ? Path.GetDirectoryName(manifest.Shortcuts[1]) : null;
        var root = manifest.RegistryKey.EndsWith("\\" + SetupEngine.KeyName, StringComparison.OrdinalIgnoreCase)
            ? manifest.RegistryKey[..^(SetupEngine.KeyName.Length + 1)] : RegistryRoot;
        return this with
        {
            StartMenu = startMenu ?? StartMenu, DesktopShortcut = desktop is not null, DesktopDirectory = desktop ?? DesktopDirectory,
            RegistryRoot = root, DataDirectory = manifest.DataDirectory,
        };
    }

    public static SetupOptions Parse(IReadOnlyList<string> args)
    {
        var o = new SetupOptions();
        for (var i = 0; i < args.Count; i++)
        {
            string Next() => i + 1 < args.Count ? args[++i] : throw new ArgumentException("Falta el valor de " + args[i]);
            o = args[i] switch
            {
                "--uninstall" => o with { Uninstall = true },
                "--quiet" => o with { Quiet = true },
                "--remove-data" => o with { RemoveData = true },
                "--desktop" => o with { DesktopShortcut = true },
                "--no-desktop" => o with { DesktopShortcut = false },
                "--no-launch" => o with { Launch = false },
                "--relocated" => o with { Relocated = true },
                "--update" => o with { Update = true },
                "--show-progress" => o with { ShowProgress = true },
                "--wait-pid" => o with { WaitPid = int.TryParse(Next(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var pid)
                    ? pid : throw new ArgumentException("Valor no válido en --wait-pid.") },
                "--launch-args" => o with { LaunchArguments = Next() },
                "--target-version" => o with { TargetVersion = System.Version.TryParse(Next(), out var target) && target.Build >= 0 ? target.ToString(3) : throw new ArgumentException("Versión no válida en --target-version") },
                "--dest" => o with { Destination = Path.GetFullPath(Next()) },
                "--start-menu" => o with { StartMenu = Path.GetFullPath(Next()) },
                "--desktop-dir" => o with { DesktopDirectory = Path.GetFullPath(Next()) },
                "--data-dir" => o with { DataDirectory = Path.GetFullPath(Next()) },
                "--install-dir" => o with { InstallDirectory = Path.GetFullPath(Next()) },
                "--registry-root" => o with { RegistryRoot = Next() },
                _ => throw new ArgumentException("Argumento no reconocido: " + args[i]),
            };
        }
        // Las pruebas solo pueden redirigir el registro a la clave de pruebas; nunca a otra rama real.
        if (o.RegistryRoot != DefaultRegistryRoot && !o.RegistryRoot.StartsWith(SandboxRegistryPrefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("--registry-root solo admite claves bajo HKCU\\" + SandboxRegistryPrefix);
        return o;
    }
}

/// <summary>FFCleaner (o su servidor MCP) está abierto desde la carpeta que se quiere sustituir o borrar.</summary>
public sealed class AppRunningException(string message) : InvalidOperationException(message);

/// <summary>Lo que se instaló, para poder quitarlo exactamente. Se guarda como install.json en la carpeta instalada.</summary>
public sealed record InstallManifest(string Version, string Destination, List<string> Shortcuts, string RegistryKey, string DataDirectory);

/// <summary>Instala, actualiza y desinstala FFCleaner por usuario, sin administrador. Sin interfaz: la usa el asistente.</summary>
public static class SetupEngine
{
    public const string KeyName = "FFCleaner";
    private static readonly string[] ExtraFiles = ["SHA256SUMS.json", "install.json", "uninstall.ps1", "ffcleaner.png"];
    public static string Version => typeof(SetupEngine).Assembly.GetName().Version?.ToString(3) ?? "0";

    /// <summary>Marca de la carpeta donde se prepara una versión antes de sustituir la instalada.</summary>
    private const string StagingMarker = ".ffcleaner-staging";

    /// <summary>
    /// Instala desde la carpeta del paquete (la del propio instalador). Devuelve la ruta de FFCleaner.exe.
    /// La versión nueva se copia y se comprueba en una carpeta aparte, junto al destino; solo entonces se cambia por la
    /// instalada con dos renombrados. Si algo falla antes, la versión instalada sigue intacta y funcionando.
    /// </summary>
    public static string Install(string source, SetupOptions o, IProgress<(double Percent, string Text)> progress)
    {
        var dest = Path.GetFullPath(o.Destination).TrimEnd('\\');
        Report(progress, 2, "Comprobando…");
        EnsureNotRunning(dest);
        var sums = ReadSums(Path.Combine(source, "SHA256SUMS.json")) ?? throw new InvalidOperationException("El paquete del instalador está incompleto (falta SHA256SUMS.json).");
        if (!File.Exists(Path.Combine(source, "FFCleaner.exe"))) throw new InvalidOperationException("El paquete del instalador está incompleto (falta FFCleaner.exe).");
        if (string.Equals(Path.GetFullPath(source).TrimEnd('\\'), dest, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("No se puede instalar sobre la misma carpeta del instalador.");
        if (Path.GetPathRoot(dest)!.Length >= dest.Length) throw new InvalidOperationException("No se puede instalar en la raíz de una unidad.");

        RecoverInterruptedSwap(dest);
        if (!File.Exists(Path.Combine(dest, "SHA256SUMS.json")) && Directory.Exists(dest) && Directory.EnumerateFileSystemEntries(dest).Any())
            throw new InvalidOperationException("La carpeta " + dest + " no está vacía y no es una instalación de FFCleaner. Elige otra.");

        // 1. La versión nueva se prepara aparte. Misma carpeta superior que el destino, así que el cambio es un renombrado.
        var suffix = "-" + Guid.NewGuid().ToString("N")[..8];
        var staging = dest + ".nueva" + suffix;
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, StagingMarker), "Versión de FFCleaner en preparación. Se puede borrar.");
        try
        {
            var files = sums.Keys.ToList();
            for (var i = 0; i < files.Count; i++)
            {
                // Un nombre del paquete no puede salir de la carpeta de destino.
                if (!TryCombineInside(staging, files[i], out var target) ||
                    !TryCombineInside(source, files[i], out var origin) || !File.Exists(origin))
                    throw new InvalidOperationException("El paquete contiene una ruta no válida: " + files[i]);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(origin, target, overwrite: true);
                if (i % 20 == 0) Report(progress, 5 + 70.0 * i / files.Count, $"Copiando archivos… {i + 1} de {files.Count}");
            }
            File.Copy(Path.Combine(source, "SHA256SUMS.json"), Path.Combine(staging, "SHA256SUMS.json"), overwrite: true);

            Report(progress, 76, "Comprobando la integridad de la versión nueva…");
            foreach (var (name, hash) in sums)
                if (!TryCombineInside(staging, name, out var copied) ||
                    !string.Equals(Hash(copied), hash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("El archivo " + name + " no se copió correctamente. Vuelve a ejecutar el instalador.");
            File.Delete(Path.Combine(staging, StagingMarker));

            // 2. Cambio: la instalada se aparta y la nueva ocupa su lugar. Si el segundo paso falla, se deshace el primero.
            Report(progress, 84, "Sustituyendo la versión instalada…");
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            string? previous = null;
            if (Directory.Exists(dest))
            {
                previous = dest + ".anterior" + suffix;
                try { Directory.Move(dest, previous); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new InvalidOperationException("No se pudo sustituir la versión instalada (" + ex.Message + "). Sigue instalada la de antes, sin cambios.");
                }
            }
            try { Directory.Move(staging, dest); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (previous is not null && !Directory.Exists(dest)) Directory.Move(previous, dest);
                throw new InvalidOperationException("No se pudo colocar la versión nueva (" + ex.Message + "). Sigue instalada la de antes, sin cambios.");
            }

            // 3. Se borra lo instalado antes; lo que no era de FFCleaner (si alguien guardó algo ahí) vuelve a la carpeta.
            if (previous is not null) RetirePrevious(previous, dest);
        }
        finally
        {
            if (Directory.Exists(staging) && File.Exists(Path.Combine(staging, StagingMarker)))
                try { Directory.Delete(staging, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            else if (Directory.Exists(staging))
                try { RemoveInstalledFiles(staging); if (!Directory.EnumerateFileSystemEntries(staging).Any()) Directory.Delete(staging); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        Report(progress, 90, "Creando accesos directos…");
        var exe = Path.Combine(dest, "FFCleaner.exe");
        var shortcuts = new List<string> { CreateShortcut(o.StartMenu, exe, dest) };
        var desktopLink = Path.Combine(o.DesktopDirectory, "FFCleaner.lnk");
        if (o.DesktopShortcut) shortcuts.Add(CreateShortcut(o.DesktopDirectory, exe, dest));
        else if (File.Exists(desktopLink) && string.Equals(ShortcutTarget(desktopLink), exe, StringComparison.OrdinalIgnoreCase)) File.Delete(desktopLink);

        Report(progress, 95, "Registrando en «Aplicaciones instaladas»…");
        var key = o.RegistryRoot + "\\" + KeyName;
        var manifest = new InstallManifest(Version, dest, shortcuts, key, o.DataDirectory);
        File.WriteAllText(Path.Combine(dest, "install.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        var setup = Path.Combine(dest, "FFCleaner.Setup.exe");
        var size = new DirectoryInfo(dest).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) / 1024;
        using (var reg = Registry.CurrentUser.CreateSubKey(key, writable: true))
        {
            reg.SetValue("DisplayName", "FFCleaner");
            reg.SetValue("DisplayVersion", Version);
            reg.SetValue("Publisher", "FFCleaner");
            reg.SetValue("DisplayIcon", exe + ",0");
            reg.SetValue("InstallLocation", dest);
            reg.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
            reg.SetValue("UninstallString", "\"" + setup + "\" --uninstall");
            reg.SetValue("QuietUninstallString", "\"" + setup + "\" --uninstall --quiet");
            reg.SetValue("Comments", "Limpieza personalizada, Registro e inicio de Windows");
            reg.SetValue("NoModify", 1, RegistryValueKind.DWord);
            reg.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            reg.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, size), RegistryValueKind.DWord);
        }
        Report(progress, 100, "Instalación completada.");
        return exe;
    }

    public static InstallManifest ReadManifest(string installDirectory)
    {
        var path = Path.Combine(installDirectory, "install.json");
        if (!File.Exists(path)) throw new InvalidOperationException("No se encuentra install.json en " + installDirectory + ": no parece una instalación de FFCleaner.");
        return JsonSerializer.Deserialize<InstallManifest>(File.ReadAllText(path)) ?? throw new InvalidOperationException("install.json no es válido.");
    }

    /// <summary>Quita lo instalado según install.json. Los datos del usuario solo si se pide.</summary>
    public static void Uninstall(InstallManifest manifest, bool removeData, IProgress<(double Percent, string Text)> progress)
    {
        Report(progress, 5, "Comprobando que FFCleaner no esté abierto…");
        EnsureNotRunning(manifest.Destination);
        EnsureManifestIsSane(manifest, removeData);
        Report(progress, 20, "Quitando accesos directos…");
        // install.json se puede editar: solo se borran accesos «FFCleaner.lnk» que apuntan a este FFCleaner.exe.
        var installedExe = Path.Combine(manifest.Destination, "FFCleaner.exe");
        foreach (var link in manifest.Shortcuts) if (IsOwnShortcut(link, installedExe)) File.Delete(link);
        Report(progress, 35, "Quitando la entrada de «Aplicaciones instaladas»…");
        Registry.CurrentUser.DeleteSubKeyTree(manifest.RegistryKey, throwOnMissingSubKey: false);
        if (removeData && Directory.Exists(manifest.DataDirectory))
        {
            Report(progress, 50, "Borrando tus datos de FFCleaner…");
            Directory.Delete(manifest.DataDirectory, recursive: true);
        }
        Report(progress, 65, "Borrando los archivos del programa…");
        RemoveInstalledFiles(manifest.Destination);
        if (Directory.Exists(manifest.Destination) && !Directory.EnumerateFileSystemEntries(manifest.Destination).Any())
            Directory.Delete(manifest.Destination);
        Report(progress, 100, "Desinstalación completada.");
    }

    /// <summary>Borra solo los archivos que instaló FFCleaner (según SHA256SUMS.json) y las carpetas que queden vacías.</summary>
    private static void RemoveInstalledFiles(string dest)
    {
        var sums = ReadSums(Path.Combine(dest, "SHA256SUMS.json")) ?? new Dictionary<string, string>();
        foreach (var name in sums.Keys.Concat(ExtraFiles))
        {
            if (!TryCombineInside(dest, name, out var path)) continue;
            if (!File.Exists(path)) continue;
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
        if (!Directory.Exists(dest)) return;
        foreach (var dir in Directory.EnumerateDirectories(dest, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
            if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
    }

    /// <summary>
    /// Falla si FFCleaner o su servidor MCP están abiertos desde la carpeta instalada. Un proceso con ese nombre cuya ruta
    /// no se puede leer cuenta como abierto: es más seguro pedir que se cierre que borrar archivos que podría estar usando.
    /// </summary>
    private static void EnsureNotRunning(string dest)
    {
        var prefix = Path.GetFullPath(dest).TrimEnd('\\') + "\\";
        foreach (var name in new[] { "FFCleaner", "FFCleaner.Mcp" })
            foreach (var process in Process.GetProcessesByName(name))
                using (process)
                {
                    var path = ProcessImage.PathOf(process);
                    if (path is null || path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        throw new AppRunningException(name == "FFCleaner"
                            ? "FFCleaner está abierto. Ciérralo y vuelve a intentarlo."
                            : "Un agente está usando el servidor MCP de FFCleaner. Ciérralo y vuelve a intentarlo.");
                }
    }

    /// <summary>
    /// Si una sustitución anterior se interrumpió entre los dos renombrados (un corte de luz), devuelve la versión
    /// apartada a su sitio. Después retira las carpetas de preparación y de versiones apartadas que quedaron.
    /// </summary>
    private static void RecoverInterruptedSwap(string dest)
    {
        var parent = Path.GetDirectoryName(dest)!;
        if (!Directory.Exists(parent)) return;
        var name = Path.GetFileName(dest);
        var previous = Directory.EnumerateDirectories(parent, name + ".anterior-*")
            .Where(d => File.Exists(Path.Combine(d, "SHA256SUMS.json"))).OrderByDescending(Directory.GetLastWriteTimeUtc).ToList();
        if (!Directory.Exists(dest) && previous.Count > 0)
        {
            Directory.Move(previous[0], dest);
            previous.RemoveAt(0);
        }
        foreach (var old in previous) RetirePrevious(old, dest);
        foreach (var staging in Directory.EnumerateDirectories(parent, name + ".nueva-*").ToList())
            try
            {
                if (File.Exists(Path.Combine(staging, StagingMarker))) Directory.Delete(staging, recursive: true);
                else if (File.Exists(Path.Combine(staging, "SHA256SUMS.json")))
                {
                    RemoveInstalledFiles(staging);
                    if (!Directory.EnumerateFileSystemEntries(staging).Any()) Directory.Delete(staging);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Retira la versión apartada: borra lo que instaló FFCleaner y devuelve a la carpeta instalada lo demás (si alguien
    /// guardó algo suyo allí). Si algo no se puede retirar ahora, se intentará en la próxima actualización.
    /// </summary>
    private static void RetirePrevious(string previous, string dest)
    {
        try
        {
            RemoveInstalledFiles(previous);
            if (!Directory.Exists(previous)) return;
            foreach (var entry in Directory.EnumerateFileSystemEntries(previous).ToList())
            {
                var target = Path.Combine(dest, Path.GetFileName(entry));
                if (File.Exists(target) || Directory.Exists(target)) continue;
                if (Directory.Exists(entry)) Directory.Move(entry, target);
                else File.Move(entry, target);
            }
            if (!Directory.EnumerateFileSystemEntries(previous).Any()) Directory.Delete(previous);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>¿Es un acceso directo «FFCleaner.lnk» que abre exactamente este FFCleaner.exe?</summary>
    private static bool IsOwnShortcut(string link, string exe)
    {
        try
        {
            return Path.GetFileName(link).Equals("FFCleaner.lnk", StringComparison.OrdinalIgnoreCase) && File.Exists(link) &&
                   string.Equals(Path.GetFullPath(ShortcutTarget(link)), Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Nombre del aviso de actualización fallida en la carpeta de datos (lo lee FFCleaner 0.3.3 o posterior).</summary>
    public const string FailedMarkerName = "update-failed.json";

    /// <summary>Anota que la instalación de esta versión falló, para que FFCleaner no la reintente en bucle durante 24 horas.</summary>
    public static void RecordFailedUpdate(string dataDirectory, string? targetVersion, string reason)
    {
        try
        {
            Directory.CreateDirectory(dataDirectory);
            File.WriteAllText(Path.Combine(dataDirectory, FailedMarkerName), JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["version"] = targetVersion ?? Version, ["utc"] = DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture), ["motivo"] = reason,
            }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>¿La versión instalada sabe leer el aviso de fallo (0.3.3 o posterior)? Si no, reabrirla repetiría el intento.</summary>
    public static bool UnderstandsFailedMarker(string exe)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(exe);
            return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart) >= new Version(0, 3, 3);
        }
        catch (FileNotFoundException) { return false; }
    }

    private static Dictionary<string, string>? ReadSums(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) : null;

    /// <summary>
    /// Une un nombre relativo al paquete con su carpeta base. Un nombre con «..», ruta absoluta, unidad o
    /// dos puntos intentaría salir de la carpeta: se descarta en lugar de copiarlo o borrarlo fuera.
    /// </summary>
    internal static bool TryCombineInside(string baseDirectory, string relative, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) ||
            relative.Contains("..", StringComparison.Ordinal) || relative.Contains(':', StringComparison.Ordinal)) return false;
        var full = Path.GetFullPath(Path.Combine(baseDirectory, relative.Replace('/', '\\')));
        var root = Path.GetFullPath(baseDirectory).TrimEnd('\\') + "\\";
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
        path = full;
        return true;
    }

    /// <summary>
    /// install.json vive en una carpeta escribible por el usuario: no se le concede permiso para borrar
    /// claves del Registro ajenas ni carpetas de datos que no sean las suyas.
    /// </summary>
    private static void EnsureManifestIsSane(InstallManifest manifest, bool removeData)
    {
        if (!RemoveInstalledFilesAllowed(manifest.Destination))
            throw new InvalidOperationException("install.json apunta a una carpeta que FFCleaner no debe modificar: " + manifest.Destination);
        var key = (manifest.RegistryKey ?? string.Empty).Trim('\\');
        if (!key.Equals(KeyName, StringComparison.OrdinalIgnoreCase) &&
            !key.EndsWith("\\" + KeyName, StringComparison.OrdinalIgnoreCase) &&
            // Con su separador final: una clave hermana como «CleanerApp_TestSandboxX» no es del sandbox.
            !key.StartsWith(SetupOptions.SandboxRegistryPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("install.json apunta a una clave del Registro ajena a FFCleaner: " + manifest.RegistryKey);
        if (!removeData) return;
        var data = manifest.DataDirectory;
        if (!OnlyOwnData(data))
            throw new InvalidOperationException("install.json apunta a una carpeta de datos que no es de FFCleaner: " + data);
    }

    /// <summary>La carpeta de la instalación debe ser una carpeta normal, no una raíz ni un enlace.</summary>
    private static bool RemoveInstalledFilesAllowed(string destination)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(destination) || !Path.IsPathFullyQualified(destination)) return false;
            var full = Path.GetFullPath(destination);
            if (full.Length <= Path.GetPathRoot(full)!.Length + 1) return false;
            if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) return false;
            return System.IO.Directory.Exists(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    /// <summary>
    /// Solo se borran datos propios: exactamente %LOCALAPPDATA%\FFCleaner, o una carpeta dentro de un sandbox de
    /// pruebas. Nunca otra carpeta que se llame igual, Documentos, Escritorio, una raíz de unidad o el perfil completo.
    /// </summary>
    private static bool OnlyOwnData(string? dataDirectory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dataDirectory) || !Path.IsPathFullyQualified(dataDirectory)) return false;
            var full = Path.GetFullPath(dataDirectory).TrimEnd('\\');
            var root = Path.GetPathRoot(full)!;
            if (full.Length <= root.Length + 1) return false;
            if (System.IO.Directory.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) return false;
            // La carpeta de datos real, exacta…
            var defaultData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), KeyName);
            if (full.Equals(Path.GetFullPath(defaultData).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return true;
            // …o una carpeta dentro de un sandbox de pruebas (%TEMP%\ffcleaner-sandbox-* con su archivo marcador).
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\') + "\\";
            if (!full.StartsWith(temp, StringComparison.OrdinalIgnoreCase)) return false;
            var sandbox = full[temp.Length..].Split('\\');
            return sandbox.Length > 1 && sandbox[0].StartsWith("ffcleaner-sandbox-", StringComparison.OrdinalIgnoreCase) &&
                   File.Exists(Path.Combine(temp, sandbox[0], ".ffcleaner-sandbox"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string CreateShortcut(string folder, string exe, string workingDirectory)
    {
        Directory.CreateDirectory(folder);
        var link = Path.Combine(folder, "FFCleaner.lnk");
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        var shortcut = shell.CreateShortcut(link);
        shortcut.TargetPath = exe;
        shortcut.WorkingDirectory = workingDirectory;
        shortcut.IconLocation = exe + ",0";
        shortcut.Description = "Limpieza personalizada, Registro e inicio de Windows";
        shortcut.Save();
        return link;
    }

    private static string ShortcutTarget(string link)
    {
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        return (string)shell.CreateShortcut(link).TargetPath;
    }

    private static void Report(IProgress<(double, string)> progress, double percent, string text) => progress.Report((percent, text));
}
