using System.Globalization;
using System.Text;
using System.Text.Json;
using FFCleaner.Core;

namespace FFCleaner.Cleaning;

/// <summary>
/// Ejecutor real de la limpieza. Solo actúa sobre una sesión de análisis vigente cuya selección coincide
/// con la actual; revalida cada objetivo, cada carpeta y cada archivo antes de eliminarlo y nunca aborta
/// el lote por un fallo parcial: un archivo bloqueado o cambiado se anota y se continúa.
/// </summary>
public sealed class FileCleaningService : IFileCleaningService
{
    private const int MaxDetailsPerTarget = 2000;
    private const int ProgressIntervalMs = 100;
    private const int JournalsKept = 20;
    private readonly IFolderProvider _folders;
    private readonly DiagnosticsLog _log;
    private readonly Func<DateTime> _clock;
    private readonly IProcessProbe? _processes;

    /// <param name="processes">Sonda de procesos: los datos de navegador solo se borran con el navegador cerrado.</param>
    public FileCleaningService(IFolderProvider folders, DiagnosticsLog? log = null, Func<DateTime>? clock = null,
        IProcessProbe? processes = null)
    {
        ArgumentNullException.ThrowIfNull(folders);
        _folders = folders;
        _log = log ?? DiagnosticsLog.Null;
        _clock = clock ?? (() => DateTime.UtcNow);
        _processes = processes;
        Totals = new CleanupTotals(folders.StateDirectory, _log);
    }

    /// <summary>Acumulado de todo lo liberado con FFCleaner (la aplicación y el servidor MCP comparten este archivo).</summary>
    public CleanupTotals Totals { get; }

    /// <summary>Carpeta donde se copian las bases de datos de navegador antes de modificarlas.</summary>
    public string BrowserBackupDirectory => Path.Combine(_folders.StateDirectory, "BrowserBackups");

    /// <summary>
    /// Borra un tipo de dato de navegador. Rechaza el objetivo si el navegador está abierto o si el objetivo dejó de
    /// coincidir con la selección; nunca toca una base con el navegador en marcha.
    /// </summary>
    private TargetCleaningResult CleanBrowserData(TargetAnalysis analysis, string? refusal, ref string? backupRoot, CancellationToken token)
    {
        var target = analysis.Target;
        var running = target.ProcessNames.FirstOrDefault(p => _processes?.IsRunning(p) == true);
        CleanStatus status;
        string detail;
        long removed = 0, freed = 0;
        if (token.IsCancellationRequested) { status = CleanStatus.Cancelado; detail = "Cancelado antes de modificarlo."; }
        else if (refusal is not null) { status = CleanStatus.Omitido; detail = refusal; }
        else if (running is not null) { status = CleanStatus.Bloqueado; detail = target.AppName + " está abierto: ciérralo para borrar estos datos."; }
        else if (analysis.DataItems == 0) { status = CleanStatus.Omitido; detail = "No había nada que borrar."; }
        else
        {
            backupRoot ??= BrowserData.NewBackupRoot(_folders.StateDirectory, _clock());
            var outcome = BrowserData.Clean(target, backupRoot);
            status = outcome.Status;
            detail = outcome.Detail;
            removed = outcome.ItemsRemoved;
            freed = Math.Max(0, outcome.BytesBefore - outcome.BytesAfter);
        }
        var notDeleted = status == CleanStatus.Eliminado ? 0 : (int)Math.Max(1, analysis.DataItems - removed);
        var reasons = status == CleanStatus.Eliminado ? new Dictionary<string, int>() : new Dictionary<string, int> { [status + ": " + detail] = notDeleted };
        _log.Write("limpieza", target.Key + ": " + status + " · " + removed + " elementos · " + detail);
        var path = target.Roots[0].Path;
        return new TargetCleaningResult(target.Key, target.AppName, target.CategoryName, target.ProfileName,
            (int)removed, freed,
            status is CleanStatus.Omitido ? notDeleted : 0, status is CleanStatus.Bloqueado ? notDeleted : 0,
            status is CleanStatus.Error ? notDeleted : 0, 0, status is CleanStatus.Cancelado ? notDeleted : 0, 0,
            [new(path, status, detail)], 0) { Reasons = reasons };
    }

    /// <summary>Carpeta de bitácoras de limpieza.</summary>
    public string JournalDirectory => Path.Combine(_folders.StateDirectory, "Journal");

    public Task<CleaningReport> CleanAsync(AnalysisSession session, SelectionSnapshot currentSelection,
        IProgress<CleaningProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(currentSelection);
        return Task.Run(() => Execute(session, currentSelection, progress, cancellationToken), CancellationToken.None);
    }

