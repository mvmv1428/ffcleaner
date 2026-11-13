namespace FFCleaner.RegistryAnalysis;

using System.Globalization;
using System.Security;
using Microsoft.Win32;

/// <summary>
/// Reparación acotada del Registro. Orden estricto: validar el lote → volver a analizar → capturar el
/// estado exacto → respaldo verificado → y solo entonces eliminar, entrada por entrada, comprobando justo
/// antes de cada cambio que nada varió. Nunca borra más que el valor o la clave exactos que respaldó.
/// </summary>
public sealed class RegistryRepairService : IRegistryRepairService
{
    public static readonly TimeSpan ScanLifetime = TimeSpan.FromMinutes(30);
    private readonly RegistryScanner _scanner;
    private readonly RegistryBackupStore _backups;
    private readonly RegistryScope _scope;
    private readonly Action<string>? _trace;
    private readonly Func<DateTime> _clock;

    public RegistryRepairService(RegistryScanner scanner, RegistryBackupStore backups, Action<string>? trace = null,
        Func<DateTime>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(backups);
        if (scanner.Scope.Id != backups.Scope.Id)
            throw new ArgumentException("El almacén de respaldos y el analizador deben usar el mismo ámbito del Registro.", nameof(backups));
        _scanner = scanner;
        _backups = backups;
        _scope = scanner.Scope;
        _trace = trace;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    public Task<RegistryRepairReport> RepairAsync(RegistryScanResult scan, IReadOnlyCollection<string> findingIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(findingIds);
        return Task.Run(() => Repair(scan, findingIds, cancellationToken), CancellationToken.None);
    }

    private RegistryRepairReport Repair(RegistryScanResult scan, IReadOnlyCollection<string> findingIds, CancellationToken token)
    {
        var started = _clock();
        RegistryRepairReport Refuse(string reason)
        {
            _trace?.Invoke("reparación rechazada: " + reason);
            return new(scan.ScanId, started, _clock(), [], 0, null, null, reason, false);
        }

        // 1. Precondiciones del lote: si alguna falla no se modifica nada.
        if (scan.ScopeId != _scope.Id) return Refuse("El análisis pertenece a otro ámbito.");
        if (scan.Cancelled) return Refuse("El análisis se canceló; vuelve a analizar.");
        if (started - scan.CreatedUtc > ScanLifetime || started < scan.CreatedUtc) return Refuse("El análisis caducó; vuelve a analizar.");
        var ids = findingIds.Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0) return Refuse("No hay hallazgos marcados.");
        var byId = scan.Findings.ToDictionary(f => f.Id, StringComparer.Ordinal);
        if (ids.Any(id => !byId.ContainsKey(id))) return Refuse("La selección contiene hallazgos que no pertenecen a este análisis.");
        if (token.IsCancellationRequested) return Refuse("Cancelado antes de empezar.");

        var selected = ids.Select(id => byId[id]).ToList();
        var outcomes = new Dictionary<string, RepairOutcome>(StringComparer.Ordinal);
        void Set(RegistryFinding finding, RepairStatus status, string detail) =>
            outcomes[finding.Id] = new(finding.Id, finding.Location.Display + "  →  " + finding.ValueDisplay, status, detail);

        // 2. Reanálisis inmediato de las familias afectadas: solo se repara lo que sigue igual y sigue siendo seguro.
        var fresh = _scanner.Scan([.. selected.Select(f => f.Family).Distinct()], token);
        if (fresh.Cancelled) return Refuse("Cancelado durante la comprobación previa; no se modificó nada.");
        var current = fresh.Findings.ToDictionary(f => f.Id, StringComparer.Ordinal);
        var plan = new List<(RegistryFinding Finding, BackupEntry Entry)>();
        foreach (var finding in selected)
        {
            if (!finding.CanRepair)
                Set(finding, RepairStatus.Omitido, "No reparable automáticamente (" + Describe(finding.Risk) + ").");
            else if (!RegistryFamilyRules.Owns(finding.Family, finding.Location.KeyPath, finding.Repair, finding.ValueName))
                Set(finding, RepairStatus.Omitido, "Fuera de las claves que gestiona esta familia.");
            else if (!current.TryGetValue(finding.Id, out var now))
                Set(finding, RepairStatus.Omitido, "Ya no se detecta: el destino volvió a existir o la entrada desapareció.");
            else if (!now.CanRepair || now.StateHash != finding.StateHash || now.StateHash.Length == 0)
                Set(finding, RepairStatus.Conflicto, "La entrada cambió desde el análisis; se conserva.");
            else
            {
                // 3. Captura exacta de lo que se va a eliminar.
                var entry = Capture(now);
                if (entry is null) Set(finding, RepairStatus.Conflicto, "La entrada cambió mientras se preparaba el respaldo; se conserva.");
                else plan.Add((now, entry));
            }
        }

        string? backupPath = null, regPath = null;
        var cancelled = false;
        if (plan.Count > 0)
        {
            // 4. Respaldo verificado. Sin respaldo válido no hay reparación.
            try
            {
                (_, backupPath, regPath) = _backups.CreateVerified(scan.ScanId, [.. plan.Select(p => p.Entry)]);
            }
            catch (RegistryBackupException ex)
            {
                return Refuse(ex.Message + " No se modificó el Registro.");
            }

            // 5. Reparación, una entrada cada vez.
            foreach (var (finding, entry) in plan)
            {
                if (token.IsCancellationRequested)
                {
                    cancelled = true;
                    Set(finding, RepairStatus.Cancelado, "Cancelado antes de modificarla.");
                    continue;
                }
                try
                {
                    var (status, detail) = entry.Operation == RepairKind.DeleteValue ? DeleteValue(entry) : DeleteKey(entry);
                    Set(finding, status, detail);
                }
                catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException
                                               or System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException)
                {
                    // Solo se afirma «no se modificó» si la relectura coincide con lo respaldado; una clave con
                    // subclaves puede haber quedado a medias.
                    var reason = ex is SecurityException or UnauthorizedAccessException ? "acceso denegado" : ex.Message;
                    if (Unchanged(entry)) Set(finding, RepairStatus.Error, "No se modificó (" + reason + ").");
                    else Set(finding, RepairStatus.NoConfirmado, "La reparación quedó incompleta (" + reason + "); restaura el respaldo para volver al estado anterior.");
                }
            }
        }

