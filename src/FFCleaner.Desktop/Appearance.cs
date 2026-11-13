using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace FFCleaner.Desktop;

/// <summary>
/// Idioma de la interfaz. Los textos fijos de la ventana están en Strings/es.xaml y Strings/en.xaml y se cambian
/// en caliente; los mensajes que componen las vistas-modelo usan <see cref="T"/> con las dos versiones juntas.
/// </summary>
public static class Loc
{
    public static string Language { get; private set; } = "es";
    public static bool English => Language == "en";

    /// <summary>Elige la versión del texto según el idioma activo.</summary>
    public static string T(string es, string en) => English ? en : es;

    /// <summary>Se dispara al cambiar de idioma, para que cada sección rehaga sus textos compuestos.</summary>
    public static event EventHandler? Changed;

    public static void Apply(string language)
    {
        Language = language == "en" ? "en" : "es";
        UiPolicy.Culture = CultureInfo.GetCultureInfo(Language);
        Swap(1, new Uri("Strings/" + Language + ".xaml", UriKind.Relative));
        Changed?.Invoke(null, EventArgs.Empty);
    }

    internal static void Swap(int index, Uri source)
    {
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var replacement = new ResourceDictionary { Source = source };
        if (index < dictionaries.Count) dictionaries[index] = replacement;
        else dictionaries.Add(replacement);
    }
}

/// <summary>Tema claro u oscuro, o el que use Windows. Cambia la paleta y la barra de título sin reiniciar.</summary>
public static class ThemeManager
{
    public static bool IsDark { get; private set; }

    /// <summary>¿Usa Windows el modo oscuro para las aplicaciones? Solo lectura del ajuste del usuario.</summary>
    public static bool WindowsPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return false;
        }
    }

    public static void Apply(string theme)
    {
        IsDark = theme switch { "dark" => true, "light" => false, _ => WindowsPrefersDark() };
        Loc.Swap(0, new Uri(IsDark ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative));
        foreach (Window window in Application.Current.Windows) ApplyTitleBar(window);
    }

    /// <summary>Barra de título oscura o clara a juego con el tema (Windows 10 20H1 o posterior; si no, se ignora).</summary>
    public static void ApplyTitleBar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var dark = IsDark ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
