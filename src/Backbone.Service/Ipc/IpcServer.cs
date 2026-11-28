using System.IO.Pipes;
using Backbone.Service.Os;
using Backbone.Service.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Backbone.Service.Ipc;

/// <summary>
/// Servidor del named pipe (§7.1, §8.2 <c>IpcServer</c>): crea las instancias con la ACL de §7.3, acepta conexiones y las
/// entrega al <see cref="IpcConnectionManager"/>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>La primera instancia se crea en <see cref="StartAsync"/> con <see cref="PipeOptions.FirstPipeInstance"/>. Si falla
/// (otro proceso ya tiene un pipe con ese nombre), se registra en el Visor de eventos y el arranque falla: el SCM aplica la
/// recuperación (A-1).</item>
/// <item>Siempre hay una instancia escuchando: la siguiente se crea antes de entregar la que acaba de conectar, así el
/// número de instancias nunca llega a cero mientras el servicio corre (nadie más puede "recrear" el pipe con otra ACL).
/// Por eso se permiten <c>MaxConnections + 1</c> instancias. Con el máximo de conexiones abiertas, la que llega se corta
/// (<c>DisconnectNamedPipe</c>) y la misma instancia sigue escuchando.</item>
/// <item>Al cambiar <c>authorizedUsers</c> se aplica la ACL nueva a la instancia que escucha: el descriptor de seguridad es
/// del pipe (todas sus instancias), no de cada instancia.</item>
/// </list>
/// </remarks>
internal sealed partial class IpcServer : IHostedService, IAsyncDisposable
{
    private readonly IpcConnectionManager _connections;
    private readonly IAuthorizedUserList _authorizedUsers;
    private readonly IEventLogWriter _eventLog;
    private readonly IpcOptions _options;
    private readonly ILogger<IpcServer> _logger;
    private readonly Lock _listenerGate = new();
    private readonly CancellationTokenSource _stopping = new();
    private NamedPipeServerStream? _listener;
    private Task _acceptLoop = Task.CompletedTask;
    private int _disposed;

    public IpcServer(
        IpcConnectionManager connections,
        IAuthorizedUserList authorizedUsers,
        IEventLogWriter eventLog,
        IpcOptions options,
        ILogger<IpcServer> logger)
    {
        _connections = connections;
        _authorizedUsers = authorizedUsers;
        _eventLog = eventLog;
        _options = options;
        _logger = logger;
    }

