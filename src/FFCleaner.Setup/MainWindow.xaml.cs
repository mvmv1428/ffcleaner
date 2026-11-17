using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace FFCleaner.Setup;

/// <summary>Asistente por pasos, todo dentro de la misma ventana (sin cuadros emergentes): opciones → progreso → terminado.</summary>
public partial class MainWindow : Window
{
    private enum Step { Options, Working, Failed, Done }

    private readonly SetupOptions _options;
    private readonly string _source;
    private readonly InstallManifest? _manifest;
    private Step _step = Step.Options;
    private string? _installedExe;

    public MainWindow(SetupOptions options, string source)
    {
        InitializeComponent();
        _options = options;
        _source = source;
        if (options.Uninstall)
        {
            Title = "Desinstalar FFCleaner";
            HeaderTitle.Text = "Desinstalar FFCleaner";
            try
            {
                _manifest = SetupEngine.ReadManifest(options.InstallDirectory ?? source);
                HeaderSubtitle.Text = "Versión " + _manifest.Version;
                UninstallText.Text = $"Se quitarán FFCleaner {_manifest.Version} instalado en {_manifest.Destination}, sus accesos directos y su entrada en «Aplicaciones instaladas».";
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                UninstallText.Text = ex.Message;
                PrimaryButton.IsEnabled = false;
            }
            OptionsPage.Visibility = Visibility.Collapsed;
            UninstallPage.Visibility = Visibility.Visible;
            PrimaryButton.Content = "Desinstalar";
            PrimaryButton.Background = (System.Windows.Media.Brush)FindResource("DangerBrush");
            PrimaryButton.BorderBrush = PrimaryButton.Background;
            StepText.Text = "Paso 1 de 2";
        }
        else
        {
            HeaderSubtitle.Text = "Versión " + SetupEngine.Version + " · asistente de instalación";
            PathBox.Text = options.Destination;
            DesktopBox.IsChecked = options.DesktopShortcut;
            PathBox.TextChanged += (_, _) => UpdateInstallLabel();
            UpdateInstallLabel();
            StepText.Text = "Paso 1 de 3";
        }
    }

    private void UpdateInstallLabel()
    {
        var existing = File.Exists(Path.Combine(PathBox.Text.Trim(), "FFCleaner.exe"));
        PrimaryButton.Content = existing ? "Actualizar" : "Instalar";
        OptionsNote.Text = existing ? "Ya hay una versión instalada en esa carpeta: se actualizará y se conservarán tus datos." : "";
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Carpeta donde instalar FFCleaner" };
        if (dialog.ShowDialog(this) == true) PathBox.Text = Path.Combine(dialog.FolderName, "FFCleaner");
    }

    private void Secondary_Click(object sender, RoutedEventArgs e) => Close();

    private async void Primary_Click(object sender, RoutedEventArgs e)
    {
        if (_step == Step.Done)
        {
            if (!_options.Uninstall && LaunchBox.IsChecked == true && _installedExe is not null)
                Process.Start(new ProcessStartInfo(_installedExe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(_installedExe)! });
            Close();
            return;
        }
        if (_step == Step.Working) return;

        _step = Step.Working;
        OptionsPage.Visibility = UninstallPage.Visibility = DonePage.Visibility = Visibility.Collapsed;
        ProgressPage.Visibility = Visibility.Visible;
        ErrorBox.Visibility = Visibility.Collapsed;
        ProgressTitle.Text = _options.Uninstall ? "Desinstalando FFCleaner…" : "Instalando FFCleaner…";
        StepText.Text = _options.Uninstall ? "Paso 1 de 2" : "Paso 2 de 3";
        PrimaryButton.IsEnabled = SecondaryButton.IsEnabled = false;
        Bar.Value = 0;
        var progress = new Progress<(double Percent, string Text)>(p => { Bar.Value = p.Percent; ProgressText.Text = p.Text; });
        try
        {
            if (_options.Uninstall)
            {
                var removeData = RemoveDataBox.IsChecked == true;
                await Task.Run(() => SetupEngine.Uninstall(_manifest!, removeData, progress));
                ShowDone("FFCleaner se desinstaló",
                    removeData ? "Se quitaron el programa, sus accesos directos y tus datos de FFCleaner."
                               : $"Se quitaron el programa y sus accesos directos. Tus ajustes y respaldos del Registro siguen en {_manifest!.DataDirectory}.");
            }
            else
            {
                var options = _options with { Destination = Path.GetFullPath(PathBox.Text.Trim()), DesktopShortcut = DesktopBox.IsChecked == true };
                _installedExe = await Task.Run(() => SetupEngine.Install(_source, options, progress));
                ShowDone("FFCleaner " + SetupEngine.Version + " está instalado",
                    $"Instalado en {options.Destination}. Lo encontrarás en el menú Inicio" + (options.DesktopShortcut ? " y en el escritorio" : "") +
                    ". Para quitarlo, usa «Aplicaciones instaladas» de Windows.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.Security.SecurityException
                                   or System.Text.Json.JsonException or System.Runtime.InteropServices.COMException or System.ComponentModel.Win32Exception)
        {
            _step = Step.Failed;
            ProgressTitle.Text = _options.Uninstall ? "No se completó la desinstalación" : "No se completó la instalación";
            ErrorText.Text = ex.Message;
            ErrorBox.Visibility = Visibility.Visible;
            PrimaryButton.Content = "Reintentar";
            SecondaryButton.Content = "Cerrar";
            PrimaryButton.IsEnabled = SecondaryButton.IsEnabled = true;
        }
    }

    private void ShowDone(string title, string text)
    {
        _step = Step.Done;
        ProgressPage.Visibility = Visibility.Collapsed;
        DonePage.Visibility = Visibility.Visible;
        DoneTitle.Text = title;
        DoneText.Text = text;
        LaunchBox.Visibility = _options.Uninstall ? Visibility.Collapsed : Visibility.Visible;
        LaunchBox.IsChecked = _options.Launch;
        StepText.Text = _options.Uninstall ? "Paso 2 de 2" : "Paso 3 de 3";
        PrimaryButton.Content = _options.Uninstall ? "Cerrar" : "Finalizar";
        PrimaryButton.Background = (System.Windows.Media.Brush)FindResource("Accent");
        PrimaryButton.BorderBrush = PrimaryButton.Background;
        PrimaryButton.IsEnabled = true;
        SecondaryButton.Visibility = Visibility.Collapsed;
    }
}
