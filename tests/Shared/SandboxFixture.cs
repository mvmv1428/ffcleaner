using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using FFCleaner.Core;

namespace FFCleaner.Tests;

/// <summary>
/// Perfil simulado bajo %TEMP%\ffcleaner-sandbox-*: imita la estructura de datos de Chrome, Brave y demás
/// aplicaciones con archivos ficticios. Las pruebas jamás tocan los perfiles reales del usuario.
/// </summary>
internal sealed class SandboxFixture : IDisposable
{
    private readonly Dictionary<string, string> _friendly = new(StringComparer.OrdinalIgnoreCase);
    private int _counter;

    private SandboxFixture(string root)
    {
        Root = root;
        Folders = new SandboxFolderProvider(root);
    }

    public string Root { get; }
    public SandboxFolderProvider Folders { get; }
    public string Outside => Path.Combine(Root, "Outside");

    public static SandboxFixture Create()
    {
        var fixture = new SandboxFixture(SandboxFolderProvider.CreateRoot());
        Directory.CreateDirectory(fixture.Outside);
        return fixture;
    }

    public string UserData(string browser) => browser switch
    {
        "chrome" => Path.Combine(Folders.LocalAppData, "Google", "Chrome", "User Data"),
        "brave" => Path.Combine(Folders.LocalAppData, "BraveSoftware", "Brave-Browser", "User Data"),
        "edge" => Path.Combine(Folders.LocalAppData, "Microsoft", "Edge", "User Data"),
        _ => throw new ArgumentException(browser),
    };

    public string Profile(string browser, string profile) => Path.Combine(UserData(browser), profile);

    /// <summary>Crea un archivo con contenido determinista. La fecha por defecto es de hace 3 días.</summary>
    public string Write(string path, int bytes = 64, DateTime? lastWriteUtc = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var content = new byte[bytes];
        new Random(Interlocked.Increment(ref _counter)).NextBytes(content);
        File.WriteAllBytes(path, content);
        File.SetLastWriteTimeUtc(path, lastWriteUtc ?? DateTime.UtcNow.AddDays(-3));
        return path;
    }

    /// <summary>
    /// Perfil de navegador Chromium con cachés y con datos personales (cookies, historial, contraseñas,
    /// sesiones, almacenamiento de sitios) que ninguna limpieza de caché debe tocar.
    /// </summary>
    public string AddChromiumProfile(string browser, string profile, int cacheFiles = 12, string? friendlyName = null)
    {
        var userData = UserData(browser);
        var dir = Path.Combine(userData, profile);
        Directory.CreateDirectory(userData);
        _friendly[browser + "|" + profile] = friendlyName ?? "Perfil " + profile;
        var names = _friendly.Where(p => p.Key.StartsWith(browser + "|", StringComparison.Ordinal))
            .Select(p => "\"" + p.Key[(browser.Length + 1)..] + "\":{\"name\":\"" + p.Value + "\"}");
        File.WriteAllText(Path.Combine(userData, "Local State"), "{\"profile\":{\"info_cache\":{" + string.Join(",", names) + "}}}");

        // Datos personales: deben quedar intactos siempre.
        foreach (var name in new[] { "Preferences", "Secure Preferences", "Cookies", "Cookies-journal", "History", "History-journal",
                     "Login Data", "Login Data For Account", "Web Data", "Bookmarks", "Top Sites", "Favicons" })
            Write(Path.Combine(dir, name), 256);
        Write(Path.Combine(dir, "Network", "Cookies"), 512);
        Write(Path.Combine(dir, "Network", "Cookies-journal"), 32);
        Write(Path.Combine(dir, "Sessions", "Session_13370000000000000"), 300);
        Write(Path.Combine(dir, "Sessions", "Tabs_13370000000000000"), 300);
        Write(Path.Combine(dir, "Session Storage", "000003.log"), 128);
        Write(Path.Combine(dir, "Local Storage", "leveldb", "000005.ldb"), 128);
        Write(Path.Combine(dir, "IndexedDB", "https_example.com_0.indexeddb.leveldb", "000003.log"), 128);
        Write(Path.Combine(dir, "Extensions", "abcdefghijklmnop", "1.0_0", "manifest.json"), 90);
        Write(Path.Combine(dir, "Service Worker", "Database", "000003.log"), 128);

        // Cachés regenerables.
        var cache = Path.Combine(dir, "Cache", "Cache_Data");
        Write(Path.Combine(cache, "index"), 512);
        for (var i = 0; i < 4; i++) Write(Path.Combine(cache, "data_" + i), 1024);
        for (var i = 0; i < cacheFiles; i++) Write(Path.Combine(cache, "f_" + i.ToString("x6")), 2048 + i);
        Write(Path.Combine(dir, "Code Cache", "js", "index-dir", "the-real-index"), 96);
        for (var i = 0; i < 5; i++) Write(Path.Combine(dir, "Code Cache", "js", Guid.NewGuid().ToString("N")[..16] + "_0"), 700);
        Write(Path.Combine(dir, "Code Cache", "wasm", "index"), 24);
        Write(Path.Combine(dir, "GPUCache", "index"), 256);
        Write(Path.Combine(dir, "GPUCache", "data_0"), 4096);
        Write(Path.Combine(dir, "DawnCache", "data_1"), 4096);
        Write(Path.Combine(dir, "Service Worker", "CacheStorage", "0a1b2c3d", "e4f5", "index"), 64);
        Write(Path.Combine(dir, "Service Worker", "CacheStorage", "0a1b2c3d", "e4f5", "9f8e7d_0"), 900);
        Write(Path.Combine(dir, "Service Worker", "ScriptCache", "2cc80dabc69f58b6_0"), 800);
        return dir;
    }

