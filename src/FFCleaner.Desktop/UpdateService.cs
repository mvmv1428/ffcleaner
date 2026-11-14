using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using FFCleaner.Core;

namespace FFCleaner.Desktop;

/// <summary>Versión publicada en GitHub con el paquete portable que la contiene.</summary>
public sealed record UpdateInfo(Version Version, string Tag, string AssetName, Uri AssetUrl, string Sha256, long Size);

/// <summary>
/// Busca versiones nuevas en las «releases» de GitHub del repositorio configurado (Directory.Build.props) y prepara
/// la actualización: descarga el ZIP portable, comprueba su SHA-256 con el que publica GitHub y la huella de cada
/// archivo, y lanza FFCleaner.Setup.exe para sustituir la instalación cuando FFCleaner se cierre.
/// Nunca instala una versión anterior ni preliminar, ni nada cuya huella no coincida.
/// </summary>
public sealed class UpdateService
{
    private const long MaxDownloadBytes = 600L * 1024 * 1024;
    private readonly HttpClient _http;
    private readonly DiagnosticsLog _log;
    private readonly Uri? _feed;
    private readonly string? _assetPrefix;

    public UpdateService(DiagnosticsLog log, string? repository, Uri? testFeed = null, HttpMessageHandler? handler = null)
    {
        _log = log;
        Repository = string.IsNullOrWhiteSpace(repository) ? null : repository.Trim();
        if (testFeed is not null) _feed = testFeed;
        else if (Repository is not null) _feed = new Uri($"https://api.github.com/repos/{Repository}/releases/latest");
        _assetPrefix = testFeed is null && Repository is not null ? $"https://github.com/{Repository}/releases/download/" : null;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromMinutes(10);
        // Identificación genérica: GitHub exige un User-Agent; no se envía ningún dato del usuario.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("FFCleaner-Updater/" + Current.ToString(3));
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    /// <summary>Repositorio configurado en la compilación («usuario/nombre»), o null.</summary>
    public string? Repository { get; }
    public bool Enabled => _feed is not null;
    public static Version Current => typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0);
    public static string InstallDirectory => AppContext.BaseDirectory.TrimEnd('\\');

    /// <summary>Solo una copia instalada (con install.json) se actualiza sola; la portátil solo avisa.</summary>
    public static bool IsInstalledCopy => File.Exists(Path.Combine(InstallDirectory, "install.json")) && File.Exists(Path.Combine(InstallDirectory, "FFCleaner.Setup.exe"));

