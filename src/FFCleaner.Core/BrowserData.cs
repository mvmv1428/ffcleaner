using System.Globalization;

namespace FFCleaner.Core;

/// <summary>Datos personales de un navegador Chromium que se pueden borrar de forma selectiva.</summary>
/// <remarks>History…FormData son de Chromium; FirefoxHistory y FirefoxCookies, de Firefox (places.sqlite y cookies.sqlite).</remarks>
public enum BrowserDataKind { History, Downloads, Cookies, Sessions, FormData, FirefoxHistory, FirefoxCookies }

/// <summary>Resultado de inspeccionar (sin modificar) un tipo de dato de navegador.</summary>
public sealed record BrowserDataInspection(long Items, long Bytes, string? Problem);

/// <summary>Resultado de borrar un tipo de dato de navegador.</summary>
public sealed record BrowserDataOutcome(CleanStatus Status, long ItemsRemoved, long BytesBefore, long BytesAfter, string Detail, string? BackupPath);

/// <summary>
/// Borrado selectivo de datos de navegadores Chromium dentro de sus propias bases SQLite. Nunca borra la base
/// completa: vacía solo las tablas del tipo elegido y conserva el resto (el historial de descargas al borrar el
/// historial, las direcciones y tarjetas al borrar formularios, los marcadores y contraseñas siempre).
/// Antes de modificar una base se copia; después se comprueba su integridad y, si falla, se restaura la copia.
/// </summary>
internal static class BrowserData
{
    /// <summary>
    /// Operación sobre una base. Por defecto vacía <see cref="Tables"/>; con <see cref="Statements"/> ejecuta esas
    /// sentencias en su lugar (Firefox: el historial comparte tabla con los marcadores y no se puede vaciar entera).
    /// </summary>
    private sealed record DbOperation(string Leaf, string CountSql, string[] Tables, bool Required, Func<SqliteDb, IEnumerable<string>>? Statements = null);

    private static readonly string[] HistoryTables =
    [
        "urls", "visits", "visit_source", "keyword_search_terms", "segments", "segment_usage", "content_annotations",
        "context_annotations", "clusters", "clusters_and_visits", "cluster_keywords", "cluster_visit_duplicates",
        "visited_links", "history_sync_metadata",
    ];

    /// <summary>Hojas del perfil que cada tipo puede tocar. Las guardas solo aceptan exactamente estas.</summary>
    public static readonly IReadOnlyDictionary<BrowserDataKind, string[]> Leaves = new Dictionary<BrowserDataKind, string[]>
    {
        [BrowserDataKind.History] = ["History", "Top Sites", "Shortcuts", "Visited Links"],
        [BrowserDataKind.Downloads] = ["History"],
        [BrowserDataKind.Cookies] = [@"Network\Cookies", "Cookies"],
        [BrowserDataKind.FormData] = ["Web Data"],
        [BrowserDataKind.Sessions] = ["Sessions", "Current Session", "Current Tabs", "Last Session", "Last Tabs"],
        [BrowserDataKind.FirefoxHistory] = ["places.sqlite"],
        [BrowserDataKind.FirefoxCookies] = ["cookies.sqlite"],
    };

    private static IEnumerable<DbOperation> Operations(BrowserDataKind kind) => kind switch
    {
        BrowserDataKind.History =>
        [
            new("History", "SELECT count(*) FROM urls", HistoryTables, true),
            new("Top Sites", "SELECT count(*) FROM top_sites", ["top_sites"], false),
            new("Shortcuts", "SELECT count(*) FROM omni_box_shortcuts", ["omni_box_shortcuts"], false),
        ],
        BrowserDataKind.Downloads => [new("History", "SELECT count(*) FROM downloads", ["downloads", "downloads_url_chains", "downloads_slices"], true)],
        BrowserDataKind.Cookies => [new(@"Network\Cookies", "SELECT count(*) FROM cookies", ["cookies"], false), new("Cookies", "SELECT count(*) FROM cookies", ["cookies"], false)],
        BrowserDataKind.FormData => [new("Web Data", "SELECT count(*) FROM autofill", ["autofill"], true)],
        BrowserDataKind.FirefoxHistory => [new("places.sqlite", "SELECT count(*) FROM moz_historyvisits",
            ["moz_historyvisits", "moz_inputhistory", "moz_places_metadata", "moz_places_metadata_search_queries"], true, FirefoxHistoryStatements)],
        BrowserDataKind.FirefoxCookies => [new("cookies.sqlite", "SELECT count(*) FROM moz_cookies", ["moz_cookies"], true)],
        _ => [],
    };

