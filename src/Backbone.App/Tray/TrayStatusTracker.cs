using Backbone.App.Ipc;
using Backbone.Contracts;

namespace Backbone.App.Tray;

/// <summary>
/// Calcula qué muestra la bandeja a partir del estado de la conexión y del último <see cref="ServiceStatus"/> (RF-50).
/// Sin tipos de WinUI. No es seguro para hilos: se usa desde el hilo de UI (los eventos del cliente llegan por <c>post</c>).
/// </summary>
/// <remarks>
/// Correspondencia: conectado → salud del servicio (verde, amarillo o rojo; "copiando" si hay una ejecución en curso; un trabajo
/// activado desactualizado cuenta como mínimo como aviso). Conectando o reconectando → amarillo (se espera recuperarse en segundos).
/// Servicio detenido, sin autorización o contrato incompatible → rojo (§9.3).
/// </remarks>
internal sealed class TrayStatusTracker
{
    private ConnectionState _connection = ConnectionState.Connecting;
    private ServiceStatus? _status;

    /// <summary>Estado actual.</summary>
    public TrayPresentation Presentation { get; private set; } = TrayPresentation.Initial;

    /// <summary>Se produce cuando cambia <see cref="Presentation"/> (no se repite si el resultado es igual).</summary>
    public event EventHandler? Changed;

    /// <summary>Registra un cambio del estado de la conexión.</summary>
    public void SetConnection(ConnectionState state)
    {
        _connection = state;
        Recalculate();
    }

    /// <summary>Registra el estado global del servicio (<see cref="StatusChangedEvent"/> o <c>GetStatusAsync</c>).</summary>
    public void SetStatus(ServiceStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        _status = status;
        Recalculate();
    }

    /// <summary>
    /// Lee el estado del servicio. Se llama al quedar conectado: los eventos emitidos mientras no había conexión no se recuperan (§9.1).
    /// Un fallo se ignora: si es de conexión, <see cref="SetConnection"/> ya lo refleja.
    /// </summary>
    public async Task RefreshAsync(IBackboneService service, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(service);
        try
        {
            SetStatus(await service.GetStatusAsync(ct).ConfigureAwait(true));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Un fallo puntual de lectura no debe tumbar la app; el estado de conexión lo cubre.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private void Recalculate()
    {
        var status = _status;
        var last = status?.Jobs.Where(j => j.LastSuccessUtc is not null).Max(j => j.LastSuccessUtc);
        var paused = status?.Pause is not null;
        var runnable = status?.Jobs.Count(j => j.Enabled && j.Activity == JobActivity.Idle) ?? 0;

        var kind = _connection switch
        {
            ConnectionState.Connected => KindFromStatus(status),
            ConnectionState.Connecting or ConnectionState.Reconnecting => TrayIconKind.Warning,
            _ => TrayIconKind.Error,
        };

        var next = new TrayPresentation(kind, _connection, last, paused, _connection == ConnectionState.Connected ? runnable : 0);
        if (next != Presentation)
        {
            Presentation = next;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private static TrayIconKind KindFromStatus(ServiceStatus? status)
    {
        if (status is null)
        {
            return TrayIconKind.Warning;
        }

        if (status.CurrentRun is not null || status.Jobs.Any(j => j.Activity == JobActivity.Running))
        {
            return TrayIconKind.Copying;
        }

        var kind = status.Health switch
        {
            HealthState.Error => TrayIconKind.Error,
            HealthState.Warning => TrayIconKind.Warning,
            _ => TrayIconKind.Ok,
        };

        if (kind == TrayIconKind.Ok && status.Jobs.Any(j => j.Enabled && j.IsOutdated))
        {
            kind = TrayIconKind.Warning;
        }

        return kind;
    }
}
