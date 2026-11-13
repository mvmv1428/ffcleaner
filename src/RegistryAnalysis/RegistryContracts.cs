namespace FFCleaner.RegistryAnalysis;

/// <summary>Estado observado de un destino, provisto por la sonda inyectada.</summary>
public enum TargetState
{
    Present,
    Missing,
    Unknown,
}

/// <summary>Entrada cruda para el evaluador puro de rutas.</summary>
public record RegistryInput(string View, string KeyPath, string ValueName, string RawTarget);

/// <summary>Resultado del evaluador puro de rutas (<see cref="RegistryEvaluator"/>). No implica modificación.</summary>
public record ReferenceFinding(
    string Id,
    string View,
    string KeyPath,
    string ValueName,
    string RawTarget,
    string? ResolvedTarget,
    string Reason,
    string Confidence,
    bool Selectable);

public enum RegHive { CurrentUser, LocalMachine }

public enum RegView { Registry64, Registry32 }

/// <summary>Ubicación lógica de una clave: hive real, vista 32/64 y ruta relativa al hive.</summary>
public readonly record struct RegLocation(RegHive Hive, RegView View, string KeyPath)
{
    public string HiveName => Hive == RegHive.CurrentUser ? "HKCU" : "HKLM";
    public string Display => HiveName + "\\" + KeyPath + (View == RegView.Registry32 ? "  [vista 32 bits]" : string.Empty);
    public RegLocation Child(string name) => this with { KeyPath = KeyPath + "\\" + name };
}

public enum RegistryFamily { AppPaths, Uninstall, FileAssociations, Startup, MuiCache, SharedDlls }

public sealed record RegistryFamilyInfo(RegistryFamily Family, string Id, string Name, string Description);

/// <summary>
/// Clasificación de riesgo. Solo <see cref="Seguro"/> se puede marcar y reparar; los demás se muestran
/// para revisión pero ningún servicio los modifica.
/// </summary>
public enum RegistryRisk
{
    /// <summary>Destino comprobado como inexistente, entrada modificable y regla de familia inequívoca.</summary>
    Seguro,
    /// <summary>No se pudo verificar o admite más de una interpretación.</summary>
    Ambiguo,
    /// <summary>Componente del sistema.</summary>
    Critico,
    /// <summary>La clave no se puede modificar con los permisos actuales.</summary>
    SinAcceso,
}

public enum RepairKind { DeleteValue, DeleteKey }

/// <summary>
/// Hallazgo del análisis. <see cref="ValueName"/> nulo significa la clave completa; cadena vacía, el valor
/// predeterminado. <see cref="StateHash"/> resume el contenido exacto observado para detectar cambios posteriores.
/// </summary>
public sealed record RegistryFinding(
    string Id,
    RegistryFamily Family,
    RegLocation Location,
    string? ValueName,
    RepairKind Repair,
    string RawTarget,
    string? ResolvedTarget,
    RegistryRisk Risk,
    string Evidence,
    string StateHash)
{
    public bool CanRepair => Risk == RegistryRisk.Seguro;
    public string ValueDisplay => ValueName is null ? "(clave completa)" : ValueName.Length == 0 ? "(Predeterminado)" : ValueName;
}

/// <summary>Traza por familia: qué se recorrió exactamente y cuánto tardó.</summary>
public sealed record FamilyScanStats(RegistryFamily Family, int KeysVisited, int EntriesExamined, int Findings,
    int Repairable, double ElapsedMs);

public sealed record RegistryScanResult(Guid ScanId, DateTime CreatedUtc, string ScopeId,
    IReadOnlyList<RegistryFamily> Families, IReadOnlyList<RegistryFinding> Findings,
    IReadOnlyList<FamilyScanStats> Stats, IReadOnlyList<string> Issues, bool Cancelled);

public enum RepairStatus { Reparado, Omitido, Conflicto, Error, NoConfirmado, Cancelado }

public sealed record RepairOutcome(string FindingId, string Location, RepairStatus Status, string Detail);

public sealed record RegistryRepairReport(Guid ScanId, DateTime StartedUtc, DateTime FinishedUtc,
    IReadOnlyList<RepairOutcome> Outcomes, int Repaired, string? BackupPath, string? RegFilePath, string? Refusal,
    bool Cancelled)
{
    /// <summary>True cuando el lote se rechazó antes de modificar nada.</summary>
    public bool Refused => Refusal is not null;
}

public enum RestoreStatus { Restaurado, YaPresente, Conflicto, Error, Cancelado }

public sealed record RestoreOutcome(string Location, RestoreStatus Status, string Detail);

public sealed record RegistryRestoreReport(Guid BackupId, DateTime StartedUtc, DateTime FinishedUtc,
    IReadOnlyList<RestoreOutcome> Outcomes, int Restored, int AlreadyPresent, int Conflicts, int Failed,
    string? Refusal, bool Cancelled)
{
    public bool Refused => Refusal is not null;
}

public sealed record RegistryBackupInfo(Guid BackupId, DateTime CreatedUtc, string Path, string RegFilePath,
    int Entries, string ScopeId, bool Valid, string? Problem);

public interface IRegistryAnalysisService
{
    IReadOnlyList<RegistryFamilyInfo> Families { get; }
    string ScopeDescription { get; }

    /// <summary>Analiza las familias indicadas. Solo lectura: nunca crea, escribe ni elimina.</summary>
    Task<RegistryScanResult> ScanAsync(IReadOnlyCollection<RegistryFamily> families, CancellationToken cancellationToken = default);
}

public interface IRegistryRepairService
{
    /// <summary>
    /// Repara únicamente los hallazgos indicados y clasificados como seguros. Antes de modificar nada
    /// vuelve a analizar, crea un respaldo y lo verifica; si el respaldo falla no se cambia nada.
    /// </summary>
    Task<RegistryRepairReport> RepairAsync(RegistryScanResult scan, IReadOnlyCollection<string> findingIds,
        CancellationToken cancellationToken = default);
}

public interface IRegistryBackupService
{
    string BackupDirectory { get; }
    IReadOnlyList<RegistryBackupInfo> List();

    /// <summary>Restaura un respaldo propio. Nunca sobrescribe datos distintos a los respaldados.</summary>
    Task<RegistryRestoreReport> RestoreAsync(string backupPath, CancellationToken cancellationToken = default);
}
