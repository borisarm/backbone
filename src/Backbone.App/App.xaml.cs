using Backbone.App.Ipc;
using Backbone.App.Shell;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.ApplicationModel.Resources;

namespace Backbone.App;

/// <summary>
/// App WinUI 3. Crea el cliente IPC compartido (§9.1), el icono de la bandeja (HU-18) y la ventana principal, que se oculta al cerrarla.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Los recursos viven tanto como el proceso y se liberan en ExitApplication.")]
public partial class App : Application
{
    /// <summary>Argumento con el que arranca la app al iniciar sesión (§9.1: minimizada en la bandeja).</summary>
    internal const string MinimizedArgument = "--minimized";

    private static App? s_instance;

    private DispatcherQueue? _dispatcher;
    private MainWindow? _window;
    private BackboneClient? _client;
    private TrayController? _tray;
    private bool _exiting;

    public App()
    {
        InitializeComponent();
        s_instance = this;
    }

    /// <summary>
    /// La primera instancia recibe aquí la activación redirigida por una segunda ejecución (llega en un hilo cualquiera):
    /// trae la ventana al frente.
    /// </summary>
    internal static void ActivateExistingInstance() => s_instance?._dispatcher?.TryEnqueue(() => s_instance.ShowMainWindow());

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        _dispatcher = dispatcher;
        var strings = new ResourceLoader();

        _window = new MainWindow(strings.GetString("App_Title"));
        _client = new BackboneClient(
            new NamedPipeBackboneConnector(new ServiceProcessVerifier(), TimeProvider.System),
            TimeProvider.System,
            post: action => dispatcher.TryEnqueue(() => action()));
        _tray = new TrayController(_client, strings.GetString, dispatcher, ShowMainWindow, ExitApplication);
        _client.Start();

        var minimized = Environment.GetCommandLineArgs().Contains(MinimizedArgument, StringComparer.OrdinalIgnoreCase);
        if (!minimized)
        {
            ShowMainWindow();
        }
    }

    private void ShowMainWindow() => _window?.ShowAndActivate();

    /// <summary>"Salir" de la bandeja: cierra la app (no el servicio, que sigue copiando).</summary>
    private void ExitApplication()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        _tray?.Dispose();
        _window?.AllowClose();
        var client = _client;
        _ = Task.Run(async () =>
        {
            if (client is not null)
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }

            _dispatcher?.TryEnqueue(() => base.Exit());
        });
    }
}