    private CleaningReport Execute(AnalysisSession session, SelectionSnapshot selection, IProgress<CleaningProgress>? progress,
        CancellationToken token)
    {
        var started = _clock();
        // Precondiciones del lote completo: si alguna falla no se toca ningún archivo.
        if (session.CleanBlocker(selection, started) is { } blocker) return Refuse(session, started, blocker);
        if (token.IsCancellationRequested) return Refuse(session, started, "Cancelado antes de empezar.");
        if (!session.TryConsume()) return Refuse(session, started, "Este análisis ya se usó en una limpieza; vuelve a analizar.");

        // El acumulado se siembra (si hace falta) antes de escribir la bitácora de esta limpieza: así no se cuenta dos veces.
        Totals.Read();
        using var journal = Journal.Open(JournalDirectory, session, started, _log);
        var results = new List<TargetCleaningResult>(session.Targets.Count);
        long totalFiles = session.EligibleFiles, processed = 0, deletedBytes = 0;
        var deletedFiles = 0;
        string? browserBackupRoot = null;
        long lastReport = 0;

        for (var t = 0; t < session.Targets.Count; t++)
        {
            var analysis = session.Targets[t];
            var target = analysis.Target;
            var counters = new Counters();
            var details = new List<CleaningItemDetail>();
            var reasons = new Dictionary<string, int>(StringComparer.Ordinal);
            var label = target.ProfileName.Length == 0 ? target.AppName + " · " + target.CategoryName
                : target.AppName + " · " + target.CategoryName + " · " + target.ProfileName;

            void Record(int fileIndex, CleanStatus status, string detail)
            {
                switch (status)
                {
                    case CleanStatus.Omitido: counters.Skipped++; break;
                    case CleanStatus.Bloqueado: counters.Locked++; break;
                    case CleanStatus.Error: counters.Failed++; break;
                    case CleanStatus.NoConfirmado: counters.Unconfirmed++; break;
                    case CleanStatus.Cancelado: counters.Cancelled++; break;
                }
                // El recuento por motivo no tiene tope: aunque el detalle se recorte, siempre se sabe por qué.
                var reason = status + ": " + detail;
                reasons[reason] = reasons.GetValueOrDefault(reason) + 1;
                if (details.Count < MaxDetailsPerTarget) details.Add(new(analysis.PathOf(fileIndex), status, detail));
                else counters.DetailsOmitted++;
            }

            // Revalidación del objetivo: debe seguir seleccionado y sus raíces deben seguir siendo aceptables.
            var targetRefusal = analysis.Refusal
                ?? (selection.Contains(target.Key) ? null : "Objetivo no seleccionado.")
                ?? (selection.Matches(target) ? null : "El objetivo no coincide con el seleccionado.")
                ?? CleaningGuard.ValidateTarget(target, _folders);
            journal.TargetStarted(target.Key, analysis.EligibleCount, analysis.EligibleBytes, targetRefusal);
            if (target.Data is not null)
            {
                var dataResult = CleanBrowserData(analysis, targetRefusal, ref browserBackupRoot, token);
                deletedFiles += dataResult.Deleted;
                deletedBytes += dataResult.DeletedBytes;
                results.Add(dataResult);
                journal.TargetFinished(dataResult);
                progress?.Report(new(t + 1, session.Targets.Count, processed, totalFiles, deletedBytes, label));
                continue;
            }

            using (var pins = new PinnedDirectoryChain())
            {
                var currentDirectory = -1;
                string? directoryProblem = null;
                var files = analysis.Files;
                for (var i = 0; i < files.Length; i++)
                {
                    ref readonly var file = ref files[i];
                    if (file.Verdict != FileVerdict.Eligible) { counters.Protected++; continue; }
                    processed++;
                    if (token.IsCancellationRequested) { Record(i, CleanStatus.Cancelado, "Cancelado antes de eliminarlo."); continue; }
                    if (targetRefusal is not null) { Record(i, CleanStatus.Omitido, targetRefusal); continue; }

                    var directory = analysis.DirectoryAt(file.Directory);
                    var root = target.Roots[directory.RootIndex];
                    if (file.Directory != currentDirectory)
                    {
                        currentDirectory = file.Directory;
                        directoryProblem = !PathSafety.IsWithin(directory.Path, root.Path)
                            ? "Carpeta fuera de la raíz autorizada."
                            : CleaningGuard.IsSensitiveDirectory(directory.Path, target.Anchor, root.Policy)
                                ? CleaningGuard.Describe(FileVerdict.SensitiveDirectory)
                                : pins.Pin(directory.Path);
                    }
                    if (directoryProblem is not null) { Record(i, CleanStatus.Omitido, directoryProblem); continue; }

                    // La política se vuelve a evaluar con los datos del análisis; el handle los confirma después.
                    var now = _clock();
                    var verdict = CleaningGuard.CheckFile(root.Policy, file.Name, file.LastWriteUtc, now);
                    if (verdict != FileVerdict.Eligible) { Record(i, CleanStatus.Omitido, CleaningGuard.Describe(verdict)); continue; }

                    var outcome = NativeDeletion.Delete(Path.Join(directory.Path, file.Name), in file,
                        analysis.RootVolumes[directory.RootIndex], root.Policy.MinimumAge, now, out var error);
                    switch (outcome)
                    {
                        case DeleteOutcome.Deleted:
                            counters.Deleted++;
                            counters.DeletedBytes += file.Length;
                            deletedFiles++;
                            deletedBytes += file.Length;
                            break;
                        case DeleteOutcome.Locked: Record(i, CleanStatus.Bloqueado, "En uso por otro programa; se omitió."); break;
                        case DeleteOutcome.AccessDenied: Record(i, CleanStatus.Bloqueado, "Acceso denegado; no se cambiaron permisos."); break;
                        case DeleteOutcome.Missing: Record(i, CleanStatus.Omitido, "Ya no existe."); break;
                        case DeleteOutcome.Changed: Record(i, CleanStatus.Omitido, "Cambió desde el análisis; se conserva."); break;
                        case DeleteOutcome.UnsafeAttributes: Record(i, CleanStatus.Omitido, "Atributos protegidos (solo lectura, sistema, enlace o nube)."); break;
                        case DeleteOutcome.MultipleLinks: Record(i, CleanStatus.Omitido, CleaningGuard.Describe(FileVerdict.MultipleLinks)); break;
                        case DeleteOutcome.TooRecent: Record(i, CleanStatus.Omitido, CleaningGuard.Describe(FileVerdict.TooRecent)); break;
                        case DeleteOutcome.Unconfirmed: Record(i, CleanStatus.NoConfirmado, "Se solicitó el borrado pero el nombre sigue presente (error " + error + ")."); break;
                        default: Record(i, CleanStatus.Error, "Error de Windows " + error + " antes de modificar el archivo."); break;
                    }

                    var tick = Environment.TickCount64;
                    if (progress is not null && tick - lastReport >= ProgressIntervalMs)
                    {
                        lastReport = tick;
                        progress.Report(new(t, session.Targets.Count, processed, totalFiles, deletedBytes, label));
                    }
                }
            }

            // Con los archivos ya tratados y las carpetas sin fijar, se retiran las subcarpetas que quedaron vacías.
            var emptyDirectories = targetRefusal is null && !token.IsCancellationRequested
                ? RemoveEmptyDirectories(analysis, target, token)
                : 0;

            var result = new TargetCleaningResult(target.Key, target.AppName, target.CategoryName, target.ProfileName,
                counters.Deleted, counters.DeletedBytes, counters.Skipped, counters.Locked, counters.Failed,
                counters.Unconfirmed, counters.Cancelled, counters.Protected, details, counters.DetailsOmitted)
            { Reasons = reasons, EmptyDirectoriesRemoved = emptyDirectories };
            results.Add(result);
            journal.TargetFinished(result);
            progress?.Report(new(t + 1, session.Targets.Count, processed, totalFiles, deletedBytes, label));
        }

        var finished = _clock();
        var cancelled = token.IsCancellationRequested;
        var report = new CleaningReport(session.Id, started, finished, results, deletedFiles, deletedBytes,
            results.Sum(r => r.NotDeleted), cancelled, null, CleaningReport.LogicalBytesWarning, journal.Path);
        journal.Finished(report);
        Totals.Add(deletedBytes, deletedFiles, started);
        _log.Write("limpieza", FormattableString.Invariant(
            $"sesión {session.Id:N}: {deletedFiles} eliminados ({deletedBytes} bytes), {report.NotDeleted} no eliminados, {report.EmptyDirectoriesRemoved} carpetas vacías retiradas, cancelado={cancelled}, {(finished - started).TotalMilliseconds:F0} ms"));
        foreach (var reason in report.Reasons())
            _log.Write("limpieza", FormattableString.Invariant($"sesión {session.Id:N}: {reason.Value} × {reason.Key}"));
        return report;
    }

