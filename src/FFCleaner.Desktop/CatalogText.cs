using FFCleaner.Core;
using FFCleaner.RegistryAnalysis;

namespace FFCleaner.Desktop;

/// <summary>
/// Textos del catálogo y de los motores en el idioma de la interfaz. Los motores trabajan en español; aquí se
/// traducen nombres y descripciones visibles. Lo que no tiene traducción se muestra tal cual.
/// </summary>
public static class CatalogText
{
    private static readonly Dictionary<string, string> AppNames = new()
    {
        ["windows"] = "Windows", ["claude"] = "Claude (desktop)", ["dev"] = "Developer tools", ["nvidia"] = "NVIDIA graphics",
    };

    private static readonly Dictionary<string, (string Name, string Description)> Categories = new()
    {
        ["history"] = ("Browsing history", "Visited pages, searches and top sites. Downloads, bookmarks and passwords are not touched."),
        ["downloads"] = ("Download history", "The browser's download list. Downloaded files are not touched."),
        ["cookies"] = ("Cookies", "Signs you out of websites. Saved passwords are not touched."),
        ["sessions"] = ("Sessions and tabs", "Open tabs and the session the browser restores at startup."),
        ["forms"] = ("Form data", "Text typed into forms. Addresses, cards and passwords are kept."),
        ["cache"] = ("Internet cache", "Local copies of pages, images and media."),
        ["code-cache"] = ("Code cache", "Already compiled JavaScript and WebAssembly."),
        ["gpu-cache"] = ("GPU and shader cache", "Shaders compiled by the browser."),
        ["sw-cache"] = ("Service Worker cache", "Resources sites store to load faster; may include offline content of web apps."),
        ["startup-cache"] = ("Startup cache", "Precompiled data for a faster start."),
        ["user-temp"] = ("User temporary files", "Temporary files older than 24 hours."),
        ["d3d-cache"] = ("Direct3D shader cache", "Shaders compiled by Windows; they are rebuilt."),
        ["thumbnails"] = ("Thumbnail cache", "Image previews in File Explorer; rebuilt when each folder is opened."),
        ["error-reports"] = ("Error reports", "Crash reports already sent or waiting to be sent to Microsoft."),
        ["crash-dumps"] = ("Application dumps", "Memory copies left by programs when they crash."),
        ["cert-cache"] = ("Web certificate cache", "Downloaded revocation lists and certificates; Windows downloads them again."),
        ["notification-images"] = ("Notification images", "Icons and images shown by notifications."),
        ["system-temp"] = ("System temporary files", "Windows temporary files older than 24 hours."),
        ["system-error-reports"] = ("System error reports", "Crash reports of services and programs for all users."),
        ["memory-dumps"] = ("Memory dumps", "Memory copies from blue screens. Keep them if you are investigating a crash."),
        ["windows-logs"] = ("Windows logs", "Log files (.log, .etl) older than 24 hours."),
        ["delivery-optimization"] = ("Delivery Optimization", "Update copies shared with other PCs; downloaded again if needed."),
        ["logs"] = ("Logs", "Log files of the tool."),
        ["models"] = ("Downloaded models", "All downloaded models. Downloading them again can take hours."),
        ["datasets"] = ("Downloaded datasets", "Datasets in the Hugging Face cache."),
        ["dx-cache"] = ("DirectX cache", "Shaders compiled by the driver."),
        ["gl-cache"] = ("OpenGL cache", "Shaders compiled by the driver."),
        ["pip-cache"] = ("pip cache", "Downloaded Python packages; downloaded again when needed."),
        ["npm-cache"] = ("npm cache", "Downloaded Node packages; downloaded again when needed."),
        ["uv-cache"] = ("uv cache", "Python packages downloaded by uv. What is shared with virtual environments is neither counted nor deleted."),
    };

    /// <summary>Descripciones propias de cada herramienta de IA (la misma categoría «cache» cambia de alcance).</summary>
    private static readonly Dictionary<string, string> ToolCacheDescriptions = new()
    {
        ["claude"] = "Web, code and GPU cache, logs and crash reports. Conversations and session are not touched.",
        ["claude-code"] = "Claude Code download cache. Projects, history, settings and plugins are not touched.",
        ["cursor"] = "Web and code cache, downloaded extensions and logs. Projects and settings are not touched.",
        ["vscode"] = "Web and code cache, downloaded extensions and logs. Projects, settings and installed extensions are not touched.",
        ["antigravity"] = "Web and code cache, logs and crash reports.",
        ["codex"] = "Codex cache and temporary files. History, session and settings are not touched.",
        ["lmstudio"] = "Web cache, server logs and crash reports.",
        ["huggingface"] = "Locks, download fragments and temporary resources.",
    };

    public static string App(CleanupApp app) => Loc.English && AppNames.TryGetValue(app.Id, out var name) ? name : app.Name;

