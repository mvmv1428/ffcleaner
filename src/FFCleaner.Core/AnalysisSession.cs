namespace FFCleaner.Core;

/// <summary>
/// Entrada compacta de archivo: un índice de directorio y el nombre, en lugar de una ruta completa por
/// archivo. La ruta se compone bajo demanda.
/// </summary>
public readonly struct FileEntry(int directory, string name, long length, long allocated, long lastWriteFileTime,
    ulong idLow, ulong idHigh, FileVerdict verdict)
{
    public readonly int Directory = directory;
    public readonly string Name = name;
    public readonly long Length = length;
    public readonly long Allocated = allocated;
    public readonly long LastWriteFileTime = lastWriteFileTime;
    public readonly ulong IdLow = idLow;
    public readonly ulong IdHigh = idHigh;
    public readonly FileVerdict Verdict = verdict;

    public DateTime LastWriteUtc => ToUtc(LastWriteFileTime);

    /// <summary>
    /// Fecha de un FILETIME sin lanzar por valores corruptos. Uno fuera de rango se trata como «ahora o
    /// después», que es la lectura conservadora para cualquier política de antigüedad.
    /// </summary>
    public static DateTime ToUtc(long fileTime) =>
        fileTime is < 0 or > 2650467743999999999 ? DateTime.MaxValue : DateTime.FromFileTimeUtc(fileTime);

    internal FileEntry With(FileVerdict verdict) =>
        new(Directory, Name, Length, Allocated, LastWriteFileTime, IdLow, IdHigh, verdict);
}

/// <summary>Directorio recorrido: ruta, raíz a la que pertenece y si es zona protegida.</summary>
public readonly record struct AnalysisDirectory(string Path, int RootIndex, bool Sensitive);

/// <summary>Resultado del análisis de un objetivo (aplicación → categoría → perfil).</summary>
public sealed class TargetAnalysis
{
    private readonly FileEntry[] _files;
    private readonly AnalysisDirectory[] _directories;

    internal TargetAnalysis(CleanupTarget target, AnalysisDirectory[] directories, FileEntry[] files,
        VolumeIdentity[] rootVolumes, IReadOnlyList<ScanIssue> issues, string? refusal)
    {
        Target = target;
        _directories = directories;
        _files = files;
        RootVolumes = rootVolumes;
        Issues = issues;
        Refusal = refusal;
        var verdicts = new Dictionary<FileVerdict, int>();
        foreach (ref readonly var file in files.AsSpan())
        {
            TotalBytes += file.Length;
            if (file.Verdict == FileVerdict.Eligible) { EligibleCount++; EligibleBytes += file.Length; }
            else verdicts[file.Verdict] = verdicts.GetValueOrDefault(file.Verdict) + 1;
        }
        ProtectedByVerdict = verdicts;
    }

    /// <summary>Elementos de datos de navegador (entradas de historial, cookies…) que se borrarían.</summary>
    public long DataItems { get; init; }

    /// <summary>Espacio estimado que liberaría borrar los datos de navegador (tablas afectadas y archivos de sesión).</summary>
    public long DataBytes { get; init; }

    /// <summary>Lo que se liberaría en total: archivos limpiables más la estimación de datos de navegador.</summary>
    public long FreeableBytes => EligibleBytes + DataBytes;

    /// <summary>Archivos limpiables más elementos de datos: lo que la interfaz cuenta como «elementos».</summary>
    public long EligibleItems => EligibleCount + DataItems;

    /// <summary>Cuántos archivos quedan fuera de la limpieza y por qué. Suma <see cref="ProtectedCount"/>.</summary>
    public IReadOnlyDictionary<FileVerdict, int> ProtectedByVerdict { get; }

    public CleanupTarget Target { get; }
    public ReadOnlySpan<FileEntry> Files => _files;
    public ReadOnlySpan<AnalysisDirectory> Directories => _directories;
    public IReadOnlyList<VolumeIdentity> RootVolumes { get; }
    public IReadOnlyList<ScanIssue> Issues { get; }

