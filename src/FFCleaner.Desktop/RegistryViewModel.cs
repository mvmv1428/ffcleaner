using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using FFCleaner.RegistryAnalysis;

namespace FFCleaner.Desktop;

public sealed class FamilyVm(RegistryFamilyInfo info, bool selected, Action changed) : ObservableObject
{
    private bool _selected = selected;
    public RegistryFamilyInfo Info => info;
    public string Name => CatalogText.FamilyName(info);
    public string Description => CatalogText.FamilyDescription(info);
    public void RefreshTexts() { Raise(nameof(Name)); Raise(nameof(Description)); }
    public string AutomationId => "fam-" + info.Id;
    public bool IsChecked { get => _selected; set { if (Set(ref _selected, value)) changed(); } }
}

public sealed class FindingRowVm(RegistryFinding finding, Func<string> family, Action changed) : ObservableObject
{
    private bool _selected;
    public RegistryFinding Finding => finding;
    public string Family => family();
    public void RefreshTexts() { Raise(nameof(Family)); Raise(nameof(Risk)); }
    /// <summary>Clave estable del riesgo para colorear su etiqueta.</summary>
    public string RiskKey => finding.Risk.ToString();

    public string Risk => CatalogText.Risk(finding.Risk);
    public string Location => UiPolicy.VisibleText(finding.Location.Display);
    public string Value => UiPolicy.VisibleText(finding.ValueDisplay);
    public string Target => UiPolicy.VisibleText(finding.ResolvedTarget ?? finding.RawTarget);
    public string Evidence => UiPolicy.VisibleText(finding.Evidence);
    public bool CanRepair => finding.CanRepair;

    /// <summary>Solo los hallazgos seguros se pueden marcar; el resto ignora cualquier intento.</summary>
    public bool IsSelected
    {
        get => _selected;
        set { if ((!value || CanRepair) && Set(ref _selected, value)) changed(); }
    }
}

public sealed record BackupRowVm(RegistryBackupInfo Info)
{
    public string Display => Info.Valid
        ? $"{Info.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {Info.Entries}" + Loc.T(" entradas", " entries")
        : $"{Info.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · " + Loc.T("NO VÁLIDO: ", "INVALID: ") + Info.Problem;
}

/// <summary>
/// Sección «Registro»: elegir familias → analizar → revisar → reparar los marcados → restaurar un respaldo,
/// todo en la misma pantalla. La lógica vive en los servicios; aquí solo hay presentación y confirmaciones.
/// </summary>
public sealed class RegistryViewModel : ObservableObject
{
    private readonly IRegistryAnalysisService _analysis;
    private readonly IRegistryRepairService _repair;
    private readonly IRegistryBackupService _backups;
    private readonly SettingsHolder _settings;
    private readonly bool _sandbox;
    private RegistryScanResult? _scan;
    private CancellationTokenSource? _cancel;
    private IReadOnlyList<FindingRowVm> _findings = [];
    private ICollectionView _findingsView = CollectionViewSource.GetDefaultView(Array.Empty<FindingRowVm>());
    private IReadOnlyList<BackupRowVm> _backupRows = [];
    private BackupRowVm? _selectedBackup;
    private string _status = Loc.T("Elige familias y pulsa Analizar Registro.", "Choose families and press Scan Registry.");
    private string _summary = "", _log = "", _confirmText = "";
    private bool _busy, _onlyRepairable;
    private string? _confirming;

