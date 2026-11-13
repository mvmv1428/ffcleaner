namespace FFCleaner.Benchmarks.Legacy;
public static class RuleCatalog
{
    public static IReadOnlyList<ScanRoot> Discover()
    {
        var result = new List<ScanRoot>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var local = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetEnvironmentVariable("APPDATA") ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        void Add(string id, string name, string path, string risk = "bajo")
        {
            if (!SafeDirectory(path)) return;
            var normalized = PathSafety.Normalize(path);
            if (seen.Add(normalized)) result.Add(new(id, name, normalized, risk));
        }
        Add("user-temp", "Temporales del usuario (solo tipos reconocidos)", Environment.GetEnvironmentVariable("TEMP") ?? Path.GetTempPath(), "medio");
        Add("pip-cache", "Caché pip", Path.Combine(local, "pip", "Cache"));
        Add("npm-cache", "Caché npm", Path.Combine(local, "npm-cache"));
        Add("uv-cache", "Caché uv (no modelos)", Path.Combine(local, "uv", "cache"));
        Add("nvidia-dxcache", "Caché NVIDIA DirectX", Path.Combine(local, "NVIDIA", "DXCache"));
        Add("nvidia-glcache", "Caché NVIDIA OpenGL", Path.Combine(local, "NVIDIA", "GLCache"));
        Add("d3d-cache", "Caché Direct3D", Path.Combine(local, "D3DSCache"));
        foreach (var browser in new[] {
            (Id: "chrome", Path: Path.Combine(local, "Google", "Chrome", "User Data")),
            (Id: "edge", Path: Path.Combine(local, "Microsoft", "Edge", "User Data")),
            (Id: "brave", Path: Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data")),
            (Id: "chromium", Path: Path.Combine(local, "Chromium", "User Data")),
            (Id: "vivaldi", Path: Path.Combine(local, "Vivaldi", "User Data")) })
        {
            foreach (var profile in ChildDirectories(browser.Path))
            {
                if (!SafeMarker(Path.Combine(profile, "Preferences"))) continue;
                foreach (var leaf in new[] { "Cache", "Code Cache", "GPUCache", "DawnCache" })
                    Add("browser-cache:" + browser.Id + ":" + Path.GetFileName(profile) + ":" + leaf,
                        browser.Id + " / " + Path.GetFileName(profile) + " / " + leaf, Path.Combine(profile, leaf));
            }
        }
        foreach (var profile in ChildDirectories(Path.Combine(local, "Mozilla", "Firefox", "Profiles")))
        {
            var name = Path.GetFileName(profile);
            if (SafeMarker(Path.Combine(roaming, "Mozilla", "Firefox", "Profiles", name, "prefs.js")))
                Add("browser-cache:firefox:" + name + ":cache2", "Firefox / " + name + " / cache2", Path.Combine(profile, "cache2"));
        }
        return result.AsReadOnly();
    }

    private static bool SafeDirectory(string path)
    {
        try
        {
            var normalized = PathSafety.Normalize(path);
            PathSafety.EnsureSafe(normalized);
            return (File.GetAttributes(normalized) & FileAttributes.Directory) != 0;
        }
        catch (Exception ex) when (PathSafety.IsIoFailure(ex) || ex is ArgumentException)
        {
            if (ex is not FileNotFoundException and not DirectoryNotFoundException)
                System.Diagnostics.Trace.TraceWarning("RuleDiscovery: {0}: {1}", path, ex.Message);
            return false;
        }
    }

    private static bool SafeMarker(string path)
    {
        try { PathSafety.EnsureSafe(path); return (File.GetAttributes(path) & FileAttributes.Directory) == 0; }
        catch (Exception ex) when (PathSafety.IsIoFailure(ex)) { return false; }
    }

    private static IReadOnlyList<string> ChildDirectories(string path)
    {
        if (!SafeDirectory(path)) return Array.Empty<string>();
        try { return Directory.EnumerateDirectories(path).Where(SafeDirectory).ToArray(); }
        catch (Exception ex) when (PathSafety.IsIoFailure(ex))
        {
            System.Diagnostics.Trace.TraceWarning("RuleDiscovery: {0}: {1}", path, ex.Message);
            return Array.Empty<string>();
        }
    }
}
