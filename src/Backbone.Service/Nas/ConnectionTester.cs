using System.Runtime.InteropServices;
using Backbone.Contracts;
using Backbone.Service.Ipc;
using Backbone.Service.Persistence;
using Microsoft.Extensions.Logging;

namespace Backbone.Service.Nas;

/// <summary>Plazos de "Probar conexión" (HU-05).</summary>
internal sealed record ConnectionTestOptions
{
    /// <summary>
    /// Plazo de toda la prueba (conexión, lectura, escritura y borrado). Con <see cref="MinimumCleanupWait"/>, la respuesta
    /// llega en menos de 15 s aunque el servidor esté apagado (HU-05 criterio 3).
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(11);

    /// <summary>
    /// Espera mínima a la limpieza (borrar el archivo de prueba y cerrar la conexión) antes de responder; si tarda más, sigue
    /// en segundo plano.
    /// </summary>
    public TimeSpan MinimumCleanupWait { get; init; } = TimeSpan.FromSeconds(2);
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
/// <item><b>Plazo</b> (<see cref="ConnectionTestOptions.Timeout"/>, con <see cref="TimeProvider"/>): vencido, la prueba
/// responde <see cref="BackboneErrorCode.NasUnreachable"/>. Las operaciones de red no se pueden interrumpir; las que quedan
/// en curso se abandonan y la limpieza espera a que terminen.</item>
/// <item><b>Sin restos</b> (criterio 5): el archivo de prueba se crea con borrado al cerrar y, además, al terminar (bien,
/// mal, por plazo o por cancelación) se comprueba que no existe y, si existe, se borra. La conexión se cierra siempre, también
/// si la llamada de conexión termina bien después de abandonarla.</item>
/// <item><b>Textos no confiables</b> (§6.1): el nombre del recurso, los mensajes del sistema y el dialecto pasan por
/// <see cref="UntrustedText.Describe"/> antes de registrarse. Nunca se registran el usuario ni la contraseña.</item>
/// </list>
/// </remarks>
internal sealed partial class ConnectionTester
{
    /// <summary>Prefijo del archivo de prueba, en la carpeta más profunda del destino que ya exista.</summary>
    internal const string ProbeFilePrefix = ".backbone-prueba-";

    /// <summary>Contenido del archivo de prueba.</summary>
    internal static ReadOnlyMemory<byte> ProbeContent { get; } = "Backbone: prueba de conexión (RF-61). Se borra al terminar.\n"u8.ToArray();

    private readonly IShareConnector _connector;
    private readonly ShareCredentialProvider _credentials;
    private readonly TimeProvider _timeProvider;
    private readonly ConnectionTestOptions _options;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly HashSet<Task> _cleanups = [];

    public ConnectionTester(
        IShareConnector connector,
        ShareCredentialProvider credentials,
        TimeProvider timeProvider,
        ConnectionTestOptions options,
        ILogger<ConnectionTester> logger)
    {
        _connector = connector;
        _credentials = credentials;
        _timeProvider = timeProvider;
        _options = options;
        _logger = logger;
    }

    /// <summary>Espera a las limpiezas que han quedado en segundo plano (pruebas y parada ordenada).</summary>
    public Task WhenCleanupsCompleteAsync()
    {
        lock (_gate)
        {
            return Task.WhenAll([.. _cleanups]);
        }
    }

    /// <summary>Prueba la conexión con <paramref name="destination"/>.</summary>
    /// <param name="destination">Destino UNC (<c>\\servidor\recurso[\ruta]</c>), con la longitud ya validada por IPC.</param>
    /// <param name="credential">Credencial a probar o <see langword="null"/> para usar la guardada.</param>
    /// <param name="ct">Cancelación del cliente: lanza <see cref="OperationCanceledException"/> y la limpieza sigue en segundo plano.</param>
    /// <exception cref="BackboneException"><see cref="BackboneErrorCode.ValidationFailed"/> si el destino o la credencial no son válidos.</exception>
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

