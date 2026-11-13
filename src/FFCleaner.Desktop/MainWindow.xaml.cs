using System.ComponentModel;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace FFCleaner.Desktop;

/// <summary>
/// Ventana con cuatro secciones: Limpieza personalizada, Registro, Inicio de Windows y Configuración. Solo navega, anima y muestra avisos; la lógica está en las vistas-modelo.
/// </summary>
public partial class MainWindow : Window
{
    private static readonly Duration Fast = new(TimeSpan.FromMilliseconds(260));
    private readonly CleanupViewModel _cleanup;
    private readonly RegistryViewModel _registry;
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    private bool _closing;

    private readonly StartupViewModel _startup;

    public MainWindow(CleanupViewModel cleanup, RegistryViewModel registry, StartupViewModel startup, SettingsViewModel settings, Func<string> mode, UpdateCoordinator updates, string? updatedFrom)
    {
        InitializeComponent();
        _cleanup = cleanup;
        _registry = registry;
        CleanupPage.DataContext = cleanup;
        RegistryPage.DataContext = registry;
        StartupPage.DataContext = startup;
        SettingsPage.DataContext = settings;
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        _startup = startup;
        ModeText.Text = mode();
        Loc.Changed += (_, _) => ModeText.Text = mode();
        registry.ShutdownRequested += (_, _) => Close();
        cleanup.Noticed += (_, notice) => ShowToast(notice);
        registry.Noticed += (_, notice) => ShowToast(notice);
        startup.Noticed += (_, notice) => ShowToast(notice);
        updates.Noticed += (_, notice) => ShowToast(notice);
        Loaded += async (_, _) =>
        {
            if (updatedFrom is not null)
                ShowToast(new(Loc.T("FFCleaner se actualizó", "FFCleaner was updated"),
                    Loc.T($"Ahora tienes la versión {UpdateService.Current.ToString(3)} (antes {updatedFrom}).", $"You now have version {UpdateService.Current.ToString(3)} (was {updatedFrom})."), NoticeKind.Exito));
            await updates.StartupAsync();
        };
        _toastTimer.Tick += (_, _) => HideToast();
        Show(CleanupPage, animate: false);
    }

    private void NavCleanup_Click(object sender, RoutedEventArgs e) => Show(CleanupPage, animate: true);
    private void NavRegistry_Click(object sender, RoutedEventArgs e) => Show(RegistryPage, animate: true);
    private void NavSettings_Click(object sender, RoutedEventArgs e) => Show(SettingsPage, animate: true);
    private void NavStartup_Click(object sender, RoutedEventArgs e)
    {
        Show(StartupPage, animate: true);
        _startup.EnsureLoaded();
    }

    /// <summary>Cambia de sección con una transición corta: la nueva entra deslizándose y aparece de forma gradual.</summary>
    private void Show(FrameworkElement incoming, bool animate)
    {
        NavState.SetIsActive(NavCleanupButton, incoming == CleanupPage);
        NavState.SetIsActive(NavRegistryButton, incoming == RegistryPage);
        NavState.SetIsActive(NavStartupButton, incoming == StartupPage);
        NavState.SetIsActive(NavSettingsButton, incoming == SettingsPage);
        if (incoming.Visibility == Visibility.Visible) return;
        foreach (var page in new FrameworkElement[] { CleanupPage, RegistryPage, StartupPage, SettingsPage })
            if (page != incoming) page.Visibility = Visibility.Collapsed;
        incoming.Visibility = Visibility.Visible;
        if (!animate) return;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        incoming.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Fast) { EasingFunction = ease });
        ((TranslateTransform)incoming.RenderTransform).BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, Fast) { EasingFunction = ease });
    }

    private void ShowToast(Notice notice)
    {
        ToastTitle.Text = notice.Title;
        ToastMessage.Text = notice.Message;
        (ToastIcon.Text, ToastBadge.Background) = notice.Kind switch
        {
            NoticeKind.Exito => ("", Gradient("#10B981", "#06B6D4")),
            NoticeKind.Aviso => ("", Gradient("#F59E0B", "#E5484D")),
            _ => ("", (Brush)FindResource("AccentGradient")),
        };
        Toast.Visibility = Visibility.Visible;
        var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 };
        Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Fast));
        ((TranslateTransform)Toast.RenderTransform).BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(30, 0, new Duration(TimeSpan.FromMilliseconds(380))) { EasingFunction = ease });
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void HideToast()
    {
        _toastTimer.Stop();
        if (Toast.Visibility != Visibility.Visible) return;
        var fade = new DoubleAnimation(1, 0, Fast);
        fade.Completed += (_, _) => { if (!_toastTimer.IsEnabled) Toast.Visibility = Visibility.Collapsed; };
        Toast.BeginAnimation(OpacityProperty, fade);
    }

    private void ToastClose_Click(object sender, RoutedEventArgs e) => HideToast();

    /// <summary>
    /// Un clic en cualquier parte de la tarjeta de una categoría marca o desmarca su casilla según su estado.
    /// Los clics sobre la propia casilla o sobre una pastilla de perfil ya los gestionan esos controles.
    /// </summary>
    private void CategoryCard_Click(object sender, MouseButtonEventArgs e)
    {
        for (var node = e.OriginalSource as DependencyObject; node is not null && node != sender; node = Parent(node))
            if (node is ToggleButton) return;
        if (sender is not FrameworkElement { DataContext: TreeNodeVm category } || !_cleanup.IsIdle) return;
        category.IsChecked = category.IsChecked != true;
        e.Handled = true;
    }

    /// <summary>Marcar o desmarcar la casilla de una aplicación también muestra sus categorías, como al pulsar su nombre.</summary>
    private void AppCheck_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TreeNodeVm app }) _cleanup.SelectedApp = app;
    }

    private static DependencyObject? Parent(DependencyObject node) =>
        node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);

    private static LinearGradientBrush Gradient(string from, string to) => new(
        (Color)ColorConverter.ConvertFromString(from), (Color)ColorConverter.ConvertFromString(to), new Point(0, 0), new Point(1, 1));

    /// <summary>
    /// Si hay una limpieza o reparación en curso, se cancela de forma cooperativa y se espera a que el
    /// elemento actual termine antes de cerrar: nunca se corta una operación a mitad de un archivo o una clave.
    /// </summary>
    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_closing || (!_cleanup.IsBusy && !_registry.IsBusy)) return;
        e.Cancel = true;
        _closing = true;
        await Task.WhenAll(_cleanup.StopAsync(), _registry.StopAsync());
        Close();
    }
}
