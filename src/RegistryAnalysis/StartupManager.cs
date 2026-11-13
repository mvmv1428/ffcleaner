namespace FFCleaner.RegistryAnalysis;

using System.Globalization;
using System.Security;

public enum StartupSource { RegistroUsuario, RegistroEquipo, RegistroEquipo32, CarpetaUsuario, CarpetaEquipo }

/// <summary>Programa que se inicia con Windows, con su estado de activación tal y como lo ve el Administrador de tareas.</summary>
public sealed record StartupEntry(string Id, string Name, string Command, StartupSource Source, bool Enabled,
    TargetState TargetState, string? TargetPath, bool RequiresAdmin)
{
    public string SourceLabel => Source switch
    {
        StartupSource.RegistroUsuario => "Registro (usuario)",
        StartupSource.RegistroEquipo => "Registro (todos los usuarios)",
        StartupSource.RegistroEquipo32 => "Registro (todos, 32 bits)",
        StartupSource.CarpetaUsuario => "Carpeta Inicio (usuario)",
        _ => "Carpeta Inicio (todos los usuarios)",
    };
}

public sealed record StartupToggleResult(bool Success, string Message);

/// <summary>
/// Activa y desactiva programas de inicio de forma reversible, con el mismo mecanismo que el Administrador de
/// tareas de Windows: un valor en <c>Explorer\StartupApproved</c>. Nunca borra ni modifica la entrada original
/// (el valor de Run o el acceso directo de la carpeta Inicio), así que reactivar deja todo como estaba.
/// </summary>
public sealed class StartupManager
{
    private const string CurrentVersion = @"Software\Microsoft\Windows\CurrentVersion";
    private const string Approved = CurrentVersion + @"\Explorer\StartupApproved";
    private readonly RegistryScope _scope;
    private readonly string? _userFolder, _commonFolder;
    private readonly ITargetProbe _probe;
    private readonly Action<string>? _trace;
    private readonly Func<DateTime> _clock;

