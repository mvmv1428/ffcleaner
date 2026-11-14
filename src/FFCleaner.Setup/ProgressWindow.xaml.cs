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

    public void ShowError(string message)
    {
        Bar.Value = 0;
        StatusText.Text = "No se pudo actualizar: " + message + " Se abrirá la versión que tenías.";
    }
}
