using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FFCleaner.RegistryAnalysis;

namespace FFCleaner.Desktop;

/// <summary>Fila de la lista de programas de inicio. Cambiar el interruptor aplica el cambio en el acto (es reversible).</summary>
public sealed class StartupRowVm(StartupEntry entry, bool canToggle, Func<StartupRowVm, bool, bool> toggle) : ObservableObject
{
    private bool _enabled = entry.Enabled;
    private string? _displayName, _publisher;
    private ImageSource? _icon;

    public StartupEntry Entry { get; private set; } = entry;
    /// <summary>Nombre de la entrada tal y como está en el Registro o en la carpeta Inicio.</summary>
    public string Name => UiPolicy.VisibleText(Entry.Name);
    /// <summary>Nombre del programa (descripción del ejecutable), o el de la entrada mientras se averigua.</summary>
    public string DisplayName => _displayName ?? Name;
    public string Subtitle
    {
        get
        {
            var parts = new List<string>();
            if (_publisher is not null) parts.Add(_publisher);
            if (!string.Equals(DisplayName, Name, StringComparison.CurrentCultureIgnoreCase)) parts.Add(Loc.T("entrada «", "entry «") + Name + "»");
            return parts.Count == 0 ? CatalogText.StartupSource(Entry) : string.Join(" · ", parts);
        }
    }
    public ImageSource? Icon => _icon;
    public bool HasIcon => _icon is not null;
    public string Initial => DisplayName.Length == 0 ? "?" : DisplayName[..1].ToUpperInvariant();
    public string Source => CatalogText.StartupSource(Entry);
    public string Command => UiPolicy.VisibleText(Entry.Command);
    public bool CanToggle => canToggle;
    public string AutomationId => "startup-" + string.Concat(Entry.Name.Where(char.IsLetterOrDigit));
    public string TargetKey => Entry.TargetState.ToString();
    public string TargetText => Entry.TargetState switch
    {
        TargetState.Present => Loc.T("Encontrado", "Found"),
        TargetState.Missing => Loc.T("No encontrado", "Not found"),
        _ => Loc.T("No verificable", "Cannot verify"),
    };
    public string ToggleHint => canToggle ? (_enabled ? Loc.T("Activo", "Enabled") : Loc.T("Desactivado", "Disabled")) : Loc.T("Requiere administrador", "Requires administrator");

    public void RefreshTexts()
    {
        foreach (var name in new[] { nameof(Subtitle), nameof(Source), nameof(TargetText), nameof(ToggleHint) }) Raise(name);
    }

    internal void SetIdentity(AppIdentity identity, ImageSource? icon)
    {
        _displayName = identity.DisplayName;
        _publisher = identity.Publisher;
        _icon = icon;
        Raise(nameof(DisplayName));
        Raise(nameof(Subtitle));
        Raise(nameof(Initial));
        Raise(nameof(Icon));
        Raise(nameof(HasIcon));
    }

    public bool IsEnabled
    {
        get => _enabled;
        set
        {
            if (value == _enabled || !canToggle) return;
            if (!toggle(this, value)) { Raise(); return; }   // si falla, el interruptor vuelve a su posición
            _enabled = value;
            Entry = Entry with { Enabled = value };
            Raise();
            Raise(nameof(ToggleHint));
        }
    }
}

/// <summary>
/// Sección «Inicio de Windows»: lista los programas que arrancan con Windows con su nombre e icono reales y
/// permite activarlos o desactivarlos de forma reversible (mismo mecanismo que el Administrador de tareas).
/// </summary>
public sealed class StartupViewModel : ObservableObject
{
    private readonly StartupManager _manager;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private IReadOnlyList<StartupRowVm> _rows = [];
    private string _status = Loc.T("Pulsa Actualizar para ver los programas que se inician con Windows.", "Press Refresh to see the programs that start with Windows.");
    private readonly bool _sandbox;
    private bool _loaded;
    private int _generation;

    public StartupViewModel(StartupManager manager, ICommand elevate, bool canElevate, bool sandbox)
    {
        _manager = manager;
        ElevateCommand = elevate;
        CanElevate = canElevate;
        _sandbox = sandbox;
        RefreshCommand = new RelayCommand(Refresh);
        Loc.Changed += (_, _) =>
        {
            foreach (var row in _rows) row.RefreshTexts();
            Raise(nameof(Note));
            Raise(nameof(Summary));
            if (_loaded) Status = Loc.T("Programas de inicio actualizados. Desactivar no borra nada y se puede deshacer.", "Startup programs updated. Disabling deletes nothing and can be undone.");
        };
    }

