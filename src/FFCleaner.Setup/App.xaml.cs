using System.Diagnostics;
using System.IO;
using System.Windows;

namespace FFCleaner.Setup;

/// <summary>
/// FFCleaner.Setup.exe: asistente de instalación (sin argumentos) y de desinstalación (--uninstall).
/// Para desinstalar, primero se copia a una carpeta temporal: Windows no deja borrar un programa en ejecución.
/// </summary>
public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        SetupOptions options;
        try { options = SetupOptions.Parse(e.Args); }
        catch (ArgumentException ex) { Fail(ex.Message); return; }

        var self = AppContext.BaseDirectory.TrimEnd('\\');
        if (options.Uninstall && !options.Relocated)
        {
            try { Relocate(self, e.Args); Shutdown(0); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { Fail("No se pudo preparar el desinstalador: " + ex.Message); }
            return;
        }
        if (options.Relocated) Exit += (_, _) => ScheduleSelfDelete(self);

        if (options.Quiet)
        {
            // En la actualización automática se ve una ventanita de progreso; en el modo silencioso puro, nada.
            var window = options.ShowProgress && !options.Uninstall ? new ProgressWindow() : null;
            window?.Show();
            IProgress<(double Percent, string Text)> progress = window is null ? new SyncProgress() : new Progress<(double Percent, string Text)>(window.Report);
            var effective = options;
            try
            {
                if (options.Uninstall)
                {
                    var manifest = SetupEngine.ReadManifest(options.InstallDirectory ?? self);
                    SetupEngine.Uninstall(manifest, options.RemoveData, new SyncProgress());
                }
                else
                {
                    progress.Report((1, "Esperando a que se cierre FFCleaner…"));
                    await Task.Run(() => WaitFor(options.WaitPid));
                    effective = options.WithPreviousChoices();
                    var exe = await Task.Run(() => SetupEngine.Install(self, effective, progress));
                    window?.Report((100, "Listo. Abriendo FFCleaner…"));
                    if (window is not null) await Task.Delay(700);
                    // Se abre desde su propia carpeta: si heredara la del instalador (temporal), esa no se podría borrar luego.
                    if (options.Launch) Process.Start(new ProcessStartInfo(exe, options.LaunchArguments ?? "") { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! });
                }
                Shutdown(0);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException or System.Text.Json.JsonException)
            {
                // Otra ventana de FFCleaner (o el servidor MCP de un agente) sigue abierta: el usuario ya tiene FFCleaner,
                // así que no se reabre nada (reabrirlo volvería a intentar la actualización) y se deja para la próxima vez.
                var running = ex is AppRunningException;
                if (window is not null)
                {
                    window.ShowError(running && options.Update
                        ? ex.Message.Split('.')[0] + ". La actualización se hará la próxima vez que abras FFCleaner."
                        : ex.Message);
                    await Task.Delay(4000);
                }
                if (options.Update && !running)
                {
                    // El aviso evita que la versión que se reabre lo vuelva a intentar enseguida (un fallo persistente sería un bucle).
                    SetupEngine.RecordFailedUpdate(effective.DataDirectory, options.TargetVersion, ex.Message);
                    var existing = Path.Combine(options.Destination, "FFCleaner.exe");
                    if (options.Launch && File.Exists(existing) && SetupEngine.UnderstandsFailedMarker(existing))
                        try
                        {
                            // Mismos argumentos (p. ej. el sandbox de las pruebas) salvo «--updated-from»: no se actualizó nada.
                            var arguments = System.Text.RegularExpressions.Regex.Replace(options.LaunchArguments ?? "", @"(^|\s)--updated-from\s+\S+", "").Trim();
                            Process.Start(new ProcessStartInfo(existing, arguments)
                            { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(existing)! });
                        }
                        catch (System.ComponentModel.Win32Exception) { }
                }
                Fail(ex.Message);
            }
            return;
        }
        MainWindow = new MainWindow(options, self);
        MainWindow.Show();
    }

    /// <summary>Espera (hasta 2 minutos) a que se cierre el FFCleaner que pidió la actualización.</summary>
    private static void WaitFor(int? pid)
    {
        if (pid is not { } id) return;
        try
        {
            using var process = Process.GetProcessById(id);
            if (!process.WaitForExit(TimeSpan.FromMinutes(2))) throw new InvalidOperationException("FFCleaner no se cerró a tiempo para actualizarse.");
        }
        catch (ArgumentException) { }   // ya se había cerrado
    }

    private void Fail(string message)
    {
        Console.Error.WriteLine(message);
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "ffcleaner-setup.log"), DateTime.Now.ToString("s") + " " + message + Environment.NewLine); }
        catch (IOException) { }
        Shutdown(1);
    }

    /// <summary>Copia el desinstalador (con su runtime) a %TEMP% y lo lanza desde allí con la carpeta instalada como argumento.</summary>
    private static void Relocate(string installDirectory, string[] args)
    {
        var temp = Path.Combine(Path.GetTempPath(), "ffcleaner-uninstall-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(temp);
        foreach (var file in Directory.EnumerateFiles(installDirectory, "FFCleaner.Setup.*"))
            File.Copy(file, Path.Combine(temp, Path.GetFileName(file)));
        var runtime = Path.Combine(installDirectory, "runtime");
        if (Directory.Exists(runtime))
            foreach (var file in Directory.EnumerateFiles(runtime, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(temp, "runtime", Path.GetRelativePath(runtime, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
        var start = new ProcessStartInfo(Path.Combine(temp, "FFCleaner.Setup.exe")) { UseShellExecute = false, WorkingDirectory = temp };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        start.ArgumentList.Add("--relocated");
        start.ArgumentList.Add("--install-dir");
        start.ArgumentList.Add(installDirectory);
        Process.Start(start);
    }

    /// <summary>La copia temporal del desinstalador se borra sola unos segundos después de cerrarse.</summary>
    private static void ScheduleSelfDelete(string directory)
    {
        if (!Path.GetFileName(directory).StartsWith("ffcleaner-uninstall-", StringComparison.Ordinal)) return;
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 4 > nul & rmdir /s /q \"{directory}\"")
        { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetTempPath() });
    }

    private sealed class SyncProgress : IProgress<(double Percent, string Text)>
    {
        public void Report((double Percent, string Text) value) { }
    }
}
