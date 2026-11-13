namespace FFCleaner.Benchmarks.Legacy;

/// <summary>
/// Copia congelada del escáner 0.1.0 usada solo como línea base del benchmark. La lógica es la original;
/// se añadieron acumuladores de tiempo y el foreach de enumeración se escribió con MoveNext explícito
/// para poder medirlo.
/// </summary>
public sealed class StorageScanner
{
    public long EnumerationTicks, PathCheckTicks, MetadataTicks;
    public long PathCheckCalls, MetadataCalls;

    public ScanResult Scan(IEnumerable<ScanRoot> roots,
        IReadOnlyCollection<string>? exclusions = null, CancellationToken token = default,
        IProgress<ScanProgress>? progress = null, int maxFiles = 500000)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxFiles);
        var limited = false;
        var files = new List<FileRecord>();
        var issues = new List<ScanIssue>();
        var normalizedRoots = new List<ScanRoot>();
        foreach (var root in roots)
        {
            if (token.IsCancellationRequested) goto Finished;
            try { normalizedRoots.Add(root with { Path = PathSafety.Normalize(root.Path) }); }
            catch (ArgumentException ex) { issues.Add(new(root.Id, root.Path, "InvalidRoot", ex.Message)); }
        }
        var excluded = (exclusions ?? []).Select(PathSafety.Normalize).ToArray();
        var covered = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in normalizedRoots.OrderBy(r => r.Path.Length).ThenBy(r => r.CanSelect))
        {
            if (token.IsCancellationRequested) goto Finished;
            if (covered.Any(p => PathSafety.IsWithin(root.Path, p))) continue;
            covered.Add(root.Path);
            var stack = new Stack<string>();
            stack.Push(root.Path);
            while (stack.TryPop(out var directory))
            {
                if (token.IsCancellationRequested) goto Finished;
                if (excluded.Any(p => PathSafety.IsWithin(directory, p))) continue;
                try
                {
                    var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    PathSafety.EnsureSafe(directory);
                    PathCheckTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0; PathCheckCalls++;
                    using var entries = Directory.EnumerateFileSystemEntries(directory).GetEnumerator();
                    while (true)
                    {
                        t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                        var more = entries.MoveNext();
                        EnumerationTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                        if (!more) break;
                        var path = entries.Current;
                        if (token.IsCancellationRequested) goto Finished;
                        if (excluded.Any(p => PathSafety.IsWithin(path, p))) continue;
                        try
                        {
                            t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                            PathSafety.EnsureSafe(path);
                            var isDirectory = (File.GetAttributes(path) & FileAttributes.Directory) != 0;
                            PathCheckTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0; PathCheckCalls++;
                            if (isDirectory) { stack.Push(path); continue; }
                            if (!seen.Add(path)) continue;
                            if (files.Count >= maxFiles)
                            {
                                limited = true;
                                issues.Add(new(root.Id, path, "LimitReached", "Se alcanzó el límite; inventario incompleto."));
                                goto Finished;
                            }
                            t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                            var info = NativeMetadata.Read(path);
                            if (PathSafety.UnsafeAttributes((FileAttributes)info.Attributes) ||
                                ((FileAttributes)info.Attributes & FileAttributes.Directory) != 0)
                                throw new UnsafePathException(path);
                            var eligibility = Eligibility.Evaluate(root, path);
                            files.Add(new(Guid.NewGuid().ToString("N"), root.Id, path, info.Length,
                                info.Allocated, info.Identity, info.Links,
                                info.LastWriteUtc, info.LastAccessUtc, eligibility.Eligible, eligibility.Reason));
                            MetadataTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0; MetadataCalls++;
                            progress?.Report(new(files.Count, path));
                        }
                        catch (UnsafePathException ex) { issues.Add(new(root.Id, path, "UnsafePath", ex.Message)); }
                        catch (Exception ex) when (PathSafety.IsIoFailure(ex)) { issues.Add(new(root.Id, path, "Unavailable", ex.Message)); }
                    }
                }
                catch (UnsafePathException ex) { issues.Add(new(root.Id, directory, "UnsafePath", ex.Message)); }
                catch (Exception ex) when (PathSafety.IsIoFailure(ex)) { issues.Add(new(root.Id, directory, "Unavailable", ex.Message)); }
            }
        }
    Finished:
        if (token.IsCancellationRequested)
            issues.Add(new("", "", "Cancelled", "Análisis cancelado cooperativamente; inventario incompleto."));
        return new(Guid.NewGuid(), DateTime.UtcNow, files, issues, token.IsCancellationRequested, limited);
    }
}
