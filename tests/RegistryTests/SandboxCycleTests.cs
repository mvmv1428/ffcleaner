using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using FFCleaner.RegistryAnalysis;
using Microsoft.Win32;

namespace FFCleaner.RegistryTests;

/// <summary>
/// Entorno aislado: todas las claves viven bajo HKCU\Software\CleanerApp_TestSandbox\&lt;id&gt; y todos los
/// archivos bajo una carpeta temporal propia. Ninguna prueba modifica una clave real del Registro.
/// </summary>
internal sealed class RegSandbox : IDisposable
{
    public const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string AppPaths = @"Software\Microsoft\Windows\CurrentVersion\App Paths";
    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    public const string Classes = @"Software\Classes";
    public const string Mui = @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache";
    public const string Shared = @"Software\Microsoft\Windows\CurrentVersion\SharedDLLs";
    public static readonly RegistryFamily[] All = Enum.GetValues<RegistryFamily>();

    public RegSandbox(params RegHive[] readOnlyHives)
    {
        Id = Guid.NewGuid().ToString("N");
        Writer = RegistryScope.Sandbox(Id);
        Scope = readOnlyHives.Length == 0 ? Writer : RegistryScope.Sandbox(Id, readOnlyHives);
        Dir = Path.Combine(Path.GetTempPath(), "ffcleaner-reg-tests-" + Id);
        Directory.CreateDirectory(Path.Combine(Dir, "con espacio"));
        Present = Path.Combine(Dir, "presente.exe");
        PresentSpaced = Path.Combine(Dir, "con espacio", "presente.exe");
        File.WriteAllText(Present, "no es un ejecutable real");
        File.WriteAllText(PresentSpaced, "no es un ejecutable real");
        Scanner = new RegistryScanner(Scope);
        Backups = new RegistryBackupStore(Scope, Path.Combine(Dir, "Respaldos"));
        Repair = new RegistryRepairService(Scanner, Backups);
    }

    public string Id { get; }
    public RegistryScope Scope { get; }
    /// <summary>Ámbito sin restricciones para preparar el fixture aunque <see cref="Scope"/> simule falta de permisos.</summary>
    public RegistryScope Writer { get; }
    public string Dir { get; }
    public string Present { get; }
    public string PresentSpaced { get; }
    public RegistryScanner Scanner { get; }
    public RegistryBackupStore Backups { get; }
    public RegistryRepairService Repair { get; }

    public string Missing(string name = "ausente.exe") => Path.Combine(Dir, "desinstalado", name);

    public void Set(RegHive hive, string key, string name, string text, int kind = RawValue.Sz, RegView view = RegView.Registry64) =>
        SetRaw(hive, key, name, kind, Encoding.Unicode.GetBytes(text + "\0"), view);

    public void SetDword(RegHive hive, string key, string name, int value, RegView view = RegView.Registry64) =>
        SetRaw(hive, key, name, RawValue.Dword, BitConverter.GetBytes(value), view);

    public void SetRaw(RegHive hive, string key, string name, int kind, byte[] data, RegView view = RegView.Registry64)
    {
        using var handle = Writer.Create(new(hive, view, key));
        RawRegistry.Set(handle, name, new(kind, data));
    }

    public void CreateKey(RegHive hive, string key, RegView view = RegView.Registry64) { using var _ = Writer.Create(new(hive, view, key)); }

    public RawValue? Get(RegHive hive, string key, string name, RegView view = RegView.Registry64)
    {
        using var handle = Writer.Open(new(hive, view, key), writable: false);
        return handle is null ? null : RawRegistry.Query(handle, name);
    }

    public bool KeyExists(RegHive hive, string key, RegView view = RegView.Registry64)
    {
        using var handle = Writer.Open(new(hive, view, key), writable: false);
        return handle is not null;
    }

    /// <summary>Huella del árbol completo de una clave (nombres, tipos y bytes de todo su contenido).</summary>
    public string TreeHash(RegHive hive, string key, RegView view = RegView.Registry64)
    {
        using var handle = Writer.Open(new(hive, view, key), writable: false);
        return handle is null ? "ausente" : RegistrySnapshot.Hash(RegistrySnapshot.CaptureTree(handle, "raiz"));
    }

    public RegistryScanResult Scan(params RegistryFamily[] families) => Scanner.Scan(families.Length == 0 ? All : families);

    public RegistryRepairReport RepairIds(RegistryScanResult scan, IEnumerable<string> ids, CancellationToken token = default) =>
        Repair.RepairAsync(scan, ids.ToList(), token).GetAwaiter().GetResult();

    public RegistryRestoreReport Restore(string path, CancellationToken token = default) =>
        Backups.RestoreAsync(path, token).GetAwaiter().GetResult();

    /// <summary>Una entrada huérfana por familia (más una segunda de inicio) junto a sus vecinas válidas.</summary>
    public void SeedOrphans()
    {
        Set(RegHive.CurrentUser, AppPaths + @"\huerfana.exe", "", Missing("huerfana.exe"));
        Set(RegHive.CurrentUser, AppPaths + @"\huerfana.exe", "Path", Path.GetDirectoryName(Missing())!);
        Set(RegHive.CurrentUser, AppPaths + @"\valida.exe", "", Present);

        Set(RegHive.CurrentUser, Run, "Huerfano", "\"" + Missing("inicio.exe") + "\" --minimizado");
        Set(RegHive.CurrentUser, Run, "HuerfanoSinComillas", Missing("otro inicio.exe") + " /tray");
        Set(RegHive.CurrentUser, Run, "Valido", "\"" + Present + "\" --ok");
        Set(RegHive.CurrentUser, Run, "ValidoSinComillas", PresentSpaced + " /arg http://ejemplo");

        Set(RegHive.CurrentUser, UninstallKey + @"\AppHuerfana", "DisplayName", "Aplicación Huérfana");
        Set(RegHive.CurrentUser, UninstallKey + @"\AppHuerfana", "UninstallString", "\"" + Missing("unins000.exe") + "\" /SILENT");
        Set(RegHive.CurrentUser, UninstallKey + @"\AppHuerfana", "InstallLocation", Path.GetDirectoryName(Missing())!);
        SetDword(RegHive.CurrentUser, UninstallKey + @"\AppHuerfana", "EstimatedSize", 1234);
        Set(RegHive.CurrentUser, UninstallKey + @"\AppValida", "DisplayName", "Aplicación Válida");
        Set(RegHive.CurrentUser, UninstallKey + @"\AppValida", "UninstallString", "\"" + Present + "\"");

        Set(RegHive.CurrentUser, Classes + @"\.clarohuerfana", "", "FFCleanerTest.Ausente");
        Set(RegHive.CurrentUser, Classes + @"\.clarohuerfana", "Content Type", "application/x-ffcleaner");
        Set(RegHive.CurrentUser, Classes + @"\.clarovalida", "", "FFCleanerTest.Presente");
        Set(RegHive.LocalMachine, Classes + @"\FFCleanerTest.Presente", "", "Documento de prueba");
        Set(RegHive.CurrentUser, Classes + @"\Applications\huerfana.exe\shell\open\command", "", "\"" + Missing("visor.exe") + "\" \"%1\"");
        Set(RegHive.CurrentUser, Classes + @"\Applications\huerfana.exe", "FriendlyAppName", "Visor desinstalado");
        Set(RegHive.CurrentUser, Classes + @"\Applications\valida.exe\shell\open\command", "", "\"" + Present + "\" \"%1\"");

        Set(RegHive.CurrentUser, Mui, Missing("viejo.exe") + ".FriendlyAppName", "Programa viejo");
        Set(RegHive.CurrentUser, Mui, Present + ".FriendlyAppName", "Programa presente");
        SetRaw(RegHive.CurrentUser, Mui, "LangID", RawValue.Binary, [0x0A, 0x34]);

        SetDword(RegHive.LocalMachine, Shared, Missing("compartida.dll"), 1);
        SetDword(RegHive.LocalMachine, Shared, Present, 2);
    }

    public void Dispose()
    {
        Writer.DeleteSandbox();
        try { Directory.Delete(Dir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Console.WriteLine("      AVISO: no se borró " + Dir + ": " + ex.Message); }
    }
}

internal static class SandboxCycleTests
{
    private const RegHive Cu = RegHive.CurrentUser, Lm = RegHive.LocalMachine;

    private static void Has(RawValue? value, string message) => Check.True(value is not null, message);