        var ordered = selected.Select(f => outcomes[f.Id]).ToList();
        var report = new RegistryRepairReport(scan.ScanId, started, _clock(), ordered,
            ordered.Count(o => o.Status == RepairStatus.Reparado), backupPath, regPath, null, cancelled);
        _trace?.Invoke(string.Create(CultureInfo.InvariantCulture,
            $"reparación [{_scope.Id}]: {report.Repaired} reparadas de {ordered.Count} marcadas; respaldo {backupPath ?? "no necesario"}"));
        if (backupPath is not null) _backups.TryWriteLog(Path.Combine(Path.GetDirectoryName(backupPath)!, "reparacion.json"), report);
        return report;
    }

    /// <summary>¿El Registro sigue conteniendo exactamente lo respaldado en esta entrada?</summary>
    private bool Unchanged(BackupEntry entry)
    {
        try
        {
            using var key = _scope.Open(entry.Location, writable: false);
            if (key is null) return false;
            return entry.Operation == RepairKind.DeleteValue
                ? RegistrySnapshot.Hash(RegistrySnapshot.CaptureValue(key, entry.Value!.Name)) == RegistrySnapshot.Hash(entry.Value)
                : RegistrySnapshot.Hash(RegistrySnapshot.CaptureTree(key, entry.Tree!.Name)) == RegistrySnapshot.Hash(entry.Tree);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException or RegistryTreeTooLargeException)
        {
            return false;
        }
    }

    private BackupEntry? Capture(RegistryFinding finding)
    {
        try
        {
            using var key = _scope.Open(finding.Location, writable: false);
            if (key is null) return null;
            var physical = RawRegistry.PhysicalPath(key) ??
                           (finding.Location.Hive == RegHive.CurrentUser ? @"HKEY_CURRENT_USER\" : @"HKEY_LOCAL_MACHINE\") + finding.Location.KeyPath;
            if (finding.Repair == RepairKind.DeleteValue)
            {
                var value = RegistrySnapshot.CaptureValue(key, finding.ValueName ?? string.Empty);
                return value is null || RegistrySnapshot.Hash(value) != finding.StateHash ? null
                    : new(finding.Id, finding.Family, finding.Location.Hive, finding.Location.View, finding.Location.KeyPath, physical, RepairKind.DeleteValue, value, null, finding.Evidence);
            }
            var tree = RegistrySnapshot.CaptureTree(key, DetectorContext.LeafName(finding.Location));
            return RegistrySnapshot.Hash(tree) != finding.StateHash ? null
                : new(finding.Id, finding.Family, finding.Location.Hive, finding.Location.View, finding.Location.KeyPath, physical, RepairKind.DeleteKey, null, tree, finding.Evidence);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException or RegistryTreeTooLargeException)
        {
            return null;
        }
    }

    private (RepairStatus, string) DeleteValue(BackupEntry entry)
    {
        using var key = _scope.Open(entry.Location, writable: true);
        if (key is null) return (RepairStatus.Conflicto, "La clave ya no existe.");
        // Última comprobación sobre la clave abierta para escribir, inmediatamente antes del cambio.
        if (RegistrySnapshot.Hash(RegistrySnapshot.CaptureValue(key, entry.Value!.Name)) != RegistrySnapshot.Hash(entry.Value))
            return (RepairStatus.Conflicto, "El valor cambió tras el respaldo; se conserva.");
        RawRegistry.DeleteValue(key, entry.Value.Name);
        return RawRegistry.Query(key, entry.Value.Name) is null
            ? (RepairStatus.Reparado, "Valor eliminado; respaldo disponible para restaurarlo.")
            : (RepairStatus.NoConfirmado, "Se solicitó eliminar el valor pero sigue presente.");
    }

    private (RepairStatus, string) DeleteKey(BackupEntry entry)
    {
        var separator = entry.KeyPath.LastIndexOf('\\');
        var parentLocation = entry.Location with { KeyPath = entry.KeyPath[..separator] };
        var name = entry.KeyPath[(separator + 1)..];
        using var parent = _scope.Open(parentLocation, writable: true);
        if (parent is null) return (RepairStatus.Conflicto, "La clave superior ya no existe.");
        using (var key = parent.OpenSubKey(name, writable: false))
        {
            if (key is null) return (RepairStatus.Conflicto, "La clave ya no existe.");
            if (RegistrySnapshot.Hash(RegistrySnapshot.CaptureTree(key, name)) != RegistrySnapshot.Hash(entry.Tree!))
                return (RepairStatus.Conflicto, "La clave cambió tras el respaldo; se conserva.");
        }
        DeleteExact(parent, entry.Tree!);
        using var after = parent.OpenSubKey(name, writable: false);
        return after is null
            ? (RepairStatus.Reparado, "Clave eliminada; respaldo disponible para restaurarla.")
            : (RepairStatus.NoConfirmado, "Se solicitó eliminar la clave pero sigue presente.");
    }

    /// <summary>
    /// Elimina exactamente la estructura respaldada, de las hojas hacia arriba y sin borrado recursivo:
    /// si apareció una subclave nueva, Windows rechaza la eliminación y ese contenido ajeno se conserva.
    /// </summary>
    private static void DeleteExact(RegistryKey parent, BackupKey node)
    {
        using (var key = parent.OpenSubKey(node.Name, writable: true))
        {
            if (key is null) return;
            foreach (var sub in node.SubKeys) DeleteExact(key, sub);
        }
        parent.DeleteSubKey(node.Name, throwOnMissingSubKey: false);
    }

    private static string Describe(RegistryRisk risk) => risk switch
    {
        RegistryRisk.Ambiguo => "ambiguo",
        RegistryRisk.Critico => "componente del sistema",
        RegistryRisk.SinAcceso => "sin permisos",
        _ => "seguro",
    };
}
