using Backbone.App.Ipc;
using Backbone.App.Tray;
using Microsoft.UI.Dispatching;

namespace Backbone.App.Shell;

/// <summary>
/// Une el cliente IPC con el icono de la bandeja (HU-18): refleja el estado (<see cref="TrayStatusTracker"/>), anima el icono
/// mientras se copia y atiende el menú contextual. No contiene reglas de negocio: solo traduce estado a icono y órdenes a
/// llamadas del contrato. Se usa desde el hilo de UI (<c>BackboneClient</c> entrega sus eventos con <c>post</c>).
/// </summary>
internal sealed class TrayController : IDisposable
{
    private static readonly TimeSpan s_animationInterval = TimeSpan.FromMilliseconds(150);

    private readonly BackboneClient _client;
    private readonly TrayStatusTracker _tracker = new();
    private readonly TrayTooltipFormatter _tooltip;
    private readonly Func<string, string> _getString;
    private readonly Action _openApp;
    private readonly Action _exitApp;
    private readonly TrayWindow _window = new();
    private readonly TrayIconFactory _icons = new();
    private readonly DispatcherQueueTimer _animation;
    private readonly CancellationTokenSource _cts = new();
    private int _frame;
    private bool _disposed;

    public TrayController(
        BackboneClient client,
        Func<string, string> getString,
        DispatcherQueue dispatcher,
        Action openApp,
        Action exitApp)
    {
        _client = client;
        _getString = getString;
        _openApp = openApp;
        _exitApp = exitApp;
        _tooltip = new TrayTooltipFormatter(getString, TimeProvider.System);

        _animation = dispatcher.CreateTimer();
        _animation.Interval = s_animationInterval;
        _animation.Tick += (_, _) =>
        {
            _frame = (_frame + 1) % TrayIconRenderer.CopyingFrames;
            Apply();
        };

        _tracker.Changed += (_, _) => OnPresentationChanged();
        _client.StateChanged += OnConnectionChanged;
        _client.StatusChanged += (_, e) => _tracker.SetStatus(e.Status);
        _window.Selected += _openApp;
        _window.ContextMenuRequested += ShowMenu;
        _window.TaskbarRecreated += Apply;

        _tracker.SetConnection(_client.State);
        Apply();
    }

    private void OnConnectionChanged(object? sender, ConnectionState state)
    {
        _tracker.SetConnection(state);
        if (state == ConnectionState.Connected)
        {
            _ = _tracker.RefreshAsync(_client, _cts.Token);
        }
    }

    private void OnPresentationChanged()
    {
        var copying = _tracker.Presentation.Kind == TrayIconKind.Copying;
        if (copying && !_animation.IsRunning)
        {
            _animation.Start();
        }
        else if (!copying && _animation.IsRunning)
        {
            _animation.Stop();
            _frame = 0;
        }

        Apply();
    }

    private void Apply()
    {
        if (_disposed)
        {
            return;
        }

        var p = _tracker.Presentation;
        _window.SetIcon(_icons.Get(p.Kind, _frame), _tooltip.Format(p));
    }

    private void ShowMenu(int x, int y)
    {
        var items = TrayMenu.Build(_tracker.Presentation)
            .Select(i => (i.Command, _getString(MenuKey(i.Command)), i.Enabled))
            .ToList();
        var chosen = _window.ShowMenu(items, x, y);
        switch (chosen)
        {
            case TrayCommand.RunAllNow:
                _ = TrayCommands.RunAllNowAsync(_client, _cts.Token);
                break;
            case TrayCommand.OpenApp:
                _openApp();
                break;
            case TrayCommand.Exit:
                _exitApp();
                break;
        }
    }

    private static string MenuKey(TrayCommand command) => command switch
    {
        TrayCommand.RunAllNow => "Tray_Menu_RunAllNow",
        TrayCommand.Pause => "Tray_Menu_Pause",
        TrayCommand.OpenApp => "Tray_Menu_Open",
        _ => "Tray_Menu_Exit",
    };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _animation.Stop();
        _cts.Cancel();
        _client.StateChanged -= OnConnectionChanged;
        _window.Dispose();
        _icons.Dispose();
        _cts.Dispose();
    }
}
