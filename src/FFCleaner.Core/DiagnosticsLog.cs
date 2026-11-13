using System.Globalization;
using System.Text;

namespace FFCleaner.Core;

/// <summary>
/// Registro de diagnóstico en texto: tiempos por fase y trazas de cada operación. Tamaño acotado;
/// un fallo al escribir nunca interrumpe la operación que se está registrando.
/// </summary>
public sealed class DiagnosticsLog
{
    private const long MaxBytes = 1024 * 1024;
    private readonly object _gate = new();
    private readonly string? _path;

    public DiagnosticsLog(string? path) => _path = path;

    public static DiagnosticsLog Null { get; } = new(null);

    public string? Path => _path;

    public void Write(string area, string message)
    {
        if (_path is null) return;
        var line = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + " [" + area + "] " +
            message.ReplaceLineEndings(" ") + Environment.NewLine;
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
                var info = new FileInfo(_path);
                if (info.Exists && info.Length > MaxBytes) File.Move(_path, _path + ".1", overwrite: true);
                File.AppendAllText(_path, line, new UTF8Encoding(false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                System.Diagnostics.Trace.TraceWarning("DiagnosticsLog: {0}", ex.Message);
            }
        }
    }
}