    /// <summary>Crea la primera instancia del pipe y empieza a aceptar conexiones.</summary>
    /// <exception cref="ServiceStartupException">El pipe ya existe o no se puede crear.</exception>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _listener = CreateInstance(first: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogPipeCreationFailed(_logger, ex, _options.PipeName);
            _eventLog.Write(
                EventLogLevel.Error,
                BackboneEventIds.ServiceStartFailed,
                $"Backbone could not create the IPC pipe '{_options.PipeName}' because it already exists or is not accessible ({ex.GetType().Name}, 0x{ex.HResult:X8}). The service does not start.");
            throw new ServiceStartupException("The IPC pipe could not be created.", ex);
        }

        _authorizedUsers.Changed += OnAuthorizedUsersChanged;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_stopping.Token), CancellationToken.None);
        LogListening(_logger, _options.PipeName);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Para el servidor. Idempotente y segura sin <see cref="StartAsync"/> previo (arranque fallido: el host no llama a
    /// StopAsync, pero sí libera) y tras <see cref="DisposeAsync"/>.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _authorizedUsers.Changed -= OnAuthorizedUsersChanged;
        await CancelStoppingAsync().ConfigureAwait(false);
        await _acceptLoop.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        await _connections.DisposeAsync().ConfigureAwait(false);
        await DisposeListenerAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Libera el servidor. Idempotente: el host puede liberarlo dos veces (<c>RunAsync</c> y el contenedor) y puede hacerlo
    /// sin que <see cref="StartAsync"/> haya llegado a ejecutarse o tras haber fallado.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _authorizedUsers.Changed -= OnAuthorizedUsersChanged;
        await CancelStoppingAsync().ConfigureAwait(false);
        await _acceptLoop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        await DisposeListenerAsync().ConfigureAwait(false);
        _stopping.Dispose();
    }

    private async Task CancelStoppingAsync()
    {
        try
        {
            await _stopping.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Liberado en paralelo: ya no hay nada que cancelar.
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? connected;
            lock (_listenerGate)
            {
                connected = _listener;
            }

            if (connected is null)
            {
                return;
            }

            try
            {
                await connected.WaitForConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (IOException ex)
            {
                // El cliente se fue entre la conexión y la aceptación: se recicla la instancia.
                LogAcceptFailed(_logger, ex);
                await ReplaceListenerAsync(connected, ct).ConfigureAwait(false);
                continue;
            }

            if (_connections.IsFull)
            {
                // Servidor lleno: se corta al cliente y la misma instancia vuelve a escuchar. No se crea otra, porque ya están
                // todas en uso (MaxConnections conectadas + esta) y el intento fallaría hasta que se liberase una.
                LogServerFull(_logger, _options.MaxConnections);
                try
                {
                    connected.Disconnect();
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException)
                {
                    LogAcceptFailed(_logger, ex);
                    await ReplaceListenerAsync(connected, ct).ConfigureAwait(false);
                }

                continue;
            }

            // Antes de entregar la instancia conectada, otra queda escuchando.
            var next = await CreateNextInstanceAsync(ct).ConfigureAwait(false);
            lock (_listenerGate)
            {
                _listener = next;
            }

            if (next is null)
            {
                await connected.DisposeAsync().ConfigureAwait(false);
                return;
            }

            _connections.Accept(new PipeClientChannel(connected));
        }
    }

    private async Task ReplaceListenerAsync(NamedPipeServerStream broken, CancellationToken ct)
    {
        var next = await CreateNextInstanceAsync(ct).ConfigureAwait(false);
        lock (_listenerGate)
        {
            _listener = next;
        }

        await broken.DisposeAsync().ConfigureAwait(false);
    }

    private async Task<NamedPipeServerStream?> CreateNextInstanceAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromMilliseconds(100);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                return CreateInstance(first: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                {
                    LogInstanceCreationFailed(_logger, ex.GetType().Name, ex.HResult);
                }
                try
                {
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }

                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, TimeSpan.FromSeconds(5).Ticks));
            }
        }

        return null;
    }

    private NamedPipeServerStream CreateInstance(bool first)
    {
        var options = PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None);
        return NamedPipeServerStreamAcl.Create(
            _options.PipeName,
            PipeDirection.InOut,
            _options.MaxConnections + 1,
            PipeTransmissionMode.Byte,
            options,
            inBufferSize: 0,
            outBufferSize: 0,
            PipeSecurityFactory.Create(_authorizedUsers.Sids, _options.AdditionalServerSid),
            HandleInheritability.None,
            // WRITE_DAC en el handle del servidor para poder actualizar la ACL del pipe (SetAccessControl).
            PipeAccessRights.ChangePermissions);
    }

    private void OnAuthorizedUsersChanged(object? sender, EventArgs e)
    {
        var security = PipeSecurityFactory.Create(_authorizedUsers.Sids, _options.AdditionalServerSid);
        lock (_listenerGate)
        {
            try
            {
                _listener?.SetAccessControl(security);
                LogAclUpdated(_logger, _authorizedUsers.Sids.Length);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or ObjectDisposedException)
            {
                LogAclUpdateFailed(_logger, ex.GetType().Name);
            }
        }
    }

    private async Task DisposeListenerAsync()
    {
        NamedPipeServerStream? listener;
        lock (_listenerGate)
        {
            listener = _listener;
            _listener = null;
        }

        if (listener is not null)
        {
            await listener.DisposeAsync().ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Servidor IPC escuchando en el pipe {PipeName}.")]
    private static partial void LogListening(ILogger logger, string pipeName);

    [LoggerMessage(Level = LogLevel.Critical, Message = "No se ha podido crear el pipe {PipeName}: ya existe o no es accesible.")]
    private static partial void LogPipeCreationFailed(ILogger logger, Exception exception, string pipeName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se ha podido crear otra instancia del pipe ({ErrorType}, 0x{HResult:X8}); se reintenta.")]
    private static partial void LogInstanceCreationFailed(ILogger logger, string errorType, int hResult);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Conexión IPC rechazada: ya hay {Max} conexiones abiertas.")]
    private static partial void LogServerFull(ILogger logger, int max);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Fallo al aceptar una conexión IPC.")]
    private static partial void LogAcceptFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "ACL del pipe actualizada: {Count} usuarios autorizados.")]
    private static partial void LogAclUpdated(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "No se ha podido actualizar la ACL del pipe ({ErrorType}).")]
    private static partial void LogAclUpdateFailed(ILogger logger, string errorType);
}
