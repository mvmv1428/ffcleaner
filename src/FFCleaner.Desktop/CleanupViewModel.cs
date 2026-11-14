using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using FFCleaner.Core;

namespace FFCleaner.Desktop;

/// <summary>Nodo Aplicación → Categoría → Perfil. Su casilla refleja y modifica el modelo de selección.</summary>
public sealed class TreeNodeVm(string title, string detail, string automationId, Func<bool?>? get, Action<bool>? set) : ObservableObject
{
    public string Id { get; init; } = "";
    public string Title { get; } = title;
    public string Detail { get; } = detail;
    public bool HasDetail => Detail.Length > 0;
    public string AutomationId { get; } = automationId;
    public string ItemAutomationId => "app-" + Id;
    public string Group { get; init; } = "";
    public string Initial => Title.Length == 0 ? "?" : Title[..1].ToUpperInvariant();

    /// <summary>Etiqueta de atención («Datos personales», «Muy grande», «Administrador») y su tipo para el color.</summary>
    public string Badge { get; init; } = "";
    public string BadgeKind { get; init; } = "";
    public bool HasBadge => Badge.Length > 0;

    /// <summary>Resumen de lo marcado dentro de la aplicación («2 de 5»).</summary>
    public Func<string>? CountSource { get; init; }
    public string CountText => CountSource?.Invoke() ?? "";
    public ObservableCollection<TreeNodeVm> Children { get; } = [];
    public bool HasProfiles => Children.Count > 0;

    public bool? IsChecked
    {
        get => get?.Invoke();
        set => set?.Invoke(value ?? false);
    }

    public void Refresh()
    {
        Raise(nameof(IsChecked));
        Raise(nameof(CountText));
        foreach (var child in Children) child.Refresh();
    }
}

public sealed record ResultRowVm(string App, string Category, string Profile, string Files, string Size, string State,
    TargetAnalysis? Analysis, TargetCleaningResult? Cleaning)
{
    /// <summary>Tamaño relativo a la fila mayor (0–1): alimenta la barra de proporción de la tabla.</summary>
    public double Share { get; init; }

    /// <summary>Ancho de la barra en píxeles, para dibujarla sin convertidores.</summary>
    public double ShareWidth => Math.Max(Share > 0 ? 3 : 0, Share * 96);

    /// <summary>La fila tiene algo que llama la atención (protegidos, omitidos, errores): la tabla la resalta.</summary>
    public bool HasNotes { get; init; }

    /// <summary>Inicial de la aplicación para su insignia de color.</summary>
    public string Initial => App.Length == 0 ? "?" : App[..1].ToUpperInvariant();

    /// <summary>«Brave · Default · 80 elementos»: aplicación, perfil y cuántos elementos (sustituye a la columna aparte).</summary>
    public string Subtitle => string.Join(" · ", new[] { App, Profile, Files + Loc.T(" elementos", " items") }.Where(p => p.Length > 0));
}

/// <summary>Una barra del desglose por aplicación del espacio que se puede liberar.</summary>
public sealed record BreakdownVm(string App, string Size, double Share)
{
    public double BarWidth => Math.Max(4, Share * 150);
}

/// <summary>Ajustes compartidos por las secciones: cada una actualiza su parte sin pisar las demás.</summary>
public sealed class SettingsHolder(SettingsStore? store, UserSettings current, DiagnosticsLog log)
{
    public UserSettings Current { get; private set; } = current;

    public void Update(Func<UserSettings, UserSettings> change)
    {
        Current = change(Current);
        if (store is null) return;
        try { store.Save(Current); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.IO.InvalidDataException or ArgumentException)
        {
            log.Write("ajustes", "No se guardaron los ajustes: " + ex.Message);
        }
    }
}

/// <summary>
/// Sección «Limpieza personalizada». No contiene reglas de limpieza: delega en <see cref="CleanupWorkflow"/>
/// y en los servicios, y se limita a presentar selección, progreso, confirmación y resultados.
/// </summary>
public sealed class CleanupViewModel : ObservableObject
{
    private const int MaxIssuesShown = 20;
    private const int MaxBreakdown = 5;
    private readonly CleanupWorkflow _workflow;
    private readonly SelectionModel _selection;
    private readonly IProcessProbe _processes;
    private readonly SettingsHolder _settings;
    private readonly DiagnosticsLog _log;
    private readonly IReadOnlyList<string> _catalogNotes;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private IReadOnlyList<ResultRowVm> _results = [];
    private IReadOnlyList<BreakdownVm> _breakdown = [];
    private ResultRowVm? _selectedResult;
    private TreeNodeVm? _selectedApp;
    private ICollectionView _appsView = CollectionViewSource.GetDefaultView(Array.Empty<TreeNodeVm>());
    private string _status = "", _filesText = "—", _sizeText = "—", _selectionText = "", _timingText = "", _confirmText = "";
    private string _heroLabel = "", _heroHint = "", _detailTitle = "", _detailSummary = "", _issuesText = "";
    private bool _busy, _confirming, _progressIndeterminate = true, _heroReady, _heroDone;
    private double _progress;
    private string _progressText = "";
    private Action? _present;
    private readonly CleanupTotals? _totals;
    private string _totalFreedText = "";