    public RegistryViewModel(IRegistryAnalysisService analysis, IRegistryRepairService repair, IRegistryBackupService backups,
        SettingsHolder settings, bool sandbox)
    {
        _analysis = analysis;
        _repair = repair;
        _backups = backups;
        _settings = settings;
        _sandbox = sandbox;
        var saved = settings.Current.RegistryFamilies;
        Families = [.. analysis.Families.Select(f => new FamilyVm(f, saved.Count == 0 || saved.Contains(f.Id), OnFamiliesChanged))];
        ScanCommand = new RelayCommand(ScanAsync, () => !IsBusy && Families.Any(f => f.IsChecked));
        SelectSafeCommand = new RelayCommand(() => SelectAll(true), () => !IsBusy && _findings.Any(f => f.CanRepair));
        ClearSelectionCommand = new RelayCommand(() => SelectAll(false), () => !IsBusy && _findings.Any(f => f.IsSelected));
        RepairCommand = new RelayCommand(AskRepair, () => !IsBusy && _confirming is null && _scan is not null && _findings.Any(f => f.IsSelected));
        RestoreCommand = new RelayCommand(AskRestore, () => !IsBusy && _confirming is null && SelectedBackup is { Info.Valid: true });
        ConfirmCommand = new RelayCommand(ConfirmAsync, () => !IsBusy && _confirming is not null);
        DismissCommand = new RelayCommand(() => Confirming = null);
        CancelCommand = new RelayCommand(() => _cancel?.Cancel(), () => IsBusy);
        OpenBackupsCommand = new RelayCommand(OpenBackups);
        ElevateCommand = new RelayCommand(RestartElevated, () => !IsBusy && CanElevate);
        RefreshBackups();
        Loc.Changed += (_, _) =>
        {
            foreach (var family in Families) family.RefreshTexts();
            foreach (var row in _findings) row.RefreshTexts();
            Raise(nameof(ElevationNote));
            Raise(nameof(RiskSummary));
            RefreshBackups();
            if (!IsBusy && _scan is null) Status = Loc.T("Elige familias y pulsa Analizar Registro.", "Choose families and press Scan Registry.");
        };
    }

    /// <summary>La ventana lo usa para cerrarse tras relanzar la app con privilegios.</summary>
    public event EventHandler? ShutdownRequested;

    public IReadOnlyList<FamilyVm> Families { get; }
    public string ScopeDescription => _analysis.ScopeDescription;
    public string BackupDirectory => _backups.BackupDirectory;
    public bool CanElevate => !_sandbox && !RegistryScope.IsElevated;
    public string ElevationNote => _sandbox
        ? Loc.T("Modo sandbox: solo se usan claves de prueba.", "Sandbox mode: only test keys are used.")
        : RegistryScope.IsElevated
            ? Loc.T("Ejecutando como administrador: las entradas seguras de HKLM también se pueden reparar.", "Running as administrator: safe HKLM entries can be repaired too.")
            : Loc.T("Sin privilegios de administrador: las entradas de HKLM se muestran como «Sin acceso» y no se modifican.", "Without administrator rights: HKLM entries are shown as «No access» and are not modified.");
    public ICommand ScanCommand { get; }
    public ICommand SelectSafeCommand { get; }
    public ICommand ClearSelectionCommand { get; }
    public ICommand RepairCommand { get; }
    public ICommand RestoreCommand { get; }
    public ICommand ConfirmCommand { get; }
    public ICommand DismissCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand OpenBackupsCommand { get; }
    public ICommand ElevateCommand { get; }

