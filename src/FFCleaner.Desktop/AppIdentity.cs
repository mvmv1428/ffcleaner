using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace FFCleaner.Desktop;

/// <summary>Nombre visible de un programa: el que muestra el Administrador de tareas (descripción del ejecutable).</summary>
public sealed record AppIdentity(string DisplayName, string? Publisher, string? ExecutablePath);

/// <summary>
/// Averigua el nombre y el editor de un programa de inicio a partir de su ejecutable o de su acceso directo.
/// Solo lee metadatos (recursos de versión y destino del acceso directo); nunca ejecuta nada. Sin dependencias
/// de WPF para poder probarlo aparte.
/// </summary>
public static class AppIdentityResolver
{
    public static AppIdentity Resolve(string fallbackName, string? path)
    {
        var executable = path;
        if (path is not null && path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            executable = ShortcutTarget(path);
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
            return new(fallbackName, null, null);
        try
        {
            var info = FileVersionInfo.GetVersionInfo(executable);
            var name = Clean(info.FileDescription) ?? Clean(info.ProductName) ?? fallbackName;
            return new(name, Clean(info.CompanyName), executable);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new(fallbackName, null, executable);
        }
    }

    /// <summary>Destino de un acceso directo .lnk, o null si no se puede leer.</summary>
    public static string? ShortcutTarget(string shortcut)
    {
        object? link = null;
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"));
            if (type is null) return null;
            link = Activator.CreateInstance(type);
            if (link is not IPersistFile file || link is not IShellLinkW shell) return null;
            file.Load(shortcut, 0);   // STGM_READ
            var target = new StringBuilder(1024);
            shell.GetPath(target, target.Capacity, IntPtr.Zero, 0);
            var text = Environment.ExpandEnvironmentVariables(target.ToString());
            return text.Length == 0 ? null : text;
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or InvalidCastException or ArgumentException)
        {
            return null;
        }
        finally
        {
            if (link is not null && Marshal.IsComObject(link)) Marshal.ReleaseComObject(link);
        }
    }

    private static string? Clean(string? text)
    {
        var value = text?.Trim();
        return string.IsNullOrEmpty(value) ? null : UiPolicy.VisibleText(value);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int capacity, IntPtr findData, uint flags);
    }
}
