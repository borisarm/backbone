using System.Collections.Concurrent;
using Backbone.Contracts;
using Backbone.Core.Repository;
using Backbone.Core.Storage;
using Backbone.Service.Ipc;
using Backbone.Service.Nas;
using Backbone.Service.Persistence;
using Microsoft.Extensions.Logging;
using ContractRepositoryState = Backbone.Contracts.RepositoryState;
using CoreRepositoryState = Backbone.Core.Repository.RepositoryState;

namespace Backbone.Service.Jobs;

/// <summary>
/// Crea el <see cref="IRepositoryStorage"/> de un destino sobre una conexión ya abierta con su recurso. El servicio usa
/// <see cref="UncRepositoryStorageFactory"/>; las pruebas, almacenamientos en memoria.
/// </summary>
internal interface IRepositoryStorageFactory
{
    /// <summary>Almacenamiento del repositorio en <paramref name="destination"/>, dentro de <paramref name="connection"/>.</summary>
    /// <param name="connection">Conexión viva con el recurso del destino (<see cref="ShareConnectionManager"/>).</param>
    /// <param name="destination">Destino UNC normalizado (<see cref="JobValidator.TryNormalizeDestination"/>).</param>
    IRepositoryStorage Create(IShareConnection connection, string destination);
}

/// <summary>
/// <see cref="FileSystemRepositoryStorage"/> sobre el destino UNC (§6.1 "Solo destinos UNC"). La conexión de
/// <c>WNetAddConnection2</c> la ha hecho antes <see cref="ShareConnectionManager"/> con la credencial guardada, y el
/// destino ya ha pasado la validación (no es el propio equipo, §7.5).
/// </summary>
internal sealed class UncRepositoryStorageFactory : IRepositoryStorageFactory
{
    public IRepositoryStorage Create(IShareConnection connection, string destination)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (!IsInsideShare(destination, connection.ShareRoot))
        {
            // Error de programación: el destino siempre es del recurso con el que se ha conectado.
            throw new ArgumentException("The destination is not inside the connected share.", nameof(destination));
        }

        return new FileSystemRepositoryStorage(destination);
    }

    internal static bool IsInsideShare(string destination, string shareRoot) =>
        destination.Equals(shareRoot, StringComparison.OrdinalIgnoreCase)
        || (destination.StartsWith(shareRoot, StringComparison.OrdinalIgnoreCase)
            && destination.Length > shareRoot.Length
            && destination[shareRoot.Length] == '\\');
}

/// <summary>
/// Estado del repositorio de cada trabajo según la última vez que se abrió (<see cref="JobStatus.RepositoryState"/>,
/// HU-06.4). Vive en memoria: tras reiniciar el servicio es <see cref="ContractRepositoryState.Unknown"/> hasta que se vuelve a
/// abrir (guardar el trabajo o, desde HU-14, una ejecución).
/// </summary>
internal sealed class RepositoryStateTracker
{
    private readonly ConcurrentDictionary<Guid, ContractRepositoryState> _states = new();

    public ContractRepositoryState Get(Guid jobId) =>
        _states.TryGetValue(jobId, out var state) ? state : ContractRepositoryState.Unknown;

    public void Set(Guid jobId, ContractRepositoryState state)
    {
        if (state == ContractRepositoryState.Unknown)
        {
            _states.TryRemove(jobId, out _);
        }
        else
        {
            _states[jobId] = state;
        }
    }

    public void Remove(Guid jobId) => _states.TryRemove(jobId, out _);

    /// <summary>
    /// Vuelve a <paramref name="previous"/> solo si el estado sigue siendo <paramref name="expected"/> (el que puso quien
    /// restaura): si otro guardado lo ha cambiado entretanto, no se pisa.
    /// </summary>
    public void RestoreIfUnchanged(Guid jobId, ContractRepositoryState expected, ContractRepositoryState previous)
    {
        if (previous == ContractRepositoryState.Unknown)
        {
            _states.TryRemove(new KeyValuePair<Guid, ContractRepositoryState>(jobId, expected));
        }
        else
        {
            _states.TryUpdate(jobId, previous, expected);
        }
    }
}

