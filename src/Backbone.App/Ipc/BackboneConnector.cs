using System.IO.Pipes;
using System.Security.Principal;
using Backbone.Contracts;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;

namespace Backbone.App.Ipc;

/// <summary>Una conexión establecida y verificada con el servicio: un único <see cref="JsonRpc"/> con su proxy.</summary>
internal interface IBackboneSession : IAsyncDisposable
{
    /// <summary>Proxy de <see cref="IBackboneService"/> sobre la conexión.</summary>
    IBackboneService Service { get; }

    /// <summary>Respuesta de <c>GetServiceInfoAsync</c> de la conexión (versión y contrato compatibles).</summary>
    ServiceInfo Info { get; }

    /// <summary>Termina cuando la conexión se cierra (por cualquiera de los extremos); nunca falla.</summary>
    Task Completion { get; }
}

/// <summary>Crea sesiones con el servicio.</summary>
internal interface IBackboneConnector
{
    /// <summary>Conecta, verifica al servidor, comprueba el contrato y devuelve la sesión.</summary>
    /// <exception cref="UnauthorizedAccessException">La ACL del pipe no concede acceso a este usuario.</exception>
    /// <exception cref="ServerVerificationException">El servidor no es el servicio auténtico.</exception>
    /// <exception cref="ContractIncompatibleException">Otra versión mayor del contrato.</exception>
    /// <exception cref="RemoteInvocationException"><c>NotAuthorized</c> u otro error del servicio en el saludo.</exception>
    Task<IBackboneSession> ConnectAsync(CancellationToken cancellationToken);
}

/// <summary>Parámetros del conector del pipe. Se fijan en código; las pruebas cambian el nombre del pipe y los tiempos.</summary>
internal sealed class NamedPipeConnectorOptions
{
    public string PipeName { get; init; } = BackboneIpc.PipeName;

    /// <summary>Tiempo que se espera a que aparezca el pipe (si el servicio no está, el intento termina con <see cref="TimeoutException"/>).</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Tiempo máximo para la respuesta de <c>GetServiceInfoAsync</c>.</summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public int MaxMessageBytes { get; init; } = BackboneIpc.MaxMessageBytes;
}

/// <summary>
/// Conector sobre el named pipe de §7.1.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Abre el pipe con derechos explícitos (<see cref="ClientRights"/>), sin <c>FILE_CREATE_PIPE_INSTANCE</c>: la ACL del
/// servidor no lo concede a los usuarios y <c>PipeDirection.InOut</c> lo pediría.</item>
/// <item>Nivel de suplantación <see cref="TokenImpersonationLevel.Impersonation"/>: el servicio lo exige (§7.3) para §8.6 y §8.7.</item>
/// <item>Antes de enviar nada, <see cref="IServerVerifier"/> comprueba el proceso servidor (§7.1 A-1).</item>
/// <item>Mismos límites de mensaje que el servidor (<see cref="BackboneIpc.MaxMessageBytes"/>).</item>
/// </list>
/// </remarks>
internal sealed class NamedPipeBackboneConnector(
    IServerVerifier verifier,
    TimeProvider timeProvider,
    NamedPipeConnectorOptions? options = null) : IBackboneConnector
{
    /// <summary>Derechos que concede la ACL del servidor a un usuario autorizado (lectura y escritura sin crear instancias).</summary>
    public const PipeAccessRights ClientRights =
        PipeAccessRights.ReadData | PipeAccessRights.WriteData | PipeAccessRights.ReadAttributes
        | PipeAccessRights.ReadPermissions | PipeAccessRights.Synchronize;

    private readonly NamedPipeConnectorOptions _options = options ?? new();

    public async Task<IBackboneSession> ConnectAsync(CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(
            ".",
            _options.PipeName,
            ClientRights,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Impersonation,
            HandleInheritability.None);
        try
        {
            await pipe.ConnectAsync((int)_options.ConnectTimeout.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
            verifier.Verify(pipe.SafePipeHandle);
            return await BackboneSession.StartAsync(pipe, _options, timeProvider, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

/// <summary>Sesión real: <see cref="JsonRpc"/> sobre el pipe con el proxy de <see cref="IBackboneService"/>.</summary>
internal sealed class BackboneSession : IBackboneSession
{
    private readonly Stream _stream;
    private readonly ClientJsonRpc _rpc;

    private BackboneSession(Stream stream, ClientJsonRpc rpc, IBackboneService service, ServiceInfo info)
    {
        _stream = stream;
        _rpc = rpc;
        Service = service;
        Info = info;
        Completion = WaitAsync(rpc);
    }

    public IBackboneService Service { get; }

    public ServiceInfo Info { get; }

    public Task Completion { get; }

    /// <summary>
    /// Arranca el <see cref="JsonRpc"/> sobre <paramref name="stream"/> y hace el saludo (<c>GetServiceInfoAsync</c>), que además
    /// identifica la conexión ante el servidor. Si falla, libera todo y relanza.
    /// </summary>
    internal static async Task<BackboneSession> StartAsync(
        Stream stream,
        NamedPipeConnectorOptions options,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        // SystemTextJsonFormatter sin cambiar sus opciones (ADR-10).
        var handler = new SizeBoundedMessageHandler(stream, new SystemTextJsonFormatter(), options.MaxMessageBytes);
        var rpc = new ClientJsonRpc(handler);
        var service = rpc.Attach<IBackboneService>();
        rpc.StartListening();
        try
        {
            using var timeout = new CancellationTokenSource(options.HandshakeTimeout, timeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            ServiceInfo info;
            try
            {
                info = await service.GetServiceInfoAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("The service did not answer the handshake in time.");
            }

            if (!ContractVersion.IsCompatibleWith(info.ContractVersion))
            {
                throw new ContractIncompatibleException(info.ContractVersion);
            }

            return new BackboneSession(stream, rpc, service, info);
        }
        catch
        {
            rpc.Dispose();
            await rpc.Completion.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _rpc.Dispose();
        await Completion.ConfigureAwait(false);
        await _stream.DisposeAsync().ConfigureAwait(false);
    }

    private static async Task WaitAsync(JsonRpc rpc) =>
        await rpc.Completion.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    /// <summary>
    /// <see cref="JsonRpc"/> del cliente (§7.4): <c>error.data</c> se deserializa como <see cref="BackboneErrorData"/> cuando el
    /// código es de Backbone, y se apagan las trazas de StreamJsonRpc (en niveles detallados vuelcan mensajes con sus parámetros,
    /// p. ej. contraseñas de <see cref="CredentialInput"/>).
    /// </summary>
    private sealed class ClientJsonRpc : JsonRpc
    {
        public ClientJsonRpc(IJsonRpcMessageHandler handler)
            : base(handler)
        {
            var source = new System.Diagnostics.TraceSource("Backbone.App.JsonRpc", System.Diagnostics.SourceLevels.Off);
            source.Listeners.Clear();
            TraceSource = source;
        }

        protected override Type? GetErrorDetailsDataType(JsonRpcError error) =>
            error.Error is { } detail && BackboneErrors.TryGetErrorCode((int)detail.Code, out _)
                ? typeof(BackboneErrorData)
                : base.GetErrorDetailsDataType(error);
    }
}