    /// <summary>Repositorio incrustado en el ensamblado al compilar (vacío si no se configuró).</summary>
    public static string? ConfiguredRepository() =>
        typeof(UpdateService).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "FFCleanerUpdateRepository")?.Value;

    /// <summary>Consulta la última versión publicada. Devuelve null si no hay ninguna más nueva que la instalada.</summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken token = default)
    {
        if (_feed is null) return null;
        using var response = await _http.GetAsync(_feed, token);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;   // repositorio sin versiones publicadas
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        var root = doc.RootElement;
        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
        if (root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version) || Normalize(version) <= Normalize(Current)) return null;
        var expected = $"FFCleaner-{version.ToString(3)}-win-x64.zip";
        JsonElement? asset = null;
        string? checksumUrl = null;
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString();
            if (string.Equals(name, expected, StringComparison.OrdinalIgnoreCase)) asset = a;
            if (string.Equals(name, expected + ".sha256", StringComparison.OrdinalIgnoreCase)) checksumUrl = a.GetProperty("browser_download_url").GetString();
        }
        if (asset is not { } found) throw new InvalidOperationException($"La versión {tag} no incluye {expected}.");
        var url = new Uri(found.GetProperty("browser_download_url").GetString()!);
        CheckOrigin(url);
        var sha = found.TryGetProperty("digest", out var digest) && digest.GetString() is { } d && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? d[7..] : null;
        if (sha is null && checksumUrl is not null)
        {
            var checksum = new Uri(checksumUrl);
            CheckOrigin(checksum);
            sha = (await _http.GetStringAsync(checksum, token)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        }
        if (sha is null || sha.Length != 64) throw new InvalidOperationException($"La versión {tag} no publica la huella SHA-256 de su paquete: no se instalará.");
        var size = found.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
        return new UpdateInfo(version, tag, expected, url, sha.ToLowerInvariant(), size);
    }

    /// <summary>
    /// Descarga y verifica la versión nueva y la descomprime en %TEMP%. Devuelve la ruta del FFCleaner.Setup.exe
    /// que la instalará. Si cualquier huella no coincide, lanza una excepción y no queda nada a medias.
    /// </summary>
    public async Task<string> PrepareAsync(UpdateInfo update, IProgress<double>? progress = null, CancellationToken token = default)
    {
        var work = Path.Combine(Path.GetTempPath(), "ffcleaner-update-" + update.Version.ToString(3) + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        try
        {
            var zip = Path.Combine(work, update.AssetName);
            using (var response = await _http.GetAsync(update.AssetUrl, HttpCompletionOption.ResponseHeadersRead, token))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? update.Size;
                if (total > MaxDownloadBytes) throw new InvalidOperationException("El paquete de actualización es demasiado grande.");
                await using var input = await response.Content.ReadAsStreamAsync(token);
                await using var output = File.Create(zip);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[1 << 16];
                long read = 0;
                int n;
                while ((n = await input.ReadAsync(buffer, token)) > 0)
                {
                    read += n;
                    if (read > MaxDownloadBytes) throw new InvalidOperationException("El paquete de actualización es demasiado grande.");
                    hash.AppendData(buffer, 0, n);
                    await output.WriteAsync(buffer.AsMemory(0, n), token);
                    if (total > 0) progress?.Report(Math.Min(1, (double)read / total));
                }
                var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                if (actual != update.Sha256)
                    throw new InvalidOperationException("La huella SHA-256 del paquete descargado no coincide con la publicada: no se instalará.");
            }
            var extracted = Path.Combine(work, "paquete");
            ZipFile.ExtractToDirectory(zip, extracted);   // rechaza rutas que salgan de la carpeta
            File.Delete(zip);
            var package = Directory.GetDirectories(extracted).SingleOrDefault() ?? throw new InvalidOperationException("El paquete de actualización no tiene la estructura esperada.");
            var sums = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(package, "SHA256SUMS.json")))
                       ?? throw new InvalidOperationException("Falta la lista de huellas del paquete.");
            foreach (var (name, expected) in sums)
            {
                // La lista de huellas no puede nombrar rutas fuera del paquete descomprimido.
                var candidate = Path.GetFullPath(Path.Combine(package, name.Replace('/', '\\')));
                if (!candidate.StartsWith(Path.GetFullPath(package).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("..", StringComparison.Ordinal) || name.Contains(':', StringComparison.Ordinal))
                    throw new InvalidOperationException("El paquete contiene una ruta no válida: " + name);
                using var stream = File.OpenRead(candidate);
                if (!string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(stream, token)), expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("El archivo " + name + " del paquete no coincide con su huella: no se instalará.");
            }
            var setup = Path.Combine(package, "FFCleaner.Setup.exe");
            if (!File.Exists(setup)) throw new InvalidOperationException("El paquete de actualización no incluye el instalador.");
            _log.Write("actualizacion", $"versión {update.Tag} descargada y verificada en {work}");
            return setup;
        }
        catch
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>Lanza el instalador de la versión nueva: espera a que FFCleaner se cierre, actualiza y lo vuelve a abrir.</summary>
    public void LaunchInstaller(string setup, IEnumerable<string> relaunchArguments)
    {
        var start = new ProcessStartInfo(setup) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(setup)! };
        foreach (var arg in new[] { "--update", "--quiet", "--show-progress", "--dest", InstallDirectory, "--wait-pid", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                    "--launch-args", string.Join(' ', relaunchArguments.Select(Quote)) })
            start.ArgumentList.Add(arg);
        Process.Start(start);
        _log.Write("actualizacion", "instalador lanzado: " + setup);
    }

    private void CheckOrigin(Uri url)
    {
        if (_assetPrefix is not null)
        {
            if (!url.AbsoluteUri.StartsWith(_assetPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("La descarga no procede de las versiones publicadas del repositorio: " + url);
        }
        else if (!url.IsLoopback) throw new InvalidOperationException("Origen de prueba no permitido: " + url);
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    private static string Quote(string arg) => arg.Contains(' ') || arg.Contains('"') ? "\"" + arg.Replace("\"", "\\\"") + "\"" : arg;
}
