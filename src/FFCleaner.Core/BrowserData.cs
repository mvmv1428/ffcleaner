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

    /// <summary>
    /// Borra el tipo de dato elegido. El navegador debe estar cerrado: lo comprueba quien llama.
    /// La copia previa es temporal: solo sirve para deshacer un fallo a mitad de la operación. Si todo termina bien (o
    /// la copia se devolvió a su sitio y se comprobó) se borra, porque guardarla dejaría a la vista justo lo que el
    /// usuario quería borrar. Solo se conserva si la restauración automática no se pudo comprobar.
    /// </summary>
    public static BrowserDataOutcome Clean(CleanupTarget target, string backupRoot)
    {
        var profile = ProfileDirectory(target);
        if (target.Data == BrowserDataKind.Sessions) return CleanSessions(profile);
        if (!SqliteDb.IsAvailable)
            return new(CleanStatus.Error, 0, 0, 0, SqliteDb.UnavailableMessage + " No se modificó nada.", null);

        var backupFolder = Path.Combine(backupRoot, Safe(target.Key.AppId), Safe(target.Key.ProfileId));
        var outcome = CleanDatabases(target, profile, backupFolder, out var keepBackup);
        if (keepBackup)
        {
            // La copia es la única forma de recuperar la base: se marca para que la poda no la borre a los pocos días.
            TryWriteKeepMarker(backupFolder);
            return outcome;
        }
        var discarded = DiscardBackup(backupFolder, Path.GetDirectoryName(Path.GetDirectoryName(backupRoot))!);
        if (outcome.Status == CleanStatus.Eliminado)
            return outcome with
            {
                BackupPath = discarded ? null : backupFolder,
                Detail = discarded ? "Datos borrados. La copia temporal de la base también se borró."
                    : "Datos borrados. No se pudo borrar la copia temporal en " + backupFolder + ": bórrala a mano.",
            };
        return outcome with { BackupPath = discarded ? null : backupFolder };
    }

    private static BrowserDataOutcome CleanDatabases(CleanupTarget target, string profile, string backupFolder, out bool keepBackup)
    {
        keepBackup = false;
        long removed = 0, before = 0, after = 0;
        foreach (var operation in Operations(target.Data!.Value))
        {
            var path = Path.Combine(profile, operation.Leaf);
            if (!File.Exists(path)) continue;
            if (!operation.Required && !IsDatabase(path)) continue;
            string backup;
            try
            {
                backup = Backup(path, backupFolder);
                before += new FileInfo(path).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Sin copia no se toca la base: es la garantía de poder deshacer un fallo.
                return new(CleanStatus.Error, removed, before, after,
                    "No se pudo copiar " + operation.Leaf + " antes de modificarla (" + ex.Message + "); esa base no se modificó.", null);
            }
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
                // Bloqueada: no se llegó a modificar. Restaurada y comprobada: la base vuelve a ser la original.
                keepBackup = !ex.IsLocked && !restored;
                var detail = ex.IsLocked
                    ? target.AppName + " tiene la base abierta; ciérralo y vuelve a analizar."
                    : restored
                        ? "No se completó en " + operation.Leaf + " (" + ex.Message + "); se restauró la copia de seguridad."
                        : "No se completó en " + operation.Leaf + " (" + ex.Message + "); NO se pudo restaurar automáticamente. " +
                          "La copia original está en " + backupFolder + " (" + Path.GetFileName(path) + "): recupérala antes de abrir el navegador.";
                return new(ex.IsLocked ? CleanStatus.Bloqueado : CleanStatus.Error, removed, before, after, detail, backupFolder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Estado desconocido de la base: la copia se conserva por si hay que recuperarla.
                keepBackup = true;
                return new(CleanStatus.Error, removed, before, after, "Error de archivo en " + operation.Leaf + ": " + ex.Message +
                    ". La copia original está en " + backupFolder + ".", backupFolder);
            }
        }
        if (target.Data == BrowserDataKind.History)
        {
            // «Visited Links» es una tabla derivada del historial: el navegador la regenera.
            var visited = new FileInfo(Path.Combine(profile, "Visited Links"));
            if (visited.Exists)
            {
                before += visited.Length;
                if (NativeMetadata.DeleteRegularFile(visited.FullName) is not null) after += visited.Length;
            }
        }
        return new(CleanStatus.Eliminado, removed, before, after, "Datos borrados.", null);
    }

    /// <summary>Nombre del aviso que conserva una copia necesaria para recuperar una base.</summary>
    internal const string KeepMarker = "CONSERVAR.txt";

    private static void TryWriteKeepMarker(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
                File.WriteAllText(Path.Combine(folder, KeepMarker),
                    "FFCleaner no pudo comprobar que la base del navegador quedara restaurada. Esta es la copia original: " +
                    "si el navegador muestra errores, ciérralo y copia estos archivos a la carpeta del perfil. Borra esta carpeta cuando ya no la necesites.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Borra la copia temporal de un perfil y las carpetas que queden vacías por encima, sin salir de BrowserBackups.
    /// Devuelve false si quedó algo (por ejemplo, un archivo bloqueado).
    /// </summary>
    private static bool DiscardBackup(string folder, string stateDirectory)
    {
        var parent = Path.Combine(stateDirectory, "BrowserBackups");
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            for (var current = Path.GetDirectoryName(folder);
                 current is not null && PathSafety.IsStrictlyWithin(current, parent) && Directory.Exists(current) && !Directory.EnumerateFileSystemEntries(current).Any();
                 current = Path.GetDirectoryName(current))
                Directory.Delete(current);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return !Directory.Exists(folder);
        }
    }

    /// <summary>
    /// Borra las copias que ya no hacen falta: las que no llevan el aviso de conservar (las de versiones anteriores, que
    /// se guardaban siempre, o las de un cierre inesperado) y, pasados 30 días, también las que lo llevan. Las de la
    /// última hora se respetan: pueden ser de una limpieza en curso en otro proceso (la aplicación y el servidor MCP).
    /// </summary>
    public static int PruneBackups(string stateDirectory, DateTime nowUtc)
    {
        var parent = new DirectoryInfo(Path.Combine(stateDirectory, "BrowserBackups"));
        var removed = 0;
        try
        {
            if (!parent.Exists || (parent.Attributes & FileAttributes.ReparsePoint) != 0) return 0;
            foreach (var batch in parent.GetDirectories())
            {
                if ((batch.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                var keep = batch.EnumerateFiles(KeepMarker, SearchOption.AllDirectories).Any();
                if (batch.CreationTimeUtc > (keep ? nowUtc.AddDays(-30) : nowUtc.AddHours(-1))) continue;
                try { batch.Delete(recursive: true); removed++; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return removed;
    }

    private static BrowserDataOutcome CleanSessions(string profile)
    {
        long removed = 0, before = 0, failed = 0;
        string? problem = null;
        foreach (var file in SessionFiles(profile))
        {
            before += file.Length;
            if (NativeMetadata.DeleteRegularFile(file.FullName) is { } reason)
            {
                failed++;
                problem = reason;
            }
            else removed++;
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

    /// <summary>
    /// Ruta de la carpeta de copias de un lote, con marca de tiempo. No toca el disco: la carpeta se crea al copiar la
    /// primera base, y un fallo al crearla se trata como el de esa copia (la base no se modifica).
    /// </summary>
    public static string NewBackupRoot(string stateDirectory, DateTime nowUtc) =>
        Path.Combine(stateDirectory, "BrowserBackups", nowUtc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6]);
}
