using System.Diagnostics;
using System.Text.Json;

namespace FFCleaner.Core;

/// <summary>
/// Descubre qué aplicaciones, categorías y perfiles existen en este equipo. Solo lectura.
/// Las definiciones son propias de FFCleaner: cada categoría enumera hojas de caché regenerable concretas.
/// </summary>
public static class CleanupCatalogBuilder
{
    public const string Version = "4";

    private static readonly CleaningPolicy NoAge = new(TimeSpan.Zero, null);

    /// <summary>
    /// Temporales: cualquier tipo de archivo con más de 24 horas. Lo más reciente puede estar en uso por un
    /// programa abierto; las guardas de nombres, bases de datos, enlaces y archivos en uso siguen aplicando.
    /// </summary>
    private static readonly CleaningPolicy UserTemp = new(TimeSpan.FromHours(24), null);

    /// <summary>uv comparte archivos de su caché con los entornos virtuales mediante enlaces físicos.</summary>
    private static readonly CleaningPolicy SharedByLinks = new(TimeSpan.Zero, null, VerifyLinks: true, NameGuards: false);

    /// <summary>Cachés de paquetes: sus nombres («token.py», «models», «session.py») son código de librerías, no datos personales.</summary>
    private static readonly CleaningPolicy PackageCache = new(TimeSpan.Zero, null, NameGuards: false);