    public ICollectionView FindingsView { get => _findingsView; private set => Set(ref _findingsView, value); }
    public IReadOnlyList<BackupRowVm> Backups { get => _backupRows; private set => Set(ref _backupRows, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string Summary { get => _summary; private set => Set(ref _summary, value); }
    public string Log { get => _log; private set => Set(ref _log, value); }
    public string ConfirmText { get => _confirmText; private set => Set(ref _confirmText, value); }
    public bool IsConfirming => _confirming is not null;

    public BackupRowVm? SelectedBackup
    {
        get => _selectedBackup;
        set { if (Set(ref _selectedBackup, value)) CommandManager.InvalidateRequerySuggested(); }
    }

    public bool OnlyRepairable
    {
        get => _onlyRepairable;
        set { if (Set(ref _onlyRepairable, value)) FindingsView.Refresh(); }
    }

    public bool IsBusy
    {
        get => _busy;
        private set
        {
            if (!Set(ref _busy, value)) return;
            Raise(nameof(IsIdle));
            Raise(nameof(ShowEmptyState));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsIdle => !_busy;

    public bool HasFindings => _findings.Count > 0;
    public bool ShowEmptyState => _findings.Count == 0 && !_busy;

    /// <summary>Recuento por riesgo del último análisis, para las etiquetas del encabezado de la tabla.</summary>
    public string RiskSummary => _findings.Count == 0 ? "" :
        $"{_findings.Count(f => f.CanRepair)}{Loc.T(" seguros", " safe")} · {_findings.Count(f => f.Finding.Risk == RegistryRisk.Ambiguo)}{Loc.T(" ambiguos", " ambiguous")} · " +
        $"{_findings.Count(f => f.Finding.Risk == RegistryRisk.Critico)}{Loc.T(" críticos", " critical")} · {_findings.Count(f => f.Finding.Risk == RegistryRisk.SinAcceso)}{Loc.T(" sin acceso", " no access")}";

    /// <summary>Se dispara al terminar un análisis, una reparación o una restauración.</summary>
    public event EventHandler<Notice>? Noticed;

    private string? Confirming
    {
        get => _confirming;
        set
        {
            _confirming = value;
            Raise(nameof(IsConfirming));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public async Task StopAsync()
    {
        _cancel?.Cancel();
        while (IsBusy) await Task.Delay(50);
    }

    /// <summary>Cambiar las familias invalida el análisis: los hallazgos mostrados ya no corresponden a la selección.</summary>
    private void OnFamiliesChanged()
    {
        _settings.Update(s => s with { RegistryFamilies = [.. Families.Where(f => f.IsChecked).Select(f => f.Info.Id)] });
        Confirming = null;
        if (_scan is null) { CommandManager.InvalidateRequerySuggested(); return; }
        _scan = null;
        SetFindings([]);
        Summary = "";
        Status = Loc.T("Las familias cambiaron; vuelve a analizar.", "The families changed; scan again.");
    }

    private void SetFindings(IReadOnlyList<FindingRowVm> rows)
    {
        _findings = rows;
        Raise(nameof(HasFindings));
        Raise(nameof(ShowEmptyState));
        Raise(nameof(RiskSummary));
        var view = CollectionViewSource.GetDefaultView(rows);
        view.Filter = item => !_onlyRepairable || ((FindingRowVm)item).CanRepair;
        FindingsView = view;
        CommandManager.InvalidateRequerySuggested();
    }

    private void SelectAll(bool selected)
    {
        foreach (var row in _findings) row.IsSelected = selected && row.CanRepair;
        UpdateSelectionStatus();
    }

    private void UpdateSelectionStatus()
    {
        Confirming = null;
        if (_scan is null || IsBusy) return;
        var marked = _findings.Count(f => f.IsSelected);
        Status = marked == 0 ? Loc.T("Marca los hallazgos seguros que quieras reparar.", "Select the safe findings you want to repair.")
            : marked + Loc.T(" hallazgos marcados para reparar.", " findings selected for repair.");
    }

    private async Task ScanAsync()
    {
        await RunAsync(Loc.T("Analizando el Registro en modo de solo lectura…", "Scanning the Registry in read-only mode…"), async token =>
        {
            var families = Families.Where(f => f.IsChecked).Select(f => f.Info.Family).ToList();
            var scan = await _analysis.ScanAsync(families, token);
            if (scan.Cancelled)
            {
                _scan = null;
                SetFindings([]);
                Summary = "";
                Status = Loc.T("Análisis cancelado; no se cambió ninguna clave.", "Scan cancelled; no key was changed.");
                return;
            }
            ShowScan(scan);
            Status = Loc.T($"Registro analizado · {scan.Findings.Count} hallazgos · {scan.Findings.Count(f => f.CanRepair)} reparables · {scan.Findings.Count(f => !f.CanRepair)} solo informativos" +
                     (OnlyRepairable ? " (ocultos: desmarca «Solo reparables» para verlos)" : "") + ". No se cambió ninguna clave.",
                     $"Registry scanned · {scan.Findings.Count} findings · {scan.Findings.Count(f => f.CanRepair)} repairable · {scan.Findings.Count(f => !f.CanRepair)} informational only" +
                     (OnlyRepairable ? " (hidden: turn off «Only repairable» to see them)" : "") + ". No key was changed.");
            Noticed?.Invoke(this, new(Loc.T("Registro analizado", "Registry scanned"), Loc.T(scan.Findings.Count(f => f.CanRepair) + " entradas reparables de " + scan.Findings.Count + " hallazgos.",
                scan.Findings.Count(f => f.CanRepair) + " repairable entries out of " + scan.Findings.Count + " findings."), NoticeKind.Info));
        });
    }

    private void ShowScan(RegistryScanResult scan)
    {
        _scan = scan;
        var infos = _analysis.Families.ToDictionary(f => f.Family);
        var names = infos.ToDictionary(p => p.Key, p => CatalogText.FamilyName(p.Value));
        // Si hay hallazgos reparables mezclados con informativos, se muestran primero solo los reparables.
        _onlyRepairable = scan.Findings.Any(f => f.CanRepair) && scan.Findings.Any(f => !f.CanRepair);
        Raise(nameof(OnlyRepairable));
        SetFindings([.. scan.Findings.Select(f => new FindingRowVm(f, () => CatalogText.FamilyName(infos[f.Family]), UpdateSelectionStatus))]);
        Summary = string.Join(Environment.NewLine, scan.Stats.Select(s =>
            Loc.T($"{names[s.Family]}: {s.KeysVisited} claves abiertas · {s.EntriesExamined} entradas examinadas · {s.Findings} hallazgos ({s.Repairable} reparables) · {s.ElapsedMs:N0} ms",
                  $"{names[s.Family]}: {s.KeysVisited} keys opened · {s.EntriesExamined} entries examined · {s.Findings} findings ({s.Repairable} repairable) · {s.ElapsedMs:N0} ms"))
            .Concat(scan.Issues));
    }

    private void AskRepair()
    {
        var marked = _findings.Where(f => f.IsSelected).ToList();
        var keys = marked.Count(f => f.Finding.Repair == RepairKind.DeleteKey);
        var values = marked.Count(f => f.Finding.Repair == RepairKind.DeleteValue);
        static string Plural(int n, string one, string many) => n + " " + (n == 1 ? one : many);
        ConfirmText = Loc.T($"Se repararán {Plural(marked.Count, "entrada", "entradas")} del Registro ({Plural(keys, "clave", "claves")} y " +
            $"{Plural(values, "valor", "valores")}). Antes se guardará y verificará un respaldo en {BackupDirectory}; si el respaldo falla no se modifica nada.",
            $"{Plural(marked.Count, "Registry entry", "Registry entries")} will be repaired ({Plural(keys, "key", "keys")} and {Plural(values, "value", "values")}). " +
            $"A backup will first be saved and verified in {BackupDirectory}; if the backup fails nothing is changed.");
        Confirming = "repair";
    }

    private void AskRestore()
    {
        ConfirmText = Loc.T($"Se restaurará el respaldo del {SelectedBackup!.Display}. Solo se vuelven a crear las entradas que falten: si alguna existe ahora con otros datos se deja como está y se informa.",
            $"The backup from {SelectedBackup!.Display} will be restored. Only missing entries are recreated: if one now exists with other data it is left as is and reported.");
        Confirming = "restore";
    }

    private Task ConfirmAsync()
    {
        var action = Confirming;
        Confirming = null;
        return action == "repair" ? RepairAsync() : RestoreAsync();
    }

    private async Task RepairAsync()
    {
        var scan = _scan;
        var ids = _findings.Where(f => f.IsSelected).Select(f => f.Finding.Id).ToList();
        if (scan is null || ids.Count == 0) return;
        await RunAsync(Loc.T("Respaldando y reparando…", "Backing up and repairing…"), async token =>
        {
            var report = await _repair.RepairAsync(scan, ids, token);
            if (report.Refused)
            {
                Log = Loc.T("Reparación rechazada: ", "Repair refused: ") + report.Refusal;
                Status = Loc.T("No se reparó nada: ", "Nothing was repaired: ") + report.Refusal;
                return;
            }
            Log = $"Reparación del {report.FinishedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}: {report.Repaired} de {report.Outcomes.Count} reparadas." +
                  (report.BackupPath is null ? "" : Environment.NewLine + "Respaldo verificado: " + report.BackupPath + Environment.NewLine + "Archivo .reg: " + report.RegFilePath) +
                  Environment.NewLine + string.Join(Environment.NewLine, report.Outcomes.Select(o => $"[{o.Status}] {UiPolicy.VisibleText(o.Location)} — {o.Detail}"));
            RefreshBackups();
            // Reanálisis inmediato: la tabla refleja el estado real tras reparar, no lo que se pretendía.
            var after = await _analysis.ScanAsync(scan.Families, CancellationToken.None);
            ShowScan(after);
            Status = Loc.T((report.Cancelled ? "Reparación cancelada a medias" : "Reparación completada") +
                     $" · {report.Repaired} reparadas · {report.Outcomes.Count - report.Repaired} sin cambios · tras reanalizar quedan {after.Findings.Count} hallazgos.",
                     (report.Cancelled ? "Repair cancelled midway" : "Repair complete") +
                     $" · {report.Repaired} repaired · {report.Outcomes.Count - report.Repaired} unchanged · {after.Findings.Count} findings remain after rescanning.");
            Noticed?.Invoke(this, new(report.Cancelled ? Loc.T("Reparación cancelada", "Repair cancelled") : Loc.T("Registro reparado", "Registry repaired"),
                report.Repaired + Loc.T(" entradas reparadas. Respaldo verificado guardado.", " entries repaired. Verified backup saved."), report.Repaired == report.Outcomes.Count ? NoticeKind.Exito : NoticeKind.Aviso));
        });
    }

    private async Task RestoreAsync()
    {
        var backup = SelectedBackup;
        if (backup is null) return;
        await RunAsync(Loc.T("Restaurando el respaldo…", "Restoring the backup…"), async token =>
        {
            var report = await _backups.RestoreAsync(backup.Info.Path, token);
            if (report.Refused)
            {
                Log = Loc.T("Restauración rechazada: ", "Restore refused: ") + report.Refusal;
                Status = Loc.T("No se restauró nada: ", "Nothing was restored: ") + report.Refusal;
                return;
            }
            Log = $"Restauración del {report.FinishedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}: {report.Restored} restauradas, {report.AlreadyPresent} ya presentes, " +
                  $"{report.Conflicts} conflictos, {report.Failed} errores." + Environment.NewLine +
                  string.Join(Environment.NewLine, report.Outcomes.Select(o => $"[{o.Status}] {UiPolicy.VisibleText(o.Location)} — {o.Detail}"));
            var families = Families.Where(f => f.IsChecked).Select(f => f.Info.Family).ToList();
            var remaining = "";
            if (families.Count > 0)
            {
                var after = await _analysis.ScanAsync(families, CancellationToken.None);
                ShowScan(after);
                remaining = Loc.T($" · tras reanalizar hay {after.Findings.Count} hallazgos", $" · {after.Findings.Count} findings after rescanning");
            }
            Status = Loc.T($"Respaldo restaurado · {report.Restored} restauradas · {report.AlreadyPresent} ya presentes · {report.Conflicts} conflictos · {report.Failed} errores",
                $"Backup restored · {report.Restored} restored · {report.AlreadyPresent} already present · {report.Conflicts} conflicts · {report.Failed} errors") + remaining + ".";
            Noticed?.Invoke(this, new(Loc.T("Respaldo restaurado", "Backup restored"), report.Restored + Loc.T(" entradas restauradas", " entries restored") +
                (report.Conflicts + report.Failed > 0 ? Loc.T($" · {report.Conflicts + report.Failed} sin restaurar (ver resultado)", $" · {report.Conflicts + report.Failed} not restored (see result)") : "."),
                report.Conflicts + report.Failed > 0 ? NoticeKind.Aviso : NoticeKind.Exito));
        });
    }

    private async Task RunAsync(string status, Func<CancellationToken, Task> body)
    {
        if (IsBusy) return;
        using var cancel = new CancellationTokenSource();
        _cancel = cancel;
        IsBusy = true;
        Confirming = null;
        Status = status;
        try { await body(cancel.Token); }
        finally
        {
            _cancel = null;
            IsBusy = false;
        }
    }

    private void RefreshBackups()
    {
        try
        {
            Backups = [.. _backups.List().Select(b => new BackupRowVm(b))];
            SelectedBackup = Backups.FirstOrDefault(b => b.Info.Valid);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Backups = [];
            Log = "No se pudo leer la carpeta de respaldos: " + ex.Message;
        }
    }

    private void OpenBackups()
    {
        try
        {
            Directory.CreateDirectory(BackupDirectory);
            using var _ = Process.Start(new ProcessStartInfo(BackupDirectory) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            Status = "No se pudo abrir la carpeta de respaldos: " + ex.Message;
        }
    }

    /// <summary>Relanza la aplicación pidiendo consentimiento de UAC. Si se rechaza, todo sigue como estaba.</summary>
    private void RestartElevated()
    {
        try
        {
            var path = Environment.ProcessPath ?? throw new InvalidOperationException("Ruta del ejecutable desconocida.");
            using var _ = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "runas" });
            ShutdownRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Status = Loc.T("Elevación cancelada; la aplicación sigue sin privilegios de administrador.", "Elevation cancelled; the app keeps running without administrator rights.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Status = Loc.T("No se pudo reiniciar como administrador: ", "Could not restart as administrator: ") + ex.Message;
        }
    }
}
