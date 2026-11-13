namespace FFCleaner.RegistryAnalysis;

using System.Diagnostics;
using System.Globalization;
using System.Security;

/// <summary>
/// Análisis de solo lectura del Registro, organizado por familias. Nunca crea, escribe ni elimina claves
/// o valores, y nunca ejecuta los destinos que encuentra. Deja una traza por familia de lo que recorrió.
/// </summary>
public sealed class RegistryScanner : IRegistryAnalysisService
{
    private readonly RegistryScope _scope;
    private readonly ITargetProbe _probe;
    private readonly Action<string>? _trace;

    public RegistryScanner(RegistryScope? scope = null, ITargetProbe? probe = null, Action<string>? trace = null)
    {
        _scope = scope ?? RegistryScope.Production;
        _probe = probe ?? new FileSystemTargetProbe();
        _trace = trace;
    }

    public static IReadOnlyList<RegistryFamilyInfo> AllFamilies { get; } =
    [
        new(RegistryFamily.AppPaths, "apppaths", "Rutas de aplicaciones",
            "Entradas de «App Paths» cuyo ejecutable ya no existe."),
        new(RegistryFamily.Uninstall, "uninstall", "Desinstaladores inexistentes",
            "Programas listados para desinstalar cuyo desinstalador y carpeta han desaparecido."),
        new(RegistryFamily.FileAssociations, "fileassoc", "Asociaciones de archivos",
            "Extensiones que apuntan a tipos no registrados y programas de «Abrir con» que ya no existen."),
        new(RegistryFamily.Startup, "startup", "Programas de inicio",
            "Entradas de Run y RunOnce que apuntan a programas inexistentes."),
        new(RegistryFamily.MuiCache, "muicache", "Cachés de nombres de programas",
            "MUI Cache y asistente de compatibilidad con rutas de programas que ya no existen."),
        new(RegistryFamily.SharedDlls, "shareddlls", "DLL compartidas",
            "Contadores de DLL compartidas de archivos que ya no existen."),
    ];

    public IReadOnlyList<RegistryFamilyInfo> Families => AllFamilies;
    public string ScopeDescription => _scope.Description;
    public RegistryScope Scope => _scope;

    public Task<RegistryScanResult> ScanAsync(IReadOnlyCollection<RegistryFamily> families, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(families);
        return Task.Run(() => Scan(families, cancellationToken), CancellationToken.None);
    }

    /// <summary>Análisis síncrono. Una cancelación devuelve un resultado marcado como cancelado, no reparable.</summary>
    public RegistryScanResult Scan(IReadOnlyCollection<RegistryFamily> families, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(families);
        var ordered = families.Distinct().OrderBy(f => f).ToArray();
        var findings = new List<RegistryFinding>();
        var stats = new List<FamilyScanStats>();
        var issues = new List<string>();
        var cancelled = false;

        foreach (var family in ordered)
        {
            if (cancellationToken.IsCancellationRequested) { cancelled = true; break; }
            var context = new DetectorContext(_scope, _probe, family, cancellationToken);
            var watch = Stopwatch.StartNew();
            try
            {
                RegistryDetectors.Run(family, context);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException or System.ComponentModel.Win32Exception)
            {
                context.Issues.Add(Name(family) + ": análisis interrumpido (" + ex.GetType().Name + "); resultados parciales de esta familia.");
            }
            watch.Stop();
            if (context.Unverifiable > 0)
                context.Issues.Add(Name(family) + ": " + context.Unverifiable.ToString(CultureInfo.InvariantCulture) +
                                   " entradas en unidades no disponibles; no se concluye nada sobre ellas.");
            findings.AddRange(context.Findings);
            issues.AddRange(context.Issues);
            var stat = new FamilyScanStats(family, context.KeysVisited, context.EntriesExamined, context.Findings.Count,
                context.Findings.Count(f => f.CanRepair), watch.Elapsed.TotalMilliseconds);
            stats.Add(stat);
            _trace?.Invoke(string.Create(CultureInfo.InvariantCulture,
                $"análisis [{_scope.Id}] {Name(family)}: {stat.KeysVisited} claves abiertas, {stat.EntriesExamined} entradas examinadas, {stat.Findings} hallazgos ({stat.Repairable} reparables), {stat.ElapsedMs:F1} ms"));
            if (cancelled) break;
        }

        findings.Sort(static (a, b) => a.Family != b.Family ? a.Family.CompareTo(b.Family)
            : string.Compare(a.Location.Display + "|" + a.ValueName, b.Location.Display + "|" + b.ValueName, StringComparison.OrdinalIgnoreCase));
        if (cancelled) issues.Add("Análisis cancelado: resultado incompleto, no se puede reparar a partir de él.");
        return new(Guid.NewGuid(), DateTime.UtcNow, _scope.Id, ordered, findings, stats, issues, cancelled);
    }

    internal static string Name(RegistryFamily family) => AllFamilies.First(f => f.Family == family).Name;
}
