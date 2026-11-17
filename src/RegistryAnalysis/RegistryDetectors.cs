namespace FFCleaner.RegistryAnalysis;

using System.Security;
using Microsoft.Win32;

/// <summary>
/// Estado compartido por los detectores de una familia: ámbito, sonda, contadores y la clasificación de
/// riesgo. Todo acceso es de solo lectura; abrir una clave «para escritura» aquí solo comprueba el permiso.
/// </summary>
internal sealed class DetectorContext(RegistryScope scope, ITargetProbe probe, RegistryFamily family, CancellationToken token)
{
    private static readonly string WindowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private readonly HashSet<string> _visited = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<RegLocation, bool> _writable = [];

    public RegistryScope Scope => scope;
    public CancellationToken Token => token;
    public List<RegistryFinding> Findings { get; } = [];
    public List<string> Issues { get; } = [];
    public int KeysVisited { get; private set; }
    public int EntriesExamined { get; set; }
    public int Unverifiable { get; set; }

    /// <summary>Abre una clave para lectura. Null si no existe; un fallo de acceso se anota y devuelve null.</summary>
    public RegistryKey? Open(RegLocation location)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            var key = scope.Open(location, writable: false);
            if (key is not null) KeysVisited++;
            return key;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException
                                   or System.ComponentModel.Win32Exception)
        {
            Issues.Add(location.Display + ": no se pudo leer (" + ex.GetType().Name + "); sin conclusiones.");
            return null;
        }
    }

    /// <summary>
    /// False si esta clave física ya se recorrió por otra vista. HKCU y muchas claves de HKLM se comparten
    /// entre las vistas de 32 y 64 bits: sin esto cada hallazgo aparecería duplicado.
    /// </summary>
    public bool FirstVisit(RegistryKey key, RegLocation location) =>
        _visited.Add(RawRegistry.PhysicalPath(key) ?? location.HiveName + "|" + location.View + "|" + location.KeyPath);

    /// <summary>Existencia de un destino, teniendo en cuenta la redirección System32 → SysWOW64 de la vista de 32 bits.</summary>
    public TargetState Probe(RegView view, string path)
    {
        var state = probe.Probe(path);
        if (state != TargetState.Missing || view != RegView.Registry32) return state;
        var system32 = Path.Combine(WindowsDirectory, "System32") + "\\";
        if (!path.StartsWith(system32, StringComparison.OrdinalIgnoreCase)) return state;
        var redirected = probe.Probe(Path.Combine(WindowsDirectory, "SysWOW64", path[system32.Length..]));
        return redirected == TargetState.Missing ? TargetState.Missing : redirected;
    }

    public CommandTarget Resolve(RegView view, string? raw) => CommandLine.Resolve(raw, new ViewProbe(this, view));

    public bool CanWrite(RegLocation location)
    {
        if (_writable.TryGetValue(location, out var known)) return known;
        bool writable;
        try
        {
            using var key = scope.Open(location, writable: true);
            writable = key is not null;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException
                                   or System.ComponentModel.Win32Exception)
        {
            writable = false;
        }
        return _writable[location] = writable;
    }

    /// <summary>
    /// Registra un hallazgo y decide su riesgo. El orden importa: lo no verificable es ambiguo; lo que
    /// apunta al sistema es crítico; lo no modificable queda sin acceso; solo el resto es seguro.
    /// </summary>
    public void Report(RegLocation location, string? valueName, RepairKind repair, string rawTarget, string? resolved,
        TargetState state, string evidence, string? ambiguity = null, bool critical = false, bool systemTargetIsCritical = true)
    {
        string stateHash;
        try
        {
            using var key = scope.Open(location, writable: false);
            if (key is null) return;
            stateHash = repair == RepairKind.DeleteValue
                ? RegistrySnapshot.Hash(RegistrySnapshot.CaptureValue(key, valueName ?? string.Empty))
                : RegistrySnapshot.Hash(RegistrySnapshot.CaptureTree(key, LeafName(location)));
        }
        catch (RegistryTreeTooLargeException ex)
        {
            stateHash = string.Empty;
            ambiguity ??= ex.Message;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException
                                   or System.ComponentModel.Win32Exception)
        {
            stateHash = string.Empty;
            ambiguity ??= "No se pudo leer el contenido completo de la clave.";
        }

        RegistryRisk risk;
        string note;
        if (state != TargetState.Missing)
        {
            risk = RegistryRisk.Ambiguo;
            note = "No verificable: no se trata como ausencia.";
        }
        else if (critical || (systemTargetIsCritical && resolved is not null && IsUnderWindows(resolved)))
        {
            risk = RegistryRisk.Critico;
            note = "Componente del sistema: no se repara automáticamente.";
        }
        else if (ambiguity is not null)
        {
            risk = RegistryRisk.Ambiguo;
            note = "Requiere revisión manual: " + ambiguity;
        }
        else if (!CanWrite(location))
        {
            risk = RegistryRisk.SinAcceso;
            note = "Sin permiso para modificar esta clave (requiere administrador).";
        }
        else
        {
            risk = RegistryRisk.Seguro;
            note = "Reparable con respaldo previo.";
        }

        Findings.Add(new(RegistryIds.ForFinding(scope.Id, family, location, valueName), family, location, valueName, repair,
            rawTarget, resolved, risk, evidence + " " + note, stateHash));
    }

    public static string LeafName(RegLocation location) => location.KeyPath[(location.KeyPath.LastIndexOf('\\') + 1)..];

    public static string[] SubKeyNames(RegistryKey key)
    {
        try { return key.GetSubKeyNames(); }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException
                                   or System.ComponentModel.Win32Exception) { return []; }
    }

    public string[] ValueNames(RegistryKey key, RegLocation location)
    {
        try { return key.GetValueNames(); }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException
                                   or System.ComponentModel.Win32Exception)
        {
            Issues.Add("No se pudieron listar los valores de " + location.Display + " (" + ex.GetType().Name + ").");
            return [];
        }
    }

    /// <summary>
    /// Examina una entrada. Si leerla falla (permisos, un valor que Windows no devuelve), se anota y se sigue con la
    /// siguiente: un solo valor ilegible no debe interrumpir la familia entera ni ocultar sus demás hallazgos.
    /// </summary>
    public void Entry(string label, Action examine)
    {
        try { examine(); }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException
                                   or System.ComponentModel.Win32Exception)
        {
            Issues.Add("No se pudo leer " + label + " (" + ex.GetType().Name + "); se omitió esa entrada.");
        }
    }

    /// <summary>La ruta se normaliza antes de comparar: «/», «\\», «..» o mayúsculas no deben ocultar un destino del sistema.</summary>
    private static bool IsUnderWindows(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or SecurityException) { return true; }
        return full.StartsWith(WindowsDirectory + "\\", StringComparison.OrdinalIgnoreCase) ||
               full.Equals(WindowsDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ViewProbe(DetectorContext context, RegView view) : ITargetProbe
    {
        public TargetState Probe(string path) => context.Probe(view, path);
    }
}

