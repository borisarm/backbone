using System.Runtime.InteropServices;
using Backbone.Contracts;
using Backbone.Service.Ipc;
using Backbone.Service.Os;
using Backbone.Service.Persistence;
using Microsoft.Extensions.Logging;

namespace Backbone.Service.Nas;

/// <summary>Plazos y límites de "Probar conexión" (HU-05).</summary>
internal sealed record ConnectionTestOptions
{
    /// <summary>
    /// Plazo de toda la prueba (espera de turno, resolución DNS, conexión, lectura, escritura y borrado). Con
    /// <see cref="MinimumCleanupWait"/>, la respuesta llega en menos de 15 s aunque el servidor esté apagado (HU-05 criterio 3).
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(11);

    /// <summary>
    /// Espera mínima a la limpieza (borrar el archivo de prueba y cerrar la conexión) antes de responder; si tarda más, sigue
    /// en segundo plano.
    /// </summary>
    public TimeSpan MinimumCleanupWait { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Tope de la resolución DNS del servidor (§7.5), dentro de <see cref="Timeout"/>.</summary>
    public TimeSpan DnsTimeout { get; init; } = TimeSpan.FromSeconds(4);

    /// <summary>Pruebas simultáneas en total (BAJA-2); por servidor, siempre una.</summary>
    public int MaxConcurrentTests { get; init; } = 2;
}

/// <summary>Paso de la prueba en el que se ha fallado (para el log).</summary>
internal enum ConnectionTestStep
{
    Connect = 0,
    Read = 1,
    Write = 2,
    Delete = 3,
}

/// <summary>
/// "Probar conexión" (RF-61, HU-05): conecta con el recurso, comprueba lectura, escritura y borrado de un archivo de prueba
/// e informa del espacio y del dialecto SMB. Un fallo es un resultado con <see cref="ConnectionTestResult.ErrorCode"/>, no
/// una excepción (§7.4).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Credencial</b>: la indicada por el cliente o, si no hay, la guardada (<see cref="ShareCredentialProvider"/>);
/// sin ninguna, <see cref="BackboneErrorCode.CredentialRequired"/> sin intentar conectar (§7.5). La contraseña indicada se
/// copia a un array fijado (<see cref="NasCredential"/>) que se pone a cero en cuanto termina la llamada de conexión.</item>
/// <item><b>Propio equipo</b> (§7.5, ALTA-1): antes de conectar, el nombre del servidor se resuelve
/// (<see cref="LocalMachine.ResolvesToSelfAsync"/>); si apunta al propio equipo, <see cref="BackboneErrorCode.ValidationFailed"/>
/// con <see cref="ValidationIssueCode.DestinationNotAllowed"/>. Si la resolución falla, se sigue (la conexión dará
/// <see cref="BackboneErrorCode.NasUnreachable"/>); si no termina en <see cref="ConnectionTestOptions.DnsTimeout"/>,
/// <see cref="BackboneErrorCode.NasUnreachable"/> sin conectar.</item>
/// <item><b>Conexiones</b>: siempre por <see cref="ShareConnectionManager"/> (una por raíz, con referencias; una credencial
/// distinta de la de una conexión viva con el mismo servidor se rechaza, BAJA-1).</item>
/// <item><b>Turnos</b> (BAJA-2): una prueba a la vez por servidor y <see cref="ConnectionTestOptions.MaxConcurrentTests"/> en
/// total; si queda una limpieza pendiente del mismo servidor, se espera dentro del plazo y, si no termina,
/// <see cref="BackboneErrorCode.NasUnreachable"/> sin conectar.</item>
/// <item><b>Plazo</b> (<see cref="ConnectionTestOptions.Timeout"/>, con <see cref="TimeProvider"/>): vencido, la prueba
/// responde <see cref="BackboneErrorCode.NasUnreachable"/>. Las operaciones de red no se pueden interrumpir; las que quedan
/// en curso se abandonan y la limpieza espera a que terminen.</item>
/// <item><b>Sin restos</b> (criterio 5): antes de escribir se comprueba que la carpeta sigue dentro del recurso
/// (<c>GetFinalPathNameByHandle</c>); el archivo de prueba se crea con borrado al cerrar y, al terminar (bien, mal, por plazo,
/// por cancelación o por un error no previsto), se comprueba que no existe y, si existe, se borra. La conexión se suelta
/// siempre, también si llega después de abandonarla.</item>
/// <item><b>Textos no confiables</b> (§6.1): el nombre del recurso, los mensajes del sistema y el dialecto pasan por
/// <see cref="UntrustedText.Describe"/> antes de registrarse. Nunca se registran el usuario ni la contraseña, ni el texto de
/// las excepciones de E/S (solo su tipo y <c>HResult</c>).</item>
/// </list>
/// </remarks>
internal sealed partial class ConnectionTester : IDisposable
{
    /// <summary>Prefijo del archivo de prueba, en la carpeta más profunda del destino que ya exista.</summary>
    internal const string ProbeFilePrefix = ".backbone-prueba-";

