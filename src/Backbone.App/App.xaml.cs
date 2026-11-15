using Microsoft.UI.Xaml;

namespace Backbone.App;

/// <summary>
/// Punto de entrada de la app WinUI 3. Esqueleto mínimo (HT-01); la funcionalidad llega en sus historias.
/// </summary>
public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