/// <summary>
/// Detectores por familia. Cada uno enumera sus claves, interpreta el destino sin ejecutarlo y entrega
/// los hallazgos al contexto, que decide el riesgo. Ninguno escribe en el Registro.
/// </summary>
internal static class RegistryDetectors
{
    private const string CurrentVersion = @"Software\Microsoft\Windows\CurrentVersion";
    private static readonly RegHive[] BothHives = [RegHive.CurrentUser, RegHive.LocalMachine];

    internal static void Run(RegistryFamily family, DetectorContext context)
    {
        switch (family)
        {
            case RegistryFamily.AppPaths: AppPaths(context); break;
            case RegistryFamily.Uninstall: Uninstall(context); break;
            case RegistryFamily.FileAssociations: FileAssociations(context); break;
            case RegistryFamily.Startup: Startup(context); break;
            case RegistryFamily.MuiCache: MuiCache(context); break;
            case RegistryFamily.SharedDlls: SharedDlls(context); break;
            default: throw new ArgumentOutOfRangeException(nameof(family));
        }
    }

    /// <summary>Rutas de aplicaciones: «App Paths\programa.exe» cuyo ejecutable ya no existe.</summary>
    private static void AppPaths(DetectorContext context)
    {
        foreach (var (location, key) in Roots(context, BothHives, CurrentVersion + @"\App Paths"))
        using (key)
        {
            foreach (var name in DetectorContext.SubKeyNames(key))
                context.Entry(location.Display + "\\" + name, () =>
                {
                    var entry = location.Child(name);
                    using var child = context.Open(entry);
                    if (child is null) return;
                    context.EntriesExamined++;
                    var raw = RawRegistry.Query(child, string.Empty);
                    var text = raw?.AsString();
                    if (string.IsNullOrWhiteSpace(text)) return;

                    // REG_EXPAND_SZ se conserva crudo; solo se expande una copia para analizar.
                    var analysed = text.Contains('%') ? Environment.ExpandEnvironmentVariables(text) : text;
                    var expanded = !string.Equals(analysed, text, StringComparison.Ordinal);
                    var pure = RegistryEvaluator.Evaluate(new(entry.View.ToString(), entry.Display, string.Empty, analysed),
                        path => context.Probe(entry.View, path));
                    if (pure is null) return;
                    if (pure.Selectable)
                    {
                        context.Report(entry, null, RepairKind.DeleteKey, text, pure.ResolvedTarget, TargetState.Missing,
                            "El ejecutable registrado ya no existe.", expanded ? "la ruta depende de variables de entorno." : null);
                        return;
                    }
                    // Sintaxis fuera del caso estricto (argumentos, otra extensión…): segunda lectura tolerante.
                    var command = context.Resolve(entry.View, text);
                    if (command.State == TargetState.Present) return;
                    context.Report(entry, null, RepairKind.DeleteKey, text, command.Path, TargetState.Unknown, pure.Reason);
                });
        }
    }

