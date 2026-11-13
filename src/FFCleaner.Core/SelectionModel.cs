using System.Security.Cryptography;
using System.Text;

namespace FFCleaner.Core;

/// <summary>
/// Estado de selección de la jerarquía Aplicación → Categoría → Perfil. Cada objetivo tiene su propio
/// estado: marcar o desmarcar una aplicación solo afecta a sus propios objetivos. Cualquier cambio
/// incrementa <see cref="Version"/> y dispara <see cref="Changed"/>. Es seguro entre hilos.
/// </summary>
public sealed class SelectionModel
{
    private readonly object _gate = new();
    private readonly Dictionary<TargetKey, CleanupTarget> _targets = [];
    private readonly HashSet<TargetKey> _selected = [];
    private readonly HashSet<TargetKey> _optIn = [];
    private long _version;

    public SelectionModel(CleanupCatalog catalog, IEnumerable<TargetKey>? initiallySelected = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        Catalog = catalog;
        foreach (var target in catalog.AllTargets())
            if (!_targets.TryAdd(target.Key, target))
                throw new ArgumentException("Objetivo duplicado en el catálogo: " + target.Key, nameof(catalog));
        foreach (var category in catalog.Apps.SelectMany(a => a.Categories))
            if (category.Risk is CategoryRisk.DatosPersonales or CategoryRisk.MuyGrande)
                foreach (var target in category.Targets) _optIn.Add(target.Key);
        // Los ajustes guardados pueden nombrar objetivos que ya no existen: se ignoran sin ampliar nada.
        foreach (var key in initiallySelected ?? [])
            if (_targets.ContainsKey(key)) _selected.Add(key);
    }

    public CleanupCatalog Catalog { get; }

    /// <summary>Se dispara tras cada cambio efectivo, fuera del bloqueo interno.</summary>
    public event EventHandler? Changed;

    public long Version => Interlocked.Read(ref _version);

    public int SelectedCount { get { lock (_gate) return _selected.Count; } }

    public bool IsSelected(TargetKey key) { lock (_gate) return _selected.Contains(key); }

    public IReadOnlyList<TargetKey> SelectedKeys()
    {
        lock (_gate) return [.. _selected.OrderBy(k => k.ToString(), StringComparer.Ordinal)];
    }

    public bool SetTarget(TargetKey key, bool selected)
    {
        if (!_targets.ContainsKey(key)) throw new ArgumentException("Objetivo desconocido: " + key, nameof(key));
        return Apply([key], selected);
    }

    /// <summary>Marca o desmarca todos los perfiles de una categoría de una aplicación.</summary>
    public bool SetCategory(string appId, string categoryId, bool selected) =>
        Apply(Keys(k => k.AppId == appId && k.CategoryId == categoryId, appId + "/" + categoryId), selected);

    /// <summary>Marca o desmarca todas las categorías y perfiles de una única aplicación.</summary>
    /// <remarks>
    /// Al marcar, solo se incluyen las categorías normales: los datos personales (cookies, historial…) y los modelos
    /// de IA se marcan siempre uno a uno. Al desmarcar, se quita todo lo de la aplicación.
    /// </remarks>
    public bool SetApp(string appId, bool selected)
    {
        var keys = Keys(k => k.AppId == appId, appId);
        return Apply(selected ? [.. keys.Where(k => !_optIn.Contains(k))] : keys, selected);
    }

    /// <summary>Marca todas las categorías normales del catálogo (nunca datos personales ni modelos). Es una acción explícita.</summary>
    public bool SelectAll() => Apply([.. _targets.Keys.Where(k => !_optIn.Contains(k))], true);

    /// <summary>¿Es un objetivo que solo se marca de forma individual (datos personales o modelos de IA)?</summary>
    public bool IsOptIn(TargetKey key) => _optIn.Contains(key);

