using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;

namespace FFCleaner.Desktop;

/// <summary>
/// Sección «Configuración»: tema, idioma, conexión de agentes por MCP y carpeta de datos. Cada cambio se guarda
/// al momento en settings.json y se aplica sin reiniciar.
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly SettingsHolder _settings;
    private string _status = "";

    public SettingsViewModel(SettingsHolder settings, string dataDirectory, string mcpPath, UpdateCoordinator? updates = null)
    {
        _settings = settings;
        Updates = updates;
        CheckUpdatesCommand = new RelayCommand(() => Updates?.RunAsync(automatic: false) ?? Task.CompletedTask, () => Updates is { Enabled: true, IsWorking: false });
        DataDirectory = dataDirectory;
        McpPath = mcpPath;
        CopyCommandCommand = new RelayCommand(() => Copy(McpCommand, Loc.T("Comando copiado. Pégalo en una terminal.", "Command copied. Paste it in a terminal.")));
        CopyJsonCommand = new RelayCommand(() => Copy(McpJson, Loc.T("Configuración copiada. Pégala en la configuración MCP de tu agente.", "Config copied. Paste it into your agent's MCP configuration.")));
        OpenDataCommand = new RelayCommand(() =>
        {
            Directory.CreateDirectory(DataDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + DataDirectory + "\"") { UseShellExecute = true });
        });
        Loc.Changed += (_, _) => { Raise(nameof(McpAvailability)); Raise(nameof(VersionText)); Status = ""; };
    }

    public string DataDirectory { get; }
    public string McpPath { get; }
    public string McpCommand => "claude mcp add ffcleaner -- \"" + McpPath + "\"";
    public string McpJson => JsonSerializer.Serialize(new { mcpServers = new { ffcleaner = new { command = McpPath, args = Array.Empty<string>() } } },
        new JsonSerializerOptions { WriteIndented = true });
    public bool HasMcpWarning => !File.Exists(McpPath);
    public string McpAvailability => HasMcpWarning
        ? Loc.T("No se encuentra FFCleaner.Mcp.exe junto a la aplicación: usa el paquete portable completo.", "FFCleaner.Mcp.exe was not found next to the app: use the full portable package.")
        : "";
    public string VersionText => "FFCleaner " + (typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "");
    public ICommand CopyCommandCommand { get; }
    public ICommand CopyJsonCommand { get; }
    public ICommand OpenDataCommand { get; }
    public ICommand CheckUpdatesCommand { get; }
    public UpdateCoordinator? Updates { get; }

    public bool AutoUpdate
    {
        get => _settings.Current.AutoUpdate;
        set { if (value == AutoUpdate) return; _settings.Update(s => s with { AutoUpdate = value }); Raise(); Status = Loc.T("Preferencia de actualizaciones guardada.", "Update preference saved."); }
    }
    public string Status { get => _status; private set => Set(ref _status, value); }

    public bool IsThemeSystem { get => Theme == "system"; set { if (value) Theme = "system"; } }
    public bool IsThemeLight { get => Theme == "light"; set { if (value) Theme = "light"; } }
    public bool IsThemeDark { get => Theme == "dark"; set { if (value) Theme = "dark"; } }
    public bool IsSpanish { get => Language == "es"; set { if (value) Language = "es"; } }
    public bool IsEnglish { get => Language == "en"; set { if (value) Language = "en"; } }

    private string Theme
    {
        get => _settings.Current.Theme;
        set
        {
            if (value == Theme) return;
            _settings.Update(s => s with { Theme = value });
            ThemeManager.Apply(value);
            Raise(nameof(IsThemeSystem)); Raise(nameof(IsThemeLight)); Raise(nameof(IsThemeDark));
            Status = Loc.T("Tema guardado.", "Theme saved.");
        }
    }

    private string Language
    {
        get => _settings.Current.Language;
        set
        {
            if (value == Language) return;
            _settings.Update(s => s with { Language = value });
            Loc.Apply(value);
            Raise(nameof(IsSpanish)); Raise(nameof(IsEnglish));
            Status = Loc.T("Idioma guardado.", "Language saved.");
        }
    }

    private void Copy(string text, string done)
    {
        try
        {
            Clipboard.SetText(text);
            Status = done;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            Status = Loc.T("El portapapeles está ocupado; vuelve a intentarlo.", "The clipboard is busy; try again.");
        }
    }
}