    /// <summary>Desinstaladores: entradas de «Uninstall» cuyo desinstalador y carpeta de instalación ya no existen.</summary>
    private static void Uninstall(DetectorContext context)
    {
        foreach (var (location, key) in Roots(context, BothHives, CurrentVersion + @"\Uninstall"))
        using (key)
        {
            foreach (var name in DetectorContext.SubKeyNames(key))
                context.Entry(location.Display + "\\" + name, () =>
                {
                    var entry = location.Child(name);
                    using var child = context.Open(entry);
                    if (child is null) return;
                    context.EntriesExamined++;
                    var uninstall = RawRegistry.Query(child, "UninstallString")?.AsString();
                    if (string.IsNullOrWhiteSpace(uninstall)) return;
                    // Windows Installer gestiona estas entradas con su propia base de datos: no se evalúan por ruta.
                    if (RawRegistry.Query(child, "WindowsInstaller")?.AsDword() == 1 ||
                        uninstall.TrimStart('"', ' ').StartsWith("msiexec", StringComparison.OrdinalIgnoreCase)) return;

                    var command = context.Resolve(entry.View, uninstall);
                    if (command.State == TargetState.Present) return;
                    var display = RawRegistry.Query(child, "DisplayName")?.AsString();
                    var label = string.IsNullOrWhiteSpace(display) ? name : display;
                    if (command.State == TargetState.Unknown)
                    {
                        context.Report(entry, null, RepairKind.DeleteKey, uninstall, command.Path, TargetState.Unknown,
                            "Desinstalador de «" + label + "» no verificable: " + command.Reason);
                        return;
                    }

                    // Un desinstalador ausente no basta (hay instaladores que borran su copia en caché con el programa
                    // aún instalado): hace falta que otra referencia comprobable de la entrada también haya desaparecido,
                    // y que ninguna siga existiendo.
                    string? ambiguity = command.Expanded ? "la ruta depende de variables de entorno." : null;
                    var folder = RawRegistry.Query(child, "InstallLocation")?.AsString()?.Trim().Trim('"').TrimEnd('\\');
                    var icon = RawRegistry.Query(child, "DisplayIcon")?.AsString()?.Trim().Trim('"');
                    var corroborated = false;
                    if (!string.IsNullOrWhiteSpace(folder))
                    {
                        if (!CommandLine.IsAbsoluteLocal(folder + "\\")) ambiguity ??= "la carpeta de instalación no es una ruta local comprobable.";
                        else if (context.Probe(entry.View, folder) != TargetState.Missing) ambiguity ??= "la carpeta de instalación todavía existe o no se pudo comprobar.";
                        else corroborated = true;
                    }
                    if (!string.IsNullOrWhiteSpace(icon))
                    {
                        var comma = icon.LastIndexOf(',');
                        var iconPath = comma > 2 ? icon[..comma].Trim('"') : icon;
                        if (!CommandLine.IsAbsoluteLocal(iconPath)) { if (!corroborated) ambiguity ??= "el icono del programa no es una ruta local comprobable."; }
                        else if (context.Probe(entry.View, iconPath) != TargetState.Missing) ambiguity ??= "el icono del programa todavía existe o no se pudo comprobar.";
                        else corroborated = true;
                    }
                    if (!corroborated) ambiguity ??= "la entrada no indica carpeta de instalación ni icono que confirmen la desinstalación.";
                    context.Report(entry, null, RepairKind.DeleteKey, uninstall, command.Path, TargetState.Missing,
                        "El desinstalador de «" + label + "» ya no existe.", ambiguity,
                        critical: RawRegistry.Query(child, "SystemComponent")?.AsDword() == 1);
                });
        }
    }