    public static void Register()
    {
        TestRegistry.Add("Sandbox: el ámbito de pruebas solo escribe bajo HKCU\\Software\\CleanerApp_TestSandbox", Scope_Is_Isolated);
        TestRegistry.Add("R6 ciclo completo: crear → analizar → respaldo verificado → reparar → reanalizar 0 → restaurar → estado original", Full_Cycle);
        TestRegistry.Add("R4 ida y vuelta exacta de tipos, bytes, valor predeterminado, Unicode, subclaves y vista de 32 bits", Types_RoundTrip);
        TestRegistry.Add("R4 el archivo .reg generado se importa con reg.exe y reproduce el estado original", Reg_File_Imports);
        TestRegistry.Add("R1 ambiguos, críticos y sin permisos se muestran pero no se pueden reparar", Risk_Classification);
        TestRegistry.Add("R1 entradas válidas, MSI, comandos del sistema y rutas con espacios no generan hallazgos", No_False_Positives);
        TestRegistry.Add("R2 solo se reparan los hallazgos marcados; vecinos y familias desmarcadas intactos", Only_Selected);
        TestRegistry.Add("R3 si el respaldo no se puede escribir o verificar no se repara nada", Backup_Failure_Blocks_Repair);
        TestRegistry.Add("R5 cambios entre el análisis y la reparación se detectan como conflicto y se conservan", Concurrent_Change_Before_Repair);
        TestRegistry.Add("R5 la restauración no sobrescribe datos nuevos y es idempotente", Restore_Conflicts_And_Idempotence);
        TestRegistry.Add("Respaldo alterado, ajeno o de otro ámbito no se restaura", Tampered_Or_Foreign_Backup);
        TestRegistry.Add("Entradas de HKLM solo se restauran con el ancla de integridad intacta", Machine_Anchor);
        TestRegistry.Add("Lotes inválidos se rechazan sin cambios: caducado, cancelado, de otro análisis o vacío", Invalid_Batches);
        TestRegistry.Add("Clave con demasiado contenido no es reparable y contenido nuevo impide el borrado", Bounded_Trees);
        TestRegistry.Add("Revisión: destinos tras enlaces, desinstaladores sin corroborar, variables en «Abrir con» y rutas del sistema disfrazadas no son seguros", Review_False_Positives);
        TestRegistry.Add("Revisión: un respaldo falsificado no puede escribir fuera de las claves exactas de cada familia ni con árboles incompletos", Review_Restore_Hardening);
        TestRegistry.Add("Revisión: nombres no representables no se reparan y el reparador exige el mismo ámbito que su almacén", Review_Misc);
        TestRegistry.Add("Renombre: un respaldo hecho con el nombre anterior (Claro) se lista y se restaura", () =>
        {
            using var box = new RegSandbox();
            box.SeedOrphans();
            var original = box.TreeHash(Cu, "Software");
            var scan = box.Scan(RegistryFamily.Startup);
            var report = box.RepairIds(scan, scan.Findings.Select(f => f.Id));
            var document = RegistryBackupStore.Load(report.BackupPath!);
            var legacy = document with { Format = RegistryBackupStore.LegacyFormat };
            File.WriteAllBytes(report.BackupPath!, RegistryBackupStore.Serialize(legacy with { ContentSha256 = RegistryBackupStore.Checksum(legacy) }));
            Check.True(box.Backups.List().Single().Valid, "El respaldo antiguo aparece como válido.");
            Check.Equal(2, box.Restore(report.BackupPath!).Restored, "Y se restaura.");
            Check.Equal(original, box.TreeHash(Cu, "Software"), "Estado original.");
            var unknown = document with { Format = "otro-programa/1" };
            File.WriteAllBytes(report.BackupPath!, RegistryBackupStore.Serialize(unknown with { ContentSha256 = RegistryBackupStore.Checksum(unknown) }));
            Check.False(box.Backups.List().Single().Valid, "Un formato desconocido sigue rechazándose.");
        });
        TestRegistry.Add("Decisión: un tipo de archivo de un programa desinstalado es reparable solo si no es más que comandos e icono inexistentes", Decision_Orphan_File_Types);
    }

    private static void Decision_Orphan_File_Types()
    {
        using var box = new RegSandbox();
        const string classes = RegSandbox.Classes;
        string Command(string type, string verb = "open") => classes + "\\" + type + @"\shell\" + verb + @"\command";
        string Line(string exe) => "\"" + exe + "\" \"%1\"";
        var gone = box.Missing("desinstalado.exe");

        // Reparables: lo que deja un programa al desinstalarse.
        box.Set(Cu, classes + @"\Ido.doc", "", "Documento de Ido");
        box.Set(Cu, classes + @"\Ido.doc\DefaultIcon", "", gone + ",0");
        box.Set(Cu, classes + @"\Ido.doc\shell\open", "Icon", "\"" + gone + "\"");
        box.Set(Cu, Command("Ido.doc"), "", Line(gone));
        box.Set(Cu, Command("Ido.doc", "edit"), "", Line(gone));
        box.Set(Cu, classes + @"\.ido\OpenWithProgids", "Ido.doc", "");          // solo como opción de «abrir con»
        box.Set(Cu, Command("Ido.sinicono"), "", Line(gone));

        // No reparables: cada uno incumple una condición.
        box.Set(Cu, Command("Ido.iconovivo"), "", Line(gone));
        box.Set(Cu, classes + @"\Ido.iconovivo\DefaultIcon", "", box.Present + ",0");
        box.Set(Cu, Command("Ido.iconorecurso"), "", Line(gone));
        box.Set(Cu, classes + @"\Ido.iconorecurso\DefaultIcon", "", @"%SystemRoot%\System32\shell32.dll,-152", RawValue.ExpandSz);
        box.Set(Cu, Command("Ido.conextras"), "", Line(gone));
        box.CreateKey(Cu, classes + @"\Ido.conextras\shellex\PropertySheetHandlers");
        box.Set(Cu, Command("Ido.enmaquina"), "", Line(gone));
        box.Set(Lm, classes + @"\Ido.enmaquina", "", "También del equipo");
        box.Set(Cu, Command("Ido.predeterminado"), "", Line(gone));
        box.Set(Cu, classes + @"\.idodef", "", "Ido.predeterminado");
        box.Set(Cu, Command("Ido.variable"), "", "\"%TEMP%\\ffcleaner-no-existe-" + box.Id + "\\x.exe\" \"%1\"", RawValue.ExpandSz);

        // Sin hallazgo: algún verbo vivo, delegación en otro componente, contenedores del sistema y apps empaquetadas.
        box.Set(Cu, Command("Ido.verbovivo"), "", Line(gone));
        box.Set(Cu, Command("Ido.verbovivo", "edit"), "", Line(box.Present));
        box.Set(Cu, Command("Ido.delegado"), "DelegateExecute", "{00000000-0000-0000-0000-000000000000}");
        box.Set(Cu, Command("Directory"), "", Line(gone));
        box.Set(Cu, Command("AppXabcdef0123456789"), "", Line(gone));
        box.CreateKey(Cu, classes + @"\Ido.sincomandos\DefaultIcon");
        var before = box.TreeHash(Cu, "Software");

        var scan = box.Scan(RegistryFamily.FileAssociations);
        RegistryFinding? Find(string type) => scan.Findings.SingleOrDefault(f => f.Location.KeyPath.Equals(classes + "\\" + type, StringComparison.OrdinalIgnoreCase));
        Check.Equal(RegistryRisk.Seguro, Find("Ido.doc")!.Risk, "Comandos e icono inexistentes.");
        Check.Equal(RegistryRisk.Seguro, Find("Ido.sinicono")!.Risk, "Sin icono declarado.");
        foreach (var type in new[] { "Ido.iconovivo", "Ido.iconorecurso", "Ido.conextras", "Ido.enmaquina", "Ido.predeterminado", "Ido.variable" })
            Check.Equal(RegistryRisk.Ambiguo, Find(type)!.Risk, "No reparable: " + type + " — " + Find(type)!.Evidence);
        foreach (var type in new[] { "Ido.verbovivo", "Ido.delegado", "Directory", "AppXabcdef0123456789", "Ido.sincomandos" })
            Check.Null(Find(type), "Sin hallazgo: " + type);
        Check.Equal(2, scan.Findings.Count(f => f.CanRepair), "Solo los dos tipos inequívocos.");

        // Reparar: desaparecen los dos tipos completos y nada más.
        var report = box.RepairIds(scan, scan.Findings.Select(f => f.Id));
        Check.Equal(2, report.Repaired, "Dos reparados: " + string.Join(" | ", report.Outcomes.Select(o => o.Status + ":" + o.Detail)));
        Check.False(box.KeyExists(Cu, classes + @"\Ido.doc") || box.KeyExists(Cu, classes + @"\Ido.sinicono"), "Tipos huérfanos eliminados con sus subclaves.");
        foreach (var type in new[] { "Ido.iconovivo", "Ido.iconorecurso", "Ido.conextras", "Ido.enmaquina", "Ido.predeterminado", "Ido.variable", "Ido.verbovivo", "Ido.delegado", "Directory" })
            Check.True(box.KeyExists(Cu, Command(type)), "Intacto: " + type);
        Has(box.Get(Cu, classes + @"\.ido\OpenWithProgids", "Ido.doc"), "La lista «abrir con» de la extensión no se toca.");
        Check.Equal(0, box.Scan(RegistryFamily.FileAssociations).Findings.Count(f => f.CanRepair), "Reanálisis sin reparables.");

        // Restaurar: vuelven exactamente como estaban.
        Check.Equal(2, box.Restore(report.BackupPath!).Restored, "Dos restaurados.");
        Check.Equal(before, box.TreeHash(Cu, "Software"), "Estado original.");

        // Un respaldo manipulado no puede crear una copia de usuario de una clase del equipo ni un contenedor del sistema.
        var document = RegistryBackupStore.Load(report.BackupPath!);
        var entry = document.Entries[0];
        box.Set(Lm, classes + @"\ClaseDelEquipo\shell\open\command", "", "legítimo");
        var forged = document with { Entries = [entry with { KeyPath = classes + @"\ClaseDelEquipo" }, entry with { KeyPath = classes + @"\CLSID" }, entry with { KeyPath = classes + @"\.exe" }] };
        File.WriteAllBytes(report.BackupPath!, RegistryBackupStore.Serialize(forged with { ContentSha256 = RegistryBackupStore.Checksum(forged) }));
        var hostile = box.Restore(report.BackupPath!);
        Check.True(hostile.Failed == 3 && hostile.Restored == 0, "Tres entradas rechazadas: " + string.Join(" | ", hostile.Outcomes.Select(o => o.Status + ":" + o.Detail)));
        Check.True(hostile.Outcomes[0].Detail.Contains("todo el equipo", StringComparison.OrdinalIgnoreCase), "Motivo: clase del equipo.");
        Check.False(box.KeyExists(Cu, classes + @"\ClaseDelEquipo") || box.KeyExists(Cu, classes + @"\CLSID") || box.KeyExists(Cu, classes + @"\.exe"), "Nada creado en HKCU.");
        Check.Equal(before, box.TreeHash(Cu, "Software"), "HKCU intacto tras el intento.");
    }