    /// <summary>
    /// Cuenta los elementos que se borrarían y estima el espacio que se liberaría. Solo lectura: si el navegador
    /// está abierto, lo informa. Para las bases se mide lo que ocupan las tablas afectadas y sus índices (no la base
    /// entera): es lo que VACUUM devuelve al disco al compactarla.
    /// </summary>
    public static BrowserDataInspection Inspect(CleanupTarget target)
    {
        var profile = ProfileDirectory(target);
        if (target.Data == BrowserDataKind.Sessions)
        {
            var files = SessionFiles(profile).ToList();
            return new(files.Count, files.Sum(f => f.Length), null);
        }
        // Sin la SQLite de Windows no se abre ninguna base: se informa en lugar de fallar con una excepción de carga.
        if (!SqliteDb.IsAvailable) return new(0, 0, SqliteDb.UnavailableMessage);
        long items = 0, bytes = 0;
        foreach (var operation in Operations(target.Data!.Value))
        {
            var path = Path.Combine(profile, operation.Leaf);
            if (!File.Exists(path)) continue;
            try
            {
                using var db = SqliteDb.Open(path, writable: false);
                var table = operation.Tables[0];
                if (!db.HasTable(table)) continue;
                items += db.Scalar(operation.CountSql);
                bytes += Math.Min(new FileInfo(path).Length, TablesSize(db, operation.Tables));
            }
            catch (SqliteException ex) when (!ex.IsLocked && !operation.Required)
            {
                continue;
            }
            catch (SqliteException ex)
            {
                return new(0, bytes, ex.IsLocked
                    ? "La base está en uso: cierra " + target.AppName + " para analizar y limpiar estos datos."
                    : "No se pudo leer la base " + operation.Leaf + ": " + ex.Message);
            }
        }
        if (target.Data == BrowserDataKind.History && File.Exists(Path.Combine(profile, "Visited Links")))
            bytes += new FileInfo(Path.Combine(profile, "Visited Links")).Length;
        return new(items, bytes, null);
    }