    /// <summary>
    /// Retira las subcarpetas vacías conocidas por el análisis, de la más profunda a la más superficial.
    /// Nunca la raíz del objetivo, nunca una carpeta protegida o excluida y nunca a través de un enlace:
    /// el padre se fija antes de tocar a cada hija, y la hija se valida sobre su propio handle.
    /// </summary>
    private int RemoveEmptyDirectories(TargetAnalysis analysis, CleanupTarget target, CancellationToken token)
    {
        var removed = 0;
        var directories = analysis.Directories;
        using var pins = new PinnedDirectoryChain();
        // En el análisis una carpeta siempre aparece después de su padre: al recorrer al revés, las hijas van primero.
        for (var i = directories.Length - 1; i >= 0 && !token.IsCancellationRequested; i--)
        {
            var directory = directories[i];
            var root = target.Roots[directory.RootIndex];
            if (directory.Sensitive || !PathSafety.IsStrictlyWithin(directory.Path, root.Path) ||
                CleaningGuard.IsSensitiveDirectory(directory.Path, target.Anchor, root.Policy)) continue;
            var parent = Path.GetDirectoryName(directory.Path);
            if (parent is null || pins.Pin(parent) is not null) continue;
            if (NativeDeletion.TryRemoveEmptyDirectory(directory.Path, root.Policy.MinimumAge, _clock())) removed++;
        }
        return removed;
    }

