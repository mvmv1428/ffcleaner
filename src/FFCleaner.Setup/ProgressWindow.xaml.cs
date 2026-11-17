using System.Windows;

namespace FFCleaner.Setup;

/// <summary>Progreso de la actualización automática (sin botones: termina sola y abre FFCleaner).</summary>
public partial class ProgressWindow : Window
{
    public ProgressWindow()
    {
        InitializeComponent();
        VersionText.Text = "Instalando la versión " + SetupEngine.Version;
    }

    public void Report((double Percent, string Text) value)
    {
        Bar.Value = value.Percent;
        StatusText.Text = value.Text;
    }

    /// <param name="relaunching">Si se va a reabrir la versión instalada; si no, no se promete.</param>
    public void ShowError(string message, bool relaunching)
    {
        Bar.Value = 0;
        StatusText.Text = "No se pudo actualizar: " + message + (relaunching ? " Se abrirá la versión que tenías." : "");
    }
}