    private static bool Junction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + link + "\" \"" + target + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        process.WaitForExit(10000);
        return process.ExitCode == 0;
    }

    private static void Review_False_Positives()
    {
        using var box = new RegSandbox();
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        // 1) Programa «instalado» detrás de una unión cuyo destino no está disponible: no se puede concluir que falte.
        var gone = Path.Combine(box.Dir, "volumen-desconectado");
        Directory.CreateDirectory(gone);
        var link = Path.Combine(box.Dir, "Juegos");
        if (Junction(link, gone))
        {
            Directory.Delete(gone);
            box.Set(Cu, RegSandbox.Run, "TrasEnlace", "\"" + Path.Combine(link, "juego", "juego.exe") + "\"");
            Check.Equal(TargetState.Unknown, new FileSystemTargetProbe().Probe(Path.Combine(link, "juego", "juego.exe")), "Sonda tras una unión rota.");
        }
        Check.Equal(TargetState.Missing, new FileSystemTargetProbe().Probe(box.Missing()), "Sonda en carpeta normal.");

        // 2) Desinstalador ausente sin ninguna otra referencia que confirme la desinstalación.
        box.Set(Cu, RegSandbox.UninstallKey + @"\SinCorroborar", "UninstallString", "\"" + box.Missing("setup-cache.exe") + "\"");
        box.Set(Cu, RegSandbox.UninstallKey + @"\CarpetaRelativa", "UninstallString", "\"" + box.Missing("u.exe") + "\"");
        box.Set(Cu, RegSandbox.UninstallKey + @"\CarpetaRelativa", "InstallLocation", @"%ProgramFiles%\Foo", RawValue.ExpandSz);
        box.Set(Cu, RegSandbox.UninstallKey + @"\CarpetaAusenteIconoVivo", "UninstallString", "\"" + box.Missing("u2.exe") + "\"");
        box.Set(Cu, RegSandbox.UninstallKey + @"\CarpetaAusenteIconoVivo", "InstallLocation", Path.GetDirectoryName(box.Missing())!);
        box.Set(Cu, RegSandbox.UninstallKey + @"\CarpetaAusenteIconoVivo", "DisplayIcon", box.Present + ",0");
        box.Set(Cu, RegSandbox.UninstallKey + @"\SoloIconoAusente", "UninstallString", "\"" + box.Missing("u3.exe") + "\"");
        box.Set(Cu, RegSandbox.UninstallKey + @"\SoloIconoAusente", "DisplayIcon", box.Missing("icono.ico"));

        // 3) «Abrir con» escrito con una variable de entorno que este proceso expande a otra carpeta.
        box.Set(Cu, RegSandbox.Classes + @"\Applications\variable.exe\shell\open\command", "", "\"%TEMP%\\ffcleaner-no-existe-" + box.Id + "\\foo.exe\" \"%1\"", RawValue.ExpandSz);

        // 4) Destinos del sistema escritos de formas que una comparación de texto no reconoce.
        box.Set(Cu, RegSandbox.Run, "BarrasInvertidas", windows.Replace('\\', '/') + "/System32/ffcleaner-inexistente-" + box.Id + ".exe");
        box.Set(Cu, RegSandbox.Run, "ConPuntos", "\"" + Path.Combine(box.Dir, "..", "..", "..", "..", "..", "..", "..", "..", windows[3..], "System32", "ffcleaner-inexistente2-" + box.Id + ".exe") + "\"");
        box.Set(Cu, RegSandbox.Run, "Control", "\"" + box.Missing("control.exe") + "\"");

        var scan = box.Scan();
        RegistryRisk Risk(string needle) => scan.Findings.Single(f => (f.ValueName ?? "") == needle || f.Location.KeyPath.EndsWith("\\" + needle, StringComparison.OrdinalIgnoreCase)).Risk;
        if (box.Get(Cu, RegSandbox.Run, "TrasEnlace") is not null) Check.Equal(RegistryRisk.Ambiguo, Risk("TrasEnlace"), "Programa tras una unión rota.");
        Check.Equal(RegistryRisk.Ambiguo, Risk("SinCorroborar"), "Desinstalador sin corroboración.");
        Check.Equal(RegistryRisk.Ambiguo, Risk("CarpetaRelativa"), "Carpeta de instalación no comprobable.");
        Check.Equal(RegistryRisk.Ambiguo, Risk("CarpetaAusenteIconoVivo"), "El icono sigue existiendo aunque falte la carpeta.");
        Check.Equal(RegistryRisk.Seguro, Risk("SoloIconoAusente"), "Icono ausente como única corroboración.");
        Check.Equal(RegistryRisk.Ambiguo, Risk("variable.exe"), "«Abrir con» dependiente de variables.");
        Check.Equal(RegistryRisk.Critico, Risk("BarrasInvertidas"), "Destino del sistema con barras normales.");
        Check.Equal(RegistryRisk.Critico, Risk("ConPuntos"), "Destino del sistema con «..».");
        Check.Equal(RegistryRisk.Seguro, Risk("Control"), "Caso de control.");
        Check.Equal(2, scan.Findings.Count(f => f.CanRepair), "Solo los dos casos inequívocos son reparables.");
        if (Directory.Exists(link)) Directory.Delete(link);
    }

    private static void Review_Restore_Hardening()
    {
        using var box = new RegSandbox();
        box.SeedOrphans();
        var scan = box.Scan(RegistryFamily.FileAssociations, RegistryFamily.AppPaths);
        var report = box.RepairIds(scan, scan.Findings.Where(f => f.CanRepair).Select(f => f.Id));
        Check.Equal(3, report.Repaired, "Extensión, «Abrir con» y App Paths reparados.");
        var before = box.TreeHash(Cu, "Software");
        var path = report.BackupPath!;
        var genuine = File.ReadAllBytes(path);
        var document = RegistryBackupStore.Load(path);
        var extension = document.Entries.Single(e => e.Operation == RepairKind.DeleteValue);
        var application = document.Entries.Single(e => e.Family == RegistryFamily.FileAssociations && e.Operation == RepairKind.DeleteKey);

        RegistryRestoreReport Forged(params BackupEntry[] entries)
        {
            var forged = document with { Entries = [.. entries] };
            File.WriteAllBytes(path, RegistryBackupStore.Serialize(forged with { ContentSha256 = RegistryBackupStore.Checksum(forged) }));
            return box.Restore(path);
        }

        // Claves que una familia «asociaciones» demasiado amplia habría aceptado.
        var attempts = Forged(
            extension with { KeyPath = @"Software\Classes\exefile\shell\open\command" },
            extension with { KeyPath = @"Software\Classes\.clarohuerfana", Value = extension.Value! with { Name = "Inyectado" } },
            extension with { KeyPath = @"Software\Classes\.clarohuerfana\ShellEx" },
            application with { KeyPath = @"Software\Classes\CLSID\{11111111-1111-1111-1111-111111111111}" },
            application with { KeyPath = @"Software\Classes\Applications\a.exe\shell" },
            document.Entries.Single(e => e.Family == RegistryFamily.AppPaths) with { KeyPath = RegSandbox.AppPaths + @"\x.exe\profundo" });
        Check.True(!attempts.Refused && attempts.Failed == 6 && attempts.Restored == 0, "Seis entradas fuera de familia rechazadas: " + string.Join(" | ", attempts.Outcomes.Select(o => o.Status)));
        Check.Equal(before, box.TreeHash(Cu, "Software"), "Nada se escribió.");

        // Árbol incompleto o con nombres de subclave que escapan de la clave: el respaldo entero se rechaza al cargarlo.
        var evil = application.Tree! with { SubKeys = [new(@"..\otra\clave", [], [])] };
        Check.True(Forged(application with { Tree = evil }).Refused, "Subclave con separadores.");
        Check.True(Forged(application with { Tree = application.Tree with { SubKeys = [new("", [], [])] } }).Refused, "Subclave sin nombre.");
        Check.True(Forged(application with { Tree = application.Tree with { Values = null! } }).Refused, "Lista de valores ausente.");
        Check.True(Forged(extension with { Value = extension.Value! with { Data = "no es base64 !!" } }).Refused, "Datos que no son Base64.");
        Check.Equal(before, box.TreeHash(Cu, "Software"), "Ningún respaldo malformado modificó el Registro.");

        File.WriteAllBytes(path, genuine);
        Check.Equal(3, box.Restore(path).Restored, "El respaldo auténtico sigue restaurándose.");
    }

