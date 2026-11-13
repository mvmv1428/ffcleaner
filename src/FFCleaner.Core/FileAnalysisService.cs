using System.Buffers;
using System.Diagnostics;

namespace FFCleaner.Core;

/// <summary>
/// Análisis de solo lectura de la selección. Recorre cada raíz autorizada una vez, obteniendo los
/// metadatos de la propia enumeración del directorio, e informa del progreso por lotes.
/// </summary>
public sealed class FileAnalysisService : IAnalysisService
{
    private const int ProgressIntervalMs = 100;
    private const int MaxLoggedIssues = 50;

    /// <summary>ReadOnly | System | Encrypted: los mismos atributos que el borrado por handle se niega a tocar.</summary>
    private const uint ProtectedAttributeMask = 0x00000001 | 0x00000004 | 0x00004000;
    private readonly IFolderProvider _folders;
    private readonly string[] _protectedPaths;
    private readonly int _maxFiles;
    private readonly DiagnosticsLog _log;
    private readonly Func<DateTime> _clock;
    private double _lastDiscoveryMs;

    /// <param name="protectedPaths">Rutas excluidas por el usuario en versiones anteriores; se siguen respetando.</param>
    public FileAnalysisService(IFolderProvider folders, IEnumerable<string>? protectedPaths = null,
        int maxFiles = 1_000_000, DiagnosticsLog? log = null, Func<DateTime>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFiles);
        _folders = folders;
        _maxFiles = maxFiles;
        _log = log ?? DiagnosticsLog.Null;
        _clock = clock ?? (() => DateTime.UtcNow);
        var normalized = new List<string>();
        foreach (var path in protectedPaths ?? [])
        {
            try { normalized.Add(PathSafety.Normalize(path)); }
            catch (ArgumentException) { _log.Write("analisis", "Exclusión guardada ignorada por no ser una ruta local: " + path); }
        }
        _protectedPaths = [.. normalized];
    }

    public CleanupCatalog Discover()
    {
        var catalog = CleanupCatalogBuilder.Discover(_folders);
        _lastDiscoveryMs = catalog.DiscoveryMs;
        _log.Write("descubrimiento", FormattableString.Invariant(
            $"{catalog.Apps.Count} aplicaciones, {catalog.AllTargets().Count()} objetivos en {catalog.DiscoveryMs:F1} ms; {catalog.Notes.Count} notas"));
        return catalog;
    }

    public Task<AnalysisSession> AnalyzeAsync(SelectionSnapshot selection, IProgress<AnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return Task.Run(() => Analyze(selection, progress, cancellationToken), CancellationToken.None);
    }

    private AnalysisSession Analyze(SelectionSnapshot selection, IProgress<AnalysisProgress>? progress, CancellationToken token)
    {
        var now = _clock();
        var state = new RunState(token, progress, _maxFiles, selection.Targets.Count);
        var buffer = ArrayPool<byte>.Shared.Rent(NativeDirectory.BufferSize);
        var builders = new List<TargetBuilder>(selection.Targets.Count);
        try
        {
            foreach (var target in selection.Targets)
            {
                if (state.Stop) break;
                state.Current = Label(target);
                builders.Add(AnalyzeTarget(target, state, buffer, now));
                state.TargetsDone++;
                state.Report(force: true);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        // Se congela al final: un enlace físico entre dos objetivos puede reclasificar un archivo ya visto.
        var results = new List<TargetAnalysis>(builders.Count);
        var issues = new List<ScanIssue>();
        foreach (var builder in builders)
        {
            results.Add(new(builder.Target, [.. builder.Directories], [.. builder.Files], builder.Volumes, builder.Issues, builder.Refusal)
                { DataItems = builder.DataItems, DataBytes = builder.DataBytes });
            issues.AddRange(builder.Issues);
        }

        var cancelled = token.IsCancellationRequested;
        if (cancelled) issues.Add(new("", "", "Cancelled", "Análisis cancelado; resultado incompleto y no limpiable."));
        if (state.Limited) issues.Add(new("", "", "LimitReached", "Se alcanzó el límite de archivos; resultado incompleto y no limpiable."));
        var timings = new PhaseTimings(_lastDiscoveryMs, Ms(state.TraversalTicks), Ms(state.MetadataTicks), 0);
        var session = new AnalysisSession(selection, CleanupCatalogBuilder.Version, results, issues, cancelled, state.Limited, timings, now);
        _log.Write("analisis", FormattableString.Invariant(
            $"{results.Count} objetivos, {session.TotalFiles} archivos ({session.EligibleFiles} limpiables, {session.EligibleBytes} bytes), {issues.Count} incidencias; {timings}; cancelado={cancelled} limitado={state.Limited}"));
        // Cada incidencia queda escrita con su ruta y su motivo, no solo contada.
        foreach (var issue in issues.Take(MaxLoggedIssues))
            _log.Write("analisis", "incidencia " + issue.Code + " · " + (issue.Target.Length == 0 ? "" : issue.Target + " · ") + issue.Path + " · " + issue.Message);
        if (issues.Count > MaxLoggedIssues) _log.Write("analisis", (issues.Count - MaxLoggedIssues) + " incidencias más no registradas.");
        return session;
    }

    private TargetBuilder AnalyzeTarget(CleanupTarget target, RunState state, byte[] buffer, DateTime now)
    {
        var label = target.Key.ToString();
        var builder = new TargetBuilder(target);
        var issues = builder.Issues;
        if (CleaningGuard.ValidateTarget(target, _folders) is { } refusal)
        {
            issues.Add(new(label, target.Roots.Count > 0 ? target.Roots[0].Path : "", "TargetRefused", refusal));
            builder.Refusal = refusal;
            return builder;
        }
        if (target.RequiresAdmin && !_folders.IsElevated)
        {
            builder.Refusal = "Requiere administrador: usa «Reiniciar como administrador» en la sección Registro.";
            issues.Add(new(label, target.Roots[0].Path, "RequiresAdmin", builder.Refusal));
            return builder;
        }
        if (target.Data is not null)
        {
            var start = Stopwatch.GetTimestamp();
            var inspection = BrowserData.Inspect(target);
            state.MetadataTicks += Stopwatch.GetTimestamp() - start;
            builder.DataItems = inspection.Items;
            builder.DataBytes = inspection.Bytes;
            if (inspection.Problem is not null) issues.Add(new(label, target.Roots[0].Path, "Locked", inspection.Problem));
            return builder;
        }

        var directories = builder.Directories;
        var files = builder.Files;
        var volumes = builder.Volumes;
        var pending = new Stack<int>();
        for (var rootIndex = 0; rootIndex < target.Roots.Count && !state.Stop; rootIndex++)
        {
            var root = target.Roots[rootIndex];
            try
            {
                // Una única verificación de la cadena de ancestros por raíz; nunca por archivo.
                PathSafety.EnsureSafe(root.Path);
                volumes[rootIndex] = NativeDirectory.QueryVolume(root.Path);
            }
            catch (Exception ex) when (PathSafety.IsIoFailure(ex))
            {
                issues.Add(new(label, root.Path, ex is UnsafePathException ? "UnsafePath" : "Unavailable", ex.Message));
                continue;
            }
            directories.Add(new(root.Path, rootIndex, CleaningGuard.IsSensitiveDirectory(root.Path, target.Anchor, root.Policy)));
            pending.Push(directories.Count - 1);
            while (pending.TryPop(out var directoryIndex) && !state.Stop)
                ReadDirectory(target, label, root, rootIndex, volumes[rootIndex], directoryIndex, directories, files, pending, issues, state, buffer, now);
            pending.Clear();
        }
        if (!state.Stop) VerifyLinks(target, builder, state);
        return builder;
    }

    /// <summary>
    /// En las categorías que lo piden, consulta cuántos enlaces físicos tiene cada archivo limpiable y aparta
    /// los compartidos con carpetas ajenas al análisis (p. ej. un entorno virtual): borrarlos no liberaría
    /// espacio y la limpieza los conservaría de todos modos. Es una apertura de solo metadatos por archivo,
    /// así que se reparte entre varios hilos.
    /// </summary>
    private static void VerifyLinks(CleanupTarget target, TargetBuilder builder, RunState state)
    {
        if (!target.Roots.Any(r => r.Policy.VerifyLinks)) return;
        var start = Stopwatch.GetTimestamp();
        var files = builder.Files;
        var directories = builder.Directories;
        var shared = new bool[files.Count];
        try
        {
            Parallel.For(0, files.Count,
                new ParallelOptions { CancellationToken = state.Token, MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, 8) },
                i =>
                {
                    var file = files[i];
                    if (file.Verdict != FileVerdict.Eligible) return;
                    var directory = directories[file.Directory];
                    if (!target.Roots[directory.RootIndex].Policy.VerifyLinks) return;
                    try { shared[i] = NativeMetadata.LinkCount(Path.Join(directory.Path, file.Name)) != 1; }
                    catch (System.ComponentModel.Win32Exception) { /* inaccesible ahora: lo decidirá la limpieza sobre el handle */ }
                });
        }
        catch (OperationCanceledException) { /* la sesión quedará marcada como cancelada */ }
        for (var i = 0; i < shared.Length; i++)
            if (shared[i]) files[i] = files[i].With(FileVerdict.MultipleLinks);
        state.MetadataTicks += Stopwatch.GetTimestamp() - start;
    }

    private void ReadDirectory(CleanupTarget target, string label, CleanupRoot root, int rootIndex, VolumeIdentity volume,
        int directoryIndex, List<AnalysisDirectory> directories, List<FileEntry> files, Stack<int> pending,
        List<ScanIssue> issues, RunState state, byte[] buffer, DateTime now)
    {
        var directory = directories[directoryIndex];
        if (IsProtected(directory.Path))
        {
            issues.Add(new(label, directory.Path, "Excluded", "Carpeta protegida por una exclusión guardada."));
            directories[directoryIndex] = directory with { Sensitive = true };   // tampoco se retirará si está vacía
            return;
        }
        // Solo se comprueban exclusiones por archivo si alguna apunta dentro de este directorio.
        var checkFiles = false;
        foreach (var path in _protectedPaths)
            if (PathSafety.IsStrictlyWithin(path, directory.Path)) { checkFiles = true; break; }

        var start = Stopwatch.GetTimestamp();
        NativeDirectory reader;
        try
        {
            reader = NativeDirectory.Open(directory.Path, buffer, volume.Extended);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            state.TraversalTicks += Stopwatch.GetTimestamp() - start;
            issues.Add(new(label, directory.Path, "Unavailable", ex.Message));
            return;
        }
        using (reader)
        {
            // El directorio se valida sobre el handle abierto: si lo sustituyeron por un enlace, se descarta.
            if (PathSafety.UnsafeAttributes(reader.Attributes) || (reader.Attributes & 0x10) == 0)
            {
                state.TraversalTicks += Stopwatch.GetTimestamp() - start;
                issues.Add(new(label, directory.Path, "UnsafePath", "Enlace, nube o dispositivo: no se recorre."));
                return;
            }
            try
            {
                while (true)
                {
                    var more = reader.MoveNext(out var entry);
                    var afterRead = Stopwatch.GetTimestamp();
                    state.TraversalTicks += afterRead - start;
                    if (!more) break;

                    if (PathSafety.UnsafeAttributes(entry.Attributes))
                    {
                        issues.Add(new(label, Path.Join(directory.Path, entry.Name), "UnsafePath", "Enlace, nube o dispositivo: no se recorre."));
                    }
                    else if (entry.IsDirectory)
                    {
                        var path = Path.Join(directory.Path, entry.Name);
                        directories.Add(new(path, rootIndex, directory.Sensitive || (root.Policy.NameGuards && CleaningGuard.IsSensitiveComponent(entry.Name.ToString()))));
                        pending.Push(directories.Count - 1);
                    }
                    else if (state.Files >= state.MaxFiles)
                    {
                        state.Limited = true;
                    }
                    else
                    {
                        var name = entry.Name.ToString();
                        var verdict = directory.Sensitive
                            ? FileVerdict.SensitiveDirectory
                            : CleaningGuard.CheckFile(root.Policy, name, FileEntry.ToUtc(entry.LastWriteFileTime), now);
                        // Solo lectura, sistema o cifrado: la limpieza los conservaría; se sabe ya, sin coste, por la enumeración.
                        if (verdict == FileVerdict.Eligible && (entry.Attributes & ProtectedAttributeMask) != 0) verdict = FileVerdict.ProtectedAttributes;
                        if (checkFiles && IsProtected(Path.Join(directory.Path, name))) verdict = FileVerdict.Excluded;
                        var known = (entry.IdLow | entry.IdHigh) != 0;
                        if (known && !state.Identities.TryAdd(new(volume.Serial, entry.IdLow, entry.IdHigh), (files, files.Count)))
                        {
                            // Misma identidad vista dos veces: enlaces físicos. No se cuentan dos veces ni se limpian.
                            var (list, index) = state.Identities[new(volume.Serial, entry.IdLow, entry.IdHigh)];
                            if (list[index].Verdict == FileVerdict.Eligible) list[index] = list[index].With(FileVerdict.MultipleLinks);
                            verdict = FileVerdict.MultipleLinks;
                            files.Add(new(directoryIndex, name, 0, 0, entry.LastWriteFileTime, entry.IdLow, entry.IdHigh, verdict));
                        }
                        else
                        {
                            files.Add(new(directoryIndex, name, entry.Length, entry.Allocated, entry.LastWriteFileTime, entry.IdLow, entry.IdHigh, verdict));
                        }
                        state.Files++;
                        state.Bytes += entry.Length;
                    }

                    start = Stopwatch.GetTimestamp();
                    state.MetadataTicks += start - afterRead;
                    if (state.Stop) break;
                }
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                issues.Add(new(label, directory.Path, "Unavailable", ex.Message));
            }
        }
        state.Report(force: false);
    }

    private bool IsProtected(string path)
    {
        foreach (var excluded in _protectedPaths)
            if (PathSafety.IsWithin(path, excluded)) return true;
        return false;
    }

    private static string Label(CleanupTarget target) =>
        target.ProfileName.Length == 0 ? target.AppName + " · " + target.CategoryName
            : target.AppName + " · " + target.CategoryName + " · " + target.ProfileName;

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    private readonly record struct FileIdentity(ulong Volume, ulong Low, ulong High);

    private sealed class TargetBuilder(CleanupTarget target)
    {
        public readonly CleanupTarget Target = target;
        public readonly List<AnalysisDirectory> Directories = [];
        public readonly List<FileEntry> Files = [];
        public readonly VolumeIdentity[] Volumes = new VolumeIdentity[target.Roots.Count];
        public readonly List<ScanIssue> Issues = [];
        public string? Refusal;
        public long DataItems, DataBytes;
    }

    private sealed class RunState(CancellationToken token, IProgress<AnalysisProgress>? progress, int maxFiles, int targetsTotal)
    {
        private long _lastReport;
        public CancellationToken Token => token;
        public readonly Dictionary<FileIdentity, (List<FileEntry> List, int Index)> Identities = [];
        public readonly int MaxFiles = maxFiles;
        public long TraversalTicks, MetadataTicks, Files, Bytes;
        public int TargetsDone;
        public bool Limited;
        public string Current = string.Empty;
        public bool Stop => Limited || token.IsCancellationRequested;

        /// <summary>Progreso por lotes: como máximo una notificación cada 100 ms, nunca una por archivo.</summary>
        public void Report(bool force)
        {
            if (progress is null) return;
            var tick = Environment.TickCount64;
            if (!force && tick - _lastReport < ProgressIntervalMs) return;
            _lastReport = tick;
            progress.Report(new(TargetsDone, targetsTotal, Files, Bytes, Current));
        }
    }
}
