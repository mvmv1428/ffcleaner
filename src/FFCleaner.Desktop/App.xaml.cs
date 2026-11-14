using System.IO;
using System.Windows;
using FFCleaner.Cleaning;
using FFCleaner.Core;
using FFCleaner.RegistryAnalysis;

namespace FFCleaner.Desktop;

/// <summary>Raíz de composición: aquí se crean los servicios y se entregan a las vistas-modelo por sus interfaces.</summary>
public partial class App : Application
{
    private DiagnosticsLog _log = DiagnosticsLog.Null;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Hasta que exista la ventana principal, cerrar la ventanita de actualización no debe cerrar la aplicación.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException += (_, a) =>
        {
            _log.Write("error", a.Exception.ToString());
            MessageBox.Show("Error inesperado: " + a.Exception.Message + Environment.NewLine + Environment.NewLine +
                "Si había una operación en curso pudo quedar incompleta; " +
                "consulta la bitácora en " + (_log.Path ?? "la carpeta de FFCleaner") + ".", "FFCleaner", MessageBoxButton.OK, MessageBoxImage.Warning);
            a.Handled = true;
        };

        IFolderProvider folders;
        RegistryScope scope;
        LaunchOptions launch;
        try
        {
            launch = UiPolicy.ParseOptions(e.Args);
            var (sandboxDirectory, sandboxRegistry) = (launch.SandboxDirectory, launch.SandboxRegistry);
            folders = sandboxDirectory is null ? new SystemFolderProvider() : new SandboxFolderProvider(sandboxDirectory);
            scope = sandboxRegistry is null ? RegistryScope.Production : RegistryScope.Sandbox(sandboxRegistry);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageBox.Show("No se puede iniciar: " + ex.Message, "FFCleaner", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(2);
            return;
        }

        _log = new DiagnosticsLog(Path.Combine(folders.StateDirectory, "Logs", "ffcleaner.log"));
        _log.Write("inicio", "FFCleaner " + (typeof(App).Assembly.GetName().Version?.ToString(3) ?? "") + (folders.IsSandbox ? " · sandbox " + folders.ContainmentRoot : "") +
            " · registro " + scope.Id + " · administrador=" + RegistryScope.IsElevated);

        // Ajustes: si el archivo existe pero no se puede leer, se trabaja sin persistencia para no sobrescribirlo.
        SettingsStore? store = new(Path.Combine(folders.StateDirectory, "settings.json"));
        var settings = UserSettings.Empty;
        try { settings = store.Load(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _log.Write("ajustes", "Ajustes no cargados; no se sobrescribirán: " + ex.Message);
            store = null;
        }
        var holder = new SettingsHolder(store, settings, _log);
        // Tema e idioma guardados, antes de crear la ventana; «Sistema» sigue los cambios de Windows en caliente.
        Loc.Apply(settings.Language);
        ThemeManager.Apply(settings.Theme);
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, args) =>
        {
            if (args.Category == Microsoft.Win32.UserPreferenceCategory.General && holder.Current.Theme == "system")
                Dispatcher.BeginInvoke(() => ThemeManager.Apply("system"));
        };

        // Actualizaciones: del repositorio configurado al compilar; en sandbox, solo con una fuente de prueba local.
        // Se buscan ANTES de abrir la ventana principal: si hay versión nueva, se instala y FFCleaner se reabre ya actualizado.
        var updater = new UpdateService(_log, folders.IsSandbox ? null : UpdateService.ConfiguredRepository(), launch.UpdateFeed);
        if (settings.AutoUpdate && await UpdateWindow.TryUpdateAsync(updater, UiPolicy.RelaunchArguments(e.Args), _log))
        {
            Shutdown(0);
            return;
        }

        IAnalysisService analysis = new FileAnalysisService(folders, settings.Exclusions, log: _log);
        IFileCleaningService cleaning = new FileCleaningService(folders, _log, processes: new SystemProcessProbe());
        var catalog = analysis.Discover();
        var saved = settings.SelectedTargets.Select(t => TargetKey.TryParse(t, out var key) ? key : (TargetKey?)null).OfType<TargetKey>();
        var selection = new SelectionModel(catalog, saved);
        var workflow = new CleanupWorkflow(selection, analysis, cleaning);

        void Trace(string message) => _log.Write("registro", message);
        var scanner = new RegistryScanner(scope, trace: Trace);
        var backups = new RegistryBackupStore(scope, Path.Combine(folders.StateDirectory, "RegistryBackups"), Trace);
        var repair = new RegistryRepairService(scanner, backups, Trace);

        var notes = new List<string>(catalog.Notes);
        if (settings.Exclusions.Count > 0)
            notes.Insert(0, settings.Exclusions.Count + " rutas protegidas por exclusiones guardadas en una versión anterior.");
        var cleanupVm = new CleanupViewModel(workflow, new SystemProcessProbe(), holder, _log, notes, new CleanupTotals(folders.StateDirectory, _log));
        // Carpetas Inicio: las reales de Windows, o sus equivalentes dentro del sandbox.
        var userStartup = folders.IsSandbox ? Path.Combine(folders.RoamingAppData, "Microsoft", "Windows", "Start Menu", "Programs", "Startup")
            : Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        var commonStartup = folders.IsSandbox ? Path.Combine(folders.ProgramData, "Microsoft", "Windows", "Start Menu", "Programs", "StartUp")
            : Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);
        var startup = new StartupManager(scope, userStartup, commonStartup, trace: message => _log.Write("inicio", message));
        var registryVm = new RegistryViewModel(scanner, repair, backups, holder, folders.IsSandbox);
        var startupVm = new StartupViewModel(startup, registryVm.ElevateCommand, registryVm.CanElevate, folders.IsSandbox);
        var updates = new UpdateCoordinator(updater, holder, _log);
        var settingsVm = new SettingsViewModel(holder, folders.StateDirectory, Path.Combine(AppContext.BaseDirectory, "FFCleaner.Mcp.exe"), updates);
        string Mode() => folders.IsSandbox ? Loc.T("MODO SANDBOX · datos de prueba", "SANDBOX MODE · test data")
            : RegistryScope.IsElevated ? Loc.T("ADMINISTRADOR", "ADMINISTRATOR") : Loc.T("USUARIO ESTÁNDAR", "STANDARD USER");
        MainWindow = new MainWindow(cleanupVm, registryVm, startupVm, settingsVm, Mode, updates, launch.UpdatedFrom);
        MainWindow.Closed += (_, _) => workflow.Dispose();
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        MainWindow.Show();
    }
}
