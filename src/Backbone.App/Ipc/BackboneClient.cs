using Backbone.Contracts;
using StreamJsonRpc;

namespace Backbone.App.Ipc;

/// <summary>Parámetros de reconexión (§9.1).</summary>
internal sealed class BackboneClientOptions
{
    /// <summary>Esperas entre intentos: 1 s, 2 s, 5 s, 10 s y después cada 10 s (la última se repite).</summary>
    public IReadOnlyList<TimeSpan> RetryDelays { get; init; } =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];

    /// <summary>
    /// Tras perder una conexión que funcionaba, mientras haya menos de este número de fallos seguidos el estado es
    /// "reconectando" (un reinicio del servicio dura unos segundos); después pasa a "servicio detenido".
    /// </summary>
    public int ReconnectingAttempts { get; init; } = 4;
}

/// <summary>
/// Cliente IPC de la app (§9.1): <b>una única conexión</b> <see cref="JsonRpc"/> compartida, con reconexión automática y estado
/// observable. Implementa <see cref="IBackboneService"/> reenviando a la conexión actual, de modo que los view models reciben
/// este objeto (o un doble) como <see cref="IBackboneService"/> y no necesitan saber de reconexiones.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Los eventos del servicio y <see cref="StateChanged"/> se entregan mediante el delegado <c>post</c> (en la app,
/// <c>DispatcherQueue.TryEnqueue</c>) para que lleguen al hilo de UI.</item>
/// <item>Una llamada sin conexión, o cuya conexión se pierde, lanza <see cref="ServiceUnavailableException"/>. Los errores
/// de negocio llegan como <see cref="RemoteInvocationException"/> (ver <see cref="ConnectionFailures.TryGetErrorCode"/>).</item>
/// <item>Tras reconectar, quien dependa de eventos debe volver a leer el estado (<see cref="IBackboneService.GetStatusAsync"/>):
/// los eventos emitidos mientras no había conexión no se recuperan.</item>
/// </list>
/// </remarks>
internal sealed class BackboneClient : IBackboneService, IAsyncDisposable
{
    private readonly IBackboneConnector _connector;
    private readonly TimeProvider _timeProvider;
    private readonly Action<Action> _post;
    private readonly BackboneClientOptions _options;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stopping = new();
    private IBackboneSession? _session;
    private ConnectionState _state = ConnectionState.Connecting;
    private ServiceInfo? _serviceInfo;
    private string? _incompatibleContractVersion;
    private TaskCompletionSource _retryNow = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _loop = Task.CompletedTask;
    private int _started;
    private int _disposed;

