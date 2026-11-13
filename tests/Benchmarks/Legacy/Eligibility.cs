namespace FFCleaner.Benchmarks.Legacy;

internal static class Eligibility
{
    private static readonly HashSet<string> SensitiveComponents = new(StringComparer.OrdinalIgnoreCase)
    {
        "Cookies", "Login Data", "Login Data For Account", "Web Data", "History", "Bookmarks",
        "Preferences", "Secure Preferences", "Local State", "Sessions", "Session Storage",
        "Local Storage", "IndexedDB", "Network", "Credentials", "Vault", "Passwords",
        "logins.json", "key4.db", "cookies.sqlite", "places.sqlite", "sessionstore.jsonlz4",
        "sessionstore-backups", ".ssh", ".aws", ".azure", "models", "Virtual Machines", "Prefetch"
    };
    private static readonly HashSet<string> SensitiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".pem", ".key", ".pfx", ".p12", ".kdbx", ".gguf", ".safetensors", ".onnx", ".vhd", ".vhdx", ".vmdk", ".qcow2" };
    private static readonly HashSet<string> TemporaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".tmp", ".temp", ".log", ".cache", ".etl" };
    private static readonly HashSet<string> CacheRules = new(StringComparer.Ordinal)
    { "pip-cache", "npm-cache", "uv-cache", "nvidia-dxcache", "nvidia-glcache", "d3d-cache" };

    private static bool SensitiveName(string component)
    {
        if (SensitiveComponents.Contains(component)) return true;
        return new[] { "cookie", "login data", "credential", "password", "session", "token" }
            .Any(prefix => component.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    internal static (bool Eligible, string Reason) Evaluate(ScanRoot root, string path)
    {
        if (!root.CanSelect) return (false, "Solo inventario: raíz no seleccionable.");
        if (path.Split(['\\', '/']).Any(SensitiveName) || SensitiveExtensions.Contains(Path.GetExtension(path)))
            return (false, "Credenciales/sesiones/datos personales o activos fuera del alcance A–C.");
        if (TemporaryExtensions.Contains(Path.GetExtension(path)) || CacheRules.Contains(root.Id) || root.Id.StartsWith("browser-cache:", StringComparison.Ordinal))
            return (true, "Candidato para previsualización; no autoriza borrado.");
        return (false, "Tipo desconocido; solo inventario.");
    }
}