    private static void Review_Misc()
    {
        using var box = new RegSandbox();
        // Nombre de valor con medio par sustituto: no se puede guardar fielmente en el respaldo.
        var lone = "Roto" + (char)0xD800;
        box.Set(Cu, RegSandbox.Run, lone, "\"" + box.Missing("s.exe") + "\"");
        box.Set(Cu, RegSandbox.Run, "Par😀Completo", "\"" + box.Missing("p.exe") + "\"");
        var scan = box.Scan(RegistryFamily.Startup);
        Check.Equal(RegistryRisk.Ambiguo, scan.Findings.Single(f => f.ValueName == lone).Risk, "Nombre no representable.");
        Check.Equal(RegistryRisk.Seguro, scan.Findings.Single(f => f.ValueName == "Par😀Completo").Risk, "Un emoji completo sí es representable.");
        var report = box.RepairIds(scan, scan.Findings.Select(f => f.Id));
        Check.Equal(1, report.Repaired, "Solo el representable.");
        Has(box.Get(Cu, RegSandbox.Run, lone), "El valor no respaldable se conserva.");
        Check.Equal(1, box.Restore(report.BackupPath!).Restored, "Y el emoji vuelve con su nombre exacto.");
        Has(box.Get(Cu, RegSandbox.Run, "Par😀Completo"), "Nombre restaurado.");

        using var other = new RegSandbox();
        try { _ = new RegistryRepairService(box.Scanner, other.Backups); throw new AssertionException("Se aceptaron ámbitos distintos."); }
        catch (ArgumentException) { }
        try { _ = new RegistryRepairService(new RegistryScanner(), box.Backups); throw new AssertionException("Analizador de producción con almacén de sandbox."); }
        catch (ArgumentException) { }
    }