/// <summary>Plazos de <see cref="RepositoryPreparer"/>.</summary>
internal sealed record RepositoryPreparationOptions
{
    /// <summary>
    /// Plazo de toda la preparación (conexión, examen del destino e inicialización). Vencido, <c>SaveJobAsync</c> responde
    /// <see cref="BackboneErrorCode.NasUnreachable"/>; la E/S de red que no se puede interrumpir se abandona y la conexión se
    /// suelta cuando termina.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Preparaciones a la vez en todo el servicio. Cuenta también las abandonadas por plazo o cancelación que aún no han vuelto
    /// de la E/S de red (cada una puede mantener un hilo bloqueado); superado, <see cref="BackboneErrorCode.NasBusy"/> sin conectar.
    /// </summary>
    public int MaxConcurrent { get; init; } = 4;
}

/// <summary>Resultado de <see cref="RepositoryPreparer.PrepareAsync"/>.</summary>
internal enum RepositoryPreparationOutcome
{
    /// <summary>El destino estaba vacío o no existía: se ha inicializado un repositorio nuevo (HU-06.1).</summary>
    Initialized = 0,

    /// <summary>Había un repositorio v1 válido: se adopta tal cual, sin escribir nada (HU-06.2).</summary>
    Adopted = 1,

    /// <summary>Repositorio de formato superior (RNF-14): solo lectura, no se escribe nada (HU-06.4).</summary>
    ReadOnlyNewerFormat = 2,
}