    public bool Clear()
    {
        lock (_gate)
        {
            if (_selected.Count == 0) return false;
            _selected.Clear();
            Interlocked.Increment(ref _version);
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>true = todo marcado, false = nada, null = parcial.</summary>
    public bool? CategoryState(string appId, string categoryId) =>
        State(k => k.AppId == appId && k.CategoryId == categoryId);

    /// <summary>true = todo marcado, false = nada, null = parcial.</summary>
    /// <remarks>Marcada cuando están todas sus categorías normales, aunque las de marcado individual no lo estén.</remarks>
    public bool? AppState(string appId)
    {
        lock (_gate)
        {
            var keys = _targets.Keys.Where(k => k.AppId == appId).ToList();
            var regular = keys.Where(k => !_optIn.Contains(k)).ToList();
            var marked = keys.Count(_selected.Contains);
            if (marked == 0) return false;
            if (regular.Count > 0 && regular.All(_selected.Contains)) return true;
            return marked == keys.Count ? true : null;
        }
    }

    /// <summary>Copia inmutable de la selección vigente.</summary>
    public SelectionSnapshot Snapshot()
    {
        lock (_gate)
        {
            var targets = _selected.OrderBy(k => k.ToString(), StringComparer.Ordinal).Select(k => _targets[k]).ToArray();
            return new SelectionSnapshot(Interlocked.Read(ref _version), targets);
        }
    }

    private List<TargetKey> Keys(Func<TargetKey, bool> match, string label)
    {
        var keys = _targets.Keys.Where(match).ToList();
        if (keys.Count == 0) throw new ArgumentException("No hay objetivos para " + label);
        return keys;
    }

    private bool Apply(List<TargetKey> keys, bool selected)
    {
        var changed = false;
        lock (_gate)
        {
            foreach (var key in keys) changed |= selected ? _selected.Add(key) : _selected.Remove(key);
            if (changed) Interlocked.Increment(ref _version);
        }
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
        return changed;
    }

    private bool? State(Func<TargetKey, bool> match)
    {
        lock (_gate)
        {
            int total = 0, marked = 0;
            foreach (var key in _targets.Keys)
            {
                if (!match(key)) continue;
                total++;
                if (_selected.Contains(key)) marked++;
            }
            return marked == 0 ? false : marked == total ? true : null;
        }
    }
}

/// <summary>Selección congelada. Su huella identifica los objetivos y sus raíces exactas.</summary>
public sealed class SelectionSnapshot
{
    private readonly HashSet<TargetKey> _keys;

    internal SelectionSnapshot(long version, IReadOnlyList<CleanupTarget> targets)
    {
        Version = version;
        Targets = targets;
        _keys = [.. targets.Select(t => t.Key)];
        var text = new StringBuilder();
        foreach (var target in targets)
        {
            text.Append(target.Key).Append('\n');
            foreach (var root in target.Roots) text.Append('\t').Append(root.Path.ToUpperInvariant()).Append('\n');
        }
        Fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    public long Version { get; }
    public IReadOnlyList<CleanupTarget> Targets { get; }
    public string Fingerprint { get; }
    public bool IsEmpty => Targets.Count == 0;
    public bool Contains(TargetKey key) => _keys.Contains(key);

    /// <summary>
    /// ¿Es <paramref name="target"/> exactamente el objetivo seleccionado con esa clave (mismas raíces y ámbito)?
    /// Impide que una sesión presente con la clave de un objetivo marcado las rutas de otro.
    /// </summary>
    public bool Matches(CleanupTarget target)
    {
        var selected = Targets.FirstOrDefault(t => t.Key == target.Key);
        return selected is not null &&
               string.Equals(selected.DataRoot, target.DataRoot, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(selected.Anchor, target.Anchor, StringComparison.OrdinalIgnoreCase) &&
               selected.Roots.Count == target.Roots.Count &&
               selected.Roots.Zip(target.Roots).All(pair =>
                   string.Equals(pair.First.Path, pair.Second.Path, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(pair.First.Leaf, pair.Second.Leaf, StringComparison.OrdinalIgnoreCase) &&
                   pair.First.Policy.MinimumAge == pair.Second.Policy.MinimumAge &&
                   pair.First.Policy.VerifyLinks == pair.Second.Policy.VerifyLinks &&
                   pair.First.Policy.NameGuards == pair.Second.Policy.NameGuards);
    }
}
