using System.Windows;

namespace FFCleaner.Desktop;

/// <summary>Marca qué botón de la barra lateral corresponde a la sección visible, para resaltarlo.</summary>
public static class NavState
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.RegisterAttached(
        "IsActive", typeof(bool), typeof(NavState), new FrameworkPropertyMetadata(false));

    public static bool GetIsActive(DependencyObject element) => (bool)element.GetValue(IsActiveProperty);
    public static void SetIsActive(DependencyObject element, bool value) => element.SetValue(IsActiveProperty, value);
}