    public StartupManager(RegistryScope scope, string? userStartupFolder, string? commonStartupFolder,
        ITargetProbe? probe = null, Action<string>? trace = null, Func<DateTime>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(scope);
        _scope = scope;
        _userFolder = userStartupFolder;
        _commonFolder = commonStartupFolder;
        _probe = probe ?? new FileSystemTargetProbe();
        _trace = trace;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    public RegistryScope Scope => _scope;

    /// <summary>¿Puede el proceso actual cambiar entradas de todo el equipo? En sandbox, siempre.</summary>
    public bool CanChangeMachineEntries => _scope.IsSandbox || RegistryScope.IsElevated;

    /// <summary>Lista todas las entradas de inicio. Solo lectura.</summary>
    public IReadOnlyList<StartupEntry> List()
    {
        var result = new List<StartupEntry>();
        ReadRun(result, new(RegHive.CurrentUser, RegView.Registry64, CurrentVersion + @"\Run"), StartupSource.RegistroUsuario);
        ReadRun(result, new(RegHive.LocalMachine, RegView.Registry64, CurrentVersion + @"\Run"), StartupSource.RegistroEquipo);
        if (_scope.Views.Contains(RegView.Registry32))
            ReadRun(result, new(RegHive.LocalMachine, RegView.Registry32, CurrentVersion + @"\Run"), StartupSource.RegistroEquipo32);
        ReadFolder(result, _userFolder, StartupSource.CarpetaUsuario);
        ReadFolder(result, _commonFolder, StartupSource.CarpetaEquipo);
        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return result;
    }

    /// <summary>Activa o desactiva una entrada. Reversible: solo cambia su marca en StartupApproved.</summary>
    public StartupToggleResult SetEnabled(StartupEntry entry, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.RequiresAdmin && !CanChangeMachineEntries)
            return new(false, "«" + entry.Name + "» es de todos los usuarios: reinicia FFCleaner como administrador para cambiarla.");
        var (location, valueName) = ApprovalLocation(entry);
        try
        {
            // El valor de StartupApproved son 12 bytes: un indicador (02 activo, 03 desactivado) y la fecha del cambio.
            var data = new byte[12];
            data[0] = enabled ? (byte)0x02 : (byte)0x03;
            if (!enabled) BitConverter.GetBytes(_clock().ToFileTimeUtc()).CopyTo(data, 4);
            using (var key = _scope.Create(location)) RawRegistry.Set(key, valueName, new(RawValue.Binary, data));
            using (var check = _scope.Open(location, writable: false))
            {
                var read = check is null ? null : RawRegistry.Query(check, valueName);
                if (read is null || IsDisabled(read.Value) == enabled)
                    return new(false, "No se pudo confirmar el cambio de «" + entry.Name + "».");
            }
            _trace?.Invoke(string.Create(CultureInfo.InvariantCulture, $"inicio [{_scope.Id}] {(enabled ? "activado" : "desactivado")}: {entry.Name} ({entry.SourceLabel})"));
            return new(true, "«" + entry.Name + "» " + (enabled ? "se iniciará con Windows." : "ya no se iniciará con Windows. Puedes reactivarlo cuando quieras."));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        {
            return new(false, "Sin permiso para cambiar «" + entry.Name + "»: requiere administrador.");
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            return new(false, "No se pudo cambiar «" + entry.Name + "»: " + ex.Message);
        }
    }

    private void ReadRun(List<StartupEntry> result, RegLocation location, StartupSource source)
    {
        try
        {
            using var key = _scope.Open(location, writable: false);
            if (key is null) return;
            foreach (var name in key.GetValueNames())
            {
                if (name.Length == 0) continue;
                var command = RawRegistry.Query(key, name)?.AsString();
                if (string.IsNullOrWhiteSpace(command)) continue;
                var target = CommandLine.Resolve(command, _probe);
                var entry = new StartupEntry(Id(source, name), name, command, source, true, target.State, target.Path, source != StartupSource.RegistroUsuario);
                result.Add(entry with { Enabled = IsEnabled(entry) });
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            _trace?.Invoke("inicio: no se pudo leer " + location.Display + " (" + ex.GetType().Name + ")");
        }
    }

    private void ReadFolder(List<StartupEntry> result, string? folder, StartupSource source)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;
        try
        {
            foreach (var file in new DirectoryInfo(folder).EnumerateFiles())
            {
                if (file.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) ||
                    (file.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                var entry = new StartupEntry(Id(source, file.Name), Path.GetFileNameWithoutExtension(file.Name), file.FullName, source, true,
                    TargetState.Present, file.FullName, source == StartupSource.CarpetaEquipo);
                result.Add(entry with { Enabled = IsEnabled(entry) });
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            _trace?.Invoke("inicio: no se pudo leer la carpeta " + folder + " (" + ex.GetType().Name + ")");
        }
    }

    private bool IsEnabled(StartupEntry entry)
    {
        var (location, valueName) = ApprovalLocation(entry);
        try
        {
            using var key = _scope.Open(location, writable: false);
            var value = key is null ? null : RawRegistry.Query(key, valueName);
            return value is null || !IsDisabled(value.Value);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            return true;
        }
    }

    /// <summary>El bit bajo del primer byte marca la entrada como desactivada (03, 07…); 02 y 06 son activas.</summary>
    private static bool IsDisabled(RawValue value) => value.Kind == RawValue.Binary && value.Data.Length > 0 && (value.Data[0] & 0x01) == 1;

    private static (RegLocation Location, string ValueName) ApprovalLocation(StartupEntry entry) => entry.Source switch
    {
        StartupSource.RegistroUsuario => (new(RegHive.CurrentUser, RegView.Registry64, Approved + @"\Run"), entry.Name),
        StartupSource.RegistroEquipo => (new(RegHive.LocalMachine, RegView.Registry64, Approved + @"\Run"), entry.Name),
        StartupSource.RegistroEquipo32 => (new(RegHive.LocalMachine, RegView.Registry64, Approved + @"\Run32"), entry.Name),
        StartupSource.CarpetaUsuario => (new(RegHive.CurrentUser, RegView.Registry64, Approved + @"\StartupFolder"), Path.GetFileName(entry.Command)),
        _ => (new(RegHive.LocalMachine, RegView.Registry64, Approved + @"\StartupFolder"), Path.GetFileName(entry.Command)),
    };

    private static string Id(StartupSource source, string name) => source + "|" + name;
}
