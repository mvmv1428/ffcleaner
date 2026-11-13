using System;
using System.IO;
using System.Linq;
using FFCleaner.RegistryAnalysis;

namespace FFCleaner.RegistryTests;

/// <summary>Gestor de programas de inicio (B1): listar, desactivar y reactivar sin borrar nada, en el sandbox.</summary>
internal static class StartupTests
{
    private const RegHive Cu = RegHive.CurrentUser, Lm = RegHive.LocalMachine;
    private const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    public static void Register()
    {
        TestRegistry.Add("B1 inicio: lista Run de usuario y equipo (64 y 32 bits) y carpetas Inicio con su estado real", List_All_Sources);
        TestRegistry.Add("B1 inicio: desactivar y reactivar solo cambia StartupApproved; la entrada original queda intacta", Toggle_Is_Reversible);
        TestRegistry.Add("B1 inicio: entradas de todo el equipo sin permisos no se tocan", Machine_Without_Rights);
    }

    private static (RegSandbox Box, string UserFolder, string CommonFolder, StartupManager Manager) Setup(params RegHive[] readOnly)
    {
        var box = new RegSandbox();
        var userFolder = Path.Combine(box.Dir, "Startup usuario");
        var commonFolder = Path.Combine(box.Dir, "Startup comun");
        Directory.CreateDirectory(userFolder);
        Directory.CreateDirectory(commonFolder);
        File.WriteAllText(Path.Combine(userFolder, "Notas.lnk"), "acceso directo");
        File.WriteAllText(Path.Combine(userFolder, "desktop.ini"), "[.ShellClassInfo]");
        File.WriteAllText(Path.Combine(commonFolder, "Agente.lnk"), "acceso directo");
        box.Set(Cu, RegSandbox.Run, "AppUsuario", "\"" + box.Present + "\" --tray");
        box.Set(Cu, RegSandbox.Run, "AppDesinstalada", "\"" + box.Missing("x.exe") + "\"");
        box.Set(Lm, RegSandbox.Run, "AppEquipo", "\"" + box.Present + "\"");
        box.Set(Lm, RegSandbox.Run, "AppEquipo32", "\"" + box.Present + "\"", view: RegView.Registry32);
        var scope = readOnly.Length == 0 ? box.Scope : RegistryScope.Sandbox(box.Id, readOnly);
        return (box, userFolder, commonFolder, new StartupManager(scope, userFolder, commonFolder, clock: () => new DateTime(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc)));
    }

    private static void List_All_Sources()
    {
        var (box, _, _, manager) = Setup();
        using (box)
        {
            // Estados ya presentes, escritos como lo hace el Administrador de tareas: 06 activo, 07 y 03 desactivados.
            box.SetRaw(Cu, Approved + @"\Run", "AppDesinstalada", RawValue.Binary, [0x03, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8]);
            box.SetRaw(Lm, Approved + @"\Run32", "AppEquipo32", RawValue.Binary, [0x07, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
            box.SetRaw(Lm, Approved + @"\Run", "AppEquipo", RawValue.Binary, [0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
            var entries = manager.List();
            Check.Equal(6, entries.Count, "Cuatro entradas de Run y dos accesos directos (sin desktop.ini): " + string.Join(", ", entries.Select(e => e.Name)));
            StartupEntry E(string name) => entries.Single(e => e.Name == name);
            Check.True(E("AppUsuario").Enabled && E("AppUsuario").Source == StartupSource.RegistroUsuario && !E("AppUsuario").RequiresAdmin, "Usuario activa");
            Check.False(E("AppDesinstalada").Enabled, "03 = desactivada");
            Check.Equal(TargetState.Missing, E("AppDesinstalada").TargetState, "Programa ausente detectado");
            Check.True(E("AppEquipo").Enabled && E("AppEquipo").RequiresAdmin, "06 = activa; equipo requiere administrador");
            Check.False(E("AppEquipo32").Enabled, "07 = desactivada (32 bits)");
            Check.Equal(StartupSource.RegistroEquipo32, E("AppEquipo32").Source, "Origen 32 bits");
            Check.True(E("Notas").Source == StartupSource.CarpetaUsuario && E("Agente").Source == StartupSource.CarpetaEquipo, "Carpetas Inicio");
        }
    }

    private static void Toggle_Is_Reversible()
    {
        var (box, userFolder, _, manager) = Setup();
        using (box)
        {
            var runBefore = box.TreeHash(Cu, RegSandbox.Run);
            var machineBefore = box.TreeHash(Lm, RegSandbox.Run);
            foreach (var name in new[] { "AppUsuario", "Notas", "AppEquipo" })
            {
                var entry = manager.List().Single(e => e.Name == name);
                var off = manager.SetEnabled(entry, false);
                Check.True(off.Success, "Desactivada: " + off.Message);
                Check.False(manager.List().Single(e => e.Name == name).Enabled, "Consta como desactivada: " + name);
                var on = manager.SetEnabled(entry, true);
                Check.True(on.Success && manager.List().Single(e => e.Name == name).Enabled, "Reactivada: " + name);
            }
            var user = box.Get(Cu, Approved + @"\Run", "AppUsuario")!.Value;
            Check.True(user.Kind == RawValue.Binary && user.Data.Length == 12 && user.Data[0] == 0x02, "Valor de activación de 12 bytes con 02");
            manager.SetEnabled(manager.List().Single(e => e.Name == "AppUsuario"), false);
            var disabled = box.Get(Cu, Approved + @"\Run", "AppUsuario")!.Value;
            Check.True(disabled.Data[0] == 0x03 && BitConverter.ToInt64(disabled.Data, 4) == new DateTime(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc).ToFileTimeUtc(), "03 y fecha del cambio");
            Check.True(box.Get(Cu, Approved + @"\StartupFolder", "Notas.lnk") is not null, "Carpeta Inicio: se marca por nombre de archivo");
            Check.True(box.Get(Lm, Approved + @"\Run", "AppEquipo") is not null, "Equipo: StartupApproved de HKLM");
            Check.Equal(runBefore, box.TreeHash(Cu, RegSandbox.Run), "El valor de Run del usuario no cambia");
            Check.Equal(machineBefore, box.TreeHash(Lm, RegSandbox.Run), "El valor de Run del equipo no cambia");
            Check.True(File.Exists(Path.Combine(userFolder, "Notas.lnk")), "El acceso directo no se mueve ni se borra");
        }
    }

    private static void Machine_Without_Rights()
    {
        var (box, _, _, manager) = Setup(Lm);
        using (box)
        {
            var entry = manager.List().Single(e => e.Name == "AppEquipo");
            var result = manager.SetEnabled(entry, false);
            Check.False(result.Success, "Sin permiso no se cambia: " + result.Message);
            Check.Null(box.Get(Lm, Approved + @"\Run", "AppEquipo"), "No se escribió nada en HKLM");
            Check.True(manager.SetEnabled(manager.List().Single(e => e.Name == "AppUsuario"), false).Success, "Las del usuario sí se pueden cambiar");
        }
    }
}