    private CleaningReport Refuse(AnalysisSession session, DateTime now, string reason)
    {
        _log.Write("limpieza", "sesión " + session.Id.ToString("N") + " rechazada: " + reason);
        return CleaningReport.Refuse(session.Id, now, reason);
    }

    private struct Counters
    {
        public int Deleted, Skipped, Locked, Failed, Unconfirmed, Cancelled, Protected, DetailsOmitted;
        public long DeletedBytes;
    }

    /// <summary>
    /// Bitácora JSONL por lote. La cabecera y el inicio de cada objetivo se escriben a disco antes de
    /// eliminar nada, de modo que un cierre inesperado deja constancia de qué se estaba limpiando.
    /// </summary>
    private sealed class Journal : IDisposable
    {
        private readonly StreamWriter? _writer;
        private readonly DiagnosticsLog _log;

        private Journal(StreamWriter? writer, string? path, DiagnosticsLog log)
        {
            _writer = writer;
            Path = path;
            _log = log;
        }

        public string? Path { get; }

        public static Journal Open(string directory, AnalysisSession session, DateTime started, DiagnosticsLog log)
        {
            try
            {
                Directory.CreateDirectory(directory);
                foreach (var old in new DirectoryInfo(directory).GetFiles("limpieza-*.jsonl")
                             .OrderByDescending(f => f.Name, StringComparer.Ordinal).Skip(JournalsKept - 1))
                    old.Delete();
                var path = System.IO.Path.Combine(directory, "limpieza-" +
                    started.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + session.Id.ToString("N")[..8] + ".jsonl");
                var writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
                var journal = new Journal(writer, path, log);
                journal.Line(new
                {
                    evento = "inicio", sesion = session.Id, inicioUtc = started, analisisUtc = session.CreatedUtc,
                    seleccion = session.Selection.Fingerprint, objetivos = session.Targets.Count,
                    archivos = session.EligibleFiles, bytes = session.EligibleBytes,
                });
                return journal;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                log.Write("limpieza", "Bitácora no disponible: " + ex.Message);
                return new Journal(null, null, log);
            }
        }

        public void TargetStarted(TargetKey key, int files, long bytes, string? refusal) =>
            Line(new { evento = "objetivo-inicio", objetivo = key.ToString(), archivos = files, bytes, rechazo = refusal });

        public void TargetFinished(TargetCleaningResult result) => Line(new
        {
            evento = "objetivo-fin", objetivo = result.Key.ToString(), eliminados = result.Deleted, bytes = result.DeletedBytes,
            omitidos = result.Skipped, bloqueados = result.Locked, errores = result.Failed, noConfirmados = result.Unconfirmed,
            cancelados = result.Cancelled, protegidos = result.Protected, carpetasVaciasRetiradas = result.EmptyDirectoriesRemoved,
            motivos = result.Reasons.OrderByDescending(p => p.Value).Select(p => new { motivo = p.Key, archivos = p.Value }),
            detalle = result.Details.Select(d => new { ruta = d.Path, estado = d.Status.ToString(), motivo = d.Detail }),
            detalleOmitido = result.DetailsOmitted,
        });

        public void Finished(CleaningReport report) => Line(new
        {
            evento = "fin", finUtc = report.FinishedUtc, eliminados = report.DeletedFiles, bytes = report.DeletedBytes,
            noEliminados = report.NotDeleted, carpetasVaciasRetiradas = report.EmptyDirectoriesRemoved, cancelado = report.Cancelled,
            motivos = report.Reasons().Select(p => new { motivo = p.Key, archivos = p.Value }),
        });

        public void Dispose() => _writer?.Dispose();

        private void Line(object value)
        {
            if (_writer is null) return;
            try
            {
                _writer.WriteLine(JsonSerializer.Serialize(value));
                _writer.Flush();
                ((FileStream)_writer.BaseStream).Flush(flushToDisk: true);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                _log.Write("limpieza", "Bitácora interrumpida: " + ex.Message);
            }
        }
    }
}
