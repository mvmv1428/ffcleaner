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
    /// <summary>Argumentos con los que se vuelve a abrir FFCleaner al terminar.</summary>
    public string? LaunchArguments { get; init; }

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
                "--wait-pid" => o with { WaitPid = int.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture) },
                "--launch-args" => o with { LaunchArguments = Next() },
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

/// <summary>Lo que se instaló, para poder quitarlo exactamente. Se guarda como install.json en la carpeta instalada.</summary>
public sealed record InstallManifest(string Version, string Destination, List<string> Shortcuts, string RegistryKey, string DataDirectory);

/// <summary>Instala, actualiza y desinstala FFCleaner por usuario, sin administrador. Sin interfaz: la usa el asistente.</summary>
public static class SetupEngine
{
    public const string KeyName = "FFCleaner";
    private static readonly string[] ExtraFiles = ["SHA256SUMS.json", "install.json", "uninstall.ps1", "ffcleaner.png"];
    public static string Version => typeof(SetupEngine).Assembly.GetName().Version?.ToString(3) ?? "0";

    /// <summary>Instala desde la carpeta del paquete (la del propio instalador). Devuelve la ruta de FFCleaner.exe.</summary>
    public static string Install(string source, SetupOptions o, IProgress<(double Percent, string Text)> progress)
    {
        var dest = o.Destination;
        Report(progress, 2, "Comprobando…");
        EnsureNotRunning(dest);
        var sums = ReadSums(Path.Combine(source, "SHA256SUMS.json")) ?? throw new InvalidOperationException("El paquete del instalador está incompleto (falta SHA256SUMS.json).");
        if (!File.Exists(Path.Combine(source, "FFCleaner.exe"))) throw new InvalidOperationException("El paquete del instalador está incompleto (falta FFCleaner.exe).");
        if (string.Equals(Path.GetFullPath(source).TrimEnd('\\'), Path.GetFullPath(dest).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("No se puede instalar sobre la misma carpeta del instalador.");

        if (File.Exists(Path.Combine(dest, "SHA256SUMS.json")))
        {
            Report(progress, 5, "Retirando la versión instalada…");
            RemoveInstalledFiles(dest);
        }
        else if (Directory.Exists(dest) && Directory.EnumerateFileSystemEntries(dest).Any())
            throw new InvalidOperationException("La carpeta " + dest + " no está vacía y no es una instalación de FFCleaner. Elige otra.");

        Directory.CreateDirectory(dest);
        var files = sums.Keys.ToList();
        for (var i = 0; i < files.Count; i++)
        {
            var relative = files[i].Replace('/', '\\');
            var target = Path.Combine(dest, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(source, relative), target, overwrite: true);
            if (i % 20 == 0) Report(progress, 8 + 72.0 * i / files.Count, $"Copiando archivos… {i + 1} de {files.Count}");
        }
        File.Copy(Path.Combine(source, "SHA256SUMS.json"), Path.Combine(dest, "SHA256SUMS.json"), overwrite: true);

        Report(progress, 82, "Comprobando la integridad de lo instalado…");
        foreach (var (name, hash) in sums)
            if (!string.Equals(Hash(Path.Combine(dest, name.Replace('/', '\\'))), hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("El archivo " + name + " no se copió correctamente. Vuelve a ejecutar el instalador.");

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
        Report(progress, 20, "Quitando accesos directos…");
        foreach (var link in manifest.Shortcuts) if (File.Exists(link)) File.Delete(link);
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
            var path = Path.Combine(dest, name.Replace('/', '\\'));
            if (!File.Exists(path)) continue;
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
        if (!Directory.Exists(dest)) return;
        foreach (var dir in Directory.EnumerateDirectories(dest, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
            if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
    }

    private static void EnsureNotRunning(string dest)
    {
        var prefix = Path.GetFullPath(dest).TrimEnd('\\') + "\\";
        foreach (var name in new[] { "FFCleaner", "FFCleaner.Mcp" })
            foreach (var process in Process.GetProcessesByName(name))
            {
                string? path = null;
                try { path = process.MainModule?.FileName; } catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
                if (path is not null && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(name == "FFCleaner"
                        ? "FFCleaner está abierto. Ciérralo y pulsa Reintentar."
                        : "Un agente está usando el servidor MCP de FFCleaner. Ciérralo y pulsa Reintentar.");
            }
    }

    private static Dictionary<string, string>? ReadSums(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) : null;

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