    private static void Scope_Is_Isolated()
    {
        Check.Equal(@"Software\CleanerApp_TestSandbox", RegistryScope.SandboxRoot, "Raíz fija del sandbox.");
        using var box = new RegSandbox();
        box.Set(Lm, RegSandbox.Run, "Prueba", "x");
        using (var real = Registry.CurrentUser.OpenSubKey(RegistryScope.SandboxRoot + "\\" + box.Id + @"\Registry64\HKLM\" + RegSandbox.Run))
            Check.NotNull(real, "HKLM del sandbox se materializa bajo HKCU.");
        Check.True(box.Scope.IsSandbox && box.Scope.Id == "sandbox:" + box.Id, "Identidad del ámbito.");
        foreach (var bad in new[] { "", "con espacio", @"a\b", "..", new string('x', 65) })
        {
            try { RegistryScope.Sandbox(bad); throw new AssertionException("Identificador aceptado: " + bad); }
            catch (ArgumentException) { }
        }
        box.Writer.DeleteSandbox();
        using var gone = Registry.CurrentUser.OpenSubKey(RegistryScope.SandboxRoot + "\\" + box.Id);
        Check.Null(gone, "El sandbox se elimina por completo.");
    }

    private static void Full_Cycle()
    {
        using var box = new RegSandbox();
        box.SeedOrphans();
        var userBefore = box.TreeHash(Cu, "Software");
        var machineBefore = box.TreeHash(Lm, @"Software\Microsoft");

        // Analizar.
        var scan = box.Scan();
        var safe = scan.Findings.Where(f => f.CanRepair).ToList();
        Check.Equal(8, scan.Findings.Count, "Hallazgos: " + string.Join(" | ", scan.Findings.Select(f => f.Family + ":" + f.Location.KeyPath + ":" + f.ValueName + ":" + f.Risk)));
        Check.Equal(8, safe.Count, "Todos los huérfanos sembrados son seguros.");
        foreach (var family in RegSandbox.All)
            Check.True(safe.Any(f => f.Family == family), "Familia con hallazgo: " + family);
        Check.Equal(2, safe.Count(f => f.Family == RegistryFamily.Startup), "Dos entradas de inicio.");
        Check.Equal(2, safe.Count(f => f.Family == RegistryFamily.FileAssociations), "Extensión y «Abrir con».");
        Check.Equal(userBefore, box.TreeHash(Cu, "Software"), "Analizar no modifica nada.");

        // Reparar (con respaldo verificado antes).
        var repair = box.RepairIds(scan, safe.Select(f => f.Id));
        Check.False(repair.Refused, "Reparación aceptada: " + repair.Refusal);
        Check.Equal(8, repair.Repaired, "Ocho reparadas: " + string.Join(" | ", repair.Outcomes.Select(o => o.Status + ":" + o.Detail)));
        Check.True(File.Exists(repair.BackupPath) && File.Exists(repair.RegFilePath), "Respaldo .json y .reg en disco.");
        var document = RegistryBackupStore.Load(repair.BackupPath!);
        Check.Equal(8, document.Entries.Count, "El respaldo contiene las ocho entradas.");
        Check.Equal(RegistryBackupStore.Checksum(document), document.ContentSha256, "Suma de verificación válida.");
        Check.True(File.Exists(Path.Combine(Path.GetDirectoryName(repair.BackupPath!)!, "reparacion.json")), "Bitácora de la reparación.");

        // Efecto real: huérfanos fuera, vecinos válidos intactos.
        Check.False(box.KeyExists(Cu, RegSandbox.AppPaths + @"\huerfana.exe"), "App Paths huérfana eliminada.");
        Check.True(box.KeyExists(Cu, RegSandbox.AppPaths + @"\valida.exe"), "App Paths válida intacta.");
        Check.Null(box.Get(Cu, RegSandbox.Run, "Huerfano"), "Inicio huérfano eliminado.");
        Has(box.Get(Cu, RegSandbox.Run, "Valido"), "Inicio válido intacto.");
        Check.False(box.KeyExists(Cu, RegSandbox.UninstallKey + @"\AppHuerfana"), "Desinstalador huérfano eliminado.");
        Check.True(box.KeyExists(Cu, RegSandbox.UninstallKey + @"\AppValida"), "Desinstalador válido intacto.");
        Check.Null(box.Get(Cu, RegSandbox.Classes + @"\.clarohuerfana", ""), "Asociación rota eliminada.");
        Has(box.Get(Cu, RegSandbox.Classes + @"\.clarohuerfana", "Content Type"), "Otros valores de la extensión intactos.");
        Check.False(box.KeyExists(Cu, RegSandbox.Classes + @"\Applications\huerfana.exe"), "«Abrir con» huérfano eliminado.");
        Check.Null(box.Get(Cu, RegSandbox.Mui, box.Missing("viejo.exe") + ".FriendlyAppName"), "MUI Cache obsoleta eliminada.");
        Has(box.Get(Cu, RegSandbox.Mui, "LangID"), "LangID intacto.");
        Check.Null(box.Get(Lm, RegSandbox.Shared, box.Missing("compartida.dll")), "DLL compartida huérfana eliminada.");
        Has(box.Get(Lm, RegSandbox.Shared, box.Present), "DLL compartida válida intacta.");

        // Reanalizar: 0 hallazgos.
        var after = box.Scan();
        Check.Equal(0, after.Findings.Count, "Reanálisis sin hallazgos: " + string.Join(" | ", after.Findings.Select(f => f.Location.Display)));

        // Restaurar y verificar el estado original, byte a byte.
        var restore = box.Restore(repair.BackupPath!);
        Check.False(restore.Refused, "Restauración aceptada: " + restore.Refusal);
        Check.Equal(8, restore.Restored, "Ocho restauradas: " + string.Join(" | ", restore.Outcomes.Select(o => o.Status + ":" + o.Detail)));
        Check.Equal(0, restore.Conflicts + restore.Failed, "Sin conflictos ni errores.");
        Check.Equal(userBefore, box.TreeHash(Cu, "Software"), "HKCU del sandbox idéntico al original.");
        Check.Equal(machineBefore, box.TreeHash(Lm, @"Software\Microsoft"), "HKLM del sandbox idéntico al original.");
        var again = box.Scan();
        Check.True(again.Findings.Select(f => f.Id).OrderBy(i => i).SequenceEqual(scan.Findings.Select(f => f.Id).OrderBy(i => i)), "Vuelven los mismos ocho hallazgos.");
        Check.True(again.Findings.Zip(scan.Findings.OrderBy(f => f.Id), (a, b) => a).All(f => f.CanRepair), "Y siguen siendo reparables.");
    }

    private static void Types_RoundTrip()
    {
        using var box = new RegSandbox();
        var key = RegSandbox.AppPaths + @"\tipos.exe";
        box.Set(Cu, key, "", box.Missing("tipos.exe"));
        box.Set(Cu, key, "Unicode ñ 日本語 😀", "valor con «comillas» \"dobles\" y \\barras\\");
        box.Set(Cu, key, "Expandible", @"%SystemRoot%\no-expandir", RawValue.ExpandSz);
        box.SetRaw(Cu, key, "SinTerminador", RawValue.Sz, Encoding.Unicode.GetBytes("abc"));
        box.SetRaw(Cu, key, "LongitudImpar", RawValue.Sz, [0x41, 0x00, 0x42]);
        box.SetRaw(Cu, key, "ConSaltos", RawValue.Sz, Encoding.Unicode.GetBytes("línea1\r\nlínea2\0"));
        box.SetRaw(Cu, key, "Binario", RawValue.Binary, [.. Enumerable.Range(0, 300).Select(i => (byte)i)]);
        box.SetRaw(Cu, key, "BinarioVacio", RawValue.Binary, []);
        box.SetDword(Cu, key, "Dword", unchecked((int)0xDEADBEEF));
        box.SetRaw(Cu, key, "Qword", RawValue.Qword, BitConverter.GetBytes(0x0123456789ABCDEFUL));
        box.SetRaw(Cu, key, "Multi", RawValue.MultiSz, Encoding.Unicode.GetBytes("uno\0dos\0\0"));
        box.SetRaw(Cu, key, "Ninguno", 0, [1, 2, 3]);
        box.SetRaw(Cu, key, "TipoRaro", 0x1234, [9, 8, 7, 6]);
        box.Set(Cu, key + @"\Sub Clave\Más Honda", "", "predeterminado anidado");
        box.SetDword(Cu, key + @"\Sub Clave", "n", 7);
        // Misma familia en la vista de 32 bits y un valor suelto con nombre predeterminado.
        box.Set(Cu, RegSandbox.Run, "Vista32", "\"" + box.Missing("x86.exe") + "\"", RawValue.ExpandSz, RegView.Registry32);
        var before64 = box.TreeHash(Cu, "Software");
        var before32 = box.TreeHash(Cu, "Software", RegView.Registry32);

        var scan = box.Scan(RegistryFamily.AppPaths, RegistryFamily.Startup);
        Check.Equal(2, scan.Findings.Count(f => f.CanRepair), "Clave de tipos y valor de 32 bits.");
        Check.True(scan.Findings.Any(f => f.Location.View == RegView.Registry32), "El hallazgo conserva su vista de 32 bits.");
        var repair = box.RepairIds(scan, scan.Findings.Select(f => f.Id));
        Check.Equal(2, repair.Repaired, "Ambas reparadas.");
        Check.False(box.KeyExists(Cu, key), "Clave completa eliminada, incluidas sus subclaves.");
        Check.Null(box.Get(Cu, RegSandbox.Run, "Vista32", RegView.Registry32), "Valor de 32 bits eliminado.");

        var restore = box.Restore(repair.BackupPath!);
        Check.Equal(2, restore.Restored, "Ambas restauradas: " + string.Join(" | ", restore.Outcomes.Select(o => o.Status + ":" + o.Detail)));
        Check.Equal(before64, box.TreeHash(Cu, "Software"), "Vista de 64 bits idéntica: tipos y bytes exactos.");
        Check.Equal(before32, box.TreeHash(Cu, "Software", RegView.Registry32), "Vista de 32 bits idéntica.");
        var odd = box.Get(Cu, key, "LongitudImpar")!.Value;
        Check.True(odd.Kind == RawValue.Sz && odd.Data.SequenceEqual(new byte[] { 0x41, 0x00, 0x42 }), "REG_SZ de longitud impar conservado tal cual.");
        Check.Equal(0x1234, box.Get(Cu, key, "TipoRaro")!.Value.Kind, "Tipo no estándar conservado.");
        Check.Equal(RawValue.ExpandSz, box.Get(Cu, key, "Expandible")!.Value.Kind, "REG_EXPAND_SZ sin expandir.");
        Check.Equal(@"%SystemRoot%\no-expandir", box.Get(Cu, key, "Expandible")!.Value.AsString(), "Texto crudo sin expansión.");
    }

    private static void Reg_File_Imports()
    {
        using var box = new RegSandbox();
        box.SeedOrphans();
        box.SetRaw(Cu, RegSandbox.AppPaths + @"\huerfana.exe", "Binario", RawValue.Binary, [.. Enumerable.Range(0, 200).Select(i => (byte)(255 - i))]);
        box.SetRaw(Cu, RegSandbox.AppPaths + @"\huerfana.exe", "Multi", RawValue.MultiSz, Encoding.Unicode.GetBytes("a\0b\0\0"));
        box.Set(Cu, RegSandbox.AppPaths + @"\huerfana.exe", "Comillas \"y\" \\barras", "valor \"con\" comillas");
        var userBefore = box.TreeHash(Cu, "Software");
        var machineBefore = box.TreeHash(Lm, @"Software\Microsoft");
        var scan = box.Scan();
        var repair = box.RepairIds(scan, scan.Findings.Where(f => f.CanRepair).Select(f => f.Id));
        Check.Equal(8, repair.Repaired, "Reparadas.");

        // El .reg es UTF-16 con BOM, formato 5.00, y solo nombra claves del sandbox.
        var bytes = File.ReadAllBytes(repair.RegFilePath!);
        Check.True(bytes[0] == 0xFF && bytes[1] == 0xFE, "BOM UTF-16 LE.");
        var text = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        Check.True(text.StartsWith("Windows Registry Editor Version 5.00\r\n", StringComparison.Ordinal), "Cabecera estándar.");
        var headers = text.Split("\r\n").Where(l => l.StartsWith('[')).ToList();
        Check.True(headers.Count >= 8, "Una sección por clave.");
        var prefix = @"[HKEY_CURRENT_USER\" + RegistryScope.SandboxRoot + "\\" + box.Id + "\\";
        Check.True(headers.All(h => h.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)), "Todas las secciones dentro del sandbox: " + headers.FirstOrDefault(h => !h.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));

        var reg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "reg.exe");
        using var process = Process.Start(new ProcessStartInfo(reg, "import \"" + repair.RegFilePath + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true })!;
        process.WaitForExit(30000);
        Check.Equal(0, process.ExitCode, "reg.exe import: " + process.StandardError.ReadToEnd());
        Check.Equal(userBefore, box.TreeHash(Cu, "Software"), "HKCU del sandbox idéntico tras importar el .reg.");
        Check.Equal(machineBefore, box.TreeHash(Lm, @"Software\Microsoft"), "HKLM del sandbox idéntico tras importar el .reg.");
    }

