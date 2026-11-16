using Backbone.App.Ipc;
using Backbone.App.Shell;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace Backbone.App;

/// <summary>
/// Ventana principal. Vacía hasta que se implemente la navegación (§9.2). Cerrarla la oculta en la bandeja (§9.1);
/// solo "Salir" la cierra de verdad.
/// </summary>
public sealed partial class MainWindow : Window
{
    private bool _allowClose;

    public MainWindow(string title)
    {
        InitializeComponent();
        Title = title;
        AppWindow.Closing += OnClosing;
    }

    /// <summary>Permite el cierre real (al salir de la app).</summary>
    internal void AllowClose() => _allowClose = true;

    /// <summary>Muestra la ventana (si estaba oculta o minimizada) y la trae al frente.</summary>
    internal void ShowAndActivate()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }

        AppWindow.Show();
        Activate();
        NativeMethods.SetForegroundWindow(WindowNative.GetWindowHandle(this));
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!_allowClose)
        {
            args.Cancel = true;
            sender.Hide();
        }
    }
}