/// <summary>
/// Inicializa o adopta el repositorio del destino de un trabajo (RF-63, HU-06), como parte de <c>SaveJobAsync</c>.
/// </summary>
/// <remarks>
/// <list type="number">
/// <item>Lee la credencial guardada del recurso (<see cref="ShareCredentialProvider"/>): sin ella,
/// <see cref="BackboneErrorCode.CredentialRequired"/> sin conectar (§7.5 A-3).</item>
/// <item>Conecta por <see cref="ShareConnectionManager"/> (§8.2 "Conexiones al NAS") y crea el almacenamiento del destino
/// (<see cref="IRepositoryStorageFactory"/>).</item>
/// <item>Examina el destino sin escribir (<see cref="BackupRepository.ProbeAsync"/>, §6.1):
/// <list type="bullet">
/// <item>inexistente o vacío → <see cref="BackupRepository.InitializeAsync"/> (HU-06.1);</item>
/// <item>compatible → se adopta: se abre con <see cref="BackupRepository.OpenAsync"/> y no se escribe nada (HU-06.2);</item>
/// <item>con archivos que no son un repositorio → <see cref="BackboneErrorCode.ValidationFailed"/> con
/// <see cref="ValidationIssueCode.DestinationNotRepository"/> en <c>Destination</c>, sin escribir nada (HU-06.3);</item>
/// <item>de formato superior → <see cref="RepositoryPreparationOutcome.ReadOnlyNewerFormat"/>, sin escribir nada (RNF-14,
/// HU-06.4);</item>
/// <item>corrupto → <see cref="BackboneErrorCode.RepositoryCorrupt"/>, sin escribir nada (§6.1: no se recrea).</item>
/// </list></item>
/// </list>
/// Admisión (revisión de seguridad de HU-06, MEDIA-1): una preparación a la vez por servidor y como mucho
/// <see cref="RepositoryPreparationOptions.MaxConcurrent"/> en total, contando las abandonadas que no han vuelto de la E/S
/// (<see cref="BackboneErrorCode.NasBusy"/> sin conectar); la cancelación del cliente espera a que vuelva la conexión en curso.
/// Antes de la primera E/S se comprueba el registro de uso de la conexión (<see cref="IShareConnection.HasUseRecordAsync"/>,
/// §8.2 B-1).
/// La conexión se suelta siempre (<c>finally</c>), también al cancelar, al vencer el plazo o con un error no previsto; si la
/// E/S sigue en curso, cuando termina. Los fallos de red se traducen con <see cref="ShareErrors"/>. Nunca se registran el
/// usuario ni la contraseña; el destino y los detalles leídos del NAS pasan por <see cref="UntrustedText.Describe"/>.
/// </remarks>
internal sealed partial class RepositoryPreparer(
    ShareCredentialProvider credentials,
    ShareConnectionManager connections,
    IRepositoryStorageFactory storageFactory,
    ServiceRuntimeInfo runtime,
    TimeProvider timeProvider,
    RepositoryPreparationOptions options,
    ILogger<RepositoryPreparer> logger)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Task, CancellationTokenSource> _pending = [];
    private readonly Dictionary<string, ServerTurn> _serverTurns = new(StringComparer.OrdinalIgnoreCase);
    private int _active;

    /// <summary>Texto de <c>createdBy</c> de los repositorios que se crean (§6.2), p. ej. "Backbone 1.0.0 en PC-SALON".</summary>
    public string CreatedBy => $"Backbone {runtime.Version} en {runtime.MachineName}";

    /// <summary>
    /// Espera a las preparaciones que no han terminado (las abandonadas por plazo o cancelación y las que siguen en curso):
    /// pruebas y parada ordenada.
    /// </summary>
    public async Task WhenPreparationsCompleteAsync()
    {
        Task[] pending;
        lock (_gate)
        {
            pending = [.. _pending.Keys];
        }

        // Las abandonadas terminan canceladas o con el error que ya no interesa a nadie.
        foreach (var task in pending)
        {
            await task.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    /// <summary>
    /// Cancela todas las preparaciones pendientes, también las abandonadas (parada del servicio, BAJA-N1): su E/S termina antes
    /// de que la parada cierre a la fuerza las conexiones, y ninguna toca el almacenamiento después.
    /// </summary>
    public void CancelAll()
    {
        CancellationTokenSource[] sources;
        lock (_gate)
        {
            sources = [.. _pending.Values];
        }

        foreach (var source in sources)
        {
            try
            {
                source.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // La preparación terminó entretanto.
            }
        }
    }

    /// <summary>Inicializa o adopta el repositorio de <paramref name="destination"/>.</summary>
    /// <param name="destination">Destino UNC ya validado y normalizado.</param>
    /// <param name="ct">Cancelación de la llamada.</param>
    /// <returns>Qué se ha hecho con el destino.</returns>
    /// <exception cref="BackboneException">
    /// <see cref="BackboneErrorCode.CredentialRequired"/>, <see cref="BackboneErrorCode.NasBusy"/> (demasiadas preparaciones en
    /// curso o abandonadas sin volver), <see cref="BackboneErrorCode.NasUnreachable"/>,
    /// <see cref="BackboneErrorCode.CredentialsRejected"/>, <see cref="BackboneErrorCode.ShareNotFound"/>,
    /// <see cref="BackboneErrorCode.AccessDenied"/>, <see cref="BackboneErrorCode.ValidationFailed"/>
    /// (<see cref="ValidationIssueCode.DestinationNotRepository"/>) o <see cref="BackboneErrorCode.RepositoryCorrupt"/>.
    /// </exception>
    public async Task<RepositoryPreparationOutcome> PrepareAsync(string destination, CancellationToken ct)
    {
        var credential = await credentials.GetRequiredAsync(destination, ct).ConfigureAwait(false);
        var described = UntrustedText.Describe(destination);
        var server = ShareConnectionManager.ServerOf(credential.ShareRoot);
        var started = timeProvider.GetTimestamp();

        var turn = Admit(server, described, credential);
        try
        {
            // Una preparación a la vez por servidor, dentro del plazo (las anteriores pueden seguir ahí si se abandonaron).
            using var timeout = new CancellationTokenSource(options.Timeout, timeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            await turn.Semaphore.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Leave(server, turn, holdsTurn: false);
            credential.Dispose();
            if (ex is OperationCanceledException && !ct.IsCancellationRequested)
            {
                LogTimeout(logger, described, options.Timeout);
                throw new BackboneException(BackboneErrorCode.NasUnreachable, "The NAS did not respond in time.");
            }

            throw;
        }

        // Desde aquí, la preparación es dueña del turno, del cupo y de la credencial hasta que termina (aunque se abandone).
        var abandon = new CancellationTokenSource();
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = Task.Run(() => RunAsync(credential, destination, described, server, turn, connected, abandon.Token), CancellationToken.None);
        Track(work, abandon);
        try
        {
            var remaining = options.Timeout - timeProvider.GetElapsedTime(started);
            return await work.WaitAsync(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, timeProvider, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            LogTimeout(logger, described, options.Timeout);
            throw new BackboneException(BackboneErrorCode.NasUnreachable, "The NAS did not respond in time.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && work.IsCanceled)
        {
            // La preparación se canceló por la parada del servicio (CancelAll), no por el cliente.
            throw new BackboneException(BackboneErrorCode.Interrupted, "The service is stopping.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested && !work.IsCompleted)
        {
            // La cancelación no abandona la conexión a medias: WNetAddConnection2 no se interrumpe y lee la contraseña mientras
            // dura. Se espera (con el plazo) a que vuelva; el resto se abandona y la conexión se suelta al terminar.
            await abandon.CancelAsync().ConfigureAwait(false);
            var remaining = options.Timeout - timeProvider.GetElapsedTime(started);
            await connected.Task.WaitAsync(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, timeProvider, CancellationToken.None).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            throw;
        }
        finally
        {
            if (!work.IsCompleted)
            {
                await abandon.CancelAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Reserva un cupo (<see cref="RepositoryPreparationOptions.MaxConcurrent"/>, que cuenta también las abandonadas que no han
    /// vuelto) y el turno del servidor. Sin cupo, <see cref="BackboneErrorCode.NasBusy"/> sin conectar.
    /// </summary>
    private ServerTurn Admit(string server, string described, NasCredential credential)
    {
        lock (_gate)
        {
            if (_active < options.MaxConcurrent)
            {
                _active++;
                if (!_serverTurns.TryGetValue(server, out var turn))
                {
                    turn = new ServerTurn();
                    _serverTurns.Add(server, turn);
                }

                turn.Users++;
                return turn;
            }
        }

        credential.Dispose();
        LogBusy(logger, described, options.MaxConcurrent);
        throw new BackboneException(BackboneErrorCode.NasBusy, "Too many repository preparations are in progress.");
    }

    /// <summary>Devuelve el cupo y, si se tenía, el turno; el turno del servidor se descarta cuando nadie lo usa (como en las pruebas de conexión).</summary>
    private void Leave(string server, ServerTurn turn, bool holdsTurn)
    {
        if (holdsTurn)
        {
            turn.Semaphore.Release();
        }

        lock (_gate)
        {
            _active--;
            if (--turn.Users == 0 && _serverTurns.TryGetValue(server, out var current) && ReferenceEquals(current, turn))
            {
                _serverTurns.Remove(server);
            }
        }
    }

    private async Task<RepositoryPreparationOutcome> RunAsync(
        NasCredential credential,
        string destination,
        string described,
        string server,
        ServerTurn turn,
        TaskCompletionSource connected,
        CancellationToken ct)
    {
        try
        {
            return await ConnectAndPrepareAsync(credential, destination, described, connected, ct).ConfigureAwait(false);
        }
        finally
        {
            Leave(server, turn, holdsTurn: true);
        }
    }

    private async Task<RepositoryPreparationOutcome> ConnectAndPrepareAsync(
        NasCredential credential,
        string destination,
        string described,
        TaskCompletionSource connected,
        CancellationToken ct)
    {
        ShareLease lease;
        try
        {
            // La credencial se desecha (contraseña a cero) en cuanto termina la conexión, y no antes (IShareConnector).
            lease = await connections.AcquireAsync(credential, CancellationToken.None).ConfigureAwait(false);
        }
        catch (ShareException ex)
        {
            LogConnectFailed(logger, described, ex.Code, ex.NativeErrorCode);
            throw new BackboneException(ex.Code, "Could not connect to the destination share.");
        }
        finally
        {
            credential.Dispose();
            connected.TrySetResult();
        }

        await using (lease.ConfigureAwait(false))
        {
            try
            {
                ct.ThrowIfCancellationRequested();

                // §8.2 B-1: antes de la primera E/S, Windows tiene que seguir teniendo el registro de uso de la conexión con la
                // credencial explícita; si no, la E/S saldría con la cuenta del equipo (§7.5 A-3). Nada se escribe.
                if (!await lease.Connection.HasUseRecordAsync(ct).ConfigureAwait(false))
                {
                    LogUseRecordMissing(logger, described);
                    throw new BackboneException(BackboneErrorCode.NasUnreachable, "The connection to the share was lost before using it.");
                }

                // M-A (HU-14): revocado al soltar la concesión o al cerrar a la fuerza la conexión; después, ninguna E/S toca la red.
                var storage = lease.Protect(storageFactory.Create(lease.Connection, destination));
                return await PrepareStorageAsync(storage, described, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not (BackboneException or OperationCanceledException) && ShareErrors.FromIoException(ex) is { } share)
            {
                LogIoFailed(logger, described, share.Code, ex.GetType().Name, ex.HResult);
                throw new BackboneException(share.Code, "Could not read or write the destination folder.");
            }
        }
    }

    private async Task<RepositoryPreparationOutcome> PrepareStorageAsync(IRepositoryStorage storage, string described, CancellationToken ct)
    {
        // Como mucho dos vueltas: si otro proceso inicializa el destino entre el examen y la inicialización, se adopta.
        for (var attempt = 0; ; attempt++)
        {
            var probe = await BackupRepository.ProbeAsync(storage, ct).ConfigureAwait(false);
            switch (probe.State)
            {
                case CoreRepositoryState.Missing or CoreRepositoryState.Empty:
                    try
                    {
                        var created = await BackupRepository.InitializeAsync(
                            storage,
                            new RepositoryInitializationOptions { CreatedBy = CreatedBy },
                            timeProvider,
                            ct).ConfigureAwait(false);
                        LogInitialized(logger, described, created.Configuration.RepositoryId);
                        return RepositoryPreparationOutcome.Initialized;
                    }
                    catch (RepositoryException ex) when (ex.Kind == RepositoryErrorKind.AlreadyInitialized && attempt == 0)
                    {
                        continue;
                    }
                    catch (RepositoryException ex)
                    {
                        throw Translate(ex, described);
                    }

                case CoreRepositoryState.Compatible:
                    try
                    {
                        var adopted = await BackupRepository.OpenAsync(storage, timeProvider, ct).ConfigureAwait(false);
                        var createdBy = UntrustedText.Describe(adopted.Configuration.CreatedBy);
                        LogAdopted(logger, described, adopted.Configuration.RepositoryId, createdBy);
                        return RepositoryPreparationOutcome.Adopted;
                    }
                    catch (RepositoryException ex)
                    {
                        throw Translate(ex, described);
                    }

                case CoreRepositoryState.NotARepository:
                    LogNotARepository(logger, described);
                    throw NotARepository();

                case CoreRepositoryState.Incompatible:
                    LogNewerFormat(logger, described, probe.FormatVersion, UntrustedText.Describe(probe.Detail));
                    return RepositoryPreparationOutcome.ReadOnlyNewerFormat;

                default:
                    LogCorrupt(logger, described, UntrustedText.Describe(probe.Detail));
                    throw Corrupt();
            }
        }
    }

    private BackboneException Translate(RepositoryException ex, string described)
    {
        switch (ex.Kind)
        {
            case RepositoryErrorKind.NotEmpty or RepositoryErrorKind.NotARepository:
                LogNotARepository(logger, described);
                return NotARepository();
            case RepositoryErrorKind.Incompatible:
                // Solo si el destino cambia entre el examen y la apertura; se trata como corrupto para no escribir nada.
            default:
                LogCorrupt(logger, described, UntrustedText.Describe(ex.Message));
                return Corrupt();
        }
    }

    private static BackboneException NotARepository() =>
        BackboneException.ValidationFailed(nameof(JobDefinition.Destination), ValidationIssueCode.DestinationNotRepository);

    private static BackboneException Corrupt() =>
        new(BackboneErrorCode.RepositoryCorrupt, "The destination contains a damaged Backbone repository.");

    private void Track(Task work, CancellationTokenSource abandon)
    {
        lock (_gate)
        {
            _pending.Add(work, abandon);
        }

        _ = work.ContinueWith(
            completed =>
            {
                _ = completed.Exception; // observada: ya se ha respondido al cliente
                lock (_gate)
                {
                    _pending.Remove(completed);
                }

                abandon.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>Turno de un servidor: una preparación a la vez (protegido por el cerrojo del preparador).</summary>
    private sealed class ServerTurn
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int Users { get; set; }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Repositorio inicializado en {Destination} ({RepositoryId}).")]
    private static partial void LogInitialized(ILogger logger, string destination, Guid repositoryId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Repositorio existente adoptado en {Destination} ({RepositoryId}, creado por {CreatedBy}).")]
    private static partial void LogAdopted(ILogger logger, string destination, Guid repositoryId, string createdBy);

    [LoggerMessage(Level = LogLevel.Warning, Message = "El destino {Destination} contiene archivos y no es un repositorio de Backbone: no se escribe nada.")]
    private static partial void LogNotARepository(ILogger logger, string destination);

    [LoggerMessage(Level = LogLevel.Warning, Message = "El repositorio de {Destination} es de un formato superior (versión {FormatVersion}): solo lectura ({Detail}).")]
    private static partial void LogNewerFormat(ILogger logger, string destination, int? formatVersion, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "El repositorio de {Destination} está dañado: no se escribe nada ({Detail}).")]
    private static partial void LogCorrupt(ILogger logger, string destination, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se pudo conectar con el destino {Destination}: {Code} (Win32 {NativeError}).")]
    private static partial void LogConnectFailed(ILogger logger, string destination, BackboneErrorCode code, int nativeError);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Error de E/S en el destino {Destination}: {Code} ({ExceptionType}, HResult {HResult}).")]
    private static partial void LogIoFailed(ILogger logger, string destination, BackboneErrorCode code, string exceptionType, int hResult);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Hay {Max} preparaciones de repositorio en curso o abandonadas sin volver: no se prepara {Destination} y no se conecta.")]
    private static partial void LogBusyCore(ILogger logger, int max, string destination);

    private static void LogBusy(ILogger logger, string destination, int max) => LogBusyCore(logger, max, destination);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Windows no tiene el registro de uso de la conexión con el destino {Destination}: no se usa el destino (se escribiría con la cuenta del equipo).")]
    private static partial void LogUseRecordMissing(ILogger logger, string destination);

    [LoggerMessage(Level = LogLevel.Warning, Message = "El destino {Destination} no ha respondido en {Timeout}: se abandona la preparación del repositorio.")]
    private static partial void LogTimeout(ILogger logger, string destination, TimeSpan timeout);
}