    private static void Risk_Classification()
    {
        using var box = new RegSandbox(Lm);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var freeDrive = Enumerable.Range('D', 23).Select(c => (char)c).First(c => DriveInfo.GetDrives().All(d => char.ToUpperInvariant(d.Name[0]) != c));
        box.Set(Cu, RegSandbox.Run, "UnidadAusente", freeDrive + @":\programas\app.exe");
        box.Set(Cu, RegSandbox.Run, "Red", @"\\servidor\recurso\app.exe");
        box.Set(Cu, RegSandbox.Run, "VariableSinResolver", @"%CLARO_VARIABLE_INEXISTENTE%\app.exe");
        box.Set(Cu, RegSandbox.Run, "VariableResuelta", @"%TEMP%\ffcleaner-no-existe-" + box.Id + @"\app.exe", RawValue.ExpandSz);
        box.Set(Cu, RegSandbox.Run, "Relativa", @"carpeta\app.exe");
        box.Set(Cu, RegSandbox.Run, "ComandoDesconocido", "ffcleaner-comando-inexistente-" + box.Id + " /x");
        box.Set(Cu, RegSandbox.Run, "Sistema", Path.Combine(windows, "System32", "ffcleaner-inexistente-" + box.Id + ".exe"));
        box.Set(Cu, RegSandbox.Run, "Seguro", "\"" + box.Missing() + "\"");
        box.Set(Lm, RegSandbox.Run, "SinPermiso", "\"" + box.Missing("maquina.exe") + "\"");
        box.Set(Cu, RegSandbox.UninstallKey + @"\Componente", "UninstallString", "\"" + box.Missing("comp.exe") + "\"");
        box.SetDword(Cu, RegSandbox.UninstallKey + @"\Componente", "SystemComponent", 1);
        box.Set(Cu, RegSandbox.UninstallKey + @"\CarpetaViva", "UninstallString", "\"" + box.Missing("unins.exe") + "\"");
        box.Set(Cu, RegSandbox.UninstallKey + @"\CarpetaViva", "InstallLocation", box.Dir);
        box.Set(Cu, RegSandbox.UninstallKey + @"\IconoVivo", "UninstallString", "\"" + box.Missing("unins2.exe") + "\"");
        box.Set(Cu, RegSandbox.UninstallKey + @"\IconoVivo", "DisplayIcon", box.Present + ",0");
        box.Set(Cu, RegSandbox.Classes + @"\FFCleanerTest.TipoHuerfano\shell\open\command", "", "\"" + box.Missing("editor.exe") + "\" \"%1\"");
        box.CreateKey(Cu, RegSandbox.Classes + @"\FFCleanerTest.TipoHuerfano\shellex\ContextMenuHandlers");
        box.Set(Cu, RegSandbox.AppPaths + @"\ads.exe", "", box.Present + ":oculto.exe");
        box.Set(Lm, RegSandbox.Shared, Path.Combine(windows, "System32", "ffcleaner-inexistente-" + box.Id + ".dll"), "1");
        box.SetDword(Lm, RegSandbox.Shared, box.Missing("otra.dll"), 1);
        var before = box.TreeHash(Cu, "Software");
        var machineBefore = box.TreeHash(Lm, "Software");

        var scan = box.Scan();
        RegistryRisk Risk(string needle) => scan.Findings.Single(f => (f.ValueName ?? "").Contains(needle, StringComparison.OrdinalIgnoreCase) || f.Location.KeyPath.EndsWith(needle, StringComparison.OrdinalIgnoreCase)).Risk;
        foreach (var name in new[] { "UnidadAusente", "Red", "VariableSinResolver", "VariableResuelta", "Relativa", "ComandoDesconocido" })
            Check.Equal(RegistryRisk.Ambiguo, Risk(name), "Inicio ambiguo: " + name);
        Check.Equal(RegistryRisk.Critico, Risk("Sistema"), "Destino dentro de Windows.");
        Check.Equal(RegistryRisk.Seguro, Risk("Seguro"), "Caso seguro de control.");
        Check.Equal(RegistryRisk.SinAcceso, Risk("SinPermiso"), "HKLM no modificable.");
        Check.Equal(RegistryRisk.Critico, Risk("Componente"), "Componente del sistema.");
        Check.Equal(RegistryRisk.Ambiguo, Risk("CarpetaViva"), "La carpeta de instalación existe.");
        Check.Equal(RegistryRisk.Ambiguo, Risk("IconoVivo"), "El icono existe.");
        Check.Equal(RegistryRisk.Ambiguo, Risk("FFCleanerTest.TipoHuerfano"), "Tipo de archivo con otros componentes.");
        Check.Equal(RegistryRisk.Ambiguo, Risk("ads.exe"), "Flujo alternativo de datos.");
        Check.Equal(RegistryRisk.Critico, Risk("ffcleaner-inexistente-" + box.Id + ".dll"), "DLL compartida de System32.");
        Check.Equal(RegistryRisk.SinAcceso, Risk("otra.dll"), "DLL compartida sin permisos.");
        Check.Equal(1, scan.Findings.Count(f => f.CanRepair), "Solo el caso de control es reparable.");

        // Marcar todo a la fuerza: el servicio solo repara lo seguro y deja constancia del resto.
        var repair = box.RepairIds(scan, scan.Findings.Select(f => f.Id));
        Check.Equal(1, repair.Repaired, "Una reparación.");
        Check.Equal(scan.Findings.Count - 1, repair.Outcomes.Count(o => o.Status == RepairStatus.Omitido), "El resto, omitido.");
        Check.Null(box.Get(Cu, RegSandbox.Run, "Seguro"), "El seguro se eliminó.");
        box.Set(Cu, RegSandbox.Run, "Seguro", "\"" + box.Missing() + "\"");
        Check.Equal(before, box.TreeHash(Cu, "Software"), "Todo lo no seguro de HKCU quedó intacto.");
        Check.Equal(machineBefore, box.TreeHash(Lm, "Software"), "HKLM intacto.");
    }

    private static void No_False_Positives()
    {
        using var box = new RegSandbox();
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        box.Set(Cu, RegSandbox.Run, "Comillas", "\"" + box.PresentSpaced + "\" --flag");
        box.Set(Cu, RegSandbox.Run, "SinComillasConEspacios", box.PresentSpaced + " /arg uno dos");
        box.Set(Cu, RegSandbox.Run, "SinExtension", Path.Combine(box.Dir, "presente") + " /x");
        box.Set(Cu, RegSandbox.Run, "Rundll", "rundll32.exe shell32.dll,Control_RunDLL");
        box.Set(Cu, RegSandbox.Run, "Cmd", "cmd /c echo hola");
        box.Set(Cu, RegSandbox.Run, "Expandible", @"%SystemRoot%\System32\cmd.exe /c rem", RawValue.ExpandSz);
        box.Set(Cu, RegSandbox.Run, "Vacio", "");
        box.SetDword(Cu, RegSandbox.Run, "NoEsCadena", 5);
        box.Set(Lm, RegSandbox.Run, "System32En32Bits", Path.Combine(system, "cmd.exe"), RawValue.Sz, RegView.Registry32);
        box.Set(Cu, RegSandbox.AppPaths + @"\valida.exe", "", box.Present);
        box.Set(Cu, RegSandbox.AppPaths + @"\expandible.exe", "", @"%SystemRoot%\System32\cmd.exe", RawValue.ExpandSz);
        box.Set(Cu, RegSandbox.AppPaths + @"\solo-path.exe", "Path", box.Dir);
        box.Set(Cu, RegSandbox.UninstallKey + @"\{11111111-2222-3333-4444-555555555555}", "UninstallString", "MsiExec.exe /X{11111111-2222-3333-4444-555555555555}");
        box.Set(Cu, RegSandbox.UninstallKey + @"\Msi2", "UninstallString", "\"" + box.Missing("msi.exe") + "\"");
        box.SetDword(Cu, RegSandbox.UninstallKey + @"\Msi2", "WindowsInstaller", 1);
        box.Set(Cu, RegSandbox.UninstallKey + @"\SinDesinstalador", "DisplayName", "Parche");
        box.Set(Cu, RegSandbox.Classes + @"\.claro1", "", "FFCleanerTest.EnUsuario");
        box.CreateKey(Cu, RegSandbox.Classes + @"\FFCleanerTest.EnUsuario");
        box.Set(Cu, RegSandbox.Classes + @"\.claro2", "", "FFCleanerTest.EnMaquina32");
        box.CreateKey(Lm, RegSandbox.Classes + @"\FFCleanerTest.EnMaquina32", RegView.Registry32);
        box.CreateKey(Cu, RegSandbox.Classes + @"\.sinvalor");
        box.Set(Cu, RegSandbox.Classes + @"\Applications\delegada.exe\shell\open\command", "DelegateExecute", "{00000000-0000-0000-0000-000000000000}");
        box.Set(Cu, RegSandbox.Classes + @"\Applications\mixta.exe\shell\open\command", "", "\"" + box.Missing("mixta.exe") + "\"");
        box.Set(Cu, RegSandbox.Classes + @"\Applications\mixta.exe\shell\edit\command", "", "\"" + box.Present + "\" \"%1\"");
        box.CreateKey(Cu, RegSandbox.Classes + @"\Applications\sin-comandos.exe\SupportedTypes");
        box.Set(Cu, RegSandbox.Mui, box.Present + ".ApplicationCompany", "Empresa");
        box.Set(Cu, RegSandbox.Mui, @"@%SystemRoot%\system32\shell32.dll,-1234", "Recurso");
        box.SetDword(Lm, RegSandbox.Shared, box.Present, 1);
        box.SetDword(Lm, RegSandbox.Shared, Path.Combine(system, "kernel32.dll"), 1, RegView.Registry32);
        var scan = box.Scan();
        Check.Equal(0, scan.Findings.Count, "Sin hallazgos: " + string.Join(" | ", scan.Findings.Select(f => f.Location.KeyPath + ":" + f.ValueName + ":" + f.Evidence)));
        Check.True(scan.Stats.All(s => s.KeysVisited > 0), "Todas las familias recorrieron claves.");
    }

