using System.ComponentModel;
using Backbone.Contracts;
using Backbone.Service.Os;
using Microsoft.Extensions.Logging;
using StreamJsonRpc;

namespace Backbone.Service.Ipc;

/// <summary>
/// Una conexión de cliente: su <see cref="JsonRpc"/>, su identidad y su estado de autorización.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>La identidad se lee con el primer mensaje (antes no se puede suplantar). Si no llega en
/// <see cref="IpcOptions.IdentificationTimeout"/>, se cierra la conexión.</item>
/// <item>Cada llamada se autoriza de nuevo (<see cref="Authorize"/>). Un rechazo de una conexión aún no autorizada responde
/// <c>NotAuthorized</c> y la conexión queda rechazada: se cierra en cuanto el cliente cierra su extremo o, como mucho, pasado
/// <see cref="IpcOptions.RejectionLinger"/> (cierre ordenado: el cliente siempre llega a leer la respuesta). La revocación
/// de una conexión ya autorizada (en una llamada o en <see cref="Revalidate"/>) la cierra de inmediato, cancelando lo que
/// esté en curso (§7.3).</item>
/// <item>Al cerrarse (por el cliente, de forma abrupta o no, o por el servidor) se cancelan las llamadas en curso, se
/// liberan el <see cref="JsonRpc"/>, el pipe y los temporizadores, y se quita del <see cref="IpcConnectionManager"/>.</item>
/// </list>
/// </remarks>
internal sealed partial class IpcConnection : IAsyncDisposable
{
    /// <summary>Texto de <c>NotAuthorized</c> para cualquier motivo de rechazo o revocación de la conexión.</summary>
    internal const string NotAuthorizedMessage = "The caller is not authorized to use the service.";

    private readonly IClientChannel _channel;
    private readonly IpcConnectionManager _manager;
    private readonly CallerAuthorizer _authorizer;
    private readonly IAccountDirectory _accounts;
    private readonly IpcOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private BoundedHeaderDelimitedMessageHandler? _handler;
    private BackboneServerJsonRpc? _rpc;
    private ITimer? _identificationTimer;
    private ITimer? _rejectionTimer;
    private State _state;
    private int _pendingNotifications;
    private int _closing;