    public void AddFirefoxProfile(string name)
    {
        Write(Path.Combine(Folders.RoamingAppData, "Mozilla", "Firefox", "Profiles", name, "prefs.js"), 100);
        foreach (var personal in new[] { "cookies.sqlite", "places.sqlite", "logins.json", "key4.db" })
            Write(Path.Combine(Folders.RoamingAppData, "Mozilla", "Firefox", "Profiles", name, personal), 200);
        var local = Path.Combine(Folders.LocalAppData, "Mozilla", "Firefox", "Profiles", name);
        Write(Path.Combine(local, "cache2", "index"), 100);
        for (var i = 0; i < 6; i++) Write(Path.Combine(local, "cache2", "entries", Guid.NewGuid().ToString("N").ToUpperInvariant()), 1500);
        Write(Path.Combine(local, "startupCache", "startupCache.8.little"), 5000);
    }

    public void AddSystemCaches()
    {
        Write(Path.Combine(Folders.Temp, "viejo.tmp"), 300);
        Write(Path.Combine(Folders.Temp, "sub", "registro.log"), 300);
        Write(Path.Combine(Folders.Temp, "reciente.tmp"), 300, DateTime.UtcNow);
        Write(Path.Combine(Folders.Temp, "documento.txt"), 300);
        Write(Path.Combine(Folders.LocalAppData, "D3DSCache", "a1b2", "F4EB2D6C-ED2B-4BDD-AD9D-F913287E6768.val"), 2000);
        Write(Path.Combine(Folders.LocalAppData, "NVIDIA", "DXCache", "0a1b2c.bin"), 3000);
        Write(Path.Combine(Folders.LocalAppData, "NVIDIA", "GLCache", "0a1b2c", "d4e5f6.toc"), 3000);
        Write(Path.Combine(Folders.LocalAppData, "pip", "Cache", "http-v2", "a", "b", "abcdef0123"), 4000);
        Write(Path.Combine(Folders.LocalAppData, "npm-cache", "_cacache", "content-v2", "sha512", "aa", "bb", "ccdd"), 4000);
        Write(Path.Combine(Folders.LocalAppData, "uv", "cache", "wheels-v5", "pypi", "pkg", "pkg-1.0.whl"), 4000);
    }

