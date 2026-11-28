using System.Globalization;
using Backbone.App.Ipc;
using Backbone.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Backbone.App.ViewModels;

/// <summary>
/// Estados especiales de la conexión en Inicio (§9.3): servicio detenido (banner con "Iniciar servicio"), sin autorización,
/// contrato incompatible y "reconectando" (§9.1). Sin <see cref="IConnectionMonitor"/> se considera conectado.
/// </summary>
internal sealed partial class ConnectionStatusViewModel : ObservableObject, IDisposable
{
    private readonly IConnectionMonitor? _monitor;
    private readonly IServiceStarter? _starter;
    private readonly Func<string, string> _getString;
    private readonly string _account;

    public ConnectionStatusViewModel(IConnectionMonitor? monitor, IServiceStarter? starter, Func<string, string> getString, string? account = null)
    {
        _monitor = monitor;
        _starter = starter;
        _getString = getString;
        _account = account ?? Environment.UserDomainName + "\\" + Environment.UserName;
        State = monitor?.State ?? ConnectionState.Connected;
        if (monitor is not null)
        {
            monitor.StateChanged += OnStateChanged;
        }
    }

    /// <summary>Estado de la conexión.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(IsConnected), nameof(ShowContent), nameof(ShowConnecting), nameof(ShowReconnecting), nameof(ShowServiceStopped),
        nameof(ShowUnauthorized), nameof(ShowIncompatible), nameof(IncompatibleText))]
    public partial ConnectionState State { get; set; }

    /// <summary>Está en marcha el arranque del servicio (UAC o <c>sc start</c> en curso).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartServiceCommand))]
    public partial bool IsStartingService { get; set; }

    /// <summary>Conectado: el contenido es fiable.</summary>
    public bool IsConnected => State == ConnectionState.Connected;

    /// <summary>Se muestran las tarjetas (conectado, o reconectando con los últimos datos).</summary>
    public bool ShowContent => State is ConnectionState.Connected or ConnectionState.Reconnecting;

    /// <summary>Primer intento en curso.</summary>
    public bool ShowConnecting => State == ConnectionState.Connecting;

    /// <summary>Se perdió la conexión y se reintenta (§9.1).</summary>
    public bool ShowReconnecting => State == ConnectionState.Reconnecting;

    /// <summary>Servicio no instalado o detenido: banner rojo persistente (§9.3).</summary>
    public bool ShowServiceStopped => State == ConnectionState.ServiceStopped;

    /// <summary>Sin autorización: pantalla explicativa (§9.3).</summary>
    public bool ShowUnauthorized => State == ConnectionState.Unauthorized;

    /// <summary>Contrato incompatible: aviso con las versiones (§9.3).</summary>
    public bool ShowIncompatible => State == ConnectionState.IncompatibleContract;

    /// <summary>Cuenta que debe autorizarse.</summary>
    public string Account => _account;

    /// <summary>Instrucciones para autorizar la cuenta actual (el servicio no define un comando propio: se hace con la app elevada, §7.3).</summary>
    public string UnauthorizedInstructions => string.Format(CultureInfo.CurrentCulture, _getString("Conn_Unauthorized_Instructions"), _account);

    /// <summary>Versión de cada parte (§9.3).</summary>
    public string IncompatibleText => string.Format(
        CultureInfo.CurrentCulture,
        _getString("Conn_Incompatible_Format"),
        _monitor?.IncompatibleContractVersion ?? _monitor?.ServiceInfo?.ContractVersion ?? "?",
        ContractVersion.Value);

    public void Dispose()
    {
        if (_monitor is not null)
        {
            _monitor.StateChanged -= OnStateChanged;
        }
    }

    /// <summary>Lanza <c>sc start</c> elevado y reintenta la conexión en el acto, interrumpiendo la espera (§9.1).</summary>
    /// <returns>Una tarea que termina cuando acaba el arranque.</returns>
    [RelayCommand(CanExecute = nameof(CanStartService))]
    public async Task StartServiceAsync()
    {
        if (_starter is null)
        {
            _monitor?.RetryNow();
            return;
        }

        IsStartingService = true;
        try
        {
            await _starter.StartAsync().ConfigureAwait(true);
        }
        finally
        {
            IsStartingService = false;
            _monitor?.RetryNow();
        }
    }

    private bool CanStartService() => !IsStartingService;

    private void OnStateChanged(object? sender, ConnectionState state) => State = state;
}