    /// <summary>Borra el tipo de dato elegido. El navegador debe estar cerrado: lo comprueba quien llama.</summary>
    public static BrowserDataOutcome Clean(CleanupTarget target, string backupRoot)
    {
        var profile = ProfileDirectory(target);
        if (target.Data == BrowserDataKind.Sessions) return CleanSessions(profile);
        if (!SqliteDb.IsAvailable)
            return new(CleanStatus.Error, 0, 0, 0, SqliteDb.UnavailableMessage + " No se modificó nada.", null);

        long removed = 0, before = 0, after = 0;
        string? backupFolder = null;
        foreach (var operation in Operations(target.Data!.Value))
        {
            var path = Path.Combine(profile, operation.Leaf);
            if (!File.Exists(path)) continue;
            if (!operation.Required && !IsDatabase(path)) continue;
            backupFolder ??= Path.Combine(backupRoot, Safe(target.Key.AppId), Safe(target.Key.ProfileId));
            var backup = Backup(path, backupFolder);
            before += new FileInfo(path).Length;
            try
            {
                using (var db = SqliteDb.Open(path, writable: true))
                {
                    var countTable = operation.Tables[0];
                    var count = db.HasTable(countTable) ? db.Scalar(operation.CountSql) : 0;
                    db.Exec("BEGIN IMMEDIATE");
                    try
                    {
                        if (operation.Statements is { } statements)
                            foreach (var statement in statements(db).ToList()) db.Exec(statement);
                        else
                            foreach (var table in operation.Tables)
                                if (db.HasTable(table)) db.Exec("DELETE FROM \"" + table + "\"");
                        db.Exec("COMMIT");
                    }
                    catch
                    {
                        try { db.Exec("ROLLBACK"); } catch (SqliteException) { }
                        throw;
                    }
                    // VACUUM reescribe la base sin las páginas liberadas: lo borrado no queda recuperable en el archivo.
                    db.Exec("VACUUM");
                    var integrity = db.Text("PRAGMA integrity_check");
                    if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
                        throw new SqliteException(11, "Comprobación de integridad fallida: " + integrity);
                    removed += count;
                }
                after += new FileInfo(path).Length;
            }
            catch (SqliteException ex)
            {
                // Solo se afirma que la copia se restauró si la restauración se comprobó; si no, se indica dónde está.
                var restored = !ex.IsLocked && Restore(backup, path);
                var detail = ex.IsLocked
                    ? target.AppName + " tiene la base abierta; ciérralo y vuelve a analizar."
                    : restored
                        ? "No se completó en " + operation.Leaf + " (" + ex.Message + "); se restauró la copia de seguridad."
                        : "No se completó en " + operation.Leaf + " (" + ex.Message + "); NO se pudo restaurar automáticamente. " +
                          "La copia original está en " + backupFolder + " (" + Path.GetFileName(path) + "): recupérala antes de abrir el navegador.";
                return new(ex.IsLocked ? CleanStatus.Bloqueado : CleanStatus.Error, removed, before, after, detail, backupFolder);
            }
            catch (IOException ex)
            {
                return new(CleanStatus.Error, removed, before, after, "Error de archivo en " + operation.Leaf + ": " + ex.Message, backupFolder);
            }
        }
        if (target.Data == BrowserDataKind.History)
        {
            // «Visited Links» es una tabla derivada del historial: el navegador la regenera.
            var visited = Path.Combine(profile, "Visited Links");
            if (File.Exists(visited) && (File.GetAttributes(visited) & FileAttributes.ReparsePoint) == 0)
            {
                before += new FileInfo(visited).Length;
                try { File.Delete(visited); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { after += new FileInfo(visited).Length; }
            }
        }
        return new(CleanStatus.Eliminado, removed, before, after, "Datos borrados; copia de seguridad en " + (backupFolder ?? "—"), backupFolder);
    }

    private static BrowserDataOutcome CleanSessions(string profile)
    {
        long removed = 0, before = 0, failed = 0;
        string? problem = null;
        foreach (var file in SessionFiles(profile))
        {
            before += file.Length;
            try
            {
                File.Delete(file.FullName);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed++;
                problem = ex.Message;
            }
        }
        return failed == 0
            ? new(CleanStatus.Eliminado, removed, before, 0, "Sesiones y pestañas guardadas eliminadas.", null)
            : new(CleanStatus.Bloqueado, removed, before, 0, failed + " archivos de sesión en uso (" + problem + ").", null);
    }

    /// <summary>
    /// Historial de Firefox. En places.sqlite las páginas visitadas y los marcadores comparten la tabla moz_places:
    /// se borran las visitas, lo escrito en la barra y los metadatos de navegación, y solo las páginas que no son
    /// marcadores, palabras clave ni tienen anotaciones; a las que se conservan se les pone el contador a cero.
    /// </summary>
    private static IEnumerable<string> FirefoxHistoryStatements(SqliteDb db)
    {
        foreach (var table in new[] { "moz_historyvisits", "moz_inputhistory", "moz_places_metadata_search_queries", "moz_places_metadata" })
            if (db.HasTable(table)) yield return "DELETE FROM \"" + table + "\"";
        if (!db.HasTable("moz_places")) yield break;
        var keep = new List<string>();
        if (db.HasTable("moz_bookmarks")) keep.Add("SELECT fk FROM moz_bookmarks WHERE fk IS NOT NULL");
        if (db.HasTable("moz_annos")) keep.Add("SELECT place_id FROM moz_annos WHERE place_id IS NOT NULL");
        if (db.HasTable("moz_keywords")) keep.Add("SELECT place_id FROM moz_keywords WHERE place_id IS NOT NULL");
        yield return keep.Count == 0 ? "DELETE FROM moz_places" : "DELETE FROM moz_places WHERE id NOT IN (" + string.Join(" UNION ", keep) + ")";
        yield return "UPDATE moz_places SET visit_count = 0, last_visit_date = NULL";
        if (db.HasTable("moz_origins"))
            yield return "DELETE FROM moz_origins WHERE id NOT IN (SELECT origin_id FROM moz_places WHERE origin_id IS NOT NULL)";
    }

    /// <summary>Archivos de sesión: los de la carpeta «Sessions» y los antiguos sueltos. Nunca enlaces ni subcarpetas.</summary>
    private static IEnumerable<FileInfo> SessionFiles(string profile)
    {
        var folder = new DirectoryInfo(Path.Combine(profile, "Sessions"));
        if (folder.Exists && (folder.Attributes & FileAttributes.ReparsePoint) == 0)
            foreach (var file in folder.EnumerateFiles())
                if ((file.Attributes & (FileAttributes.ReparsePoint | FileAttributes.ReadOnly | FileAttributes.System)) == 0) yield return file;
        foreach (var name in new[] { "Current Session", "Current Tabs", "Last Session", "Last Tabs" })
        {
            var file = new FileInfo(Path.Combine(profile, name));
            if (file.Exists && (file.Attributes & FileAttributes.ReparsePoint) == 0) yield return file;
        }
    }

    /// <summary>
    /// Bytes que ocupan las tablas indicadas y sus índices. Con la tabla virtual dbstat (incluida en la SQLite de
    /// Windows) es la medida exacta de páginas; si no existe, se aproxima sumando la longitud de cada columna.
    /// </summary>
    private static long TablesSize(SqliteDb db, IEnumerable<string> tables)
    {
        var present = tables.Where(db.HasTable).ToList();
        if (present.Count == 0) return 0;
        var list = string.Join(",", present.Select(t => "'" + t.Replace("'", "''") + "'"));
        try
        {
            return db.Scalar("SELECT CAST(total(pgsize) AS INTEGER) FROM dbstat WHERE name IN (SELECT name FROM sqlite_master WHERE tbl_name IN (" + list + "))");
        }
        catch (SqliteException ex) when (!ex.IsLocked)
        {
            long total = 0;
            foreach (var table in present)
            {
                var columns = db.Text("SELECT group_concat('length(\"' || replace(name, '\"', '\"\"') || '\")', '+') FROM pragma_table_info('" + table.Replace("'", "''") + "')");
                if (!string.IsNullOrEmpty(columns)) total += db.Scalar("SELECT CAST(total(" + columns + ") AS INTEGER) FROM \"" + table + "\"");
            }
            return total;
        }
    }

    /// <summary>¿Es una base SQLite legible? Un bloqueo cuenta como base (se informará al intentar limpiarla).</summary>
    private static bool IsDatabase(string path)
    {
        try
        {
            using var db = SqliteDb.Open(path, writable: false);
            db.Scalar("SELECT count(*) FROM sqlite_master");
            return true;
        }
        catch (SqliteException ex) { return ex.IsLocked; }
    }

    private static string ProfileDirectory(CleanupTarget target) =>
        Path.Combine(target.DataRoot ?? throw new InvalidOperationException("Objetivo sin carpeta de datos."), target.Key.ProfileId);

    /// <summary>Copia la base y sus archivos auxiliares a la carpeta de copias de FFCleaner.</summary>
    private static string Backup(string path, string folder)
    {
        Directory.CreateDirectory(folder);
        var name = Path.GetFileName(path);
        var destination = Path.Combine(folder, name);
        foreach (var suffix in new[] { "", "-journal", "-wal", "-shm" })
            if (File.Exists(path + suffix)) File.Copy(path + suffix, destination + suffix, overwrite: true);
        return destination;
    }

    /// <summary>
    /// Devuelve la copia a su sitio. Devuelve false si no se pudo comprobar la restauración: quien llama no debe
    /// presentar entonces la limpieza como «revertida».
    /// </summary>
    internal static bool Restore(string backup, string path)
    {
        try
        {
            foreach (var suffix in new[] { "-journal", "-wal", "-shm" })
                if (File.Exists(path + suffix)) File.Delete(path + suffix);
            foreach (var suffix in new[] { "", "-journal", "-wal", "-shm" })
                if (File.Exists(backup + suffix)) File.Copy(backup + suffix, path + suffix, overwrite: true);
            // La copia principal debe existir y coincidir en tamaño: es lo mínimo comprobable aquí.
            var original = new FileInfo(backup);
            var restoredFile = new FileInfo(path);
            return restoredFile.Exists && restoredFile.Length == original.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning("No se pudo restaurar {0}: {1}", path, ex.Message);
            return false;
        }
    }

    private static string Safe(string text) =>
        string.Concat(text.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    /// <summary>Carpeta de copias para un lote, con marca de tiempo; conserva solo las cinco más recientes.</summary>
    public static string NewBackupRoot(string stateDirectory, DateTime nowUtc)
    {
        var parent = Path.Combine(stateDirectory, "BrowserBackups");
        Directory.CreateDirectory(parent);
        foreach (var old in new DirectoryInfo(parent).GetDirectories().OrderByDescending(d => d.Name, StringComparer.Ordinal).Skip(4))
        {
            try { old.Delete(recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return Path.Combine(parent, nowUtc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6]);
    }
}
