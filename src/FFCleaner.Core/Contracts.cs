namespace FFCleaner.Core;

// ───────────────────────────── Catálogo ─────────────────────────────

public enum AppGroup { Navegadores, Sistema, InteligenciaArtificial, Graficos, Desarrollo }

/// <summary>Nivel de atención que merece una categoría; la interfaz lo muestra como etiqueta.</summary>
public enum CategoryRisk { Normal, DatosPersonales, MuyGrande, Administrador }

/// <summary>Clave estable de un objetivo: aplicación → categoría → perfil. No depende de la ruta visible.</summary>
public readonly record struct TargetKey(string AppId, string CategoryId, string ProfileId)
{
    public override string ToString() => AppId + "/" + CategoryId + "/" + ProfileId;

    public static bool TryParse(string? text, out TargetKey key)
    {
        key = default;
        var parts = (text ?? string.Empty).Split('/');
        if (parts.Length != 3 || parts.Any(string.IsNullOrWhiteSpace)) return false;
        key = new(parts[0], parts[1], parts[2]);
        return true;
    }
}

/// <summary>Política por categoría: antigüedad mínima y, si procede, lista blanca de extensiones.</summary>
/// <param name="VerifyLinks">
/// Consultar al analizar cuántos enlaces físicos tiene cada archivo. Cuesta una apertura por archivo, así que
/// solo se activa en cachés que comparten archivos con otras carpetas (uv con sus entornos virtuales).
/// </param>
/// <param name="NameGuards">
/// Aplicar las reglas de nombres de datos personales (cookies, token*, session*, carpetas «Network», «models»…).
/// Se desactivan solo en cachés de paquetes (pip, npm, uv), donde esos nombres son código de librerías; las
/// protecciones por extensión (.pem, .key, bases de datos…) y las estructurales se mantienen siempre.
/// </param>
public sealed record CleaningPolicy(TimeSpan MinimumAge, IReadOnlySet<string>? AllowedExtensions, bool VerifyLinks = false,
    bool NameGuards = true)
{
    /// <summary>Extensiones protegidas que esta categoría sí puede borrar (p. ej. «.gguf» en los modelos de IA).</summary>
    public IReadOnlySet<string>? ExemptExtensions { get; init; }

    /// <summary>Si se indica, solo se consideran los archivos cuyo nombre empieza así (p. ej. «thumbcache_»).</summary>
    public string? NamePrefix { get; init; }
}

/// <summary>Directorio hoja autorizado para una categoría (p. ej. «Default\Cache»).</summary>
public sealed record CleanupRoot(string Path, string Leaf, CleaningPolicy Policy);

/// <summary>
/// Objetivo concreto de limpieza. <see cref="DataRoot"/> es la raíz de datos del navegador
/// («User Data»); cuando existe, las guardas exigen que cada raíz sea una hoja de caché conocida
/// dentro del perfil indicado. <see cref="Anchor"/> es la base desde la que se evalúan nombres sensibles.
/// </summary>
public sealed record CleanupTarget(TargetKey Key, string AppName, string CategoryName, string ProfileName,
    IReadOnlyList<CleanupRoot> Roots, string? DataRoot, string Anchor)
{
    /// <summary>Si no es null, el objetivo es un tipo de dato de navegador dentro de sus bases, no archivos sueltos.</summary>
    public BrowserDataKind? Data { get; init; }

    /// <summary>Solo se puede analizar y limpiar ejecutando FFCleaner como administrador.</summary>
    public bool RequiresAdmin { get; init; }

    /// <summary>Procesos de la aplicación; los datos de navegador solo se tocan con ellos cerrados.</summary>
    public IReadOnlyList<string> ProcessNames { get; init; } = [];
}

public sealed record CleanupCategory(string Id, string Name, string Description, IReadOnlyList<CleanupTarget> Targets)
{
    public CategoryRisk Risk { get; init; }
}

public sealed record CleanupApp(string Id, string Name, AppGroup Group, IReadOnlyList<string> ProcessNames,
    IReadOnlyList<CleanupCategory> Categories);

public sealed record CleanupCatalog(string Version, IReadOnlyList<CleanupApp> Apps, double DiscoveryMs,
    IReadOnlyList<string> Notes)
{
    public IEnumerable<CleanupTarget> AllTargets() =>
        Apps.SelectMany(a => a.Categories).SelectMany(c => c.Targets);
}

// ───────────────────────────── Análisis ─────────────────────────────

public enum FileVerdict : byte
{
    Eligible = 0,
    SensitiveName,
    SensitiveDirectory,
    ProtectedExtension,
    DatabaseSidecar,
    ExtensionNotAllowed,
    TooRecent,
    Excluded,
    MultipleLinks,
    ProtectedAttributes,
}

public sealed record ScanIssue(string Target, string Path, string Code, string Message);

/// <summary>Tiempos instrumentados por fase, en milisegundos.</summary>
public sealed record PhaseTimings(double DiscoveryMs, double TraversalMs, double MetadataMs, double UiMs)
{
    public double TotalMs => DiscoveryMs + TraversalMs + MetadataMs + UiMs;
    public override string ToString() => FormattableString.Invariant(
        $"descubrimiento {DiscoveryMs:F1} ms · recorrido {TraversalMs:F1} ms · metadatos {MetadataMs:F1} ms · interfaz {UiMs:F1} ms");
}