    public IpcConnection(
        long id,
        IClientChannel channel,
        IpcConnectionManager manager,
        CallerAuthorizer authorizer,
        IAccountDirectory accounts,
        IpcOptions options,
        TimeProvider timeProvider,
        ILogger logger)
    {
        Id = id;
        _channel = channel;
        _manager = manager;
        _authorizer = authorizer;
        _accounts = accounts;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    private enum State
    {
        Pending = 0,
        Authorized = 1,
        Rejected = 2,
        Closed = 3,
    }

    public long Id { get; }

    internal ILogger Logger => _logger;

    /// <summary>Identidad del cliente; <see langword="null"/> hasta su primer mensaje.</summary>
    public ClientIdentity? Identity { get; private set; }

    public string? AccountName { get; private set; }

    /// <summary>La conexión ha superado la autorización y no se ha rechazado ni cerrado: recibe eventos.</summary>
    public bool IsAuthorized
    {
        get
        {
            lock (_gate)
            {
                return _state == State.Authorized;
            }
        }
    }

    /// <summary>Termina cuando la conexión se ha cerrado y liberado.</summary>
    public Task Completion => _completion.Task;

    /// <summary>SID contabilizado en el límite por cuenta (lo asigna el <see cref="IpcConnectionManager"/>).</summary>
    internal string? CountedSid { get; set; }

    internal void Start(IBackboneService target)
    {
        _handler = new BoundedHeaderDelimitedMessageHandler(
            _channel.Stream, new SystemTextJsonFormatter(), _options.MaxMessageBytes, _options.MaxConcurrentRequestsPerConnection, _logger);
        _rpc = new BackboneServerJsonRpc(_handler, _logger);
        _rpc.AddLocalRpcTarget<IBackboneService>(target, new JsonRpcTargetOptions
        {
            // Los eventos los envía IpcConnection.Notify, con control de clientes bloqueados (no los eventos del objetivo).
            NotifyClientOfEvents = false,
            DisposeOnDisconnect = false,
            AllowNonPublicInvocation = false,
        });
        _rpc.Disconnected += (_, e) => Close(
            ContainsException<TooManyConcurrentRequestsException>(e.Exception) ? CloseReason.TooManyRequests : CloseReason.Disconnected);
        _identificationTimer = _timeProvider.CreateTimer(
            static state => ((IpcConnection)state!).OnIdentificationTimeout(), this, _options.IdentificationTimeout, Timeout.InfiniteTimeSpan);
        _rpc.StartListening();
        LogAccepted(_logger, Id);
    }

    /// <summary>
    /// Autoriza una llamada contra la lista y la pertenencia actuales (§7.3). La primera llamada identifica la conexión.
    /// </summary>
    /// <exception cref="BackboneException"><see cref="BackboneErrorCode.NotAuthorized"/>. Si la conexión aún no estaba autorizada,
    /// queda rechazada (cierre ordenado); si ya lo estaba (revocación), se cierra de inmediato.</exception>
    public CallerContext Authorize(string method, bool isWrite)
    {
        var identity = EnsureIdentified(method);
        var failure = _authorizer.Check(identity);
        if (failure != AuthorizationFailure.None)
        {
            if (RejectOrRevoke(failure, method))
            {
                // Revocación de una conexión ya autorizada (§7.3): cierre inmediato, sin el cierre ordenado del rechazo. Cierra
                // el JsonRpc, que cancela las llamadas en curso de esta conexión y no deja salir ninguna respuesta más (tampoco
                // la de esta llamada: el cliente ve la conexión cerrada y, al reconectar, recibe NotAuthorized).
                LogRevoked(_logger, Id, identity.Sid.Value, failure);
                Close(CloseReason.Revoked);
            }

            throw NotAuthorized();
        }

        lock (_gate)
        {
            if (_state is State.Rejected or State.Closed)
            {
                throw NotAuthorized();
            }

            _state = State.Authorized;
        }

        var account = AccountName ?? identity.Sid.Value;
        if (isWrite)
        {
            // Auditoría (§7.3): SID, cuenta y método; nunca los argumentos.
            LogAudit(_logger, method, identity.Sid.Value, account, Id);
        }

        return new CallerContext(Id, identity, account, _channel);
    }

    /// <summary>Vuelve a comprobar la autorización (revalidación periódica o cambio de la lista) y cierra si ya no procede.</summary>
    internal void Revalidate()
    {
        ClientIdentity? identity;
        lock (_gate)
        {
            if (_state != State.Authorized)
            {
                return;
            }

            identity = Identity;
        }

        if (identity is null)
        {
            return;
        }

        var failure = _authorizer.Check(identity);
        if (failure != AuthorizationFailure.None)
        {
            LogRevoked(_logger, Id, identity.Sid.Value, failure);
            Close(CloseReason.Revoked);
        }
    }

    /// <summary>
    /// Envía un evento al cliente si está autorizado. Si el cliente acumula demasiadas notificaciones sin leer, se le
    /// desconecta para no retener memoria por él.
    /// </summary>
    internal void Notify(string eventName, object argument)
    {
        var rpc = _rpc;
        if (rpc is null || !IsAuthorized)
        {
            return;
        }

        if (Interlocked.Increment(ref _pendingNotifications) > _options.MaxPendingNotifications)
        {
            Interlocked.Decrement(ref _pendingNotifications);
            LogSlowClient(_logger, Id);
            Close(CloseReason.SlowClient);
            return;
        }

        Task send;
        try
        {
            send = rpc.NotifyAsync(eventName, [argument]);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or ConnectionLostException or InvalidOperationException)
        {
            Interlocked.Decrement(ref _pendingNotifications);
            return;
        }

        _ = send.ContinueWith(
            static (t, state) =>
            {
                var connection = (IpcConnection)state!;
                Interlocked.Decrement(ref connection._pendingNotifications);
                _ = t.Exception; // Observada: un fallo de envío significa que la conexión se está cerrando.
            },
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public void Close(CloseReason reason) => _ = CloseAsync(reason);

    public async Task CloseAsync(CloseReason reason)
    {
        if (Interlocked.Exchange(ref _closing, 1) == 1)
        {
            await _completion.Task.ConfigureAwait(false);
            return;
        }

        lock (_gate)
        {
            _state = State.Closed;
        }

        try
        {
            _identificationTimer?.Dispose();
            _rejectionTimer?.Dispose();

            // Dispose cancela las llamadas en curso (CancelLocallyInvokedMethodsWhenConnectionIsClosed) y cierra el handler.
            _rpc?.Dispose();
            if (_rpc is not null)
            {
                await _rpc.Completion.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }

            await _channel.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            LogCloseError(_logger, ex, Id);
        }
        finally
        {
            _manager.Remove(this);
            LogClosed(_logger, Id, reason);
            _completion.TrySetResult();
        }
    }

    public ValueTask DisposeAsync() => new(CloseAsync(CloseReason.ServerShutdown));

    private ClientIdentity EnsureIdentified(string method)
    {
        lock (_gate)
        {
            if (_state is State.Rejected or State.Closed)
            {
                throw NotAuthorized();
            }

            if (Identity is { } known)
            {
                return known;
            }

            _identificationTimer?.Dispose();
            ClientIdentity identity;
            try
            {
                identity = _channel.GetClientIdentity();
            }
            catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException or UnauthorizedAccessException)
            {
                LogIdentityFailed(_logger, Id, ex.GetType().Name);
                RejectLocked(AuthorizationFailure.ImpersonationLevelTooLow, method);
                throw NotAuthorized();
            }

            Identity = identity;
            AccountName = TryResolveAccountName(identity);
            LogIdentified(_logger, Id, identity.Sid.Value, identity.ImpersonationLevel, identity.IsElevated);

            // Cuenta en su SID aunque supere el límite: la rechazada sigue contando mientras espera a cerrarse (§7.1, B-4).
            if (!_manager.TryCountConnection(this, identity.Sid.Value))
            {
                RejectLocked(AuthorizationFailure.TooManyConnections, method);
                throw NotAuthorized();
            }

            return identity;
        }
    }

    private static bool ContainsException<T>(Exception? exception)
        where T : Exception
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is T || (current is AggregateException aggregate && aggregate.InnerExceptions.Any(e => ContainsException<T>(e))))
            {
                return true;
            }
        }

