using System.Net.Http;
using System.Windows;
using FFCleaner.Core;

namespace FFCleaner.Desktop;

/// <summary>
/// Ventana «Actualizando FFCleaner»: se muestra al abrir, antes de la ventana principal, solo si hay una versión
/// nueva. Descarga y verifica el paquete y lanza el instalador, que termina la instalación y vuelve a abrir FFCleaner.
/// Así la actualización nunca coincide con un análisis, una limpieza o una reparación.
/// </summary>
public partial class UpdateWindow : Window
{
    private readonly CancellationTokenSource _cancel = new();

    public UpdateWindow()
    {
        InitializeComponent();
        TitleText.Text = Loc.T("Actualizando FFCleaner", "Updating FFCleaner");
        SkipButton.Content = Loc.T("Abrir sin actualizar", "Open without updating");
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
    }

    /// <summary>
    /// Comprueba (como mucho unos segundos) si hay versión nueva; si la hay, la descarga, la verifica y lanza el
    /// instalador. Devuelve true si FFCleaner debe cerrarse para que el instalador termine; false para abrir la app.
    /// </summary>
    public static async Task<bool> TryUpdateAsync(UpdateService service, IReadOnlyList<string> relaunchArguments, DiagnosticsLog log)
    {
        if (!service.Enabled || !UpdateService.IsInstalledCopy) return false;
        UpdateInfo? update;
        try
        {
            using var quick = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            update = await service.CheckAsync(quick.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or System.Text.Json.JsonException or KeyNotFoundException)
        {
            log.Write("actualizacion", "comprobación al abrir sin resultado: " + ex.Message);
            return false;
        }
        if (update is null) return false;

        var window = new UpdateWindow();
        var version = update.Version.ToString(3);
        window.VersionText.Text = Loc.T($"Versión {UpdateService.Current.ToString(3)} → {version}", $"Version {UpdateService.Current.ToString(3)} → {version}");
        window.StatusText.Text = Loc.T("Descargando la versión nueva…", "Downloading the new version…");
        window.Show();
        log.Write("actualizacion", $"actualización antes de abrir: {UpdateService.Current.ToString(3)} → {version}");
        try
        {
            var progress = new Progress<double>(p =>
            {
                window.Bar.IsIndeterminate = false;
                window.Bar.Value = p * 90;
                window.StatusText.Text = p < 1 ? Loc.T($"Descargando… {p:P0}", $"Downloading… {p:P0}") : Loc.T("Comprobando las huellas SHA-256…", "Checking SHA-256 fingerprints…");
            });
            var setup = await service.PrepareAsync(update, progress, window._cancel.Token);
            window.Bar.Value = 95;
            window.StatusText.Text = Loc.T("Instalando… FFCleaner se abrirá en unos segundos.", "Installing… FFCleaner will open in a few seconds.");
            window.SkipButton.IsEnabled = false;
            service.LaunchInstaller(setup, relaunchArguments.Concat(["--updated-from", UpdateService.Current.ToString(3)]));
            await Task.Delay(800);   // el instalador muestra su propia ventana de progreso
            window.Close();
            return true;
        }
        catch (OperationCanceledException) when (window._cancel.IsCancellationRequested)
        {
            log.Write("actualizacion", "actualización omitida por el usuario");
            window.Close();
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or System.IO.IOException
                                   or UnauthorizedAccessException or System.Text.Json.JsonException or System.IO.InvalidDataException or System.ComponentModel.Win32Exception)
        {
            log.Write("actualizacion", "no se actualizó: " + ex.Message);
            window.Bar.IsIndeterminate = false;
            window.Bar.Value = 0;
            window.StatusText.Text = Loc.T("No se pudo actualizar: ", "Could not update: ") + ex.Message + Loc.T(" FFCleaner se abrirá igualmente.", " FFCleaner will open anyway.");
            window.SkipButton.IsEnabled = false;
            await Task.Delay(3500);
            window.Close();
            return false;
        }
    }

    private void Skip_Click(object sender, RoutedEventArgs e) => _cancel.Cancel();
}