    public static string Category(CleanupApp app, CleanupCategory category)
    {
        if (!Loc.English) return category.Name;
        if (category.Id == "cache" && ToolCacheDescriptions.ContainsKey(app.Id))
            return app.Id == "huggingface" ? "Download cache" : app.Id is "claude-code" or "codex" ? "Cache" : "App cache";
        return Categories.TryGetValue(category.Id, out var text) ? text.Name : category.Name;
    }

    public static string Description(CleanupApp app, CleanupCategory category)
    {
        if (!Loc.English) return category.Description;
        if (app.Id == "firefox" && category.Id == "history")
            return "Visited pages and what you typed in the address bar. Bookmarks and passwords are not touched.";
        var text =category.Id == "cache" && ToolCacheDescriptions.TryGetValue(app.Id, out var tool) ? tool
            : Categories.TryGetValue(category.Id, out var known) ? known.Description : category.Description;
        // Las categorías de administrador añaden un aviso al describirse sin permisos.
        return category.Description.EndsWith("Requiere ejecutar FFCleaner como administrador.", StringComparison.Ordinal)
            ? text + " Requires running FFCleaner as administrator." : text;
    }

    public static string Group(AppGroup group) => group switch
    {
        AppGroup.Navegadores => Loc.T("Navegadores", "Browsers"),
        AppGroup.Sistema => "Windows",
        AppGroup.InteligenciaArtificial => Loc.T("IA y desarrollo", "AI and development"),
        AppGroup.Graficos => Loc.T("Gráficos", "Graphics"),
        _ => Loc.T("Cachés de paquetes", "Package caches"),
    };

    public static string Badge(CategoryRisk risk) => risk switch
    {
        CategoryRisk.DatosPersonales => Loc.T("Datos personales", "Personal data"),
        CategoryRisk.MuyGrande => Loc.T("Muy grande", "Very large"),
        CategoryRisk.Administrador => Loc.T("Administrador", "Administrator"),
        _ => "",
    };

    public static string Verdict(FileVerdict verdict) => !Loc.English ? CleaningGuard.Describe(verdict) : verdict switch
    {
        FileVerdict.Eligible => "Can be cleaned",
        FileVerdict.SensitiveName => "Protected: personal data name (cookies, sessions, credentials…)",
        FileVerdict.SensitiveDirectory => "Protected: personal data folder",
        FileVerdict.ProtectedExtension => "Protected: database, key or virtual disk file type",
        FileVerdict.DatabaseSidecar => "Protected: database helper file",
        FileVerdict.ExtensionNotAllowed => "Skipped: file type not recognized as temporary",
        FileVerdict.TooRecent => "Skipped: too recent for this category",
        FileVerdict.Excluded => "Protected: exclusion saved in settings",
        FileVerdict.MultipleLinks => "Shared through hard links with another folder: deleting it would not free space",
        FileVerdict.ProtectedAttributes => "Skipped: protected attributes (read-only, system or encrypted)",
        _ => verdict.ToString(),
    };

    public static string FamilyName(RegistryFamilyInfo info) => !Loc.English ? info.Name : info.Id switch
    {
        "apppaths" => "Application paths",
        "uninstall" => "Missing uninstallers",
        "fileassoc" => "File associations",
        "startup" => "Startup programs",
        "muicache" => "Program name caches",
        "shareddlls" => "Shared DLLs",
        _ => info.Name,
    };

    public static string FamilyDescription(RegistryFamilyInfo info) => !Loc.English ? info.Description : info.Id switch
    {
        "apppaths" => "«App Paths» entries whose executable no longer exists.",
        "uninstall" => "Programs listed for uninstall whose uninstaller and folder are gone.",
        "fileassoc" => "Extensions pointing to unregistered types and programs that no longer exist.",
        "startup" => "Run and RunOnce entries pointing to missing programs.",
        "muicache" => "MUI Cache and compatibility assistant with paths to programs that no longer exist.",
        "shareddlls" => "Shared DLL counters for files that no longer exist.",
        _ => info.Description,
    };

    public static string Risk(RegistryRisk risk) => risk switch
    {
        RegistryRisk.Seguro => Loc.T("Seguro", "Safe"),
        RegistryRisk.Ambiguo => Loc.T("Ambiguo", "Ambiguous"),
        RegistryRisk.Critico => Loc.T("Crítico", "Critical"),
        _ => Loc.T("Sin acceso", "No access"),
    };

    public static string StartupSource(StartupEntry entry) => !Loc.English ? entry.SourceLabel : entry.Source switch
    {
        RegistryAnalysis.StartupSource.RegistroUsuario => "Registry (user)",
        RegistryAnalysis.StartupSource.RegistroEquipo => "Registry (all users)",
        RegistryAnalysis.StartupSource.RegistroEquipo32 => "Registry (all users, 32-bit)",
        RegistryAnalysis.StartupSource.CarpetaUsuario => "Startup folder (user)",
        _ => "Startup folder (all users)",
    };
}
