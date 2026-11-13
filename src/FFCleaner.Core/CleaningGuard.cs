namespace FFCleaner.Core;

/// <summary>
/// Guardas explícitas de la limpieza. Se aplican dos veces: al analizar (para clasificar) y de nuevo
/// justo antes de eliminar (para decidir). Ninguna categoría puede relajarlas.
/// </summary>
public static class CleaningGuard
{
    /// <summary>Nombres de componentes que nunca se eliminan: cookies, historial, contraseñas, sesiones, perfiles.</summary>
    private static readonly HashSet<string> SensitiveComponents = new(StringComparer.OrdinalIgnoreCase)
    {
        "Cookies", "Cookies-journal", "Login Data", "Login Data For Account", "Web Data", "History",
        "History-journal", "Top Sites", "Visited Links", "Favicons", "Shortcuts", "Bookmarks", "Bookmarks.bak",
        "Preferences", "Secure Preferences", "Local State", "Sessions", "Session Storage", "Current Session",
        "Current Tabs", "Last Session", "Last Tabs", "Local Storage", "IndexedDB", "File System", "Network",
        "Extensions", "Extension State", "Local Extension Settings", "Sync Data", "Credentials", "Vault",
        "Passwords", "logins.json", "key4.db", "cert9.db", "cookies.sqlite", "places.sqlite", "formhistory.sqlite",
        "favicons.sqlite", "sessionstore.jsonlz4", "sessionstore-backups", "prefs.js", ".ssh", ".aws", ".azure",
        "models", "Virtual Machines", "Prefetch",
    };

    private static readonly string[] SensitivePrefixes =
        ["cookie", "login data", "credential", "password", "session", "token"];