    /// <summary>Contenido del archivo de prueba.</summary>
    internal static ReadOnlyMemory<byte> ProbeContent { get; } = "Backbone: prueba de conexión (RF-61). Se borra al terminar.\n"u8.ToArray();

    private readonly ShareConnectionManager _connections;
    private readonly IHostResolver _resolver;
    private readonly ShareCredentialProvider _credentials;
    private readonly TimeProvider _timeProvider;
    private readonly ConnectionTestOptions _options;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly Dictionary<Task, string> _cleanups = [];
    private readonly Dictionary<string, SemaphoreSlim> _serverTurns = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _globalTurns;

    public ConnectionTester(
        ShareConnectionManager connections,
        IHostResolver resolver,
        ShareCredentialProvider credentials,
        TimeProvider timeProvider,
        ConnectionTestOptions options,
        ILogger<ConnectionTester> logger)
    {
        _connections = connections;
        _resolver = resolver;
        _credentials = credentials;
        _timeProvider = timeProvider;
        _options = options;
        _logger = logger;
        _globalTurns = new SemaphoreSlim(Math.Max(1, options.MaxConcurrentTests));
    }

    public void Dispose()
    {
        _globalTurns.Dispose();
        lock (_gate)
        {
            foreach (var turn in _serverTurns.Values)
            {
                turn.Dispose();
            }

            _serverTurns.Clear();
        }
    }

    /// <summary>Espera a las limpiezas que han quedado en segundo plano (pruebas y parada ordenada).</summary>
    public Task WhenCleanupsCompleteAsync()
    {
        lock (_gate)
        {
            return Task.WhenAll([.. _cleanups.Keys]);
        }
    }

    /// <summary>Prueba la conexión con <paramref name="destination"/>.</summary>
    /// <param name="destination">Destino UNC (<c>\\servidor\recurso[\ruta]</c>), con la longitud ya validada por IPC.</param>
    /// <param name="credential">Credencial a probar o <see langword="null"/> para usar la guardada.</param>
    /// <param name="ct">Cancelación del cliente: lanza <see cref="OperationCanceledException"/> y la limpieza sigue en segundo plano.</param>
    /// <exception cref="BackboneException">
    /// <see cref="BackboneErrorCode.ValidationFailed"/> si el destino o la credencial no son válidos o el servidor resuelve al
    /// propio equipo.
    /// </exception>
    public async Task<ConnectionTestResult> TestAsync(string destination, CredentialInput? credential, CancellationToken ct)
    {
        if (!CredentialStore.TryNormalizeShareRoot(destination, out var shareRoot, out var issue)
            || !UncPaths.TryParse(destination, out _, out var normalizedDestination, out issue))
        {
            throw BackboneException.ValidationFailed(string.Empty, issue);
        }

        NasCredential nasCredential;
        if (credential is null)
        {
            try
            {
                nasCredential = await _credentials.GetRequiredAsync(shareRoot, ct).ConfigureAwait(false);
            }
            catch (BackboneException ex) when (ex.Code == BackboneErrorCode.CredentialRequired)
            {
                // ShareCredentialProvider ya lo ha registrado; no se intenta conectar (§7.5).
                return new ConnectionTestResult { ShareRoot = shareRoot, ErrorCode = BackboneErrorCode.CredentialRequired };
            }
        }
        else
        {
            nasCredential = FromInput(shareRoot, credential);
        }

        var attempt = new Attempt(this, shareRoot, normalizedDestination, nasCredential, ct);
        return await attempt.RunAsync().ConfigureAwait(false);
    }