        return false;
    }

    private string? TryResolveAccountName(ClientIdentity identity)
    {
        if (identity.ImpersonationLevel == System.Security.Principal.TokenImpersonationLevel.Anonymous)
        {
            return null;
        }

        try
        {
            return _accounts.LookupBySid(identity.Sid)?.AccountName;
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Mismo texto para todos los motivos (el motivo solo va al log): el cliente no aprende por qué se le rechaza.
    /// </summary>
    private static BackboneException NotAuthorized() => BackboneException.NotAuthorized(NotAuthorizedMessage);

    /// <summary>
    /// Rechaza la conexión si aún no estaba autorizada (cierre ordenado) y devuelve <see langword="true"/> si ya lo estaba
    /// (revocación: quien llama la cierra de inmediato). En ambos casos deja de estar autorizada antes de salir del cerrojo,
    /// así ninguna otra llamada ni evento la ve autorizada.
    /// </summary>
    private bool RejectOrRevoke(AuthorizationFailure failure, string method)
    {
        lock (_gate)
        {
            if (_state == State.Authorized)
            {
                _state = State.Closed;
                return true;
            }

            RejectLocked(failure, method);
            return false;
        }
    }

    private void RejectLocked(AuthorizationFailure failure, string method)
    {
        if (_state is State.Rejected or State.Closed)
        {
            return;
        }

        _state = State.Rejected;
        if (_logger.IsEnabled(LogLevel.Warning))
        {
            LogRejected(_logger, Id, Identity?.Sid.Value ?? "-", method, failure);
        }

        // No se cierra al enviar la respuesta NotAuthorized: cerrar el extremo del servidor (DisconnectNamedPipe o el cierre del
        // handle con datos aún en tránsito) puede hacer que el cliente no llegue a procesarla y vea ConnectionLostException.
        // Se deja que cierre el cliente (Disconnected cierra aquí al momento) y, si no lo hace, se fuerza el cierre pasado
        // RejectionLinger: un cliente no autorizado no retiene una instancia del pipe más de ese tiempo (HT-03, B-2). Mientras
        // tanto toda llamada suya recibe NotAuthorized (estado Rejected) y no recibe eventos.
        _rejectionTimer = _timeProvider.CreateTimer(
            static state => ((IpcConnection)state!).Close(CloseReason.Rejected), this, _options.RejectionLinger, Timeout.InfiniteTimeSpan);
    }

    private void OnIdentificationTimeout()
    {
        lock (_gate)
        {
            if (Identity is not null || _state != State.Pending)
            {
                return;
            }
        }

        LogIdentificationTimeout(_logger, Id);
        Close(CloseReason.IdentificationTimeout);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Conexión IPC {ConnectionId} aceptada.")]
    private static partial void LogAccepted(ILogger logger, long connectionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Conexión IPC {ConnectionId} identificada: {Sid}, nivel {ImpersonationLevel}, elevado {IsElevated}.")]
    private static partial void LogIdentified(ILogger logger, long connectionId, string sid, System.Security.Principal.TokenImpersonationLevel impersonationLevel, bool isElevated);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se ha podido leer la identidad del cliente de la conexión IPC {ConnectionId} ({ErrorType}).")]
    private static partial void LogIdentityFailed(ILogger logger, long connectionId, string errorType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Conexión IPC {ConnectionId} de {Sid} rechazada en {Method}: {Failure}.")]
    private static partial void LogRejected(ILogger logger, long connectionId, string sid, string method, AuthorizationFailure failure);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Conexión IPC {ConnectionId} de {Sid} cerrada: ya no está autorizada ({Failure}).")]
    private static partial void LogRevoked(ILogger logger, long connectionId, string sid, AuthorizationFailure failure);

    [LoggerMessage(Level = LogLevel.Information, Message = "Auditoría IPC: {Method} por {Sid} ({Account}), conexión {ConnectionId}.")]
    private static partial void LogAudit(ILogger logger, string method, string sid, string account, long connectionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Conexión IPC {ConnectionId} cerrada: el cliente no lee los eventos.")]
    private static partial void LogSlowClient(ILogger logger, long connectionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Conexión IPC {ConnectionId} cerrada: no ha enviado ningún mensaje a tiempo.")]
    private static partial void LogIdentificationTimeout(ILogger logger, long connectionId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Error al cerrar la conexión IPC {ConnectionId}.")]
    private static partial void LogCloseError(ILogger logger, Exception exception, long connectionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Conexión IPC {ConnectionId} cerrada ({Reason}).")]
    private static partial void LogClosed(ILogger logger, long connectionId, CloseReason reason);
}

/// <summary>Motivo del cierre de una conexión (para el log).</summary>
internal enum CloseReason
{
    Disconnected = 0,
    Rejected = 1,
    Revoked = 2,
    SlowClient = 3,
    IdentificationTimeout = 4,
    ServerShutdown = 5,
    ServerFull = 6,
    TooManyRequests = 7,
}