    public BackboneClient(
        IBackboneConnector connector,
        TimeProvider timeProvider,
        Action<Action>? post = null,
        BackboneClientOptions? options = null)
    {
        _connector = connector;
        _timeProvider = timeProvider;
        _post = post ?? (static action => action());
        _options = options ?? new();
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.RetryDelays.Count, 1);
    }

    public event EventHandler<StatusChangedEvent>? StatusChanged;

    public event EventHandler<RunProgressEvent>? RunProgress;

    public event EventHandler<RunCompletedEvent>? RunCompleted;

    public event EventHandler<RestoreProgressEvent>? RestoreProgress;

    public event EventHandler<RestoreConflictEvent>? RestoreConflict;

    /// <summary>Se produce en cada cambio de <see cref="State"/> (en el contexto de <c>post</c>).</summary>
    public event EventHandler<ConnectionState>? StateChanged;

    public ConnectionState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>Información de la conexión actual; <see langword="null"/> si no hay conexión.</summary>
    public ServiceInfo? ServiceInfo
    {
        get
        {
            lock (_gate)
            {
                return _serviceInfo;
            }
        }
    }

    /// <summary>Versión de contrato del servicio cuando <see cref="State"/> es <see cref="ConnectionState.IncompatibleContract"/>.</summary>
    public string? IncompatibleContractVersion
    {
        get
        {
            lock (_gate)
            {
                return _incompatibleContractVersion;
            }
        }
    }

    /// <summary>Empieza a conectar (y a reconectar). Idempotente.</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 0)
        {
            _loop = Task.Run(() => RunAsync(_stopping.Token), CancellationToken.None);
        }
    }

    /// <summary>Interrumpe la espera hasta el siguiente intento y reintenta ya (p. ej. tras "Iniciar servicio").</summary>
    public void RetryNow()
    {
        lock (_gate)
        {
            _retryNow.TrySetResult();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await _stopping.CancelAsync().ConfigureAwait(false);
        await _loop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _stopping.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var failures = 0;
        var hadSession = false;
        while (!ct.IsCancellationRequested)
        {
            IBackboneSession? session = null;
            try
            {
                session = await _connector.ConnectAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // Cualquier fallo de conexión se clasifica; el bucle de reconexión no debe morir.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                failures++;
                var failure = ConnectionFailures.Classify(ex);
                var version = (ex as ContractIncompatibleException)?.ServiceContractVersion;
                SetState(ConnectionFailures.ToState(failure, hadSession, failures, _options.ReconnectingAttempts), session: null, version);
            }

            if (session is not null)
            {
                failures = 0;
                hadSession = true;
                if (!await RunSessionAsync(session, ct).ConfigureAwait(false))
                {
                    return;
                }

                // La pérdida de la conexión cuenta como el primer fallo: la primera espera es de 1 s.
                failures = 1;
            }

            if (!await DelayAsync(failures, ct).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    /// <summary>Mantiene la sesión mientras viva. Devuelve <see langword="false"/> si se pidió parar.</summary>
    private async Task<bool> RunSessionAsync(IBackboneSession session, CancellationToken ct)
    {
        Subscribe(session.Service);
        SetState(ConnectionState.Connected, session, null);
        var stopped = false;
        try
        {
            await session.Completion.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            stopped = true;
        }
        finally
        {
            Unsubscribe(session.Service);
            lock (_gate)
            {
                _session = null;
                _serviceInfo = null;
            }

            await session.DisposeAsync().ConfigureAwait(false);
        }

        if (!stopped)
        {
            SetState(ConnectionState.Reconnecting, session: null, incompatibleVersion: null);
        }

        return !stopped;
    }

    /// <summary>Espera el tiempo que toca tras <paramref name="failures"/> fallos seguidos, o hasta <see cref="RetryNow"/>.</summary>
    private async Task<bool> DelayAsync(int failures, CancellationToken ct)
    {
        var index = Math.Min(Math.Max(failures - 1, 0), _options.RetryDelays.Count - 1);
        Task retry;
        lock (_gate)
        {
            _retryNow = new(TaskCreationOptions.RunContinuationsAsynchronously);
            retry = _retryNow.Task;
        }

        using var cancelDelay = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var delay = Task.Delay(_options.RetryDelays[index], _timeProvider, cancelDelay.Token);
        await Task.WhenAny(delay, retry).ConfigureAwait(false);
        await cancelDelay.CancelAsync().ConfigureAwait(false);
        await delay.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        return !ct.IsCancellationRequested;
    }

    private void SetState(ConnectionState state, IBackboneSession? session, string? incompatibleVersion)
    {
        bool changed;
        lock (_gate)
        {
            _session = session;
            _serviceInfo = session?.Info;
            _incompatibleContractVersion = incompatibleVersion;
            changed = _state != state;
            _state = state;
        }

        if (changed)
        {
            _post(() => StateChanged?.Invoke(this, state));
        }
    }

    private void Subscribe(IBackboneService service)
    {
        service.StatusChanged += OnStatusChanged;
        service.RunProgress += OnRunProgress;
        service.RunCompleted += OnRunCompleted;
        service.RestoreProgress += OnRestoreProgress;
        service.RestoreConflict += OnRestoreConflict;
    }

    private void Unsubscribe(IBackboneService service)
    {
        service.StatusChanged -= OnStatusChanged;
        service.RunProgress -= OnRunProgress;
        service.RunCompleted -= OnRunCompleted;
        service.RestoreProgress -= OnRestoreProgress;
        service.RestoreConflict -= OnRestoreConflict;
    }

    private void OnStatusChanged(object? sender, StatusChangedEvent e) => _post(() => StatusChanged?.Invoke(this, e));

    private void OnRunProgress(object? sender, RunProgressEvent e) => _post(() => RunProgress?.Invoke(this, e));

    private void OnRunCompleted(object? sender, RunCompletedEvent e) => _post(() => RunCompleted?.Invoke(this, e));

    private void OnRestoreProgress(object? sender, RestoreProgressEvent e) => _post(() => RestoreProgress?.Invoke(this, e));

    private void OnRestoreConflict(object? sender, RestoreConflictEvent e) => _post(() => RestoreConflict?.Invoke(this, e));

    private IBackboneService Current
    {
        get
        {
            lock (_gate)
            {
                return _session?.Service ?? throw new ServiceUnavailableException(_state);
            }
        }
    }

    private async Task<T> CallAsync<T>(Func<IBackboneService, Task<T>> call)
    {
        try
        {
            return await call(Current).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ConnectionLostException or ObjectDisposedException or EndOfStreamException)
        {
            throw new ServiceUnavailableException("The connection with the service was lost during the call.", ex);
        }
    }

    private async Task CallAsync(Func<IBackboneService, Task> call)
    {
        try
        {
            await call(Current).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ConnectionLostException or ObjectDisposedException or EndOfStreamException)
        {
            throw new ServiceUnavailableException("The connection with the service was lost during the call.", ex);
        }
    }

    // ---- IBackboneService: reenvío a la conexión actual ----

    public Task<ServiceInfo> GetServiceInfoAsync(CancellationToken ct = default) => CallAsync(s => s.GetServiceInfoAsync(ct));

    public Task<ServiceStatus> GetStatusAsync(CancellationToken ct = default) => CallAsync(s => s.GetStatusAsync(ct));

    public Task<IReadOnlyList<JobDefinition>> GetJobsAsync(CancellationToken ct = default) => CallAsync(s => s.GetJobsAsync(ct));

    public Task<JobDefinition> SaveJobAsync(JobDefinition job, CancellationToken ct = default) => CallAsync(s => s.SaveJobAsync(job, ct));

    public Task DeleteJobAsync(Guid jobId, CancellationToken ct = default) => CallAsync(s => s.DeleteJobAsync(jobId, ct));

    public Task<ValidationResult> ValidateJobAsync(JobDefinition job, CancellationToken ct = default) => CallAsync(s => s.ValidateJobAsync(job, ct));

    public Task RunNowAsync(Guid jobId, CancellationToken ct = default) => CallAsync(s => s.RunNowAsync(jobId, ct));

    public Task CancelRunAsync(Guid jobId, CancellationToken ct = default) => CallAsync(s => s.CancelRunAsync(jobId, ct));

    public Task SetPauseAsync(PauseRequest request, CancellationToken ct = default) => CallAsync(s => s.SetPauseAsync(request, ct));

    public Task SaveCredentialAsync(string shareRoot, CredentialInput credential, CancellationToken ct = default) =>
        CallAsync(s => s.SaveCredentialAsync(shareRoot, credential, ct));

    public Task<CredentialInfo?> GetCredentialInfoAsync(string shareRoot, CancellationToken ct = default) =>
        CallAsync(s => s.GetCredentialInfoAsync(shareRoot, ct));

    public Task DeleteCredentialAsync(string shareRoot, CancellationToken ct = default) => CallAsync(s => s.DeleteCredentialAsync(shareRoot, ct));

    public Task<ConnectionTestResult> TestConnectionAsync(string destination, CredentialInput? credential, CancellationToken ct = default) =>
        CallAsync(s => s.TestConnectionAsync(destination, credential, ct));

    public Task<StorageInfo> GetStorageInfoAsync(Guid jobId, CancellationToken ct = default) => CallAsync(s => s.GetStorageInfoAsync(jobId, ct));

    public Task<HistoryPage> GetHistoryAsync(HistoryQuery query, CancellationToken ct = default) => CallAsync(s => s.GetHistoryAsync(query, ct));

    public Task<RunDetail> GetRunDetailAsync(long runId, CancellationToken ct = default) => CallAsync(s => s.GetRunDetailAsync(runId, ct));

    public Task<IReadOnlyList<SnapshotInfo>> GetSnapshotsAsync(Guid jobId, CancellationToken ct = default) => CallAsync(s => s.GetSnapshotsAsync(jobId, ct));

    public Task<SnapshotItemPage> BrowseSnapshotAsync(Guid jobId, string snapshotId, string path, int offset, int limit, CancellationToken ct = default) =>
        CallAsync(s => s.BrowseSnapshotAsync(jobId, snapshotId, path, offset, limit, ct));

    public Task<IReadOnlyList<SnapshotItem>> SearchSnapshotAsync(Guid jobId, string snapshotId, string text, int limit, CancellationToken ct = default) =>
        CallAsync(s => s.SearchSnapshotAsync(jobId, snapshotId, text, limit, ct));

    public Task<IReadOnlyList<FileVersion>> GetFileVersionsAsync(Guid jobId, string path, CancellationToken ct = default) =>
        CallAsync(s => s.GetFileVersionsAsync(jobId, path, ct));

    public Task<Guid> StartRestoreAsync(RestoreRequest request, CancellationToken ct = default) => CallAsync(s => s.StartRestoreAsync(request, ct));

    public Task ResolveRestoreConflictAsync(Guid restoreId, ConflictResolution resolution, bool applyToAll, CancellationToken ct = default) =>
        CallAsync(s => s.ResolveRestoreConflictAsync(restoreId, resolution, applyToAll, ct));

    public Task CancelRestoreAsync(Guid restoreId, CancellationToken ct = default) => CallAsync(s => s.CancelRestoreAsync(restoreId, ct));

    public Task<RetentionResult> ApplyRetentionAsync(Guid jobId, bool dryRun, CancellationToken ct = default) =>
        CallAsync(s => s.ApplyRetentionAsync(jobId, dryRun, ct));

    public Task StartPruneAsync(Guid jobId, CancellationToken ct = default) => CallAsync(s => s.StartPruneAsync(jobId, ct));

    public Task StartCheckAsync(Guid jobId, bool full, CancellationToken ct = default) => CallAsync(s => s.StartCheckAsync(jobId, full, ct));

    public Task<ServiceSettings> GetSettingsAsync(CancellationToken ct = default) => CallAsync(s => s.GetSettingsAsync(ct));

    public Task SaveSettingsAsync(ServiceSettings settings, CancellationToken ct = default) => CallAsync(s => s.SaveSettingsAsync(settings, ct));

    public Task<IReadOnlyList<AuthorizedUser>> GetAuthorizedUsersAsync(CancellationToken ct = default) => CallAsync(s => s.GetAuthorizedUsersAsync(ct));

    public Task<AuthorizedUser> AddAuthorizedUserAsync(string account, CancellationToken ct = default) => CallAsync(s => s.AddAuthorizedUserAsync(account, ct));

    public Task RemoveAuthorizedUserAsync(string sid, CancellationToken ct = default) => CallAsync(s => s.RemoveAuthorizedUserAsync(sid, ct));

    public Task<DiagnosticsExportResult> ExportDiagnosticsAsync(DiagnosticsExportRequest request, CancellationToken ct = default) =>
        CallAsync(s => s.ExportDiagnosticsAsync(request, ct));
}