    private static void Only_Selected()
    {
        using var box = new RegSandbox();
        box.SeedOrphans();
        var scan = box.Scan();
        var chosen = scan.Findings.Single(f => f.ValueName == "Huerfano");
        var machineBefore = box.TreeHash(Lm, @"Software\Microsoft");
        var report = box.RepairIds(scan, [chosen.Id]);
        Check.Equal(1, report.Repaired, "Una reparada.");
        Check.Equal(1, RegistryBackupStore.Load(report.BackupPath!).Entries.Count, "El respaldo solo contiene lo marcado.");
        Check.Null(box.Get(Cu, RegSandbox.Run, "Huerfano"), "El marcado se eliminó.");
        Has(box.Get(Cu, RegSandbox.Run, "HuerfanoSinComillas"), "Otro huérfano de la misma clave, no marcado, intacto.");
        Check.True(box.KeyExists(Cu, RegSandbox.AppPaths + @"\huerfana.exe") && box.KeyExists(Cu, RegSandbox.UninstallKey + @"\AppHuerfana"), "Otras familias intactas.");
        Check.Equal(machineBefore, box.TreeHash(Lm, @"Software\Microsoft"), "HKLM intacto.");
        Check.Equal(7, box.Scan().Findings.Count, "Quedan los otros siete hallazgos.");
    }

    private static void Backup_Failure_Blocks_Repair()
    {
        using var box = new RegSandbox();
        box.SeedOrphans();
        var before = box.TreeHash(Cu, "Software");
        var scan = box.Scan();
        // La «carpeta» de respaldos es en realidad un archivo: no se puede crear nada dentro.
        var blocked = Path.Combine(box.Dir, "no-es-carpeta");
        File.WriteAllText(blocked, "x");
        var repair = new RegistryRepairService(box.Scanner, new RegistryBackupStore(box.Scope, blocked));
        var report = repair.RepairAsync(scan, scan.Findings.Select(f => f.Id).ToList()).GetAwaiter().GetResult();
        Check.True(report.Refused && report.Refusal!.Contains("respaldo", StringComparison.OrdinalIgnoreCase), "Rechazado por el respaldo: " + report.Refusal);
        Check.Equal(0, report.Repaired, "Cero reparaciones.");
        Check.Equal(before, box.TreeHash(Cu, "Software"), "Registro intacto.");
        Check.Equal(8, box.Scan().Findings.Count, "Los hallazgos siguen ahí.");
    }

    private static void Concurrent_Change_Before_Repair()
    {
        using var box = new RegSandbox();
        box.SeedOrphans();
        var scan = box.Scan();
        // 1) El valor cambia de datos. 2) El destino de otro vuelve a existir. 3) Aparece contenido nuevo en una clave. 4) Otro valor desaparece.
        box.Set(Cu, RegSandbox.Run, "Huerfano", "\"" + box.Missing("otro-destino.exe") + "\"");
        Directory.CreateDirectory(Path.GetDirectoryName(box.Missing())!);
        File.WriteAllText(box.Missing("otro inicio.exe"), "reinstalado");
        box.Set(Cu, RegSandbox.AppPaths + @"\huerfana.exe", "NuevoValor", "añadido por otro programa");
        using (var key = box.Writer.Open(new(Cu, RegView.Registry64, RegSandbox.Mui), writable: true))
            RawRegistry.DeleteValue(key!, box.Missing("viejo.exe") + ".FriendlyAppName");
        var before = box.TreeHash(Cu, "Software");

        var report = box.RepairIds(scan, scan.Findings.Select(f => f.Id));
        RepairOutcome Outcome(string needle) => report.Outcomes.Single(o => needle.StartsWith("→", StringComparison.Ordinal) ? o.Location.EndsWith(needle, StringComparison.Ordinal) : o.Location.Contains(needle, StringComparison.OrdinalIgnoreCase));
        Check.Equal(RepairStatus.Conflicto, Outcome("→  Huerfano").Status, "Valor cambiado.");
        Check.Equal(RepairStatus.Omitido, Outcome("HuerfanoSinComillas").Status, "Destino reaparecido.");
        Check.Equal(RepairStatus.Conflicto, Outcome(@"App Paths\huerfana.exe").Status, "Clave con contenido nuevo.");
        Check.Equal(RepairStatus.Omitido, Outcome("viejo.exe").Status, "Valor desaparecido.");
        Check.Equal("\"" + box.Missing("otro-destino.exe") + "\"", box.Get(Cu, RegSandbox.Run, "Huerfano")!.Value.AsString(), "Dato nuevo conservado.");
        Has(box.Get(Cu, RegSandbox.Run, "HuerfanoSinComillas"), "Entrada válida conservada.");
        Has(box.Get(Cu, RegSandbox.AppPaths + @"\huerfana.exe", "NuevoValor"), "Contenido ajeno conservado.");
        // La carpeta de instalación del desinstalador huérfano ha vuelto a existir: deja de ser un caso seguro.
        Check.Equal(RepairStatus.Conflicto, Outcome("AppHuerfana").Status, "Carpeta de instalación reaparecida.");
        Check.True(box.KeyExists(Cu, RegSandbox.UninstallKey + @"\AppHuerfana"), "Desinstalador conservado.");
        Check.Equal(3, report.Repaired, "Los tres sin cambios sí se repararon.");
        Check.Equal(3, RegistryBackupStore.Load(report.BackupPath!).Entries.Count, "El respaldo solo incluye lo que se iba a modificar.");
        // Las claves afectadas por conflictos no cambiaron respecto al momento previo a reparar.
        Check.True(before != box.TreeHash(Cu, "Software"), "Las reparadas sí cambiaron el Registro.");
    }

    private static void Restore_Conflicts_And_Idempotence()
    {
        using var box = new RegSandbox();
        box.SeedOrphans();
        var original = box.TreeHash(Cu, "Software");
        var scan = box.Scan();
        var report = box.RepairIds(scan, scan.Findings.Select(f => f.Id));
        Check.Equal(8, report.Repaired, "Reparadas.");

        // Tras reparar, otro programa vuelve a crear dos entradas con datos distintos.
        box.Set(Cu, RegSandbox.Run, "Huerfano", "dato nuevo del usuario");
        box.Set(Cu, RegSandbox.AppPaths + @"\huerfana.exe", "", "C:\\otra\\ruta\\nueva.exe");
        var first = box.Restore(report.BackupPath!);
        Check.Equal(6, first.Restored, "Seis restauradas: " + string.Join(" | ", first.Outcomes.Select(o => o.Status + ":" + o.Location)));
        Check.Equal(2, first.Conflicts, "Dos conflictos.");
        Check.Equal("dato nuevo del usuario", box.Get(Cu, RegSandbox.Run, "Huerfano")!.Value.AsString(), "El valor nuevo no se sobrescribió.");
        Check.Equal("C:\\otra\\ruta\\nueva.exe", box.Get(Cu, RegSandbox.AppPaths + @"\huerfana.exe", "")!.Value.AsString(), "El valor nuevo de la clave no se sobrescribió.");
        Has(box.Get(Cu, RegSandbox.AppPaths + @"\huerfana.exe", "Path"), "Los valores que faltaban en la clave sí se restauraron.");

        // Resuelto el conflicto a mano (se quitan los datos nuevos), una segunda restauración completa el resto.
        using (var key = box.Writer.Open(new(Cu, RegView.Registry64, RegSandbox.Run), writable: true)) RawRegistry.DeleteValue(key!, "Huerfano");
        using (var key = box.Writer.Open(new(Cu, RegView.Registry64, RegSandbox.AppPaths + @"\huerfana.exe"), writable: true)) RawRegistry.DeleteValue(key!, "");
        var second = box.Restore(report.BackupPath!);
        Check.Equal(2, second.Restored, "Las dos pendientes.");
        Check.Equal(6, second.AlreadyPresent, "Las seis anteriores ya estaban.");
        Check.Equal(original, box.TreeHash(Cu, "Software"), "Estado original recuperado.");
        var third = box.Restore(report.BackupPath!);
        Check.True(third.Restored == 0 && third.AlreadyPresent == 8 && third.Conflicts == 0, "Restaurar de nuevo no cambia nada.");
        Check.Equal(original, box.TreeHash(Cu, "Software"), "Idempotente.");
        Check.True(box.Backups.List().Single().Valid && box.Backups.List().Single().Entries == 8, "El respaldo aparece en la lista.");
    }

