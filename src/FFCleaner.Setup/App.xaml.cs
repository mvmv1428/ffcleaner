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
    protected override void OnStartup(StartupEventArgs e)
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
            var progress = new Progress<(double, string)>(_ => { });
            try
            {
                if (options.Uninstall)
                {
                    var manifest = SetupEngine.ReadManifest(options.InstallDirectory ?? self);
                    SetupEngine.Uninstall(manifest, options.RemoveData, new SyncProgress());
                }
                else
                {
                    WaitFor(options.WaitPid);
                    var effective = options.WithPreviousChoices();
                    var exe = SetupEngine.Install(self, effective, new SyncProgress());
                    // Se abre desde su propia carpeta: si heredara la del instalador (temporal), esa no se podría borrar luego.
                    if (options.Launch) Process.Start(new ProcessStartInfo(exe, options.LaunchArguments ?? "") { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! });
                }
                Shutdown(0);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException or System.Text.Json.JsonException)
            {
                // Si una actualización automática falla, se vuelve a abrir la versión que hubiera para no dejar al usuario sin nada.
                var existing = Path.Combine(options.Destination, "FFCleaner.exe");
                if (options.Update && options.Launch && File.Exists(existing))
                    try { Process.Start(new ProcessStartInfo(existing) { UseShellExecute = true }); } catch (System.ComponentModel.Win32Exception) { }
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