    private static readonly HashSet<string> ProtectedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pem", ".key", ".pfx", ".p12", ".kdbx", ".gguf", ".safetensors", ".onnx", ".vhd", ".vhdx", ".vmdk",
        ".qcow2", ".sqlite", ".db", ".wal", ".shm", ".journal",
    };

    /// <summary>
    /// Únicas hojas de caché regenerable admitidas dentro de un perfil de navegador. Todo lo demás del
    /// perfil (cookies, historial, contraseñas, sesiones, almacenamiento de sitios) queda fuera por construcción.
    /// </summary>
    public static readonly IReadOnlySet<string> BrowserCacheLeaves = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Cache", "Media Cache", "Code Cache", "GPUCache", "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache",
        "GrShaderCache", "GraphiteDawnCache", "ShaderCache", @"Service Worker\CacheStorage",
        @"Service Worker\ScriptCache", "cache2", "startupCache",
    };

    private static readonly HashSet<string> BrowserDataFolders = new(StringComparer.OrdinalIgnoreCase) { "User Data", "Profiles" };

    public static bool IsSensitiveComponent(string component)
    {
        if (SensitiveComponents.Contains(component)) return true;
        foreach (var prefix in SensitivePrefixes)
            if (component.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>¿Algún componente de <paramref name="directory"/> bajo <paramref name="anchor"/> es sensible?</summary>
    public static bool IsSensitiveDirectory(string directory, string anchor)
    {
        if (!PathSafety.IsWithin(directory, anchor)) return true;
        var relative = directory.AsSpan(Math.Min(anchor.Length, directory.Length)).Trim('\\');
        foreach (var range in relative.Split('\\'))
        {
            var part = relative[range];
            if (part.Length != 0 && IsSensitiveComponent(part.ToString())) return true;
        }
        return false;
    }

    /// <summary>Veredicto por nombre y antigüedad. No consulta el disco.</summary>
    /// <summary>
    /// Igual que <see cref="IsSensitiveDirectory(string, string)"/>, pero en una categoría sin reglas de nombres
    /// solo se exige que la carpeta esté dentro de su ámbito.
    /// </summary>
    public static bool IsSensitiveDirectory(string directory, string anchor, CleaningPolicy policy) =>
        policy.NameGuards ? IsSensitiveDirectory(directory, anchor) : !PathSafety.IsWithin(directory, anchor);

    public static FileVerdict CheckFile(CleaningPolicy policy, string fileName, DateTime lastWriteUtc, DateTime nowUtc)
    {
        // Un nombre de archivo es un único componente: con separadores o «:» podría nombrar otra carpeta o un flujo.
        if (fileName.Length == 0 || fileName.IndexOfAny(['\\', '/', ':']) >= 0 || fileName is "." or "..") return FileVerdict.SensitiveName;
        if (policy.NameGuards && IsSensitiveComponent(fileName)) return FileVerdict.SensitiveName;
        if (policy.NamePrefix is { } prefix && !fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return FileVerdict.ExtensionNotAllowed;
        var extension = Path.GetExtension(fileName);
        if (ProtectedExtensions.Contains(extension) && policy.ExemptExtensions?.Contains(extension) != true) return FileVerdict.ProtectedExtension;
        if (fileName.EndsWith("-wal", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith("-shm", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith("-journal", StringComparison.OrdinalIgnoreCase))
            return FileVerdict.DatabaseSidecar;
        if (policy.AllowedExtensions is not null && !policy.AllowedExtensions.Contains(extension))
            return FileVerdict.ExtensionNotAllowed;
        if (policy.MinimumAge > TimeSpan.Zero && lastWriteUtc > nowUtc - policy.MinimumAge)
            return FileVerdict.TooRecent;
        return FileVerdict.Eligible;
    }

    /// <summary>
    /// Valida un objetivo completo antes de recorrerlo o de limpiarlo. Devuelve null si es aceptable
    /// o el motivo del rechazo.
    /// </summary>
    public static string? ValidateTarget(CleanupTarget target, IFolderProvider folders)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(folders);
        if (target.Roots.Count == 0) return "Objetivo sin raíces.";
        if (target.Data is { } kind)
        {
            // Datos de navegador: solo los archivos exactos de ese tipo dentro del perfil marcado.
            if (target.DataRoot is null) return "Datos de navegador sin carpeta de perfiles.";
            foreach (var root in target.Roots)
            {
                if (ValidateRoot(root.Path, folders) is { } problem) return problem;
                var expected = Path.Combine(target.DataRoot, target.Key.ProfileId, root.Leaf);
                if (!BrowserData.Leaves[kind].Contains(root.Leaf, StringComparer.OrdinalIgnoreCase) ||
                    !string.Equals(root.Path, expected, StringComparison.OrdinalIgnoreCase))
                    return "Archivo de navegador no reconocido para este tipo de dato: " + root.Path;
            }
            return null;
        }
        foreach (var root in target.Roots)
        {
            var reason = ValidateRoot(root.Path, folders);
            if (reason is not null) return reason;
            if (!PathSafety.IsStrictlyWithin(root.Path, target.Anchor))
                return "Raíz fuera del ámbito de su aplicación: " + root.Path;
            if (IsSensitiveDirectory(root.Path, target.Anchor, root.Policy))
                return "La raíz contiene un componente protegido: " + root.Path;
            if (target.DataRoot is null)
            {
                // Una categoría que no es de navegador jamás puede apuntar dentro de los datos de un navegador.
                foreach (var component in root.Path.Split('\\'))
                    if (BrowserDataFolders.Contains(component)) return "Raíz dentro de los datos de un navegador: " + root.Path;
                continue;
            }
            // Datos de navegador: la raíz debe ser exactamente <DataRoot>\<perfil>\<hoja de caché conocida>.
            var expected = Path.Combine(target.DataRoot, target.Key.ProfileId, root.Leaf);
            if (!BrowserCacheLeaves.Contains(root.Leaf) ||
                !string.Equals(root.Path, expected, StringComparison.OrdinalIgnoreCase))
                return "Raíz de navegador no reconocida como caché: " + root.Path;
        }
        return null;
    }

    /// <summary>Denegación incondicional de raíces peligrosas, con independencia de la categoría.</summary>
    public static string? ValidateRoot(string path, IFolderProvider folders)
    {
        string normalized;
        try { normalized = PathSafety.Normalize(path); }
        catch (ArgumentException ex) { return "Ruta inválida: " + ex.Message; }
        if (!string.Equals(normalized, path, StringComparison.OrdinalIgnoreCase)) return "Ruta no normalizada: " + path;
        if (!PathSafety.IsFixedDrive(normalized)) return "Unidad no fija o de red: " + path;
        if (folders.ContainmentRoot is { } containment && !PathSafety.IsStrictlyWithin(normalized, containment))
            return "Fuera del sandbox: " + path;
        // Lista cerrada de carpetas del sistema que sí se pueden limpiar (con administrador): exactas, sin comodines.
        if (SystemCleanupRoots(folders).Any(r => PathSafety.IsWithin(normalized, r))) return null;
        if (PathSafety.Depth(normalized) < 3) return "Raíz demasiado cercana a la unidad: " + path;
        if (folders.ContainmentRoot is null && !InAllowedBase(normalized, folders))
            return "Fuera de las carpetas de datos de aplicación conocidas: " + path;
        foreach (var location in ProtectedLocations.Value)
        {
            if (PathSafety.IsWithin(location.Path, normalized))
                return "La raíz contiene una carpeta protegida (" + location.Name + "): " + path;
            if (location.DenySubtree && PathSafety.IsWithin(normalized, location.Path))
                return "La raíz está dentro de una carpeta protegida (" + location.Name + "): " + path;
        }
        return null;
    }

    /// <summary>
    /// Bases donde puede haber cachés de aplicaciones: datos locales, itinerantes y LocalLow, TEMP, y las carpetas de
    /// herramientas del perfil cuyo nombre empieza por punto («.cache», «.ollama», «.lmstudio»…).
    /// </summary>
    private static bool InAllowedBase(string path, IFolderProvider folders)
    {
        if (PathSafety.IsStrictlyWithin(path, folders.LocalAppData) || PathSafety.IsWithin(path, folders.Temp) ||
            PathSafety.IsStrictlyWithin(path, folders.RoamingAppData) || PathSafety.IsStrictlyWithin(path, folders.LocalLowAppData))
            return true;
        if (!PathSafety.IsStrictlyWithin(path, folders.UserProfile)) return false;
        var first = path[(folders.UserProfile.TrimEnd('\\').Length + 1)..].Split('\\')[0];
        return first.Length > 1 && first[0] == '.' && path.Length > folders.UserProfile.Length + first.Length + 2;
    }

    /// <summary>Carpetas del sistema admitidas, derivadas de las carpetas reales de Windows (o del sandbox).</summary>
    public static IReadOnlyList<string> SystemCleanupRoots(IFolderProvider folders)
    {
        var windows = folders.WindowsDirectory;
        var wer = Path.Combine(folders.ProgramData, "Microsoft", "Windows", "WER");
        return
        [
            Path.Combine(windows, "Temp"),
            Path.Combine(windows, "Minidump"),
            Path.Combine(windows, "LiveKernelReports"),
            Path.Combine(windows, "Logs"),
            Path.Combine(windows, "ServiceProfiles", "NetworkService", "AppData", "Local", "Microsoft", "Windows", "DeliveryOptimization", "Cache"),
            Path.Combine(wer, "ReportArchive"),
            Path.Combine(wer, "ReportQueue"),
            Path.Combine(wer, "Temp"),
        ];
    }

    public static string Describe(FileVerdict verdict) => verdict switch
    {
        FileVerdict.Eligible => "Se puede limpiar",
        FileVerdict.SensitiveName => "Protegido: nombre de datos personales (cookies, sesiones, credenciales…)",
        FileVerdict.SensitiveDirectory => "Protegido: carpeta de datos personales",
        FileVerdict.ProtectedExtension => "Protegido: tipo de archivo de base de datos, clave o disco virtual",
        FileVerdict.DatabaseSidecar => "Protegido: archivo auxiliar de base de datos",
        FileVerdict.ExtensionNotAllowed => "Omitido: tipo de archivo no reconocido como temporal",
        FileVerdict.TooRecent => "Omitido: demasiado reciente para esta categoría",
        FileVerdict.Excluded => "Protegido: exclusión guardada en los ajustes",
        FileVerdict.MultipleLinks => "Compartido mediante enlaces físicos con otra carpeta: borrarlo no liberaría espacio",
        FileVerdict.ProtectedAttributes => "Omitido: atributos protegidos (solo lectura, sistema o cifrado)",
        _ => verdict.ToString(),
    };

    private sealed record ProtectedLocation(string Name, string Path, bool DenySubtree);

    private static readonly Lazy<IReadOnlyList<ProtectedLocation>> ProtectedLocations = new(() =>
    {
        var result = new List<ProtectedLocation>();
        void Add(string name, string? path, bool denySubtree)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try { result.Add(new(name, PathSafety.Normalize(path), denySubtree)); }
            catch (ArgumentException) { /* carpeta redirigida a red u otra forma no local: no aplica */ }
        }
        string Folder(Environment.SpecialFolder folder) =>
            Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);
        var profile = Folder(Environment.SpecialFolder.UserProfile);
        Add("Windows", Folder(Environment.SpecialFolder.Windows), true);
        Add("Archivos de programa", Folder(Environment.SpecialFolder.ProgramFiles), true);
        Add("Archivos de programa (x86)", Folder(Environment.SpecialFolder.ProgramFilesX86), true);
        Add("ProgramData", Folder(Environment.SpecialFolder.CommonApplicationData), false);
        Add("Perfil de usuario", profile, false);
        Add("Escritorio", Folder(Environment.SpecialFolder.DesktopDirectory), true);
        Add("Documentos", Folder(Environment.SpecialFolder.MyDocuments), true);
        Add("Imágenes", Folder(Environment.SpecialFolder.MyPictures), true);
        Add("Música", Folder(Environment.SpecialFolder.MyMusic), true);
        Add("Vídeos", Folder(Environment.SpecialFolder.MyVideos), true);
        if (!string.IsNullOrWhiteSpace(profile)) Add("Descargas", Path.Combine(profile, "Downloads"), true);
        Add("Datos de aplicación locales", Folder(Environment.SpecialFolder.LocalApplicationData), false);
        Add("Datos de aplicación", Folder(Environment.SpecialFolder.ApplicationData), false);
        foreach (var variable in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
            Add("OneDrive", Environment.GetEnvironmentVariable(variable), true);
        return result;
    });
}
