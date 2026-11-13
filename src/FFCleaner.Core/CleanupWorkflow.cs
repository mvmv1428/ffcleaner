namespace FFCleaner.Core;

public enum WorkflowState { SinAnalisis, Analizando, Analizado, Invalidado, Limpiando, Limpiado }

/// <summary>
/// Coordina Seleccionar → Analizar → Limpiar sin depender de ninguna interfaz gráfica. Mantiene la única
/// sesión de análisis vigente y la invalida en el mismo instante en que cambia cualquier casilla, de modo
/// que nunca se pueda limpiar con una selección antigua.
/// </summary>
public sealed class CleanupWorkflow : IDisposable
{
    private readonly object _gate = new();
    private readonly SelectionModel _selection;
    private readonly IAnalysisService _analysis;
    private readonly IFileCleaningService _cleaning;
    private readonly Func<DateTime> _clock;
    private AnalysisSession? _session;
    private CancellationTokenSource? _running;
    private WorkflowState _state = WorkflowState.SinAnalisis;
    private string? _invalidationReason;
    private CleaningReport? _lastReport;

    public CleanupWorkflow(SelectionModel selection, IAnalysisService analysis, IFileCleaningService cleaning,
        Func<DateTime>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(cleaning);
        _selection = selection;
        _analysis = analysis;
        _cleaning = cleaning;
        _clock = clock ?? (() => DateTime.UtcNow);
        _selection.Changed += OnSelectionChanged;
    }

    public event EventHandler? StateChanged;

    public SelectionModel Selection => _selection;
    public WorkflowState State { get { lock (_gate) return _state; } }
    public bool IsBusy { get { lock (_gate) return _state is WorkflowState.Analizando or WorkflowState.Limpiando; } }

    /// <summary>Sesión que autoriza una limpieza, o null si no hay análisis vigente.</summary>
    public AnalysisSession? CurrentSession { get { lock (_gate) return _session; } }

    public string? InvalidationReason { get { lock (_gate) return _invalidationReason; } }
    public CleaningReport? LastReport { get { lock (_gate) return _lastReport; } }

    /// <summary>Motivo por el que ahora mismo no se puede limpiar, o null si se puede.</summary>
    public string? CleanBlocker()
    {
        lock (_gate)
        {
            if (_state is WorkflowState.Analizando or WorkflowState.Limpiando) return "Hay una operación en curso.";
            if (_session is null) return _invalidationReason ?? "Primero analiza la selección.";
            return _session.CleanBlocker(_selection.Snapshot(), _clock());
        }
    }

    /// <summary>
    /// Analiza la selección vigente. La sesión devuelta solo queda como vigente si la selección no cambió
    /// mientras tanto y el análisis no se canceló; compruébese con <see cref="CurrentSession"/>.
    /// </summary>
    public async Task<AnalysisSession> AnalyzeAsync(IProgress<AnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        SelectionSnapshot snapshot;
        CancellationTokenSource running;
        lock (_gate)
        {
            if (_state is WorkflowState.Analizando or WorkflowState.Limpiando)
                throw new InvalidOperationException("Hay una operación en curso.");
            snapshot = _selection.Snapshot();
            if (snapshot.IsEmpty) throw new InvalidOperationException("Marca al menos una categoría antes de analizar.");
            _session = null;
            _invalidationReason = null;
            _state = WorkflowState.Analizando;
            _running = running = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        }
        Notify();

        AnalysisSession? session = null;
        try
        {
            session = await _analysis.AnalyzeAsync(snapshot, progress, running.Token).ConfigureAwait(false);
            return session;
        }
        finally
        {
            lock (_gate)
            {
                _running = null;
                if (session is null)
                    Invalidate("El análisis falló; vuelve a analizar.");
                else if (_selection.Version != snapshot.Version)
                    Invalidate("La selección cambió durante el análisis; vuelve a analizar.");
                else if (session.Cancelled)
                    Invalidate("El análisis se canceló; vuelve a analizar.");
                else if (session.Limited)
                    Invalidate("El análisis alcanzó el límite de archivos; reduce la selección.");
                else
                {
                    _session = session;
                    _state = WorkflowState.Analizado;
                }
            }
            running.Dispose();
            Notify();
        }
    }

    /// <summary>
    /// Limpia con la sesión vigente. Si no la hay, o ya no corresponde a la selección, devuelve un
    /// informe rechazado sin tocar ningún archivo.
    /// </summary>
    public async Task<CleaningReport> CleanAsync(IProgress<CleaningProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        AnalysisSession session;
        SelectionSnapshot snapshot;
        CancellationTokenSource running;
        lock (_gate)
        {
            if (_state is WorkflowState.Analizando or WorkflowState.Limpiando)
                throw new InvalidOperationException("Hay una operación en curso.");
            var now = _clock();
            if (_session is null)
                return CleaningReport.Refuse(Guid.Empty, now, _invalidationReason ?? "Primero analiza la selección.");
            snapshot = _selection.Snapshot();
            if (_session.CleanBlocker(snapshot, now) is { } blocker)
                return CleaningReport.Refuse(_session.Id, now, blocker);
            session = _session;
            _state = WorkflowState.Limpiando;
            _running = running = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        }
        Notify();

        CleaningReport? report = null;
        try
        {
            report = await _cleaning.CleanAsync(session, snapshot, progress, running.Token).ConfigureAwait(false);
            return report;
        }
        finally
        {
            lock (_gate)
            {
                _running = null;
                _session = null;
                _lastReport = report;
                _invalidationReason = "La limpieza ya se ejecutó; analiza de nuevo para repetirla.";
                _state = report is null || _selection.Version != snapshot.Version
                    ? WorkflowState.Invalidado
                    : WorkflowState.Limpiado;
            }
            running.Dispose();
            Notify();
        }
    }

    /// <summary>Cancela la operación en curso, si la hay. Lo ya eliminado no se deshace.</summary>
    public void Cancel()
    {
        lock (_gate)
        {
            try { _running?.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    public void Dispose()
    {
        _selection.Changed -= OnSelectionChanged;
        Cancel();
    }

    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            switch (_state)
            {
                case WorkflowState.Analizando:
                case WorkflowState.Limpiando:
                    // La operación trabaja sobre una selección que ya no es la vigente: se detiene.
                    try { _running?.Cancel(); }
                    catch (ObjectDisposedException) { }
                    return;
                case WorkflowState.Analizado:
                case WorkflowState.Limpiado:
                    Invalidate("La selección cambió; vuelve a analizar antes de limpiar.");
                    break;
                default:
                    return;
            }
        }
        Notify();
    }

    private void Invalidate(string reason)
    {
        _session = null;
        _invalidationReason = reason;
        _state = WorkflowState.Invalidado;
    }

    private void Notify() => StateChanged?.Invoke(this, EventArgs.Empty);
}