    private void Track(Task cleanup)
    {
        lock (_gate)
        {
            _cleanups.Add(cleanup);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Prueba de conexión con {ShareRoot}: se agotó el plazo de {Timeout} en el paso {Step}.")]
    private static partial void LogTimedOut(ILogger logger, string shareRoot, TimeSpan timeout, ConnectionTestStep step);

    [LoggerMessage(Level = LogLevel.Information, Message = "Prueba de conexión con {ShareRoot} cancelada por el cliente en el paso {Step}.")]
    private static partial void LogCanceled(ILogger logger, string shareRoot, ConnectionTestStep step);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Prueba de conexión con {ShareRoot}: no se pudo obtener {What} (error Win32 {NativeError}).")]
    private static partial void LogOptionalFailed(ILogger logger, string shareRoot, string what, int nativeError);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Prueba de conexión con {ShareRoot}: el servidor no borró al cerrar el archivo de prueba {File}; se borra aparte.")]
    private static partial void LogProbeLeftOnClose(ILogger logger, string shareRoot, string file);

    [LoggerMessage(Level = LogLevel.Error, Message = "Prueba de conexión con {ShareRoot}: no se pudo borrar el archivo de prueba {File}.")]
    private static partial void LogProbeNotRemoved(ILogger logger, Exception exception, string shareRoot, string file);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Prueba de conexión con {ShareRoot}: no se pudo cerrar la conexión.")]
    private static partial void LogDisconnectFailed(ILogger logger, Exception exception, string shareRoot);

    /// <summary>Una ejecución de la prueba: estado, plazo y limpieza garantizada.</summary>
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

        private readonly string _describedRoot = UntrustedText.Describe(shareRoot);

        private Task<IShareConnection>? _connect;
        private IShareConnection? _connection;
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
            finally
            {
                await FinishAsync(canceled).ConfigureAwait(false);
            }
        }

        private async Task ProbeAsync()
        {
            _connect = owner._connector.ConnectAsync(credential, CancellationToken.None);
            _connection = await BoundedAsync(_connect).ConfigureAwait(false);

            _step = ConnectionTestStep.Read;
            var target = await FindTargetDirectoryAsync(_connection).ConfigureAwait(false);
            await BoundedAsync(_connection.ListDirectoryAsync(target, ct)).ConfigureAwait(false);
            _canRead = true;

            _space = await OptionalAsync(AsNullable(_connection.GetSpaceAsync(target, ct)), "el espacio libre").ConfigureAwait(false);
            _dialect = await OptionalAsync(_connection.GetSmbDialectAsync(shareRoot, ct), "el dialecto SMB").ConfigureAwait(false);

            _step = ConnectionTestStep.Write;
            _probePath = Path.Join(target, ProbeFilePrefix + Guid.NewGuid().ToString("N") + ".tmp");
            await BoundedAsync(_connection.WriteProbeFileAsync(_probePath, ProbeContent, ct)).ConfigureAwait(false);
            _canWrite = true;

            _step = ConnectionTestStep.Delete;
            if (await BoundedAsync(_connection.FileExistsAsync(_probePath, ct)).ConfigureAwait(false))
            {
                LogProbeLeftOnClose(Logger, _describedRoot, UntrustedText.Describe(_probePath));
                await BoundedAsync(_connection.DeleteFileAsync(_probePath, ct)).ConfigureAwait(false);
                if (await BoundedAsync(_connection.FileExistsAsync(_probePath, ct)).ConfigureAwait(false))
                {
                    throw new ShareException(BackboneErrorCode.AccessDenied, 0);
                }
            }

            _probeRemoved = true;
            _canDelete = true;
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
        /// Desecha la credencial y limpia (archivo de prueba y conexión). Sin operaciones abandonadas, se espera a la limpieza
        /// (con un tope); si las hay o el cliente ha cancelado, sigue en segundo plano.
        /// </summary>
        private async Task FinishAsync(bool canceled)
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
            owner.Track(cleanup);
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

            var connection = _connection;
            if (connection is null && _connect is not null)
            {
                try
                {
                    // La conexión pudo terminar bien después de abandonarla: también hay que cerrarla.
                    connection = await _connect.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    connection = null;
                }
            }

            if (connection is null)
            {
                return;
            }

            try
            {
                if (_probePath is { } probe && !_probeRemoved)
                {
                    try
                    {
                        if (await connection.FileExistsAsync(probe, CancellationToken.None).ConfigureAwait(false))
                        {
                            await connection.DeleteFileAsync(probe, CancellationToken.None).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        // Cualquier fallo: la limpieza nunca falla (puede ir en segundo plano, sin nadie que la espere).
                        LogProbeNotRemoved(Logger, ex, _describedRoot, UntrustedText.Describe(probe));
                    }
                }
            }
            finally
            {
                try
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogDisconnectFailed(Logger, ex, _describedRoot);
                }
            }
        }
    }
}
