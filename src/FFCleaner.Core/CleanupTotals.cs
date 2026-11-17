using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FFCleaner.Core;

/// <summary>Lo liberado desde que se usa FFCleaner.</summary>
public sealed record CleanupTotalsData(long Bytes, long Files, int Cleanings, DateTime? SinceUtc)
{
    public static CleanupTotalsData Empty => new(0, 0, 0, null);
}

/// <summary>
/// Acumulado de todo lo liberado, en State\totals.json. Las bitácoras solo conservan las últimas limpiezas, así
/// que no basta con sumarlas: la primera vez se siembra con las que haya (incluidas las migradas de Claro) y
/// después cada limpieza suma lo suyo, la haga la aplicación o un agente por MCP.
/// </summary>
public sealed class CleanupTotals(string stateDirectory, DiagnosticsLog? log = null)
{
    private static readonly object Gate = new();
    private readonly DiagnosticsLog _log = log ?? DiagnosticsLog.Null;

    public string FilePath => Path.Combine(stateDirectory, "totals.json");
    private string JournalDirectory => Path.Combine(stateDirectory, "Journal");

    /// <summary>
    /// Mutex con nombre para leer-sumar-escribir: la aplicación y el servidor MCP son procesos distintos y, sin él, si
    /// limpian a la vez uno podría pisar la suma del otro.
    /// </summary>
    private string MutexName => @"Local\FFCleaner-totals-" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(FilePath).ToUpperInvariant())))[..24];

    /// <summary>Lee el acumulado; si aún no existe, lo crea a partir de las bitácoras.</summary>
    public CleanupTotalsData Read()
    {
        lock (Gate)
        {
            using var mutex = new Mutex(false, MutexName);
            var owned = Acquire(mutex);
            try { return ReadUnlocked(); }
            finally { if (owned) mutex.ReleaseMutex(); }
        }
    }

    private static bool Acquire(Mutex mutex)
    {
        try { return mutex.WaitOne(TimeSpan.FromSeconds(10)); }
        catch (AbandonedMutexException) { return true; }   // el otro proceso terminó sin soltarlo: el archivo se escribe siempre entero
    }

    private CleanupTotalsData ReadUnlocked() => ReadUnlocked(out _);

    /// <param name="reliable">
    /// false si el archivo existe pero no se pudo leer (en uso, sin permisos): quien suma no debe sobrescribirlo, porque
    /// perdería el acumulado. Un archivo dañado (JSON no válido) se aparta como «totals.json.danado-…» y el acumulado se
    /// rehace desde las bitácoras que queden.
    /// </param>
    private CleanupTotalsData ReadUnlocked(out bool reliable)
    {
        reliable = true;
        try
        {
            if (File.Exists(FilePath))
            {
                try { return JsonSerializer.Deserialize<CleanupTotalsData>(File.ReadAllText(FilePath)) ?? throw new JsonException("vacío"); }
                catch (JsonException ex)
                {
                    var aside = FilePath + ".danado-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
                    File.Move(FilePath, aside, overwrite: true);
                    _log.Write("totales", "Acumulado dañado (" + ex.Message + "); se apartó como " + Path.GetFileName(aside) + " y se rehace desde las bitácoras.");
                }
            }
            var seeded = SeedFromJournals();
            Write(seeded);
            return seeded;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Write("totales", "No se pudo leer el acumulado: " + ex.Message);
            reliable = !File.Exists(FilePath);
            return CleanupTotalsData.Empty;
        }
    }

    /// <summary>Suma una limpieza terminada. La bitácora de esa limpieza ya existe, así que la siembra la incluiría:
    /// por eso se lee (y siembra si hace falta) antes de escribir la bitácora nueva, desde quien llama.</summary>
    public CleanupTotalsData Add(long bytes, long files, DateTime startedUtc)
    {
        lock (Gate)
        {
            using var mutex = new Mutex(false, MutexName);
            var owned = Acquire(mutex);
            try { return AddUnlocked(bytes, files, startedUtc); }
            finally { if (owned) mutex.ReleaseMutex(); }
        }
    }

    private CleanupTotalsData AddUnlocked(long bytes, long files, DateTime startedUtc)
    {
        var current = ReadUnlocked(out var reliable);
        if (bytes <= 0 && files <= 0) return current;
        if (!reliable)
        {
            // Escribir ahora sustituiría el acumulado que no se pudo leer por esta sola limpieza.
            _log.Write("totales", FormattableString.Invariant($"Acumulado ilegible: no se sumaron {bytes} bytes para no perder el total guardado."));
            return current;
        }
        var updated = new CleanupTotalsData(current.Bytes + bytes, current.Files + files, current.Cleanings + 1,
            current.SinceUtc is { } since && since <= startedUtc ? since : startedUtc);
        try { Write(updated); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Write("totales", "No se pudo guardar el acumulado: " + ex.Message);
        }
        return updated;
    }

    private CleanupTotalsData SeedFromJournals()
    {
        long bytes = 0, files = 0;
        var cleanings = 0;
        DateTime? since = null;
        if (!Directory.Exists(JournalDirectory)) return CleanupTotalsData.Empty;
        foreach (var journal in Directory.EnumerateFiles(JournalDirectory, "limpieza-*.jsonl"))
        {
            DateTime? started = null;
            try
            {
                foreach (var line in File.ReadLines(journal))
                {
                    if (!line.Contains("\"evento\":\"inicio\"", StringComparison.Ordinal) && !line.Contains("\"evento\":\"fin\"", StringComparison.Ordinal)) continue;
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    var evento = root.GetProperty("evento").GetString();
                    if (evento == "inicio" && root.TryGetProperty("inicioUtc", out var s) && s.TryGetDateTime(out var start)) started = start.ToUniversalTime();
                    if (evento != "fin") continue;
                    var b = root.TryGetProperty("bytes", out var bb) ? bb.GetInt64() : 0;
                    var f = root.TryGetProperty("eliminados", out var ff) ? ff.GetInt64() : 0;
                    if (b <= 0 && f <= 0) continue;
                    bytes += b; files += f; cleanings++;
                    if (started is { } st && (since is null || st < since)) since = st;
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
            {
                _log.Write("totales", "Bitácora ilegible al sembrar el acumulado: " + Path.GetFileName(journal) + " (" + ex.Message + ")");
            }
        }
        return new(bytes, files, cleanings, since);
    }

    private void Write(CleanupTotalsData data)
    {
        Directory.CreateDirectory(stateDirectory);
        var temporary = FilePath + ".writing-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, JsonSerializer.Serialize(data), new UTF8Encoding(false));
        File.Move(temporary, FilePath, overwrite: true);
    }
}