    /// <summary>Mismas reglas que al guardar (HU-04): usuario no vacío y sin controles; ninguno con NUL ni suplentes sueltas.</summary>
    private static NasCredential FromInput(string shareRoot, CredentialInput input)
    {
        var issues = new List<ValidationIssue>();
        if (string.IsNullOrWhiteSpace(input.UserName) || !CredentialStore.IsStorable(input.UserName, allowControl: false))
        {
            issues.Add(new ValidationIssue { Field = nameof(CredentialInput.UserName), Code = ValidationIssueCode.ValueOutOfRange });
        }

        if (!CredentialStore.IsStorable(input.Password, allowControl: true))
        {
            issues.Add(new ValidationIssue { Field = nameof(CredentialInput.Password), Code = ValidationIssueCode.ValueOutOfRange });
        }

        if (issues.Count > 0)
        {
            throw BackboneException.ValidationFailed(new ValidationResult { Errors = issues });
        }

        // Fijado y con NUL final, como el que devuelve CredentialStore: se pone a cero al desecharlo.
        var buffer = GC.AllocateArray<char>(input.Password.Length + 1, pinned: true);
        input.Password.AsSpan().CopyTo(buffer);
        return new NasCredential(shareRoot, input.UserName, DateTimeOffset.UnixEpoch, buffer, input.Password.Length);
    }

    /// <summary>Indica si <paramref name="finalPath"/> está dentro de <paramref name="finalRoot"/> (sin distinguir mayúsculas).</summary>
    internal static bool IsWithin(string finalPath, string finalRoot)
    {
        var root = finalRoot.TrimEnd('\\');
        return finalPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
               && (finalPath.Length == root.Length || finalPath[root.Length] == '\\');
    }

    private SemaphoreSlim ServerTurn(string server)
    {
        lock (_gate)
        {
            if (!_serverTurns.TryGetValue(server, out var turn))
            {
                turn = new SemaphoreSlim(1, 1);
                _serverTurns.Add(server, turn);
            }

            return turn;
        }
    }

    private Task PendingCleanupsOf(string server)
    {
        lock (_gate)
        {
            return Task.WhenAll([.. _cleanups.Where(c => string.Equals(c.Value, server, StringComparison.OrdinalIgnoreCase)).Select(c => c.Key)]);
        }
    }