    /// <summary>Modelos de IA: las extensiones de pesos que, en general, se protegen, aquí sí se pueden borrar.</summary>
    private static readonly CleaningPolicy Models = new(TimeSpan.Zero, null, NameGuards: false)
    {
        ExemptExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".gguf", ".safetensors", ".onnx", ".bin", ".pt", ".pth", ".h5", ".ckpt", ".msgpack", ".tflite" },
    };

    private static readonly CleaningPolicy Thumbnails = new(TimeSpan.Zero, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".db" })
    {
        ExemptExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".db" },
        NamePrefix = "thumbcache_",
    };

    private static CleaningPolicy Only(params string[] extensions) =>
        new(TimeSpan.Zero, new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase));

    private static CleaningPolicy OnlyOlder(TimeSpan age, params string[] extensions) =>
        new(age, new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase));

    private sealed record CategoryDef(string Id, string Name, string Description, string[] Leaves);

    private sealed record DataDef(string Id, string Name, string Description, BrowserDataKind Kind);

    /// <summary>Datos personales de Chromium, cada uno con su operación propia sobre las bases del perfil.</summary>
    private static readonly DataDef[] ChromiumData =
    [
        new("history", "Historial de navegación", "Páginas visitadas, búsquedas y sitios frecuentes. No toca descargas, marcadores ni contraseñas.", BrowserDataKind.History),
        new("downloads", "Historial de descargas", "La lista de descargas del navegador. Los archivos descargados no se tocan.", BrowserDataKind.Downloads),
        new("cookies", "Cookies", "Cierra la sesión en los sitios web. Las contraseñas guardadas no se tocan.", BrowserDataKind.Cookies),
        new("sessions", "Sesiones y pestañas", "Pestañas abiertas y sesión que el navegador restaura al iniciar.", BrowserDataKind.Sessions),
        new("forms", "Datos de formularios", "Texto escrito en formularios. Conserva direcciones, tarjetas y contraseñas.", BrowserDataKind.FormData),
    ];

    /// <summary>Hojas regenerables habituales de una aplicación Electron (Claude, Cursor, VS Code…).</summary>
    private static readonly string[] ElectronLeaves =
        ["Cache", "Code Cache", "GPUCache", "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache", "Crashpad", "logs", "Shared Dictionary"];

    /// <summary>Además, las bifurcaciones de VS Code guardan código compilado y extensiones descargadas que se regeneran.</summary>
    private static readonly string[] CodeForkLeaves = [.. ElectronLeaves, "CachedData", "CachedExtensionVSIXs", "CachedProfilesData"];

    private sealed record ToolCategory(string Id, string Name, string Description, string[] Roots, CleaningPolicy Policy,
        CategoryRisk Risk = CategoryRisk.Normal);

    private static readonly CategoryDef[] ChromiumCategories =
    [
        new("cache", "Caché de Internet", "Copias locales de páginas, imágenes y multimedia.", ["Cache", "Media Cache"]),
        new("code-cache", "Caché de código", "JavaScript y WebAssembly ya compilados.", ["Code Cache"]),
        new("gpu-cache", "Caché de GPU y sombreadores", "Sombreadores compilados por el navegador.",
            ["GPUCache", "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache", "GrShaderCache", "GraphiteDawnCache", "ShaderCache"]),
        new("sw-cache", "Caché de Service Workers", "Recursos que los sitios guardan para cargar más rápido; puede incluir contenido sin conexión de aplicaciones web.",
            [@"Service Worker\CacheStorage", @"Service Worker\ScriptCache"]),
    ];

    /// <summary>Datos personales de Firefox (en el perfil «Roaming»): historial y cookies.</summary>
    private static readonly DataDef[] FirefoxData =
    [
        new("history", "Historial de navegación", "Páginas visitadas y lo escrito en la barra. Los marcadores y las contraseñas no se tocan.", BrowserDataKind.FirefoxHistory),
        new("cookies", "Cookies", "Cierra la sesión en los sitios web. Las contraseñas guardadas no se tocan.", BrowserDataKind.FirefoxCookies),
    ];

    private static readonly CategoryDef[] FirefoxCategories =
    [
        new("cache", "Caché de Internet", "Copias locales de páginas, imágenes y multimedia.", ["cache2"]),
        new("startup-cache", "Caché de inicio", "Datos precompilados para arrancar más rápido.", ["startupCache"]),
    ];

    private sealed record ChromiumDef(string Id, string Name, string[] RelativeUserData, string Process);

    private static readonly ChromiumDef[] ChromiumBrowsers =
    [
        new("chrome", "Google Chrome", ["Google", "Chrome", "User Data"], "chrome"),
        new("brave", "Brave", ["BraveSoftware", "Brave-Browser", "User Data"], "brave"),
        new("edge", "Microsoft Edge", ["Microsoft", "Edge", "User Data"], "msedge"),
        new("chromium", "Chromium", ["Chromium", "User Data"], "chrome"),
        new("vivaldi", "Vivaldi", ["Vivaldi", "User Data"], "vivaldi"),
    ];

    public static CleanupCatalog Discover(IFolderProvider folders)
    {
        ArgumentNullException.ThrowIfNull(folders);
        var started = Stopwatch.GetTimestamp();
        var probe = new DirectoryProbe();
        var notes = new List<string>();
        var apps = new List<CleanupApp>();

        foreach (var browser in ChromiumBrowsers)
        {
            var userData = Path.Combine([folders.LocalAppData, .. browser.RelativeUserData]);
            var profiles = ChromiumProfiles(userData, probe, browser.Name, notes);
            AddBrowser(apps, browser.Id, browser.Name, browser.Process, ChromiumCategories, userData, profiles, folders, probe, notes, ChromiumData);
        }

        var firefoxLocal = Path.Combine(folders.LocalAppData, "Mozilla", "Firefox", "Profiles");
        var firefoxRoaming = Path.Combine(folders.RoamingAppData, "Mozilla", "Firefox", "Profiles");
        var firefoxProfiles = new List<(string Dir, string Name)>();
        foreach (var name in probe.ChildDirectories(firefoxLocal, "Firefox", notes))
            if (probe.IsPlainFile(Path.Combine(firefoxRoaming, name, "prefs.js"))) firefoxProfiles.Add((name, name));
        AddBrowser(apps, "firefox", "Mozilla Firefox", "firefox", FirefoxCategories, firefoxLocal, firefoxProfiles, folders, probe, notes, FirefoxData, firefoxRoaming);

        var local = folders.LocalAppData;
        var roaming = folders.RoamingAppData;
        var home = folders.UserProfile;
        var win = folders.WindowsDirectory;
        var wer = Path.Combine(folders.ProgramData, "Microsoft", "Windows", "WER");
        var userWer = Path.Combine(local, "Microsoft", "Windows", "WER");
        AddToolApp(apps, "windows", "Windows", AppGroup.Sistema, [], folders, probe, notes,
            new ToolCategory("user-temp", "Temporales del usuario", "Archivos temporales con más de 24 horas.", [folders.Temp], UserTemp),
            new ToolCategory("d3d-cache", "Caché de sombreadores Direct3D", "Sombreadores compilados por Windows; se regeneran.", [Path.Combine(local, "D3DSCache")], NoAge),
            new ToolCategory("thumbnails", "Caché de miniaturas", "Vistas previas de imágenes del Explorador; se regeneran al abrir cada carpeta.", [Path.Combine(local, "Microsoft", "Windows", "Explorer")], Thumbnails),
            new ToolCategory("error-reports", "Informes de errores", "Informes de fallos ya enviados o pendientes de enviar a Microsoft.",
                [Path.Combine(userWer, "ReportArchive"), Path.Combine(userWer, "ReportQueue"), Path.Combine(userWer, "Temp")], NoAge),
            new ToolCategory("crash-dumps", "Volcados de aplicaciones", "Copias de memoria que dejan los programas al fallar.", [Path.Combine(local, "CrashDumps")], Only(".dmp")),
            new ToolCategory("cert-cache", "Caché de certificados web", "Listas de revocación y certificados descargados; Windows los vuelve a descargar.", [Path.Combine(folders.LocalLowAppData, "Microsoft", "CryptnetUrlCache")], NoAge),
            new ToolCategory("notification-images", "Imágenes de notificaciones", "Iconos e imágenes que muestran las notificaciones.", [Path.Combine(local, "Microsoft", "Windows", "Notifications", "wpnidm")], Only(".jpg", ".png")),
            new ToolCategory("system-temp", "Temporales del sistema", "Archivos temporales de Windows con más de 24 horas.", [Path.Combine(win, "Temp")], UserTemp, CategoryRisk.Administrador),
            new ToolCategory("system-error-reports", "Informes de errores del sistema", "Informes de fallos de servicios y programas de todos los usuarios.",
                [Path.Combine(wer, "ReportArchive"), Path.Combine(wer, "ReportQueue"), Path.Combine(wer, "Temp")], NoAge, CategoryRisk.Administrador),
            new ToolCategory("memory-dumps", "Volcados de memoria", "Copias de memoria de pantallazos azules. Consérvalos si estás investigando un fallo.",
                [Path.Combine(win, "Minidump"), Path.Combine(win, "LiveKernelReports")], Only(".dmp"), CategoryRisk.Administrador),
            new ToolCategory("windows-logs", "Registros de Windows", "Archivos de registro (.log, .etl) de más de 24 horas.", [Path.Combine(win, "Logs")], OnlyOlder(TimeSpan.FromHours(24), ".log", ".etl"), CategoryRisk.Administrador),
            new ToolCategory("delivery-optimization", "Optimización de distribución", "Copias de actualizaciones compartidas con otros equipos; se descargan de nuevo si hacen falta.",
                [Path.Combine(win, "ServiceProfiles", "NetworkService", "AppData", "Local", "Microsoft", "Windows", "DeliveryOptimization", "Cache")], NoAge, CategoryRisk.Administrador));

        // Herramientas de IA y de desarrollo: solo lo regenerable. Historial, sesiones iniciadas, ajustes y plugins nunca;
        // los modelos y datasets, en categorías aparte porque se tarda en volver a descargarlos.
        static string[] Leaves(string folder, string[] leaves) => [.. leaves.Select(l => Path.Combine(folder, l))];
        var claudeMsix = Path.Combine(local, "Packages", "Claude_pzs8sxrjxfjjc", "LocalCache", "Roaming", "Claude");
        var codexMsix = Path.Combine(local, "Packages", "OpenAI.Codex_2p2nqsd0c76g0", "LocalCache", "Roaming", "Codex", "web", "Codex", "Default");
        AddToolApp(apps, "claude", "Claude (escritorio)", AppGroup.InteligenciaArtificial, ["claude"], folders, probe, notes,
            new ToolCategory("cache", "Caché de la aplicación", "Caché web, de código y de GPU, registros e informes de fallo. Conversaciones y sesión no se tocan.",
                [.. Leaves(Path.Combine(roaming, "Claude"), ElectronLeaves), .. Leaves(claudeMsix, ElectronLeaves)], NoAge));
        AddToolApp(apps, "claude-code", "Claude Code", AppGroup.InteligenciaArtificial, [], folders, probe, notes,
            new ToolCategory("cache", "Caché", "Caché descargable de Claude Code. Proyectos, historial, ajustes y plugins no se tocan.",
                [Path.Combine(home, ".claude", "cache"), Path.Combine(local, "claude-cli-nodejs")], NoAge));
        AddToolApp(apps, "cursor", "Cursor", AppGroup.InteligenciaArtificial, ["Cursor"], folders, probe, notes,
            new ToolCategory("cache", "Caché de la aplicación", "Caché web y de código, extensiones descargadas y registros. Proyectos y ajustes no se tocan.",
                [.. Leaves(Path.Combine(roaming, "Cursor"), CodeForkLeaves)], NoAge));
        AddToolApp(apps, "vscode", "Visual Studio Code", AppGroup.InteligenciaArtificial, ["Code"], folders, probe, notes,
            new ToolCategory("cache", "Caché de la aplicación", "Caché web y de código, extensiones descargadas y registros. Proyectos, ajustes y extensiones instaladas no se tocan.",
                [.. Leaves(Path.Combine(roaming, "Code"), CodeForkLeaves)], NoAge));
        AddToolApp(apps, "antigravity", "Antigravity", AppGroup.InteligenciaArtificial, ["Antigravity"], folders, probe, notes,
            new ToolCategory("cache", "Caché de la aplicación", "Caché web y de código, registros e informes de fallo.",
                [.. Leaves(Path.Combine(roaming, "Antigravity"), CodeForkLeaves), .. Leaves(Path.Combine(roaming, "Antigravity IDE"), CodeForkLeaves)], NoAge));
        AddToolApp(apps, "codex", "ChatGPT Codex", AppGroup.InteligenciaArtificial, ["Codex"], folders, probe, notes,
            new ToolCategory("cache", "Caché", "Caché y temporales de Codex. Historial, sesión y ajustes no se tocan.",
                [Path.Combine(home, ".codex", "cache"), Path.Combine(home, ".codex", "tmp"), Path.Combine(home, ".codex", ".tmp"), .. Leaves(codexMsix, ElectronLeaves)], NoAge));
        AddToolApp(apps, "copilot", "GitHub Copilot CLI", AppGroup.InteligenciaArtificial, [], folders, probe, notes,
            new ToolCategory("logs", "Registros", "Registros de la herramienta de línea de comandos.", [Path.Combine(home, ".copilot", "logs")], NoAge));
        AddToolApp(apps, "lmstudio", "LM Studio", AppGroup.InteligenciaArtificial, ["LM Studio"], folders, probe, notes,
            new ToolCategory("cache", "Caché de la aplicación", "Caché web, registros del servidor e informes de fallo.",
                [.. Leaves(Path.Combine(roaming, "LM Studio"), ElectronLeaves), Path.Combine(home, ".lmstudio", "server-logs")], NoAge),
            new ToolCategory("models", "Modelos descargados", "Todos los modelos descargados. Volver a descargarlos puede llevar horas.",
                [Path.Combine(home, ".lmstudio", "models"), Path.Combine(home, ".lmstudio", "hub", "models")], Models, CategoryRisk.MuyGrande));
        AddToolApp(apps, "ollama", "Ollama", AppGroup.InteligenciaArtificial, ["ollama", "ollama app"], folders, probe, notes,
            new ToolCategory("logs", "Registros", "Archivos de registro de Ollama.", [Path.Combine(local, "Ollama")], Only(".log")),
            new ToolCategory("models", "Modelos descargados", "Todos los modelos descargados. Volver a descargarlos puede llevar horas.",
                [Path.Combine(home, ".ollama", "models")], Models, CategoryRisk.MuyGrande));
        var hf = Path.Combine(home, ".cache", "huggingface");
        AddToolApp(apps, "huggingface", "Hugging Face", AppGroup.InteligenciaArtificial, [], folders, probe, notes,
            new ToolCategory("cache", "Caché de descargas", "Bloqueos, fragmentos de descarga y recursos temporales.",
                [Path.Combine(hf, "xet"), Path.Combine(hf, "hub", ".locks"), Path.Combine(hf, "assets")], NoAge),
            new ToolCategory("models", "Modelos descargados", "Modelos de la caché de Hugging Face. Volver a descargarlos puede llevar horas.",
                [Path.Combine(hf, "hub", "models--*")], Models, CategoryRisk.MuyGrande),
            new ToolCategory("datasets", "Datasets descargados", "Conjuntos de datos de la caché de Hugging Face.",
                [Path.Combine(hf, "hub", "datasets--*")], Models, CategoryRisk.MuyGrande));
        AddSimpleApp(apps, "nvidia", "Gráficos NVIDIA", AppGroup.Graficos, folders, probe, notes,
            ("dx-cache", "Caché DirectX", "Sombreadores compilados por el controlador.", Path.Combine(folders.LocalAppData, "NVIDIA", "DXCache"), NoAge),
            ("gl-cache", "Caché OpenGL", "Sombreadores compilados por el controlador.", Path.Combine(folders.LocalAppData, "NVIDIA", "GLCache"), NoAge));
        AddSimpleApp(apps, "dev", "Herramientas de desarrollo", AppGroup.Desarrollo, folders, probe, notes,
            ("pip-cache", "Caché de pip", "Paquetes de Python descargados; se vuelven a descargar si hacen falta.", Path.Combine(folders.LocalAppData, "pip", "Cache"), PackageCache),
            ("npm-cache", "Caché de npm", "Paquetes de Node descargados; se vuelven a descargar si hacen falta.", Path.Combine(folders.LocalAppData, "npm-cache"), PackageCache),
            ("uv-cache", "Caché de uv", "Paquetes de Python descargados por uv. Lo compartido con entornos virtuales no se cuenta ni se borra.", Path.Combine(folders.LocalAppData, "uv", "cache"), SharedByLinks));

        return new(Version, apps, Stopwatch.GetElapsedTime(started).TotalMilliseconds, notes);
    }

    private static List<(string Dir, string Name)> ChromiumProfiles(string userData, DirectoryProbe probe, string browser, List<string> notes)
    {
        var result = new List<(string, string)>();
        var names = (Dictionary<string, string>?)null;
        foreach (var dir in probe.ChildDirectories(userData, browser, notes))
        {
            if (!probe.IsPlainFile(Path.Combine(userData, dir, "Preferences"))) continue;
            names ??= ProfileNames(userData);
            var friendly = names.TryGetValue(dir, out var name) && !string.IsNullOrWhiteSpace(name) ? name + " (" + dir + ")" : dir;
            result.Add((dir, friendly));
        }
        return result;
    }

    /// <summary>Nombres visibles de perfiles desde «Local State». Lectura acotada; cualquier fallo se ignora.</summary>
    private static Dictionary<string, string> ProfileNames(string userData)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var file = new FileInfo(Path.Combine(userData, "Local State"));
            if (!file.Exists || file.Length > 8 * 1024 * 1024) return result;
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.TryGetProperty("profile", out var profile) &&
                profile.TryGetProperty("info_cache", out var cache) && cache.ValueKind == JsonValueKind.Object)
                foreach (var entry in cache.EnumerateObject())
                    if (entry.Value.ValueKind == JsonValueKind.Object && entry.Value.TryGetProperty("name", out var name) &&
                        name.ValueKind == JsonValueKind.String)
                        result[entry.Name] = name.GetString() ?? string.Empty;
        }
        catch (Exception ex) when (PathSafety.IsIoFailure(ex) || ex is JsonException) { }
        return result;
    }

    private static void AddBrowser(List<CleanupApp> apps, string id, string name, string process, CategoryDef[] definitions,
        string dataRoot, List<(string Dir, string Name)> profiles, IFolderProvider folders, DirectoryProbe probe, List<string> notes,
        DataDef[]? data = null, string? personalDataRoot = null)
    {
        // Firefox guarda la caché en «Local» y los datos personales en «Roaming»; Chromium, ambos en la misma carpeta.
        var dataFilesRoot = personalDataRoot ?? dataRoot;
        if (profiles.Count == 0) return;
        var categories = new List<CleanupCategory>();
        foreach (var definition in definitions)
        {
            var targets = new List<CleanupTarget>();
            foreach (var (dir, friendly) in profiles)
            {
                var roots = new List<CleanupRoot>();
                foreach (var leaf in definition.Leaves)
                {
                    var path = Path.Combine(dataRoot, dir, leaf);
                    if (probe.IsSafeDirectory(path, name + " / " + dir + " / " + leaf, notes)) roots.Add(new(path, leaf, NoAge));
                }
                if (roots.Count == 0) continue;
                var target = new CleanupTarget(new(id, definition.Id, dir), name, definition.Name, friendly, roots, dataRoot, dataRoot);
                if (CleaningGuard.ValidateTarget(target, folders) is { } refusal) notes.Add(target.Key + ": " + refusal);
                else targets.Add(target);
            }
            if (targets.Count > 0) categories.Add(new(definition.Id, definition.Name, definition.Description, targets));
        }
        foreach (var definition in data ?? [])
        {
            var targets = new List<CleanupTarget>();
            foreach (var (dir, friendly) in profiles)
            {
                var roots = new List<CleanupRoot>();
                foreach (var leaf in BrowserData.Leaves[definition.Kind])
                {
                    var path = Path.Combine(dataFilesRoot, dir, leaf);
                    if (probe.IsPlainFile(path) || probe.IsSafeDirectory(path, name + " / " + dir + " / " + leaf, notes)) roots.Add(new(path, leaf, NoAge));
                }
                if (roots.Count == 0) continue;
                var target = new CleanupTarget(new(id, definition.Id, dir), name, definition.Name, friendly, roots, dataFilesRoot, dataFilesRoot)
                { Data = definition.Kind, ProcessNames = [process] };
                if (CleaningGuard.ValidateTarget(target, folders) is { } refusal) notes.Add(target.Key + ": " + refusal);
                else targets.Add(target);
            }
            if (targets.Count > 0) categories.Add(new(definition.Id, definition.Name, definition.Description, targets) { Risk = CategoryRisk.DatosPersonales });
        }
        if (categories.Count > 0) apps.Add(new(id, name, AppGroup.Navegadores, [process], categories));
    }

    /// <summary>
    /// Aplicación con categorías de una o varias carpetas absolutas. Solo se incluyen las carpetas que existen;
    /// un último segmento con «*» (p. ej. «models--*») se expande a las subcarpetas que empiezan así.
    /// </summary>
    private static void AddToolApp(List<CleanupApp> apps, string id, string name, AppGroup group, string[] processes,
        IFolderProvider folders, DirectoryProbe probe, List<string> notes, params ToolCategory[] definitions)
    {
        var categories = new List<CleanupCategory>();
        foreach (var definition in definitions)
        {
            var paths = new List<string>();
            foreach (var root in definition.Roots)
            {
                var leaf = Path.GetFileName(root);
                if (leaf.EndsWith('*'))
                {
                    var parent = Path.GetDirectoryName(root)!;
                    foreach (var child in probe.ChildDirectories(parent, name + " / " + definition.Name, notes))
                        if (child.StartsWith(leaf[..^1], StringComparison.OrdinalIgnoreCase)) paths.Add(Path.Combine(parent, child));
                }
                else if (probe.IsSafeDirectory(root, name + " / " + definition.Name, notes)) paths.Add(root);
            }
            if (paths.Count == 0) continue;
            var anchor = CommonAncestor(paths.Select(p => Path.GetDirectoryName(p)!));
            var target = new CleanupTarget(new(id, definition.Id, "all"), name, definition.Name, string.Empty,
                [.. paths.Select(p => new CleanupRoot(p, Path.GetFileName(p), definition.Policy))], null, anchor)
            { RequiresAdmin = definition.Risk == CategoryRisk.Administrador, ProcessNames = processes };
            if (CleaningGuard.ValidateTarget(target, folders) is { } refusal) { notes.Add(target.Key + ": " + refusal); continue; }
            var description = definition.Risk == CategoryRisk.Administrador && !folders.IsElevated
                ? definition.Description + " Requiere ejecutar FFCleaner como administrador."
                : definition.Description;
            categories.Add(new(definition.Id, definition.Name, description, [target]) { Risk = definition.Risk });
        }
        if (categories.Count > 0) apps.Add(new(id, name, group, processes, categories));
    }

    /// <summary>Carpeta común más profunda de varias rutas.</summary>
    private static string CommonAncestor(IEnumerable<string> paths)
    {
        var parts = paths.Select(p => p.Split('\\')).ToList();
        var length = 0;
        while (parts.All(p => p.Length > length) && parts.All(p => string.Equals(p[length], parts[0][length], StringComparison.OrdinalIgnoreCase)))
            length++;
        return string.Join('\\', parts[0].Take(length));
    }

    private static void AddSimpleApp(List<CleanupApp> apps, string id, string name, AppGroup group, IFolderProvider folders,
        DirectoryProbe probe, List<string> notes,
        params (string Id, string Name, string Description, string Path, CleaningPolicy Policy)[] definitions)
    {
        var categories = new List<CleanupCategory>();
        foreach (var definition in definitions)
        {
            if (!probe.IsSafeDirectory(definition.Path, name + " / " + definition.Name, notes)) continue;
            var anchor = Path.GetDirectoryName(definition.Path)!;
            var target = new CleanupTarget(new(id, definition.Id, "all"), name, definition.Name, string.Empty,
                [new(definition.Path, Path.GetFileName(definition.Path), definition.Policy)], null, anchor);
            if (CleaningGuard.ValidateTarget(target, folders) is { } refusal) { notes.Add(target.Key + ": " + refusal); continue; }
            categories.Add(new(definition.Id, definition.Name, definition.Description, [target]));
        }
        if (categories.Count > 0) apps.Add(new(id, name, group, [], categories));
    }

    /// <summary>
    /// Comprobaciones de directorios con memoria: cada ancestro se consulta una sola vez por descubrimiento,
    /// en lugar de recorrer toda la cadena por cada hoja.
    /// </summary>
    private sealed class DirectoryProbe
    {
        private readonly Dictionary<string, bool> _safe = new(StringComparer.OrdinalIgnoreCase);

        public bool IsSafeDirectory(string path, string label, List<string> notes)
        {
            try
            {
                if (!ChainIsSafe(Path.GetDirectoryName(path))) return false;
                if (!TryAttributes(path, out var attributes)) return false;
                if (PathSafety.UnsafeAttributes(attributes))
                {
                    notes.Add(label + ": enlace, nube o dispositivo; no se ofrece.");
                    return false;
                }
                return (attributes & FileAttributes.Directory) != 0;
            }
            catch (Exception ex) when (PathSafety.IsIoFailure(ex) || ex is ArgumentException or NotSupportedException)
            {
                notes.Add(label + ": no accesible (" + ex.GetType().Name + ").");
                return false;
            }
        }

        public bool IsPlainFile(string path)
        {
            try
            {
                if (!ChainIsSafe(Path.GetDirectoryName(path))) return false;
                return TryAttributes(path, out var attributes) && !PathSafety.UnsafeAttributes(attributes) &&
                    (attributes & FileAttributes.Directory) == 0;
            }
            catch (Exception ex) when (PathSafety.IsIoFailure(ex) || ex is ArgumentException or NotSupportedException) { return false; }
        }

        public IReadOnlyList<string> ChildDirectories(string path, string label, List<string> notes)
        {
            if (!IsSafeDirectory(path, label, notes)) return [];
            try
            {
                var result = new List<string>();
                foreach (var entry in new DirectoryInfo(path).EnumerateDirectories())
                    if (!PathSafety.UnsafeAttributes(entry.Attributes)) result.Add(entry.Name);
                _safe[path] = true;
                foreach (var name in result) _safe[Path.Combine(path, name)] = true;
                return result;
            }
            catch (Exception ex) when (PathSafety.IsIoFailure(ex))
            {
                notes.Add(label + ": no se pudo listar (" + ex.GetType().Name + ").");
                return [];
            }
        }

        private bool ChainIsSafe(string? directory)
        {
            if (directory is null) return true;
            if (_safe.TryGetValue(directory, out var known)) return known;
            var safe = ChainIsSafe(Path.GetDirectoryName(directory));
            if (safe)
                safe = TryAttributes(directory, out var attributes) && !PathSafety.UnsafeAttributes(attributes) &&
                    (attributes & FileAttributes.Directory) != 0;
            _safe[directory] = safe;
            return safe;
        }

        /// <summary>Atributos sin excepción cuando la ruta no existe (caso habitual al sondear hojas).</summary>
        private static bool TryAttributes(string path, out FileAttributes attributes)
        {
            attributes = new FileInfo(path).Attributes;
            return (int)attributes != -1;
        }
    }
}