    private static void Tampered_Or_Foreign_Backup()
    {
        using var box = new RegSandbox();
        box.SeedOrphans();
        var scan = box.Scan(RegistryFamily.Startup);
        var report = box.RepairIds(scan, scan.Findings.Select(f => f.Id));
        var after = box.TreeHash(Cu, "Software");
        var path = report.BackupPath!;
        var good = File.ReadAllBytes(path);

        // 1) Un byte alterado: la suma no cuadra.
        File.WriteAllText(path, Encoding.UTF8.GetString(good).Replace("Huerfano", "Inyectado"));
        var tampered = box.Restore(path);
        Check.True(tampered.Refused && tampered.Refusal!.Contains("suma", StringComparison.OrdinalIgnoreCase), "Alterado: " + tampered.Refusal);
        Check.False(box.Backups.List().Single().Valid, "La lista lo marca como no válido.");

        // 2) Suma recalculada pero entrada fuera de las claves de su familia (intento de escribir en otra clave).
        var document = RegistryBackupStore.Load(WriteTemp(box, good));
        var hostile = document with { Entries = [document.Entries[0] with { KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Policies\System" }] };
        File.WriteAllBytes(path, RegistryBackupStore.Serialize(hostile with { ContentSha256 = RegistryBackupStore.Checksum(hostile) }));
        var escaped = box.Restore(path);
        Check.True(!escaped.Refused && escaped.Failed == 1 && escaped.Restored == 0, "Entrada fuera de familia no se restaura.");
        Check.False(box.KeyExists(Cu, @"Software\Microsoft\Windows\CurrentVersion\Policies"), "No se creó la clave ajena.");

        // 3) Otro usuario/equipo, otro ámbito y rutas fuera de la carpeta de respaldos.
        var other = document with { Machine = "OTRO-EQUIPO" };
        File.WriteAllBytes(path, RegistryBackupStore.Serialize(other with { ContentSha256 = RegistryBackupStore.Checksum(other) }));
        Check.True(box.Restore(path).Refusal!.Contains("otro usuario", StringComparison.OrdinalIgnoreCase), "Otro equipo.");
        File.WriteAllBytes(path, good);
        using (var second = new RegSandbox())
        {
            var foreign = new RegistryBackupStore(second.Scope, box.Backups.BackupDirectory).RestoreAsync(path).GetAwaiter().GetResult();
            Check.True(foreign.Refused && foreign.Refusal!.Contains("ámbito", StringComparison.OrdinalIgnoreCase), "Otro ámbito: " + foreign.Refusal);
            var production = new RegistryBackupStore(RegistryScope.Production, box.Backups.BackupDirectory).RestoreAsync(path).GetAwaiter().GetResult();
            Check.True(production.Refused, "Un respaldo del sandbox jamás se restaura sobre el Registro real.");
        }
        var outside = Path.Combine(box.Dir, RegistryBackupStore.DocumentName);
        File.WriteAllBytes(outside, good);
        Check.True(box.Restore(outside).Refused, "Archivo fuera de la carpeta de respaldos.");
        Check.Equal(after, box.TreeHash(Cu, "Software"), "Ningún intento modificó el Registro.");

        // 4) El respaldo original, intacto, sí se restaura.
        Check.Equal(2, box.Restore(path).Restored, "Respaldo legítimo restaurado.");
    }

    private static string WriteTemp(RegSandbox box, byte[] content)
    {
        var path = Path.Combine(box.Dir, "copia-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllBytes(path, content);
        return path;
    }

    private static void Machine_Anchor()
    {
        using var box = new RegSandbox();
        box.SetDword(Lm, RegSandbox.Shared, box.Missing("a.dll"), 1);
        box.Set(Lm, RegSandbox.Run, "Maquina", "\"" + box.Missing("m.exe") + "\"");
        box.Set(Cu, RegSandbox.Run, "Usuario", "\"" + box.Missing("u.exe") + "\"");
        var scan = box.Scan();
        var report = box.RepairIds(scan, scan.Findings.Select(f => f.Id));
        Check.Equal(3, report.Repaired, "Tres reparadas (HKLM modificable en este ámbito).");
        var document = RegistryBackupStore.Load(report.BackupPath!);
        const string anchor = @"Software\FFCleaner\RegistryBackups";
        Check.Equal(document.ContentSha256, box.Get(Lm, anchor, document.BackupId.ToString("N"))?.AsString(), "Ancla con la suma del respaldo en HKLM.");

        // Respaldo reescrito con suma coherente: pasa la suma pero no el ancla; solo HKCU se restaura.
        var forged = document with { Entries = [.. document.Entries.Select(e => e.Hive == Lm && e.Value!.Name == "Maquina" ? e with { Value = e.Value with { Data = Convert.ToBase64String(Encoding.Unicode.GetBytes("C:\\malware.exe\0")) } } : e)] };
        File.WriteAllBytes(report.BackupPath!, RegistryBackupStore.Serialize(forged with { ContentSha256 = RegistryBackupStore.Checksum(forged) }));
        var restore = box.Restore(report.BackupPath!);
        Check.Equal(1, restore.Restored, "Solo la entrada de HKCU.");
        Check.Equal(2, restore.Failed, "Las dos de HKLM se rechazan.");
        Check.True(restore.Outcomes.Where(o => o.Status == RestoreStatus.Error).All(o => o.Detail.Contains("ancla", StringComparison.OrdinalIgnoreCase)), "Motivo: ancla.");
        Check.Null(box.Get(Lm, RegSandbox.Run, "Maquina"), "Nada falsificado llegó a HKLM.");
        Has(box.Get(Cu, RegSandbox.Run, "Usuario"), "HKCU restaurado.");

        // Con el documento auténtico, HKLM sí se restaura.
        File.WriteAllBytes(report.BackupPath!, RegistryBackupStore.Serialize(document));
        var genuine = box.Restore(report.BackupPath!);
        Check.True(genuine.Restored == 2 && genuine.AlreadyPresent == 1 && genuine.Failed == 0, "Auténtico restaurado.");
        Check.Equal("\"" + box.Missing("m.exe") + "\"", box.Get(Lm, RegSandbox.Run, "Maquina")!.Value.AsString(), "Dato original en HKLM.");
    }

    private static void Invalid_Batches()
    {
        using var box = new RegSandbox();
        box.SeedOrphans();
        var before = box.TreeHash(Cu, "Software");
        var scan = box.Scan();
        var ids = scan.Findings.Select(f => f.Id).ToList();

        Check.True(box.RepairIds(scan, []).Refused, "Sin hallazgos marcados.");
        Check.True(box.RepairIds(scan, ["reg-inventado"]).Refused, "Hallazgo ajeno al análisis.");
        using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel();
            Check.True(box.RepairIds(scan, ids, cancel.Token).Refused, "Cancelado antes de empezar.");
            Check.True(box.Scanner.Scan(RegSandbox.All, cancel.Token).Cancelled, "Análisis cancelado.");
            Check.True(box.RepairIds(box.Scanner.Scan(RegSandbox.All, cancel.Token), ids).Refused, "Un análisis cancelado no autoriza reparar.");
        }
        var late = new RegistryRepairService(box.Scanner, box.Backups, clock: () => scan.CreatedUtc + RegistryRepairService.ScanLifetime + TimeSpan.FromSeconds(1));
        Check.True(late.RepairAsync(scan, ids).GetAwaiter().GetResult().Refusal!.Contains("caducó"), "Análisis caducado.");
        using (var other = new RegSandbox())
            Check.True(new RegistryRepairService(other.Scanner, other.Backups).RepairAsync(scan, ids).GetAwaiter().GetResult().Refused, "Análisis de otro ámbito.");
        var production = new RegistryScanner();
        Check.True(new RegistryRepairService(production, new RegistryBackupStore(RegistryScope.Production, Path.Combine(box.Dir, "prod"))).RepairAsync(scan, ids).GetAwaiter().GetResult().Refused,
            "Un análisis del sandbox jamás repara el Registro real.");
        Check.Equal(before, box.TreeHash(Cu, "Software"), "Ningún lote rechazado modificó nada.");
        Check.False(Directory.Exists(box.Backups.BackupDirectory) && Directory.GetDirectories(box.Backups.BackupDirectory).Length > 0, "Tampoco se crearon respaldos.");
    }

    private static void Bounded_Trees()
    {
        using var box = new RegSandbox();
        var big = RegSandbox.UninstallKey + @"\Enorme";
        box.Set(Cu, big, "UninstallString", "\"" + box.Missing("enorme.exe") + "\"");
        box.Set(Cu, big, "InstallLocation", Path.GetDirectoryName(box.Missing())!);
        for (var i = 0; i < 300; i++) box.CreateKey(Cu, big + @"\sub" + i);
        var small = RegSandbox.UninstallKey + @"\Normal";
        box.Set(Cu, small, "UninstallString", "\"" + box.Missing("normal.exe") + "\"");
        box.Set(Cu, small, "InstallLocation", Path.GetDirectoryName(box.Missing())!);
        box.CreateKey(Cu, small + @"\Componentes\Uno");
        var scan = box.Scan(RegistryFamily.Uninstall);
        Check.Equal(RegistryRisk.Ambiguo, scan.Findings.Single(f => f.Location.KeyPath.EndsWith("Enorme", StringComparison.Ordinal)).Risk, "Árbol demasiado grande.");
        var normal = scan.Findings.Single(f => f.Location.KeyPath.EndsWith("Normal", StringComparison.Ordinal));
        Check.True(normal.CanRepair, "Árbol pequeño reparable.");
        var report = box.RepairIds(scan, scan.Findings.Select(f => f.Id));
        Check.Equal(1, report.Repaired, "Solo el pequeño.");
        Check.True(box.KeyExists(Cu, big + @"\sub299") && !box.KeyExists(Cu, small), "Grande intacto; pequeño eliminado con sus subclaves.");
        Check.Equal(1, box.Restore(report.BackupPath!).Restored, "Restaurado.");
        Check.True(box.KeyExists(Cu, small + @"\Componentes\Uno"), "Subclaves restauradas.");
    }
}