    public CleanupViewModel(CleanupWorkflow workflow, IProcessProbe processes, SettingsHolder settings,
        DiagnosticsLog log, IReadOnlyList<string> catalogNotes, CleanupTotals? totals = null)
    {
        _totals = totals;
        _workflow = workflow;
        _selection = workflow.Selection;
        _processes = processes;
        _settings = settings;
        _log = log;
        _catalogNotes = catalogNotes;
        BuildTree();
        AnalyzeCommand = new RelayCommand(AnalyzeAsync, () => !IsBusy && _selection.SelectedCount > 0);
        CleanCommand = new RelayCommand(AskConfirmation, () => !IsBusy && !IsConfirming && _workflow.CleanBlocker() is null);
        ConfirmCleanCommand = new RelayCommand(CleanAsync, () => !IsBusy && IsConfirming);
        DismissCommand = new RelayCommand(() => IsConfirming = false);
        CancelCommand = new RelayCommand(_workflow.Cancel, () => IsBusy);
        ClearSelectionCommand = new RelayCommand(() => _selection.Clear(), () => !IsBusy && _selection.SelectedCount > 0);
        SelectAllCommand = new RelayCommand(() => _selection.SelectAll(), () => !IsBusy && _selection.SelectedCount < _selection.Catalog.AllTargets().Count());
        _selection.Changed += (_, _) => OnUi(OnSelectionChanged);
        Loc.Changed += (_, _) => OnUi(OnLanguageChanged);
        Status = Loc.T("Marca qué quieres limpiar y pulsa Analizar.", "Choose what to clean and press Analyze.");
        ResetHero();
        UpdateSelectionText();
        UpdateTotals();
    }

    /// <summary>«Total liberado con FFCleaner: X en N limpiezas desde …». Vacío hasta la primera limpieza.</summary>
    public string TotalFreedText { get => _totalFreedText; private set { if (Set(ref _totalFreedText, value)) Raise(nameof(HasTotalFreed)); } }
    public bool HasTotalFreed => _totalFreedText.Length > 0;

    private void UpdateTotals()
    {
        var t = _totals?.Read() ?? CleanupTotalsData.Empty;
        if (t.Cleanings == 0) { TotalFreedText = ""; return; }
        var since = t.SinceUtc is { } d ? d.ToLocalTime().ToString(Loc.English ? "MMM d, yyyy" : "d MMM yyyy", UiPolicy.Culture) : "";
        TotalFreedText = Loc.T($"Total liberado con FFCleaner: {UiPolicy.Human(t.Bytes)} en {t.Cleanings} {(t.Cleanings == 1 ? "limpieza" : "limpiezas")}" + (since.Length > 0 ? " desde el " + since : ""),
            $"Total freed with FFCleaner: {UiPolicy.Human(t.Bytes)} in {t.Cleanings} {(t.Cleanings == 1 ? "cleanup" : "cleanups")}" + (since.Length > 0 ? " since " + since : ""));
    }

    /// <summary>Aplicaciones agrupadas (Navegadores, Windows…) para la lista de la izquierda.</summary>
    public ObservableCollection<TreeNodeVm> Apps { get; } = [];
    public ICollectionView AppsView { get => _appsView; private set => Set(ref _appsView, value); }

    /// <summary>Aplicación cuyas categorías se muestran a la derecha de la lista.</summary>
    public TreeNodeVm? SelectedApp { get => _selectedApp; set => Set(ref _selectedApp, value); }

    public string CatalogNotes => _catalogNotes.Count == 0 ? "" : string.Join(Environment.NewLine, _catalogNotes);
    public bool HasCatalogNotes => _catalogNotes.Count > 0;
    public ICommand AnalyzeCommand { get; }
    public ICommand CleanCommand { get; }
    public ICommand ConfirmCleanCommand { get; }
    public ICommand DismissCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand ClearSelectionCommand { get; }
    public ICommand SelectAllCommand { get; }

    public IReadOnlyList<ResultRowVm> Results
    {
        get => _results;
        private set { if (Set(ref _results, value)) { Raise(nameof(HasResults)); Raise(nameof(ShowEmptyState)); } }
    }

    public bool HasResults => _results.Count > 0;

    /// <summary>Sin resultados ni operación en curso: se muestra la guía de primeros pasos.</summary>
    public bool ShowEmptyState => _results.Count == 0 && !_busy;

    /// <summary>Desglose por aplicación del espacio que se puede liberar (o que se liberó).</summary>
    public IReadOnlyList<BreakdownVm> Breakdown { get => _breakdown; private set { if (Set(ref _breakdown, value)) Raise(nameof(HasBreakdown)); } }
    public bool HasBreakdown => _breakdown.Count > 0;

    /// <summary>Tarjeta principal: qué se puede liberar. Antes de analizar invita a hacerlo.</summary>
    public string HeroLabel { get => _heroLabel; private set => Set(ref _heroLabel, value); }
    public string HeroHint { get => _heroHint; private set => Set(ref _heroHint, value); }
    public bool HeroReady { get => _heroReady; private set => Set(ref _heroReady, value); }
    public bool HeroDone { get => _heroDone; private set => Set(ref _heroDone, value); }

    /// <summary>Avance de la operación en curso (0–100) y si se conoce el total.</summary>
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public bool IsProgressIndeterminate { get => _progressIndeterminate; private set => Set(ref _progressIndeterminate, value); }
    public string ProgressText { get => _progressText; private set => Set(ref _progressText, value); }