    private void Track(Task cleanup, string server)
    {
        lock (_gate)
        {
            _cleanups.TryAdd(cleanup, server);
        }

        _ = cleanup.ContinueWith(
            t =>
            {
                lock (_gate)
                {
                    _cleanups.Remove(t);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static string SystemMessage(int error) =>
        error == 0 ? string.Empty : UntrustedText.Describe(Marshal.GetPInvokeErrorMessage(error));

    [LoggerMessage(Level = LogLevel.Information, Message = "Prueba de conexión con {ShareRoot} correcta (SMB {Dialect}).")]
    private static partial void LogSucceeded(ILogger logger, string shareRoot, string dialect);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Prueba de conexión con {ShareRoot} fallida en el paso {Step}: {ErrorCode} (error Win32 {NativeError}: {SystemMessage}).")]
    private static partial void LogFailed(ILogger logger, string shareRoot, ConnectionTestStep step, BackboneErrorCode errorCode, int nativeError, string systemMessage);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Prueba de conexión con {ShareRoot} fallida en el paso {Step} por un error no previsto ({ExceptionType}, HResult {HResult}): {ErrorCode}.")]
    private static partial void LogUnexpected(ILogger logger, string shareRoot, ConnectionTestStep step, string exceptionType, int hResult, BackboneErrorCode errorCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Prueba de conexión con {ShareRoot}: se agotó el plazo de {Timeout} en el paso {Step}.")]
    private static partial void LogTimedOut(ILogger logger, string shareRoot, TimeSpan timeout, ConnectionTestStep step);

    [LoggerMessage(Level = LogLevel.Information, Message = "Prueba de conexión con {ShareRoot} cancelada por el cliente en el paso {Step}.")]
    private static partial void LogCanceled(ILogger logger, string shareRoot, ConnectionTestStep step);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Prueba de conexión con {ShareRoot}: el servidor resuelve al propio equipo; se rechaza sin conectar (§7.5).")]
    private static partial void LogResolvesToSelf(ILogger logger, string shareRoot);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Prueba de conexión con {ShareRoot}: la carpeta {Folder} está fuera del recurso ({FinalPath}); no se escribe.")]
    private static partial void LogOutsideShare(ILogger logger, string shareRoot, string folder, string finalPath);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Prueba de conexión con {ShareRoot}: no se pudo obtener {What} (error Win32 {NativeError}).")]
    private static partial void LogOptionalFailed(ILogger logger, string shareRoot, string what, int nativeError);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Prueba de conexión con {ShareRoot}: el servidor no borró al cerrar el archivo de prueba {File}; se borra aparte.")]
    private static partial void LogProbeLeftOnClose(ILogger logger, string shareRoot, string file);

    [LoggerMessage(Level = LogLevel.Error, Message = "Prueba de conexión con {ShareRoot}: no se pudo borrar el archivo de prueba {File} ({ExceptionType}, HResult {HResult}).")]
    private static partial void LogProbeNotRemoved(ILogger logger, string shareRoot, string file, string exceptionType, int hResult);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Prueba de conexión con {ShareRoot}: no se pudo cerrar la conexión ({ExceptionType}, HResult {HResult}).")]
    private static partial void LogDisconnectFailed(ILogger logger, string shareRoot, string exceptionType, int hResult);

    /// <summary>Una ejecución de la prueba: estado, plazo, turnos y limpieza garantizada.</summary>
    private sealed class Attempt(
        ConnectionTester owner,
        string shareRoot,
        string destination,
        NasCredential credential,
        CancellationToken ct)
    {
        private readonly long _started = owner._timeProvider.GetTimestamp();

        // Operaciones abandonadas por plazo o cancelación: la limpieza espera a que terminen antes de borrar y desconectar.
        private readonly List<Task> _abandoned = [];

        private readonly string _server = ShareConnectionManager.ServerOf(shareRoot);
        private readonly string _describedRoot = UntrustedText.Describe(shareRoot);

        private SemaphoreSlim? _serverTurn;
        private bool _globalTurn;
        private Task<ShareLease>? _connect;
        private ShareLease? _lease;
        private string? _probePath;
        private bool _probeRemoved;
        private ConnectionTestStep _step = ConnectionTestStep.Connect;

        private bool _canRead;
        private bool _canWrite;
        private bool _canDelete;
        private ShareSpace? _space;
        private string? _dialect;

        private ILogger Logger => owner._logger;

        public async Task<ConnectionTestResult> RunAsync()
        {
            var canceled = false;
            try
            {
                await ProbeAsync().ConfigureAwait(false);
                var dialect = UntrustedText.Describe(_dialect);
                LogSucceeded(Logger, _describedRoot, dialect);

                return Result(null);
            }
            catch (ShareException ex)
            {
                var systemMessage = SystemMessage(ex.NativeErrorCode);
                LogFailed(Logger, _describedRoot, _step, ex.Code, ex.NativeErrorCode, systemMessage);
                return Result(ex.Code);
            }
            catch (TimeoutException)
            {
                LogTimedOut(Logger, _describedRoot, owner._options.Timeout, _step);
                return Result(BackboneErrorCode.NasUnreachable);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                canceled = true;
                LogCanceled(Logger, _describedRoot, _step);
                throw;
            }
            catch (Exception ex) when (ex is not BackboneException)
            {
                // Error no previsto del conector o del sistema: un resultado con código, nunca Internal (§7.4); la limpieza sigue.
                var code = _step == ConnectionTestStep.Connect ? BackboneErrorCode.NasUnreachable : BackboneErrorCode.AccessDenied;
                LogUnexpected(Logger, _describedRoot, _step, ex.GetType().Name, ex.HResult, code);
                return Result(code);
            }
            finally
            {
                await FinishAsync(canceled).ConfigureAwait(false);
            }
        }

        private async Task ProbeAsync()
        {
            await AdmitAsync().ConfigureAwait(false);
            await RejectSelfAsync().ConfigureAwait(false);

            // Sin cancelación: la tarea solo termina cuando termina la llamada al sistema (la credencial se desecha después).
            _connect = owner._connections.AcquireAsync(credential, CancellationToken.None);
            _lease = await BoundedAsync(_connect).ConfigureAwait(false);
            var connection = _lease.Connection;

            _step = ConnectionTestStep.Read;
            var target = await FindTargetDirectoryAsync(connection).ConfigureAwait(false);
            await BoundedAsync(connection.ListDirectoryAsync(target, ct)).ConfigureAwait(false);
            _canRead = true;

            _space = await OptionalAsync(AsNullable(connection.GetSpaceAsync(target, ct)), "el espacio libre").ConfigureAwait(false);
            _dialect = await OptionalAsync(connection.GetSmbDialectAsync(shareRoot, ct), "el dialecto SMB").ConfigureAwait(false);

            _step = ConnectionTestStep.Write;
            await EnsureInsideShareAsync(connection, target).ConfigureAwait(false);
            _probePath = Path.Join(target, ProbeFilePrefix + Guid.NewGuid().ToString("N") + ".tmp");
            await BoundedAsync(connection.WriteProbeFileAsync(_probePath, ProbeContent, ct)).ConfigureAwait(false);
            _canWrite = true;

            _step = ConnectionTestStep.Delete;
            if (await BoundedAsync(connection.FileExistsAsync(_probePath, ct)).ConfigureAwait(false))
            {
                LogProbeLeftOnClose(Logger, _describedRoot, UntrustedText.Describe(_probePath));
                await BoundedAsync(connection.DeleteFileAsync(_probePath, ct)).ConfigureAwait(false);
                if (await BoundedAsync(connection.FileExistsAsync(_probePath, ct)).ConfigureAwait(false))
                {
                    throw new ShareException(BackboneErrorCode.AccessDenied, 0);
                }
            }

            _probeRemoved = true;
            _canDelete = true;
        }

        /// <summary>
        /// Turno por servidor y global (BAJA-2) y espera a las limpiezas pendientes del mismo servidor, todo dentro del plazo:
        /// si vence, <see cref="TimeoutException"/> (→ <see cref="BackboneErrorCode.NasUnreachable"/> sin conectar).
        /// </summary>
        private async Task AdmitAsync()
        {
            using var timeout = new CancellationTokenSource(Remaining(), owner._timeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            try
            {
                var serverTurn = owner.ServerTurn(_server);
                await serverTurn.WaitAsync(linked.Token).ConfigureAwait(false);
                _serverTurn = serverTurn;
                await owner._globalTurns.WaitAsync(linked.Token).ConfigureAwait(false);
                _globalTurn = true;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException();
            }

            await owner.PendingCleanupsOf(_server).WaitAsync(Remaining(), owner._timeProvider, ct).ConfigureAwait(false);
        }

        /// <summary>§7.5 (ALTA-1): el servidor no puede resolver al propio equipo. Riesgo residual de DNS rebinding en <see cref="LocalMachine.ResolvesToSelfAsync"/>.</summary>
        private async Task RejectSelfAsync()
        {
            var limit = Remaining() < owner._options.DnsTimeout ? Remaining() : owner._options.DnsTimeout;

            // La resolución recibe su propio token: si vence el tope, se le pide que pare (Dns lo respeta si puede) y se abandona.
            using var abandon = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var resolution = LocalMachine.ResolvesToSelfAsync(_server, owner._resolver, LocalMachine.Addresses(), abandon.Token);
            bool isSelf;
            try
            {
                isSelf = await resolution.WaitAsync(limit, owner._timeProvider, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException && !resolution.IsCompleted)
            {
                _ = resolution.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                await abandon.CancelAsync().ConfigureAwait(false);
                throw;
            }

            if (isSelf)
            {
                LogResolvesToSelf(Logger, _describedRoot);
                throw BackboneException.ValidationFailed(string.Empty, ValidationIssueCode.DestinationNotAllowed);
            }
        }

        /// <summary>
        /// Antes de escribir, la carpeta destino (ruta final, <c>GetFinalPathNameByHandle</c>) sigue dentro de la ruta final del
        /// recurso (así también un recurso DFS redirigido). Si el redirector no la da, no se comprueba.
        /// </summary>
        private async Task EnsureInsideShareAsync(IShareConnection connection, string target)
        {
            string? finalRoot;
            string? finalTarget;
            try
            {
                finalRoot = await BoundedAsync(connection.GetFinalPathAsync(shareRoot, ct)).ConfigureAwait(false);
                finalTarget = string.Equals(target, shareRoot, StringComparison.OrdinalIgnoreCase)
                    ? finalRoot
                    : await BoundedAsync(connection.GetFinalPathAsync(target, ct)).ConfigureAwait(false);
            }
            catch (ShareException ex)
            {
                LogOptionalFailed(Logger, _describedRoot, "la ruta final", ex.NativeErrorCode);
                return;
            }

            if (finalRoot is not null && finalTarget is not null && !IsWithin(finalTarget, finalRoot))
            {
                LogOutsideShare(Logger, _describedRoot, UntrustedText.Describe(target), UntrustedText.Describe(finalTarget));
                throw new ShareException(BackboneErrorCode.AccessDenied, 0);
            }
        }

        /// <summary>La carpeta más profunda del destino que ya existe (el repositorio se crea al guardar el trabajo, HU-06).</summary>
        private async Task<string> FindTargetDirectoryAsync(IShareConnection connection)
        {
            var candidate = destination;
            while (candidate.Length > shareRoot.Length)
            {
                if (await BoundedAsync(connection.DirectoryExistsAsync(candidate, ct)).ConfigureAwait(false))
                {
                    return candidate;
                }

                candidate = candidate[..candidate.LastIndexOf('\\')];
            }

            return shareRoot;
        }

        /// <summary>Espacio y dialecto: si fallan, el resultado los lleva vacíos; el plazo sí cuenta.</summary>
        private async Task<T?> OptionalAsync<T>(Task<T> operation, string what)
        {
            try
            {
                return await BoundedAsync(operation).ConfigureAwait(false);
            }
            catch (ShareException ex)
            {
                LogOptionalFailed(Logger, _describedRoot, what, ex.NativeErrorCode);
                return default;
            }
        }

        private Task<object?> BoundedAsync(Task operation) => BoundedAsync(AsObject(operation));

        private async Task<T> BoundedAsync<T>(Task<T> operation)
        {
            try
            {
                return await operation.WaitAsync(Remaining(), owner._timeProvider, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException && !operation.IsCompleted)
            {
                _abandoned.Add(operation);
                throw;
            }
        }

        private static async Task<ShareSpace?> AsNullable(Task<ShareSpace> operation) => await operation.ConfigureAwait(false);

        private static async Task<object?> AsObject(Task operation)
        {
            await operation.ConfigureAwait(false);
            return null;
        }

        private TimeSpan Remaining()
        {
            var remaining = owner._options.Timeout - owner._timeProvider.GetElapsedTime(_started);
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }

        private ConnectionTestResult Result(BackboneErrorCode? error) => new()
        {
            Success = error is null,
            ErrorCode = error,
            ShareRoot = shareRoot,
            CanRead = _canRead,
            CanWrite = _canWrite,
            CanDelete = _canDelete,
            FreeBytes = _space?.FreeBytes,
            TotalBytes = _space?.TotalBytes,
            SmbDialect = _dialect is null ? null : UntrustedText.Describe(_dialect),
        };

        /// <summary>
        /// Desecha la credencial, limpia (archivo de prueba y conexión) y suelta los turnos. Sin operaciones abandonadas, se
        /// espera a la limpieza (con un tope); si las hay o el cliente ha cancelado, sigue en segundo plano y la siguiente prueba
        /// del mismo servidor la espera.
        /// </summary>
        private async Task FinishAsync(bool canceled)
        {
            try
            {
                // La contraseña no se pone a cero mientras la llamada al sistema la pueda estar leyendo.
                if (_connect is null || _connect.IsCompleted)
                {
                    credential.Dispose();
                }
                else
                {
                    _ = _connect.ContinueWith(_ => credential.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }

                var cleanup = CleanupAsync();
                owner.Track(cleanup, _server);
                if (canceled || _abandoned.Count > 0)
                {
                    return;
                }

                var wait = Remaining();
                if (wait < owner._options.MinimumCleanupWait)
                {
                    wait = owner._options.MinimumCleanupWait;
                }

                try
                {
                    await cleanup.WaitAsync(wait, owner._timeProvider).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // Sigue en segundo plano (registrada en Track).
                }
            }
            finally
            {
                if (_globalTurn)
                {
                    owner._globalTurns.Release();
                }

                _serverTurn?.Release();
            }
        }

        private async Task CleanupAsync()
        {
            // Primero, que terminen las operaciones abandonadas (no se borra mientras se escribe ni se desconecta a mitad).
            foreach (var operation in _abandoned)
            {
                try
                {
                    await operation.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Su resultado ya no importa: la prueba ya respondió.
                }
            }

            var lease = _lease;
            if (lease is null && _connect is not null)
            {
                try
                {
                    // La conexión pudo terminar bien después de abandonarla: también hay que soltarla.
                    lease = await _connect.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    lease = null;
                }
            }

            if (lease is null)
            {
                return;
            }

            try
            {
                if (_probePath is { } probe && !_probeRemoved)
                {
                    try
                    {
                        if (await lease.Connection.FileExistsAsync(probe, CancellationToken.None).ConfigureAwait(false))
                        {
                            await lease.Connection.DeleteFileAsync(probe, CancellationToken.None).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        // Cualquier fallo: la limpieza nunca falla (puede ir en segundo plano, sin nadie que la espere).
                        LogProbeNotRemoved(Logger, _describedRoot, UntrustedText.Describe(probe), ex.GetType().Name, ex.HResult);
                    }
                }
            }
            finally
            {
                try
                {
                    await lease.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogDisconnectFailed(Logger, _describedRoot, ex.GetType().Name, ex.HResult);
                }
            }
        }
    }
}