    /// <summary>Motivo por el que el objetivo completo no se recorrió, o null.</summary>
    public string? Refusal { get; }

    public int FileCount => _files.Length;
    public long TotalBytes { get; }
    public int EligibleCount { get; }
    public long EligibleBytes { get; }
    public int ProtectedCount => FileCount - EligibleCount;

    public FileEntry FileAt(int index) => _files[index];
    public AnalysisDirectory DirectoryAt(int index) => _directories[index];
    public string PathOf(int index) => Path.Join(_directories[_files[index].Directory].Path, _files[index].Name);
}

/// <summary>
/// Sesión de análisis inmutable. Solo la emite el servicio de análisis, queda ligada a la selección exacta
/// que la originó, caduca y se puede consumir en una única limpieza.
/// </summary>
public sealed class AnalysisSession
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    private int _consumed;

    internal AnalysisSession(SelectionSnapshot selection, string catalogVersion, IReadOnlyList<TargetAnalysis> targets,
        IReadOnlyList<ScanIssue> issues, bool cancelled, bool limited, PhaseTimings timings, DateTime createdUtc)
    {
        Selection = selection;
        CatalogVersion = catalogVersion;
        Targets = targets;
        Issues = issues;
        Cancelled = cancelled;
        Limited = limited;
        Timings = timings;
        CreatedUtc = createdUtc;
        ExpiresUtc = createdUtc + Lifetime;
        foreach (var target in targets)
        {
            TotalFiles += target.FileCount;
            TotalBytes += target.TotalBytes;
            EligibleFiles += target.EligibleItems;
            EligibleBytes += target.EligibleBytes;
            FreeableBytes += target.FreeableBytes;
        }
    }

    public Guid Id { get; } = Guid.NewGuid();
    public DateTime CreatedUtc { get; }
    public DateTime ExpiresUtc { get; }
    public SelectionSnapshot Selection { get; }
    public string CatalogVersion { get; }
    public IReadOnlyList<TargetAnalysis> Targets { get; }
    public IReadOnlyList<ScanIssue> Issues { get; }
    public bool Cancelled { get; }
    public bool Limited { get; }
    public PhaseTimings Timings { get; private set; }
    public long TotalFiles { get; }
    public long TotalBytes { get; }
    public long EligibleFiles { get; }
    public long EligibleBytes { get; }

    /// <summary>Espacio total que se liberaría, incluidos los datos de navegador marcados (estimado).</summary>
    public long FreeableBytes { get; }
    public bool Consumed => Volatile.Read(ref _consumed) != 0;

    /// <summary>Anota el tiempo de presentación medido por la interfaz.</summary>
    public void RecordUiTime(double milliseconds) => Timings = Timings with { UiMs = milliseconds };

    /// <summary>Motivo por el que esta sesión no autoriza una limpieza, o null si la autoriza.</summary>
    public string? CleanBlocker(SelectionSnapshot currentSelection, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(currentSelection);
        if (Cancelled) return "El análisis se canceló; vuelve a analizar.";
        if (Limited) return "El análisis alcanzó el límite de archivos; reduce la selección y vuelve a analizar.";
        if (Consumed) return "Este análisis ya se usó en una limpieza; vuelve a analizar.";
        if (nowUtc >= ExpiresUtc || nowUtc < CreatedUtc) return "El análisis caducó; vuelve a analizar.";
        if (!string.Equals(Selection.Fingerprint, currentSelection.Fingerprint, StringComparison.Ordinal))
            return "La selección cambió después del análisis; vuelve a analizar.";
        if (EligibleFiles == 0) return "No hay archivos que limpiar en esta selección.";
        return null;
    }

    /// <summary>Marca la sesión como usada. Solo la primera llamada devuelve true.</summary>
    internal bool TryConsume() => Interlocked.Exchange(ref _consumed, 1) == 0;
}