    /// <summary>
    /// Asociaciones de archivos: extensiones que apuntan a un tipo no registrado, programas de «Abrir con»
    /// inexistentes y, solo como aviso, tipos de archivo del usuario cuyo programa falta.
    /// </summary>
    private static void FileAssociations(DetectorContext context)
    {
        const string classes = @"Software\Classes";
        // Tipos que alguna extensión tiene como programa predeterminado: esos no se tocan aunque su programa falte.
        var defaultHandlers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (location, key) in Roots(context, BothHives, classes))
        using (key)
        {
            foreach (var name in DetectorContext.SubKeyNames(key))
                context.Entry(location.Display + "\\" + name, () =>
                {
                    if (!name.StartsWith('.')) return;
                    var entry = location.Child(name);
                    using var child = context.Open(entry);
                    if (child is null) return;
                    context.EntriesExamined++;
                    var progId = RawRegistry.Query(child, string.Empty)?.AsString();
                    if (string.IsNullOrWhiteSpace(progId) || progId.IndexOfAny(['\\', '/']) >= 0) return;
                    defaultHandlers.Add(progId);
                    var registered = ClassRegistered(context, progId);
                    if (registered != TargetState.Missing) return;
                    // Las asociaciones de todo el equipo las escriben instaladores y el propio Windows: solo se informan.
                    context.Report(entry, string.Empty, RepairKind.DeleteValue, progId, @"HKCR\" + progId, TargetState.Missing,
                        "La extensión " + name + " apunta al tipo «" + progId + "», que no está registrado.",
                        entry.Hive == RegHive.LocalMachine ? "es una asociación de todo el equipo." : null, systemTargetIsCritical: false);
                });
        }

        foreach (var (location, key) in Roots(context, BothHives, classes + @"\Applications"))
        using (key)
        {
            foreach (var name in DetectorContext.SubKeyNames(key))
                context.Entry(location.Display + "\\" + name, () =>
                {
                    var entry = location.Child(name);
                    context.EntriesExamined++;
                    var (state, raw, path, reason, expanded) = ShellCommands(context, entry);
                    if (state is null or TargetState.Present) return;
                    context.Report(entry, null, RepairKind.DeleteKey, raw, path, state.Value,
                        state == TargetState.Missing ? "«Abrir con»: el programa " + name + " ya no existe." : "«Abrir con» " + name + ": " + reason,
                        state == TargetState.Missing && expanded ? "la ruta depende de variables de entorno." : null);
                });
        }

