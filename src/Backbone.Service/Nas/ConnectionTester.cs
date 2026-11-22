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

    /// <summary>
    /// <see cref="BackboneErrorCode.CredentialsRejected"/> seguidos de un mismo SID (cualquier servidor) a partir de los cuales sus
    /// pruebas con credencial explícita dan <see cref="BackboneErrorCode.NasBusy"/> sin conectar (§8.2 "Conexiones al NAS", M-1).
    /// </summary>
    public int MaxConsecutiveRejections { get; init; } = 5;

    /// <summary>Cuánto dura ese bloqueo (M-1).</summary>
    public TimeSpan RejectionBlock { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Tras guardar una credencial, durante cuánto cuentan también los rechazos de las pruebas con la credencial guardada del
    /// mismo SID en el contador de <see cref="MaxConsecutiveRejections"/> (B-2 de la revisión de seguridad de HU-14: guardar y
    /// probar sin credencial explícita permitiría probar contraseñas sin límite).
    /// </summary>
    public TimeSpan SavedCredentialWindow { get; init; } = TimeSpan.FromMinutes(5);
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
/// <item><b>Conexiones</b>: siempre por <see cref="ShareConnectionManager"/> (una por raíz, con referencias). Una credencial
/// explícita nunca comparte una conexión viva y, si ya hay otra con ese servidor, la respuesta es
/// <see cref="BackboneErrorCode.NasBusy"/> coincida o no el secreto (M-1). Además, tras
/// <see cref="ConnectionTestOptions.MaxConsecutiveRejections"/> <see cref="BackboneErrorCode.CredentialsRejected"/> seguidos
/// de un mismo SID, sus pruebas con credencial explícita dan <see cref="BackboneErrorCode.NasBusy"/> sin conectar durante
/// <see cref="ConnectionTestOptions.RejectionBlock"/>.</item>
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
    private readonly Dictionary<string, RejectionState> _rejections = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Sid, string ShareRoot), DateTimeOffset> _savedCredentials = new(SavedKeyComparer.Instance);
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
    /// <param name="callerSid">
    /// SID del cliente que pide la prueba (el que calcula el servidor de su token, nunca el que diga el cliente): cuenta los
    /// rechazos seguidos de credenciales explícitas (M-1).
    /// </param>
    /// <param name="ct">Cancelación del cliente: lanza <see cref="OperationCanceledException"/> y la limpieza sigue en segundo plano.</param>
    /// <exception cref="BackboneException">
    /// <see cref="BackboneErrorCode.ValidationFailed"/> si el destino o la credencial no son válidos o el servidor resuelve al
    /// propio equipo.
    /// </exception>
    public async Task<ConnectionTestResult> TestAsync(string destination, CredentialInput? credential, string callerSid, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(callerSid);
        if (!CredentialStore.TryNormalizeShareRoot(destination, out var shareRoot, out var issue)
            || !UncPaths.TryParse(destination, out _, out var normalizedDestination, out issue))
        {
            throw BackboneException.ValidationFailed(string.Empty, issue);
        }

        // M-1: tras demasiados rechazos seguidos, las pruebas con credencial explícita de este SID no llegan a conectar.
        // Se comprueba antes de validar la credencial, para que la respuesta no dependa de ella. B-2: también las que usan la
        // credencial que ese mismo SID acaba de guardar (guardar y probar sin credencial explícita es lo mismo).
        var testsJustSaved = credential is null && SavedRecently(callerSid, shareRoot);
        if ((credential is not null || testsJustSaved) && IsBlocked(callerSid))
        {
            LogBlocked(_logger, UntrustedText.Describe(shareRoot));
            return new ConnectionTestResult { ShareRoot = shareRoot, ErrorCode = BackboneErrorCode.NasBusy };
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
        var result = await attempt.RunAsync().ConfigureAwait(false);
        if (credential is not null || testsJustSaved)
        {
            RecordOutcome(callerSid, ShareConnectionManager.ServerOf(shareRoot), result, attempt.NativeError);
        }

        return result;
    }

    /// <summary>
    /// El SID <paramref name="callerSid"/> acaba de guardar la credencial de <paramref name="shareRoot"/> (B-2): durante
    /// <see cref="ConnectionTestOptions.SavedCredentialWindow"/>, los rechazos de las pruebas con la credencial guardada cuentan
    /// como los de una explícita.
    /// </summary>
    public void NoteCredentialSaved(string callerSid, string shareRoot)
    {
        ArgumentNullException.ThrowIfNull(callerSid);
        ArgumentNullException.ThrowIfNull(shareRoot);
        var now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            foreach (var expired in _savedCredentials.Where(e => now - e.Value >= _options.SavedCredentialWindow).Select(e => e.Key).ToArray())
            {
                _savedCredentials.Remove(expired);
            }

            _savedCredentials[(callerSid, shareRoot)] = now;
        }
    }

    private bool SavedRecently(string sid, string shareRoot)
    {
        lock (_gate)
        {
            return _savedCredentials.TryGetValue((sid, shareRoot), out var savedUtc)
                   && _timeProvider.GetUtcNow() - savedUtc < _options.SavedCredentialWindow;
        }
    }

    /// <summary>El SID tiene bloqueadas las pruebas con credencial explícita (M-1).</summary>
    private bool IsBlocked(string sid)
    {
        lock (_gate)
        {
            if (!_rejections.TryGetValue(sid, out var state) || state.BlockedUntil is not { } until)
            {
                return false;
            }

            if (_timeProvider.GetUtcNow() < until)
            {
                return true;
            }

            _rejections.Remove(sid); // el bloqueo ha vencido: se empieza de cero
            return false;
        }
    }

    /// <summary>
    /// Una prueba correcta pone a cero el contador <b>de ese servidor</b> (B-1: un éxito contra otro servidor no sirve para
    /// reiniciar los rechazos de este); cada <see cref="BackboneErrorCode.CredentialsRejected"/> lo suma (los demás fallos no lo
    /// tocan: no dicen nada de la credencial). <c>ERROR_SESSION_CREDENTIAL_CONFLICT</c> (1219) no cuenta: es un conflicto con una
    /// sesión ya abierta, no una contraseña probada. Los rechazos de todos los servidores se suman por SID; al llegar al máximo,
    /// el SID queda bloqueado.
    /// </summary>
    private void RecordOutcome(string sid, string server, ConnectionTestResult result, int nativeError)
    {
        lock (_gate)
        {
            if (result.Success)
            {
                if (_rejections.TryGetValue(sid, out var previous))
                {
                    previous.PerServer.Remove(server);
                    if (previous.PerServer.Count == 0 && previous.BlockedUntil is null)
                    {
                        _rejections.Remove(sid);
                    }
                }

                return;
            }

            if (result.ErrorCode != BackboneErrorCode.CredentialsRejected || nativeError == ShareErrors.ErrorSessionCredentialConflict)
            {
                return;
            }

            _rejections.TryGetValue(sid, out var state);
            state ??= new RejectionState();
            state.PerServer[server] = state.PerServer.GetValueOrDefault(server) + 1;
            var total = state.Count;
            if (total >= _options.MaxConsecutiveRejections && state.BlockedUntil is null)
            {
                state.BlockedUntil = _timeProvider.GetUtcNow() + _options.RejectionBlock;
                LogBlockStarted(_logger, total, _options.RejectionBlock);
            }

            _rejections[sid] = state;
        }
    }

    /// <summary>Rechazos seguidos de un SID, por servidor (M-1, B-1). Solo en memoria.</summary>
    private sealed class RejectionState
    {
        public Dictionary<string, int> PerServer { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Rechazos de todos los servidores.</summary>
        public int Count => PerServer.Values.Sum();

        public DateTimeOffset? BlockedUntil { get; set; }
    }

    /// <summary>SID y recurso, sin distinguir mayúsculas.</summary>
    private sealed class SavedKeyComparer : IEqualityComparer<(string Sid, string ShareRoot)>
    {
        public static SavedKeyComparer Instance { get; } = new();

        public bool Equals((string Sid, string ShareRoot) x, (string Sid, string ShareRoot) y) =>
            string.Equals(x.Sid, y.Sid, StringComparison.OrdinalIgnoreCase) && string.Equals(x.ShareRoot, y.ShareRoot, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Sid, string ShareRoot) obj) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Sid), StringComparer.OrdinalIgnoreCase.GetHashCode(obj.ShareRoot));
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
        return new NasCredential(shareRoot, input.UserName, DateTimeOffset.UnixEpoch, buffer, input.Password.Length, isExplicit: true);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Prueba de conexión con {ShareRoot}: demasiados rechazos de credencial seguidos de este usuario; NasBusy sin conectar.")]
    private static partial void LogBlocked(ILogger logger, string shareRoot);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Un usuario ha acumulado {Count} rechazos de credencial seguidos en pruebas de conexión: sus pruebas con credencial explícita dan NasBusy durante {Block}.")]
    private static partial void LogBlockStarted(ILogger logger, int count, TimeSpan block);

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

        /// <summary>Código Win32 del fallo de la prueba (0 si no lo hubo o no es de un <see cref="ShareException"/>).</summary>
        public int NativeError { get; private set; }

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
                NativeError = ex.NativeErrorCode;
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
            var finalRoot = await FinalRootAsync(connection).ConfigureAwait(false);
            _probePath = Path.Join(target, ProbeFilePrefix + Guid.NewGuid().ToString("N") + ".tmp");

            // B-3: la conexión comprueba la ruta final del archivo sobre su handle abierto, antes de escribir.
            await BoundedAsync(connection.WriteProbeFileAsync(_probePath, ProbeContent, finalRoot, ct)).ConfigureAwait(false);
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
        /// Ruta final de la raíz conectada (así también un recurso DFS redirigido). Si el redirector no la da, la que sale del
        /// nombre: <c>\\?\UNC\servidor\recurso</c>. La ruta final del archivo de prueba se compara con ella sobre su handle
        /// (<see cref="IShareConnection.WriteProbeFileAsync"/>, B-3).
        /// </summary>
        private async Task<string> FinalRootAsync(IShareConnection connection)
        {
            try
            {
                if (await BoundedAsync(connection.GetFinalPathAsync(shareRoot, ct)).ConfigureAwait(false) is { } finalRoot)
                {
                    return finalRoot;
                }
            }
            catch (ShareException ex)
            {
                LogOptionalFailed(Logger, _describedRoot, "la ruta final del recurso", ex.NativeErrorCode);
            }

            return @"\\?\UNC\" + shareRoot[2..];
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
