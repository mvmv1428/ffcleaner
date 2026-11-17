using System.Net.Http;
using FFCleaner.Core;

namespace FFCleaner.Desktop;

/// <summary>
/// Actualizaciones mientras FFCleaner está abierto. La instalación nunca ocurre aquí: se hace al abrir la aplicación,
/// antes de su ventana principal (<see cref="UpdateWindow"/>), para no coincidir nunca con un análisis, una limpieza o
/// una reparación. Aquí solo se retiran las descargas antiguas, se avisa en las copias portátiles y se atiende
/// «Buscar ahora» de Configuración.
/// </summary>
public sealed class UpdateCoordinator(UpdateService service, SettingsHolder settings, DiagnosticsLog log) : ObservableObject
{
    private string _status = "";
    private bool _working;

    public event EventHandler<Notice>? Noticed;
    public string Status { get => _status; private set => Set(ref _status, value); }
    public bool IsWorking { get => _working; private set => Set(ref _working, value); }
    public bool Enabled => service.Enabled;
    public string RepositoryText => service.Repository ?? Loc.T("sin configurar", "not configured");

    /// <summary>Al abrir: retira descargas antiguas y, en una copia portátil, avisa si hay versión nueva.</summary>
    public Task StartupAsync()
    {
        _ = Task.Run(RemoveLeftovers);
        Status = Loc.T($"Versión {UpdateService.Current.ToString(3)}.", $"Version {UpdateService.Current.ToString(3)}.");
        return settings.Current.AutoUpdate && !UpdateService.IsInstalledCopy ? RunAsync(automatic: true) : Task.CompletedTask;
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
            Status = UpdateService.IsInstalledCopy
                ? Loc.T($"Hay una versión nueva ({version}). Se instalará la próxima vez que abras FFCleaner.",
                        $"A new version ({version}) is available. It will be installed the next time you open FFCleaner.")
                : Loc.T($"Hay una versión nueva ({version}). Esta copia es portátil: descárgala desde GitHub.",
                        $"A new version ({version}) is available. This is a portable copy: download it from GitHub.");
            Noticed?.Invoke(this, new(Loc.T("Versión nueva disponible", "New version available"), Status, NoticeKind.Info));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException
                                   or System.Text.Json.JsonException or KeyNotFoundException or ArgumentException or FormatException)
        {
            log.Write("actualizacion", "no se pudo comprobar: " + ex.Message);
            Status = Loc.T("No se pudo comprobar si hay actualizaciones: ", "Could not check for updates: ") + ex.Message;
            if (!automatic) Noticed?.Invoke(this, new(Loc.T("Actualización", "Update"), Status, NoticeKind.Aviso));
        }
        finally { IsWorking = false; }
    }

    /// <summary>
    /// Borra las descargas de actualizaciones anteriores que quedaron en la carpeta temporal del actualizador: el %TEMP%
    /// real, o el Temp del sandbox en las pruebas (así una prueba nunca toca una descarga real en curso).
    /// </summary>
    private async Task RemoveLeftovers()
    {
        // Justo después de actualizar, el instalador que nos abrió aún se está cerrando: se reintenta unos segundos.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var pending = false;
            System.IO.DirectoryInfo[] leftovers;
            try { leftovers = new System.IO.DirectoryInfo(service.WorkRoot).GetDirectories("ffcleaner-update-*"); }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { return; }
            foreach (var dir in leftovers)
            {
                try { dir.Delete(recursive: true); log.Write("actualizacion", "descarga anterior retirada: " + dir.Name); }
                catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { pending = true; }
            }
            if (!pending) return;
            await Task.Delay(3000);
        }
    }
}