        // Tipos de archivo del usuario cuyo programa ya no existe (lo que deja un programa al desinstalarse).
        // Solo es seguro cuando el tipo no es más que comandos e icono, todos apuntan a archivos inexistentes,
        // no existe también para todo el equipo y ninguna extensión lo tiene como programa predeterminado.
        var userClasses = new RegLocation(RegHive.CurrentUser, RegView.Registry64, classes);
        using var user = context.Open(userClasses);
        if (user is null) return;
        foreach (var name in DetectorContext.SubKeyNames(user))
            context.Entry(userClasses.Display + "\\" + name, () =>
            {
                if (!RegistryFamilyRules.IsUserFileType(name)) return;
                var entry = userClasses.Child(name);
                var (state, raw, path, _, expanded) = ShellCommands(context, entry);
                if (state != TargetState.Missing) return;   // sin comandos, alguno vivo o alguno no verificable
                context.EntriesExamined++;

                string? ambiguity = expanded ? "la ruta depende de variables de entorno." : null;
                using (var key = context.Open(entry))
                {
                    if (key is null) return;
                    var extras = DetectorContext.SubKeyNames(key)
                        .Where(n => !n.Equals("shell", StringComparison.OrdinalIgnoreCase) && !n.Equals("DefaultIcon", StringComparison.OrdinalIgnoreCase)).ToList();
                    if (extras.Count > 0) ambiguity ??= "el tipo declara otros componentes (" + string.Join(", ", extras.Take(3)) + ").";
                }
                using (var iconKey = context.Open(entry.Child("DefaultIcon")))
                {
                    var icon = iconKey is null ? null : RawRegistry.Query(iconKey, string.Empty)?.AsString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(icon))
                    {
                        var comma = icon.LastIndexOf(',');
                        var iconPath = (comma > 2 ? icon[..comma] : icon).Trim().Trim('"');
                        if (!CommandLine.IsAbsoluteLocal(iconPath)) ambiguity ??= "su icono no es una ruta local comprobable.";
                        else if (context.Probe(entry.View, iconPath) != TargetState.Missing) ambiguity ??= "su icono todavía existe o no se pudo comprobar.";
                    }
                }
                if (MachineClassState(context, name) != TargetState.Missing) ambiguity ??= "el tipo también está registrado para todo el equipo.";
                if (defaultHandlers.Contains(name)) ambiguity ??= "una extensión lo tiene como programa predeterminado.";
                context.Report(entry, null, RepairKind.DeleteKey, raw, path, TargetState.Missing,
                    "El tipo de archivo o protocolo «" + name + "» pertenece a un programa que ya no existe.", ambiguity);
            });
    }

    /// <summary>¿Existe la clase en HKLM, en cualquier vista? Un fallo de acceso cuenta como «no se sabe».</summary>
    private static TargetState MachineClassState(DetectorContext context, string name)
    {
        var issuesBefore = context.Issues.Count;
        foreach (var view in context.Scope.Views)
        {
            using var key = context.Open(new(RegHive.LocalMachine, view, @"Software\Classes\" + name));
            if (key is not null) return TargetState.Present;
        }
        return context.Issues.Count == issuesBefore ? TargetState.Missing : TargetState.Unknown;
    }


    /// <summary>Entradas de inicio: valores de Run y RunOnce cuyo programa ya no existe.</summary>
    private static void Startup(DetectorContext context)
    {
        foreach (var keyName in new[] { "Run", "RunOnce" })
        foreach (var (location, key) in Roots(context, BothHives, CurrentVersion + "\\" + keyName))
        using (key)
        {
            foreach (var valueName in context.ValueNames(key, location))
                context.Entry(location.Display + " → " + valueName, () =>
                {
                    context.Token.ThrowIfCancellationRequested();
                    var text = RawRegistry.Query(key, valueName)?.AsString();
                    if (string.IsNullOrWhiteSpace(text)) return;
                    context.EntriesExamined++;
                    var command = context.Resolve(location.View, text);
                    if (command.State == TargetState.Present) return;
                    context.Report(location, valueName, RepairKind.DeleteValue, text, command.Path, command.State,
                        command.State == TargetState.Missing ? "El programa de inicio ya no existe." : "Entrada de inicio no verificable: " + command.Reason,
                        command.State == TargetState.Missing && command.Expanded ? "la ruta depende de variables de entorno." : null);
                });
        }
    }

    /// <summary>Cachés de nombres de programas (MUI Cache y asistente de compatibilidad) para ejecutables que ya no existen.</summary>
    private static void MuiCache(DetectorContext context)
    {
        string[] stores =
        [
            @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache",
            @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant\Store",
        ];
        string[] suffixes = [".FriendlyAppName", ".ApplicationCompany"];
        foreach (var store in stores)
        foreach (var (location, key) in Roots(context, [RegHive.CurrentUser], store))
        using (key)
        {
            foreach (var valueName in context.ValueNames(key, location))
                context.Entry(location.Display + " → " + valueName, () =>
                {
                    context.Token.ThrowIfCancellationRequested();
                    var path = valueName;
                    foreach (var suffix in suffixes)
                        if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { path = path[..^suffix.Length]; break; }
                    if (!CommandLine.IsAbsoluteLocal(path)) return;
                    context.EntriesExamined++;
                    var state = context.Probe(location.View, path);
                    if (state == TargetState.Present) return;
                    if (state == TargetState.Unknown) { context.Unverifiable++; return; }
                    context.Report(location, valueName, RepairKind.DeleteValue, valueName, path, TargetState.Missing,
                        "Entrada de caché de un programa que ya no existe.", systemTargetIsCritical: false);
                });
        }
    }

    /// <summary>DLL compartidas: contadores de referencias de archivos que ya no existen.</summary>
    private static void SharedDlls(DetectorContext context)
    {
        foreach (var (location, key) in Roots(context, [RegHive.LocalMachine], CurrentVersion + @"\SharedDLLs"))
        using (key)
        {
            foreach (var valueName in context.ValueNames(key, location))
                context.Entry(location.Display + " → " + valueName, () =>
                {
                    context.Token.ThrowIfCancellationRequested();
                    if (!CommandLine.IsAbsoluteLocal(valueName)) return;
                    context.EntriesExamined++;
                    var state = context.Probe(location.View, valueName);
                    if (state == TargetState.Present) return;
                    if (state == TargetState.Unknown) { context.Unverifiable++; return; }
                    context.Report(location, valueName, RepairKind.DeleteValue, valueName, valueName, TargetState.Missing,
                        "Contador de DLL compartida de un archivo que ya no existe.");
                });
        }
    }

    /// <summary>Abre la misma ruta en cada hive y vista, sin repetir claves físicas compartidas.</summary>
    private static IEnumerable<(RegLocation Location, RegistryKey Key)> Roots(DetectorContext context, RegHive[] hives, string path)
    {
        foreach (var hive in hives)
        foreach (var view in context.Scope.Views)
        {
            var location = new RegLocation(hive, view, path);
            var key = context.Open(location);
            if (key is null) continue;
            if (!context.FirstVisit(key, location)) { key.Dispose(); continue; }
            yield return (location, key);
        }
    }

    /// <summary>¿Existe el tipo en HKCU o HKLM, en cualquier vista? Un fallo de acceso cuenta como «no se sabe».</summary>
    private static TargetState ClassRegistered(DetectorContext context, string progId)
    {
        var issuesBefore = context.Issues.Count;
        foreach (var hive in BothHives)
        foreach (var view in context.Scope.Views)
        {
            using var key = context.Open(new(hive, view, @"Software\Classes\" + progId));
            if (key is not null) return TargetState.Present;
        }
        return context.Issues.Count == issuesBefore ? TargetState.Missing : TargetState.Unknown;
    }

    /// <summary>
    /// Estado conjunto de los comandos «shell\verbo\command» de una aplicación: presente si alguno existe o
    /// delega en otro componente; ausente solo si todos faltan; null si no declara ninguno.
    /// </summary>
    private static (TargetState? State, string Raw, string? Path, string Reason, bool Expanded) ShellCommands(DetectorContext context, RegLocation application)
    {
        using var shell = context.Open(application.Child("shell"));
        if (shell is null) return (null, string.Empty, null, string.Empty, false);
        var expanded = false;
        TargetState? overall = null;
        string firstRaw = string.Empty, reason = string.Empty;
        string? firstPath = null;
        foreach (var verb in DetectorContext.SubKeyNames(shell))
        {
            var verbLocation = application.Child("shell").Child(verb);
            using var command = context.Open(verbLocation.Child("command"));
            using var dropTarget = context.Open(verbLocation.Child("DropTarget"));
            if (dropTarget is not null) return (TargetState.Present, string.Empty, null, string.Empty, false);
            if (command is null) continue;
            if (RawRegistry.Query(command, "DelegateExecute") is not null) return (TargetState.Present, string.Empty, null, string.Empty, false);
            var raw = RawRegistry.Query(command, string.Empty)?.AsString();
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var target = context.Resolve(application.View, raw);
            if (target.State == TargetState.Present) return (TargetState.Present, raw, target.Path, string.Empty, false);
            expanded |= target.Expanded;
            if (firstRaw.Length == 0) { firstRaw = raw; firstPath = target.Path; }
            if (target.State == TargetState.Unknown) { overall = TargetState.Unknown; reason = target.Reason; }
            else overall ??= TargetState.Missing;
        }
        return (overall, firstRaw, firstPath, reason, expanded);
    }
}
