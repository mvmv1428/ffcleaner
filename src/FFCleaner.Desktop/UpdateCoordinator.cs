using System.Net.Http;
using System.Windows;
using FFCleaner.Core;

namespace FFCleaner.Desktop;

/// <summary>
/// Actualización automática al abrir FFCleaner: busca, descarga y verifica en segundo plano y, cuando no hay
/// ninguna operación en curso, instala la versión nueva y vuelve a abrir la aplicación. Al reabrirse, la nueva
/// versión muestra un aviso breve («FFCleaner se actualizó a la versión X») que desaparece solo.
/// </summary>
public sealed class UpdateCoordinator(UpdateService service, SettingsHolder settings, Func<bool> isBusy, IReadOnlyList<string> relaunchArguments, DiagnosticsLog log)
    : ObservableObject
{
    private string _status = "";
    private bool _working;

    public event EventHandler<Notice>? Noticed;
    public string Status { get => _status; private set => Set(ref _status, value); }
    public bool IsWorking { get => _working; private set => Set(ref _working, value); }
    public bool Enabled => service.Enabled;
    public string RepositoryText => service.Repository ?? Loc.T("sin configurar", "not configured");

    /// <summary>Al abrir: solo si el usuario no lo desactivó en Configuración.</summary>
    public Task StartupAsync()
    {
        _ = Task.Run(RemoveLeftovers);
        return settings.Current.AutoUpdate ? RunAsync(automatic: true) : Task.CompletedTask;
    }

    /// <summary>Borra las descargas de actualizaciones anteriores que quedaron en %TEMP% (la que está en uso no se puede borrar y se deja).</summary>
    private async Task RemoveLeftovers()
    {
        // Justo después de actualizar, el instalador que nos abrió aún se está cerrando: se reintenta unos segundos.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var pending = false;
            foreach (var dir in new System.IO.DirectoryInfo(System.IO.Path.GetTempPath()).EnumerateDirectories("ffcleaner-update-*"))
            {
                try { dir.Delete(recursive: true); log.Write("actualizacion", "descarga anterior retirada: " + dir.Name); }
                catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { pending = true; }
            }
            if (!pending) return;
            await Task.Delay(3000);
        }
    }

    public async Task RunAsync(bool automatic)
    {
        if (IsWorking) return;
        if (!service.Enabled)
        {
            Status = Loc.T("Las actualizaciones no están configuradas en esta compilación.", "Updates are not configured in this build.");
            return;
        }
        IsWorking = true;
        try
        {
            Status = Loc.T("Buscando actualizaciones…", "Checking for updates…");
            var update = await service.CheckAsync();
            if (update is null)
            {
                Status = Loc.T($"Tienes la última versión ({UpdateService.Current.ToString(3)}).", $"You have the latest version ({UpdateService.Current.ToString(3)}).");
                if (!automatic) Noticed?.Invoke(this, new(Loc.T("Sin actualizaciones", "No updates"), Status, NoticeKind.Info));
                return;
            }
            var version = update.Version.ToString(3);
            if (!UpdateService.IsInstalledCopy)
            {
                Status = Loc.T($"Hay una versión nueva ({version}). Esta copia es portátil: descárgala desde GitHub.",
                    $"A new version ({version}) is available. This is a portable copy: download it from GitHub.");
                Noticed?.Invoke(this, new(Loc.T("Versión nueva disponible", "New version available"), Status, NoticeKind.Info));
                return;
            }
            Status = Loc.T($"Descargando la versión {version}…", $"Downloading version {version}…");
            var progress = new Progress<double>(p => Status = Loc.T($"Descargando la versión {version}… {p:P0}", $"Downloading version {version}… {p:P0}"));
            var setup = await service.PrepareAsync(update, progress);
            // Nunca se interrumpe una limpieza ni una reparación: se espera a que terminen.
            while (isBusy())
            {
                Status = Loc.T($"Versión {version} lista; se instalará al terminar la operación en curso.", $"Version {version} ready; it will install when the current operation finishes.");
                await Task.Delay(1000);
            }
            Status = Loc.T($"Actualizando a la versión {version}…", $"Updating to version {version}…");
            Noticed?.Invoke(this, new(Loc.T("Actualizando FFCleaner", "Updating FFCleaner"),
                Loc.T($"Se instalará la versión {version} y FFCleaner se volverá a abrir en unos segundos.", $"Version {version} will be installed and FFCleaner will reopen in a few seconds."), NoticeKind.Info));
            await Task.Delay(2500);
            if (isBusy()) { Status = Loc.T("Actualización aplazada: hay una operación en curso.", "Update postponed: an operation is running."); return; }
            service.LaunchInstaller(setup, relaunchArguments.Concat(["--updated-from", UpdateService.Current.ToString(3)]));
            Application.Current.Shutdown();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or System.IO.IOException
                                   or UnauthorizedAccessException or System.Text.Json.JsonException or KeyNotFoundException or System.ComponentModel.Win32Exception)
        {
            log.Write("actualizacion", "no se actualizó: " + ex.Message);
            Status = Loc.T("No se pudo buscar o instalar la actualización: ", "Could not check for or install the update: ") + ex.Message;
            if (!automatic) Noticed?.Invoke(this, new(Loc.T("Actualización", "Update"), Status, NoticeKind.Aviso));
        }
        finally { IsWorking = false; }
    }
}