    /// <summary>Se dispara al terminar un análisis o una limpieza, para el aviso emergente.</summary>
    public event EventHandler<Notice>? Noticed;
    public string DetailTitle { get => _detailTitle; private set => Set(ref _detailTitle, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string FilesText { get => _filesText; private set => Set(ref _filesText, value); }
    public string SizeText { get => _sizeText; private set => Set(ref _sizeText, value); }
    public string SelectionText { get => _selectionText; private set => Set(ref _selectionText, value); }
    public string TimingText { get => _timingText; private set => Set(ref _timingText, value); }

    /// <summary>Por qué hay archivos protegidos o no eliminados en el objetivo seleccionado.</summary>
    public string DetailSummary { get => _detailSummary; private set { if (Set(ref _detailSummary, value)) Raise(nameof(HasDetailSummary)); } }
    public bool HasDetailSummary => _detailSummary.Length > 0;

    /// <summary>Incidencias del último análisis, con ruta y motivo.</summary>
    public string IssuesText { get => _issuesText; private set { if (Set(ref _issuesText, value)) Raise(nameof(HasIssues)); } }
    public bool HasIssues => _issuesText.Length > 0;
    public string ConfirmText { get => _confirmText; private set => Set(ref _confirmText, value); }
    public bool IsConfirming { get => _confirming; private set { if (Set(ref _confirming, value)) CommandManager.InvalidateRequerySuggested(); } }

    public bool IsBusy
    {
        get => _busy;
        private set
        {
            if (!Set(ref _busy, value)) return;
            Raise(nameof(IsIdle));
            Raise(nameof(ShowEmptyState));
            if (value) { Progress = 0; ProgressText = ""; IsProgressIndeterminate = true; }
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsIdle => !_busy;

    public ResultRowVm? SelectedResult
    {
        get => _selectedResult;
        set { if (Set(ref _selectedResult, value)) ShowDetail(value); }
    }

    /// <summary>Cancela lo que esté en curso y devuelve una tarea que termina cuando la sección queda inactiva.</summary>
    public async Task StopAsync()
    {
        _workflow.Cancel();
        while (IsBusy) await Task.Delay(50);
    }

    private void BuildTree()
    {
        var keep = _selectedApp?.Id;
        Apps.Clear();
        var catalog = _selection.Catalog;
        foreach (var app in catalog.Apps.OrderBy(a => a.Group))
        {
            var total = app.Categories.Sum(c => c.Targets.Count);
            var appNode = new TreeNodeVm(CatalogText.App(app), "", Id("chk", app.Id), () => _selection.AppState(app.Id), v => _selection.SetApp(app.Id, v))
            {
                Id = app.Id,
                Group = CatalogText.Group(app.Group),
                CountSource = () =>
                {
                    var marked = app.Categories.Sum(c => c.Targets.Count(t => _selection.IsSelected(t.Key)));
                    return marked == 0 ? Loc.T("Nada marcado", "Nothing selected") : Loc.T($"{marked} de {total} marcados", $"{marked} of {total} selected");
                },
            };
            foreach (var category in app.Categories)
            {
                var name = CatalogText.Category(app, category);
                var description = CatalogText.Description(app, category);
                TreeNodeVm categoryNode;
                if (category.Targets.Count == 1 && category.Targets[0].ProfileName.Length == 0)
                {
                    var key = category.Targets[0].Key;
                    categoryNode = new(name, description, Id("chk", app.Id, category.Id),
                        () => _selection.IsSelected(key), v => _selection.SetTarget(key, v)) { Badge = CatalogText.Badge(category.Risk), BadgeKind = category.Risk.ToString() };
                }
                else
                {
                    categoryNode = new(name, description, Id("chk", app.Id, category.Id),
                        () => _selection.CategoryState(app.Id, category.Id), v => _selection.SetCategory(app.Id, category.Id, v)) { Badge = CatalogText.Badge(category.Risk), BadgeKind = category.Risk.ToString() };
                    foreach (var target in category.Targets)
                    {
                        var key = target.Key;
                        categoryNode.Children.Add(new(target.ProfileName, "", Id("chk", app.Id, category.Id, key.ProfileId),
                            () => _selection.IsSelected(key), v => _selection.SetTarget(key, v)));
                    }
                }
                appNode.Children.Add(categoryNode);
            }
            Apps.Add(appNode);
        }
        var view = new ListCollectionView(Apps);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(TreeNodeVm.Group)));
        AppsView = view;
        SelectedApp = Apps.FirstOrDefault(a => a.Id == keep) ?? Apps.FirstOrDefault();
    }

    private static string Id(params string[] parts) => string.Join("-", parts).Replace(' ', '_');

    private void OnLanguageChanged()
    {
        UpdateTotals();
        BuildTree();
        UpdateSelectionText();
        if (_present is not null) _present();
        if (IsConfirming) AskConfirmation();
        if (_present is null)
        {
            ResetHero();
            Status = Loc.T("Marca qué quieres limpiar y pulsa Analizar.", "Choose what to clean and press Analyze.");
        }
    }

    private void OnSelectionChanged()
    {
        foreach (var app in Apps) app.Refresh();
        IsConfirming = false;
        UpdateSelectionText();
        // El análisis anterior deja de ser válido: se retiran sus resultados para no mostrar datos obsoletos.
        if (_workflow.CurrentSession is null && (Results.Count > 0 || FilesText != "—"))
        {
            Results = [];
            _present = null;
            DetailTitle = DetailSummary = IssuesText = TimingText = "";
            ResetHero();
            if (!IsBusy) Status = _workflow.InvalidationReason ?? Loc.T("La selección cambió; vuelve a analizar.", "The selection changed; analyze again.");
        }
        CommandManager.InvalidateRequerySuggested();
    }

    private void ResetHero()
    {
        FilesText = SizeText = "—";
        Breakdown = [];
        HeroReady = HeroDone = false;
        HeroLabel = Loc.T("Espacio que puedes liberar", "Space you can free up");
        HeroHint = Loc.T("Marca qué limpiar y pulsa Analizar: verás aquí cuánto se libera y en qué.",
            "Choose what to clean and press Analyze: you will see here how much is freed and where.");
    }

    private void UpdateSelectionText()
    {
        var count = _selection.SelectedCount;
        SelectionText = count == 0 ? Loc.T("Nada marcado", "Nothing selected")
            : count == 1 ? Loc.T("1 objetivo marcado", "1 target selected")
            : Loc.T(count + " objetivos marcados", count + " targets selected");
    }

    private static string AppName(CleanupTarget target, CleanupCatalog catalog) =>
        catalog.Apps.FirstOrDefault(a => a.Id == target.Key.AppId) is { } app ? CatalogText.App(app) : target.AppName;

    private static string CategoryName(CleanupTarget target, CleanupCatalog catalog) =>
        catalog.Apps.FirstOrDefault(a => a.Id == target.Key.AppId) is { } app &&
        app.Categories.FirstOrDefault(c => c.Id == target.Key.CategoryId) is { } category ? CatalogText.Category(app, category) : target.CategoryName;

    private async Task AnalyzeAsync()
    {
        IsBusy = true;
        IsConfirming = false;
        Results = [];
        _present = null;
        DetailSummary = IssuesText = "";
        Status = Loc.T("Analizando… solo se leen nombres y tamaños; no se elimina nada.", "Analyzing… only names and sizes are read; nothing is deleted.");
        _settings.Update(s => s with { SelectedTargets = [.. _selection.SelectedKeys().Select(k => k.ToString())] });
        try
        {
            var progress = new Progress<AnalysisProgress>(p =>
            {
                Status = Loc.T($"Analizando · {p.TargetsDone}/{p.TargetsTotal} objetivos · {UiPolicy.Count(p.Files)} archivos · {UiPolicy.Human(p.Bytes)} · {p.Current}",
                    $"Analyzing · {p.TargetsDone}/{p.TargetsTotal} targets · {UiPolicy.Count(p.Files)} files · {UiPolicy.Human(p.Bytes)} · {p.Current}");
                IsProgressIndeterminate = p.TargetsTotal == 0;
                Progress = p.TargetsTotal == 0 ? 0 : 100.0 * p.TargetsDone / p.TargetsTotal;
                ProgressText = UiPolicy.Count(p.Files) + Loc.T(" archivos", " files");
            });
            var session = await _workflow.AnalyzeAsync(progress);
            var valid = ReferenceEquals(session, _workflow.CurrentSession);
            var watch = Stopwatch.StartNew();
            _present = () => PresentAnalysis(session, valid);
            _present();
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            watch.Stop();
            session.RecordUiTime(watch.Elapsed.TotalMilliseconds);
            TimingText = Loc.T("Tiempos: ", "Timings: ") + session.Timings;
            _log.Write("interfaz", "presentación del análisis: " + session.Timings);
            if (valid)
                Noticed?.Invoke(this, new(Loc.T("Análisis listo", "Analysis ready"), session.EligibleFiles == 0
                    ? Loc.T("No hay nada que limpiar en la selección.", "There is nothing to clean in the selection.")
                    : Loc.T("Puedes liberar ", "You can free up ") + Approx(session) + UiPolicy.Human(session.FreeableBytes) + Loc.T(". Revisa y pulsa Limpiar.", ". Review and press Clean."), NoticeKind.Info));
        }
        catch (InvalidOperationException error) { Status = error.Message; }
        finally { IsBusy = false; }
    }

    private void PresentAnalysis(AnalysisSession session, bool valid)
    {
        var catalog = _selection.Catalog;
        var largest = Math.Max(1, session.Targets.Select(t => t.FreeableBytes).DefaultIfEmpty(0).Max());
        Results = [.. session.Targets.OrderByDescending(t => t.FreeableBytes).Select(t =>
        {
            var (state, notes) = TargetState(t);
            return new ResultRowVm(AppName(t.Target, catalog), CategoryName(t.Target, catalog), t.Target.ProfileName, UiPolicy.Count(t.EligibleItems),
                t.Target.Data is null ? UiPolicy.Human(t.EligibleBytes) : "≈ " + UiPolicy.Human(t.DataBytes), state, t, null)
            { Share = (double)t.FreeableBytes / largest, HasNotes = notes };
        })];
        FilesText = UiPolicy.Count(session.EligibleFiles);
        SizeText = Approx(session) + UiPolicy.Human(session.FreeableBytes);
        Breakdown = BuildBreakdown(session.Targets.Select(t => (AppName(t.Target, catalog), t.FreeableBytes)));
        var kept = session.Targets.Sum(t => (long)t.ProtectedCount);
        var dataBytes = session.Targets.Sum(t => t.DataBytes);   // archivos que se conservan (los datos de navegador no son archivos)
        HeroReady = session.FreeableBytes > 0;
        HeroDone = false;
        HeroLabel = session.EligibleFiles == 0 ? Loc.T("No hay nada que liberar", "Nothing to free up") : Loc.T("Puedes liberar", "You can free up");
        HeroHint = session.EligibleFiles == 0
            ? Loc.T("La selección ya está limpia o todo lo que contiene está protegido.", "The selection is already clean or everything in it is protected.")
            : Loc.T($"en {UiPolicy.Count(session.EligibleFiles)} elementos de {session.Targets.Count(t => t.EligibleItems > 0)} objetivos · {UiPolicy.Count(kept)} se conservan por seguridad",
                $"in {UiPolicy.Count(session.EligibleFiles)} items from {session.Targets.Count(t => t.EligibleItems > 0)} targets · {UiPolicy.Count(kept)} kept for safety") +
              (dataBytes == 0 ? "" : Loc.T($"\nCachés y temporales: {UiPolicy.Human(session.EligibleBytes)} · datos del navegador: ≈ {UiPolicy.Human(dataBytes)}",
                $"\nCaches and temporary files: {UiPolicy.Human(session.EligibleBytes)} · browser data: ≈ {UiPolicy.Human(dataBytes)}"));
        SelectedResult = Results.FirstOrDefault(r => r.HasNotes) ?? Results.FirstOrDefault();
        IssuesText = string.Join(Environment.NewLine, session.Issues.Take(MaxIssuesShown).Select(i =>
                (i.Code switch
                {
                    "UnsafePath" => Loc.T("Enlace no recorrido", "Link not followed"), "Unavailable" => Loc.T("Sin acceso", "No access"),
                    "Excluded" => Loc.T("Excluido", "Excluded"), "TargetRefused" => Loc.T("Objetivo rechazado", "Target refused"),
                    "Cancelled" => Loc.T("Cancelado", "Cancelled"), "LimitReached" => Loc.T("Límite", "Limit"), _ => i.Code,
                }) + (i.Path.Length == 0 ? "" : " · " + UiPolicy.VisibleText(i.Path)) + " · " + i.Message)) +
            (session.Issues.Count > MaxIssuesShown ? Environment.NewLine + Loc.T($"… y {session.Issues.Count - MaxIssuesShown} más (ver ffcleaner.log)", $"… and {session.Issues.Count - MaxIssuesShown} more (see ffcleaner.log)") : "");
        Status = !valid
            ? _workflow.InvalidationReason ?? Loc.T("Análisis no válido; vuelve a analizar.", "Analysis no longer valid; analyze again.")
            : session.EligibleFiles == 0
                ? Loc.T("Análisis completo: no hay nada que limpiar en la selección.", "Analysis complete: there is nothing to clean in the selection.")
                : Loc.T($"Análisis completo · {UiPolicy.Count(session.EligibleFiles)} elementos limpiables ({Approx(session)}{UiPolicy.Human(session.FreeableBytes)}) · {UiPolicy.Count(kept)} protegidos u omitidos · {session.Issues.Count} incidencias. No se borró nada.",
                    $"Analysis complete · {UiPolicy.Count(session.EligibleFiles)} cleanable items ({Approx(session)}{UiPolicy.Human(session.FreeableBytes)}) · {UiPolicy.Count(kept)} protected or skipped · {session.Issues.Count} issues. Nothing was deleted.");
    }

    /// <summary>«≈ » cuando el total incluye datos de navegador: lo que devuelve compactar una base es una estimación.</summary>
    private static string Approx(AnalysisSession session) => session.Targets.Any(t => t.DataBytes > 0) ? "≈ " : "";

    private static IReadOnlyList<BreakdownVm> BuildBreakdown(IEnumerable<(string App, long Bytes)> items)
    {
        var byApp = items.GroupBy(i => i.App).Select(g => (App: g.Key, Bytes: g.Sum(i => i.Bytes))).Where(g => g.Bytes > 0)
            .OrderByDescending(g => g.Bytes).ToList();
        if (byApp.Count == 0) return [];
        var top = byApp.Take(MaxBreakdown).ToList();
        if (byApp.Count > MaxBreakdown) top.Add((Loc.T("Otras", "Others"), byApp.Skip(MaxBreakdown).Sum(g => g.Bytes)));
        var largest = (double)top.Max(g => g.Bytes);
        return [.. top.Select(g => new BreakdownVm(g.App, UiPolicy.Human(g.Bytes), g.Bytes / largest))];
    }

    private static long Remaining(TargetAnalysis target) => target.EligibleBytes + (target.DataItems > 0 ? target.DataBytes : 0);

    private static (string State, bool Notes) TargetState(TargetAnalysis target)
    {
        if (target.Refusal is not null)
            return (target.Refusal.StartsWith("Requiere administrador", StringComparison.Ordinal)
                ? Loc.T(target.Refusal, "Requires administrator: use «Restart as administrator».") : Loc.T("Rechazado: ", "Refused: ") + target.Refusal, true);
        if (target.Target.Data is not null)
        {
            var problem = target.Issues.FirstOrDefault(i => i.Code == "Locked");
            if (problem is not null) return (problem.Message, true);
            return target.DataItems == 0 ? (Loc.T("Sin datos que borrar", "No data to delete"), false)
                : (Loc.T(UiPolicy.Count(target.DataItems) + " elementos · se copia antes", UiPolicy.Count(target.DataItems) + " items · backed up first"), false);
        }
        var notes = new List<string>();
        if (target.ProtectedCount > 0) notes.Add(Loc.T(UiPolicy.Count(target.ProtectedCount) + " se conservan", UiPolicy.Count(target.ProtectedCount) + " kept"));
        if (target.Issues.Count > 0) notes.Add(Loc.T(target.Issues.Count + " incidencias", target.Issues.Count + " issues"));
        return notes.Count == 0 ? (Loc.T("Listo para limpiar", "Ready to clean"), false) : (string.Join(" · ", notes), true);
    }

    private void AskConfirmation()
    {
        var session = _workflow.CurrentSession;
        if (session is null) { Status = _workflow.CleanBlocker() ?? Loc.T("Primero analiza la selección.", "Analyze the selection first."); return; }
        var catalog = _selection.Catalog;
        var open = catalog.Apps
            .Where(a => session.Selection.Targets.Any(t => t.Key.AppId == a.Id) && a.ProcessNames.Any(_processes.IsRunning))
            .Select(CatalogText.App).ToList();
        var personal = session.Targets.Where(t => t.Target.Data is not null && t.DataItems > 0).Select(t => CategoryName(t.Target, catalog).ToLowerInvariant()).Distinct().ToList();
        var models = session.Targets.Where(t => t.Target.Key.CategoryId is "models" or "datasets" && t.EligibleCount > 0).Select(t => AppName(t.Target, catalog)).Distinct().ToList();
        var targets = session.Targets.Count(t => t.EligibleItems > 0);
        ConfirmText = Loc.T($"Se liberarán {Approx(session)}{UiPolicy.Human(session.FreeableBytes)}: se eliminarán de forma permanente {UiPolicy.Count(session.EligibleFiles)} elementos de {targets} objetivos. Nada desmarcado se toca; las contraseñas, nunca.",
                          $"{Approx(session)}{UiPolicy.Human(session.FreeableBytes)} will be freed: {UiPolicy.Count(session.EligibleFiles)} items from {targets} targets will be permanently deleted. Nothing unselected is touched; passwords, never.") +
            (personal.Count == 0 ? "" : Loc.T($"\n⚠ Datos personales: {string.Join(", ", personal)}. Se guarda una copia de cada base antes de borrar; las cookies cierran la sesión en los sitios.",
                                              $"\n⚠ Personal data: {string.Join(", ", personal)}. Each database is backed up first; cookies sign you out of websites.")) +
            (models.Count == 0 ? "" : Loc.T($"\n⚠ Modelos de IA de {string.Join(", ", models)}: volver a descargarlos puede llevar horas.",
                                            $"\n⚠ AI models from {string.Join(", ", models)}: downloading them again can take hours.")) +
            (open.Count == 0 ? "" : Loc.T($"\n⚠ {string.Join(", ", open)} está abierto: sus archivos en uso se omitirán y sus datos personales no se tocarán. Ciérralo si quieres una limpieza completa.",
                                          $"\n⚠ {string.Join(", ", open)} is open: files in use will be skipped and its personal data will not be touched. Close it for a full clean."));
        IsConfirming = true;
    }

    private async Task CleanAsync()
    {
        IsConfirming = false;
        IsBusy = true;
        Status = Loc.T("Limpiando…", "Cleaning…");
        try
        {
            var progress = new Progress<CleaningProgress>(p =>
            {
                Status = Loc.T($"Limpiando · {UiPolicy.Count(p.FilesProcessed)}/{UiPolicy.Count(p.FilesTotal)} archivos · {UiPolicy.Human(p.DeletedBytes)} liberados · {p.Current}",
                    $"Cleaning · {UiPolicy.Count(p.FilesProcessed)}/{UiPolicy.Count(p.FilesTotal)} files · {UiPolicy.Human(p.DeletedBytes)} freed · {p.Current}");
                IsProgressIndeterminate = p.FilesTotal == 0;
                Progress = p.FilesTotal == 0 ? 0 : Math.Min(100, 100.0 * p.FilesProcessed / p.FilesTotal);
                ProgressText = $"{Progress:0}% · {UiPolicy.Human(p.DeletedBytes)}";
            });
            var report = await _workflow.CleanAsync(progress);
            if (report.Refused)
            {
                Status = Loc.T("No se limpió nada: ", "Nothing was cleaned: ") + report.Refusal;
                return;
            }
            // Comprobación automática: se vuelve a analizar la misma selección (solo lectura) para que la tabla
            // muestre lo que queda de verdad y no lo que había antes de limpiar.
            Status = Loc.T("Comprobando lo que queda…", "Checking what is left…");
            AnalysisSession? after = null;
            try { after = await _workflow.AnalyzeAsync(); }
            catch (InvalidOperationException ex) { _log.Write("interfaz", "reanálisis tras limpiar no disponible: " + ex.Message); }
            _present = () => PresentCleaning(report, after);
            _present();
            UpdateTotals();
            Noticed?.Invoke(this, new(report.Cancelled ? Loc.T("Limpieza cancelada", "Cleaning cancelled") : Loc.T("Limpieza completada", "Cleaning complete"),
                UiPolicy.Human(report.DeletedBytes) + Loc.T(" liberados · ", " freed · ") + UiPolicy.Count(report.DeletedFiles) + Loc.T(" archivos", " files") +
                (report.NotDeleted > 0 ? " · " + UiPolicy.Count(report.NotDeleted) + Loc.T(" se conservaron", " kept") : ""),
                report.Cancelled || report.NotDeleted > 0 ? NoticeKind.Aviso : NoticeKind.Exito));
        }
        catch (InvalidOperationException error) { Status = error.Message; }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
        {
            // El motor ya anotó lo borrado hasta el fallo en la bitácora y en el total.
            _log.Write("interfaz", "limpieza interrumpida: " + error);
            Status = Loc.T("La limpieza se interrumpió por un error de archivo: ", "Cleaning stopped because of a file error: ") + error.Message +
                     Loc.T(" Vuelve a analizar para ver lo que queda.", " Analyze again to see what is left.");
            UpdateTotals();
        }
        finally { IsBusy = false; }
    }

    private void PresentCleaning(CleaningReport report, AnalysisSession? after)
    {
        var catalog = _selection.Catalog;
        var remaining = after?.Targets.ToDictionary(t => t.Target.Key, Remaining) ?? [];
        var remainingItems = after?.Targets.ToDictionary(t => t.Target.Key, t => t.EligibleItems) ?? [];
        var largest = Math.Max(1, report.Targets.Select(t => remaining.GetValueOrDefault(t.Key)).DefaultIfEmpty(0).Max());
        Results = [.. report.Targets.OrderByDescending(t => remaining.GetValueOrDefault(t.Key)).ThenByDescending(t => t.DeletedBytes).Select(t =>
        {
            var app = catalog.Apps.FirstOrDefault(a => a.Id == t.Key.AppId);
            var category = app?.Categories.FirstOrDefault(c => c.Id == t.Key.CategoryId);
            var left = remaining.GetValueOrDefault(t.Key);
            var freed = Loc.T("Liberado ", "Freed ") + UiPolicy.Human(t.DeletedBytes);
            var notes = CleaningState(t);
            var state = after is null ? notes
                : left == 0 && t.NotDeleted == 0 ? freed
                : left > 0 ? Loc.T("Quedan ", "Left: ") + UiPolicy.Human(left) + (t.NotDeleted > 0 ? " · " + notes : "")
                : notes;
            return new ResultRowVm(app is null ? t.AppName : CatalogText.App(app), app is null || category is null ? t.CategoryName : CatalogText.Category(app, category),
                t.ProfileName, UiPolicy.Count(after is null ? t.Deleted : remainingItems.GetValueOrDefault(t.Key)),
                UiPolicy.Human(after is null ? t.DeletedBytes : left), state, null, t)
            { Share = after is null ? (double)t.DeletedBytes / largest : (double)left / largest, HasNotes = left > 0 || t.NotDeleted > 0 };
        })];
        var leftTotal = remaining.Values.Sum();
        FilesText = UiPolicy.Count(report.DeletedFiles);
        SizeText = UiPolicy.Human(report.DeletedBytes);
        Breakdown = BuildBreakdown(report.Targets.Select(t => (catalog.Apps.FirstOrDefault(a => a.Id == t.Key.AppId) is { } a ? CatalogText.App(a) : t.AppName, t.DeletedBytes)));
        HeroReady = false;
        HeroDone = true;
        HeroLabel = report.Cancelled ? Loc.T("Liberado antes de cancelar", "Freed before cancelling") : Loc.T("Liberado", "Freed");
        HeroHint = Loc.T($"{UiPolicy.Count(report.DeletedFiles)} archivos eliminados · {UiPolicy.Count(report.NotDeleted)} se conservaron (en uso o protegidos)",
            $"{UiPolicy.Count(report.DeletedFiles)} files deleted · {UiPolicy.Count(report.NotDeleted)} kept (in use or protected)") +
            (after is null ? "" : leftTotal == 0
                ? Loc.T("\nComprobado: la selección quedó limpia.", "\nChecked: the selection is now clean.")
                : Loc.T($"\nComprobado: quedan {UiPolicy.Human(leftTotal)} (archivos en uso o creados de nuevo); puedes volver a pulsar Limpiar.",
                    $"\nChecked: {UiPolicy.Human(leftTotal)} remain (files in use or newly created); you can press Clean again."));
        TimingText = Loc.T("Duración: ", "Duration: ") + $"{(report.FinishedUtc - report.StartedUtc).TotalMilliseconds:N0} ms" +
            (report.JournalPath is null ? "" : Loc.T(" · Bitácora: ", " · Journal: ") + report.JournalPath);
        SelectedResult = Results.FirstOrDefault(r => r.Cleaning!.NotDeleted > 0) ?? Results.FirstOrDefault();
        IssuesText = "";
        var reasons = report.Reasons();
        Status = (report.Cancelled ? Loc.T("Limpieza cancelada (lo ya eliminado no se recupera)", "Cleaning cancelled (what was deleted cannot be recovered)") : Loc.T("Limpieza completada", "Cleaning complete")) +
            Loc.T($" · {UiPolicy.Count(report.DeletedFiles)} archivos eliminados · {UiPolicy.Human(report.DeletedBytes)} liberados", $" · {UiPolicy.Count(report.DeletedFiles)} files deleted · {UiPolicy.Human(report.DeletedBytes)} freed") +
            (report.EmptyDirectoriesRemoved > 0 ? Loc.T($" · {UiPolicy.Count(report.EmptyDirectoriesRemoved)} carpetas vacías retiradas", $" · {UiPolicy.Count(report.EmptyDirectoriesRemoved)} empty folders removed") : "") +
            Loc.T($" · {UiPolicy.Count(report.NotDeleted)} no eliminados", $" · {UiPolicy.Count(report.NotDeleted)} not deleted") +
            (reasons.Count == 0 ? ". " : " (" + string.Join("; ", reasons.Take(3).Select(r => UiPolicy.Count(r.Value) + " × " + r.Key.TrimEnd('.'))) + (reasons.Count > 3 ? "; …" : "") + "). ") +
            Loc.T("Analiza de nuevo para repetir.", "Analyze again to repeat.");
    }

    private static string CleaningState(TargetCleaningResult result)
    {
        var notes = new List<string>();
        if (result.Locked > 0) notes.Add(result.Locked + Loc.T(" en uso", " in use"));
        if (result.Skipped > 0) notes.Add(result.Skipped + Loc.T(" omitidos", " skipped"));
        if (result.Failed > 0) notes.Add(result.Failed + Loc.T(" con error", " failed"));
        if (result.Unconfirmed > 0) notes.Add(result.Unconfirmed + Loc.T(" sin confirmar", " unconfirmed"));
        if (result.Cancelled > 0) notes.Add(result.Cancelled + Loc.T(" cancelados", " cancelled"));
        if (result.Protected > 0) notes.Add(result.Protected + Loc.T(" protegidos", " protected"));
        return notes.Count == 0 ? Loc.T("Limpio", "Clean") : string.Join(" · ", notes);
    }

    /// <summary>Resumen del objetivo elegido en la tabla: qué se conserva y por qué. Sin lista de archivos.</summary>
    private void ShowDetail(ResultRowVm? row)
    {
        if (row is null) { DetailTitle = DetailSummary = ""; return; }
        var name = row.App + " · " + row.Category + (row.Profile.Length == 0 ? "" : " · " + row.Profile);
        if (row.Analysis is { Target.Data: not null } data)
        {
            DetailTitle = name + Loc.T(": " + UiPolicy.Count(data.DataItems) + " elementos dentro de las bases del navegador", ": " + UiPolicy.Count(data.DataItems) + " items inside the browser databases");
            DetailSummary = Loc.T($"Liberará unos {UiPolicy.Human(data.DataBytes)} (lo que ocupan esos registros; la cifra exacta se ve tras compactar la base). Se borran solo los registros de este tipo; el resto del perfil se conserva. Cada base se copia antes en BrowserBackups y se comprueba su integridad al terminar.",
                $"Frees about {UiPolicy.Human(data.DataBytes)} (the space those records take; the exact figure is known after compacting the database). Only records of this type are deleted; the rest of the profile is kept. Each database is first copied to BrowserBackups and checked for integrity afterwards.");
        }
        else if (row.Analysis is { } analysis)
        {
            DetailTitle = name + Loc.T($": {UiPolicy.Count(analysis.EligibleCount)} archivos limpiables ({UiPolicy.Human(analysis.EligibleBytes)})",
                $": {UiPolicy.Count(analysis.EligibleCount)} cleanable files ({UiPolicy.Human(analysis.EligibleBytes)})");
            DetailSummary = analysis.ProtectedCount == 0 ? "" : Loc.T("Se conservan ", "Kept: ") + UiPolicy.Count(analysis.ProtectedCount) + " — " +
                string.Join(" · ", analysis.ProtectedByVerdict.OrderByDescending(p => p.Value).Select(p => UiPolicy.Count(p.Value) + " × " + CatalogText.Verdict(p.Key)));
        }
        else if (row.Cleaning is { } cleaning)
        {
            DetailTitle = cleaning.NotDeleted == 0 ? name + Loc.T(": todo eliminado", ": everything deleted")
                : name + Loc.T($": {UiPolicy.Count(cleaning.NotDeleted)} no eliminados", $": {UiPolicy.Count(cleaning.NotDeleted)} not deleted");
            DetailSummary = string.Join(" · ", cleaning.Reasons.OrderByDescending(p => p.Value).Select(p => UiPolicy.Count(p.Value) + " × " + p.Key)) +
                (cleaning.EmptyDirectoriesRemoved > 0 ? (cleaning.Reasons.Count > 0 ? " · " : "") + UiPolicy.Count(cleaning.EmptyDirectoriesRemoved) + Loc.T(" carpetas vacías retiradas", " empty folders removed") : "");
        }
    }

    private void OnUi(Action action)
    {
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.BeginInvoke(action);
    }
}