    public ICommand RefreshCommand { get; }
    public ICommand ElevateCommand { get; }
    public bool CanElevate { get; }
    public string Note => _sandbox
        ? Loc.T("Modo sandbox: solo se usan claves y carpetas de prueba.", "Sandbox mode: only test keys and folders are used.")
        : _manager.CanChangeMachineEntries
            ? Loc.T("Ejecutando como administrador: también puedes cambiar los programas de todos los usuarios.", "Running as administrator: you can also change programs for all users.")
            : Loc.T("Los programas de todos los usuarios se muestran, pero cambiarlos requiere reiniciar como administrador.", "Programs for all users are shown, but changing them requires restarting as administrator.");
    public IReadOnlyList<StartupRowVm> Entries { get => _rows; private set => Set(ref _rows, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public int TotalCount => _rows.Count;
    public int EnabledCount => _rows.Count(r => r.IsEnabled);
    public int DisabledCount => _rows.Count(r => !r.IsEnabled);
    public string Summary => Loc.T($"{TotalCount} programas · {EnabledCount} activos · {DisabledCount} desactivados", $"{TotalCount} programs · {EnabledCount} enabled · {DisabledCount} disabled");
    public bool ShowEmptyState => _loaded && _rows.Count == 0;

    public event EventHandler<Notice>? Noticed;

    /// <summary>La ventana lo llama al abrir la sección por primera vez.</summary>
    public void EnsureLoaded()
    {
        if (!_loaded) Refresh();
    }

    private void Refresh()
    {
        var canMachine = _manager.CanChangeMachineEntries;
        Entries = [.. _manager.List().Select(e => new StartupRowVm(e, !e.RequiresAdmin || canMachine, Toggle))];
        _loaded = true;
        RaiseCounts();
        Raise(nameof(ShowEmptyState));
        Status = Loc.T("Programas de inicio actualizados. Desactivar no borra nada y se puede deshacer.", "Startup programs updated. Disabling deletes nothing and can be undone.");
        LoadIdentities(_rows, ++_generation);
    }

    /// <summary>
    /// Nombre e icono de cada programa en un hilo STA aparte (las API de Shell lo requieren), para no bloquear la
    /// ventana. Los iconos se congelan para poder pasarlos al hilo de la interfaz.
    /// </summary>
    private void LoadIdentities(IReadOnlyList<StartupRowVm> rows, int generation)
    {
        var work = rows.Select(r => (Row: r, r.Entry.Name, Path: r.Entry.TargetPath)).ToList();
        var thread = new Thread(() =>
        {
            foreach (var (row, name, path) in work)
            {
                if (generation != Volatile.Read(ref _generation)) return;
                var identity = AppIdentityResolver.Resolve(name, path);
                var icon = ShellIcon.Load(identity.ExecutablePath ?? (path is not null && File.Exists(path) ? path : null));
                _dispatcher.BeginInvoke(() => { if (generation == _generation) row.SetIdentity(identity, icon); });
            }
        }) { IsBackground = true, Name = "FFCleaner identidades de inicio" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private bool Toggle(StartupRowVm row, bool enabled)
    {
        var result = _manager.SetEnabled(row.Entry, enabled);
        var message = !Loc.English ? result.Message
            : !result.Success ? "Could not change «" + row.DisplayName + "»: " + result.Message
            : enabled ? "«" + row.DisplayName + "» will start with Windows." : "«" + row.DisplayName + "» will no longer start with Windows. You can re-enable it any time.";
        Status = message;
        if (result.Success)
        {
            Noticed?.Invoke(this, new(enabled ? Loc.T("Programa activado", "Program enabled") : Loc.T("Programa desactivado", "Program disabled"), message, NoticeKind.Exito));
            _dispatcher.BeginInvoke(RaiseCounts);
        }
        else Noticed?.Invoke(this, new(Loc.T("No se cambió", "Not changed"), message, NoticeKind.Aviso));
        return result.Success;
    }

    private void RaiseCounts()
    {
        Raise(nameof(TotalCount));
        Raise(nameof(EnabledCount));
        Raise(nameof(DisabledCount));
        Raise(nameof(Summary));
    }
}

/// <summary>Icono de un archivo tal y como lo muestra el Explorador (sin la flecha de acceso directo).</summary>
internal static class ShellIcon
{
    private const uint ShgfiIcon = 0x100, ShgfiLargeIcon = 0x0;

    public static ImageSource? Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var info = new ShFileInfo();
        var result = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), ShgfiIcon | ShgfiLargeIcon);
        if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
        try
        {
            var image = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref ShFileInfo info, uint size, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);
}