public sealed record AnalysisProgress(int TargetsDone, int TargetsTotal, long Files, long Bytes, string Current);

public interface IAnalysisService
{
    /// <summary>Descubre aplicaciones, categorías y perfiles presentes. Solo lectura.</summary>
    CleanupCatalog Discover();

    /// <summary>
    /// Analiza exactamente la selección indicada. Solo lectura. Una cancelación devuelve una sesión
    /// marcada como cancelada (no limpiable), nunca resultados parciales autorizables.
    /// </summary>
    Task<AnalysisSession> AnalyzeAsync(SelectionSnapshot selection, IProgress<AnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

// ───────────────────────────── Limpieza ─────────────────────────────

public enum CleanStatus : byte { Eliminado, Omitido, Bloqueado, Error, NoConfirmado, Cancelado }

public sealed record CleaningItemDetail(string Path, CleanStatus Status, string Detail);

public sealed record TargetCleaningResult(TargetKey Key, string AppName, string CategoryName, string ProfileName,
    int Deleted, long DeletedBytes, int Skipped, int Locked, int Failed, int Unconfirmed, int Cancelled,
    int Protected, IReadOnlyList<CleaningItemDetail> Details, int DetailsOmitted)
{
    public int NotDeleted => Skipped + Locked + Failed + Unconfirmed + Cancelled;

    /// <summary>
    /// Recuento completo de lo no eliminado, por motivo. A diferencia de <see cref="Details"/> no tiene tope:
    /// la suma de sus valores es siempre <see cref="NotDeleted"/>.
    /// </summary>
    public IReadOnlyDictionary<string, int> Reasons { get; init; } = new Dictionary<string, int>();

    /// <summary>Subcarpetas que quedaron vacías bajo las raíces del objetivo y se retiraron.</summary>
    public int EmptyDirectoriesRemoved { get; init; }
}

public sealed record CleaningProgress(int TargetsDone, int TargetsTotal, long FilesProcessed, long FilesTotal,
    long DeletedBytes, string Current);

public sealed record CleaningReport(Guid SessionId, DateTime StartedUtc, DateTime FinishedUtc,
    IReadOnlyList<TargetCleaningResult> Targets, int DeletedFiles, long DeletedBytes, int NotDeleted,
    bool Cancelled, string? Refusal, string Warning, string? JournalPath)
{
    public const string LogicalBytesWarning =
        "Eliminación permanente. Los bytes indicados son lógicos; el espacio físico recuperado puede diferir.";

    /// <summary>True cuando el lote completo fue rechazado antes de tocar ningún archivo.</summary>
    public bool Refused => Refusal is not null;

    public int EmptyDirectoriesRemoved => Targets.Sum(t => t.EmptyDirectoriesRemoved);

    /// <summary>Motivos de lo no eliminado en todo el lote, de mayor a menor.</summary>
    public IReadOnlyList<KeyValuePair<string, int>> Reasons() =>
        [.. Targets.SelectMany(t => t.Reasons).GroupBy(p => p.Key, StringComparer.Ordinal)
            .Select(g => KeyValuePair.Create(g.Key, g.Sum(p => p.Value))).OrderByDescending(p => p.Value)];

    public static CleaningReport Refuse(Guid sessionId, DateTime nowUtc, string reason) =>
        new(sessionId, nowUtc, nowUtc, [], 0, 0, 0, false, reason, LogicalBytesWarning, null);
}

public interface IFileCleaningService
{
    /// <summary>
    /// Elimina únicamente los archivos elegibles de <paramref name="session"/>, y solo si la selección
    /// vigente coincide con la analizada. Nunca lanza por fallos parciales: los reporta por elemento.
    /// </summary>
    Task<CleaningReport> CleanAsync(AnalysisSession session, SelectionSnapshot currentSelection,
        IProgress<CleaningProgress>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>Consulta de procesos en ejecución; permite avisar de navegadores abiertos sin cerrarlos.</summary>
public interface IProcessProbe
{
    bool IsRunning(string processName);
}

// ───────────────────────────── Ajustes ─────────────────────────────

/// <summary>
/// Ajustes persistentes. <see cref="Exclusions"/> conserva las rutas protegidas de versiones anteriores:
/// ya no hay página para editarlas, pero el análisis las sigue respetando.
/// </summary>
public sealed record UserSettings(List<string> SelectedTargets, List<string> Exclusions, List<string> RegistryFamilies)
{
    public static UserSettings Empty => new([], [], []);

    /// <summary>Tema de la interfaz: «system» (el de Windows), «light» o «dark».</summary>
    public string Theme { get; init; } = "system";

    /// <summary>Idioma de la interfaz: «es» o «en».</summary>
    public string Language { get; init; } = "es";

    /// <summary>Buscar e instalar actualizaciones al abrir FFCleaner.</summary>
    public bool AutoUpdate { get; init; } = true;
}