    /// <summary>Huella de todos los archivos bajo <paramref name="directory"/>: ruta relativa → SHA-256.</summary>
    public static SortedDictionary<string, string> HashTree(string directory)
    {
        var result = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(directory)) return result;
        foreach (var file in Directory.EnumerateFiles(directory, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            result[Path.GetRelativePath(directory, file)] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
        return result;
    }

    public static int CountFiles(string directory) => Directory.Exists(directory)
        ? Directory.EnumerateFiles(directory, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }).Count()
        : 0;

    public static bool TryJunction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + link + "\" \"" + target + "\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
        process!.WaitForExit(10000);
        return process.ExitCode == 0 && Directory.Exists(link);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CreateHardLink(string newName, string existingName, IntPtr security);

    /// <summary>Quita las junctions (solo el enlace, nunca su destino) antes del borrado recursivo.</summary>
    private static void RemoveLinks(string directory)
    {
        foreach (var child in Directory.GetDirectories(directory))
        {
            if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) Directory.Delete(child, recursive: false);
            else RemoveLinks(child);
        }
    }

    public void Dispose()
    {
        // Solo se elimina la carpeta de pruebas creada por este fixture (prefijo y marcador verificados).
        if (!Path.GetFileName(Root).StartsWith(SandboxFolderProvider.DirectoryPrefix, StringComparison.Ordinal) ||
            !File.Exists(Path.Combine(Root, SandboxFolderProvider.MarkerFile))) return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(Root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                File.SetAttributes(file, FileAttributes.Normal);
            RemoveLinks(Root);
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine("AVISO no se pudo borrar el sandbox " + Root + ": " + ex.Message);
        }
    }
}

/// <summary>Runner mínimo sin dependencias: líneas PASS/FAIL/SKIP y código de salida.</summary>
internal sealed class TestRunner
{
    private readonly List<(string Name, Func<Task> Body)> _tests = [];

    public void Add(string name, Action body) => _tests.Add((name, () => { body(); return Task.CompletedTask; }));
    public void Add(string name, Func<Task> body) => _tests.Add((name, body));

    public async Task<int> RunAsync(string[] args)
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        var filter = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        int passed = 0, failed = 0, skipped = 0;
        foreach (var (name, body) in _tests)
        {
            if (filter is not null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            try { await body(); passed++; Console.WriteLine("PASS " + name); }
            catch (SkipException ex) { skipped++; Console.WriteLine("SKIP " + name + ": " + ex.Message); }
            catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
        }
        Console.WriteLine($"RESULT total={passed + failed + skipped} passed={passed} failed={failed} skipped={skipped}");
        return failed == 0 ? 0 : 1;
    }
}

internal sealed class SkipException(string message) : Exception(message);

internal static class Check
{
    public static void True(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{message}: esperado «{expected}», obtenido «{actual}»");
    }
    public static void SameTree(SortedDictionary<string, string> before, SortedDictionary<string, string> after, string label)
    {
        var missing = before.Keys.Except(after.Keys, StringComparer.OrdinalIgnoreCase).ToList();
        var added = after.Keys.Except(before.Keys, StringComparer.OrdinalIgnoreCase).ToList();
        var changed = before.Where(p => after.TryGetValue(p.Key, out var hash) && hash != p.Value).Select(p => p.Key).ToList();
        if (missing.Count + added.Count + changed.Count != 0)
            throw new Exception($"{label} cambió: faltan [{string.Join(", ", missing.Take(5))}] nuevos [{string.Join(", ", added.Take(5))}] modificados [{string.Join(", ", changed.Take(5))}]");
    }
    public static async Task ThrowsAsync<T>(Func<Task> body, string message) where T : Exception
    {
        try { await body(); } catch (T) { return; }
        throw new Exception(message + ": no lanzó " + typeof(T).Name);
    }
    public static void Throws<T>(Action body, string message) where T : Exception
    {
        try { body(); } catch (T) { return; }
        throw new Exception(message + ": no lanzó " + typeof(T).Name);
    }
}

/// <summary>IProgress síncrono: el callback se ejecuta en el hilo que informa.</summary>
internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
