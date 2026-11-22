using Backbone.Contracts;
using Backbone.Service.Ipc;
using Backbone.Service.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Backbone.Service.Jobs;

/// <summary>
/// Cola de ejecuciones (RF-15, RF-20, RF-21, HU-14): <b>una sola ejecución a la vez en todo el servicio</b> y las demás en
/// espera, en orden de llegada. Un trabajo no puede estar dos veces entre la ejecución en curso y la cola.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><see cref="Enqueue"/> es lo que llama "Copiar ahora" (<see cref="RunTrigger.Manual"/>) y lo que llamará el scheduler
/// (HU-09) con <see cref="RunTrigger.Scheduled"/> y <see cref="RunTrigger.CatchUp"/>, después de comprobar las condiciones
/// (paso 1 de §8.4). Un trabajo ya en curso o en cola da <see cref="BackboneErrorCode.JobAlreadyRunning"/>: no se duplica.</item>
/// <item><see cref="Cancel"/> es idempotente. Cancelar una ejecución en curso cancela su <see cref="CancellationToken"/>: el
/// motor para entre archivos y bloques de 1 MiB, no se publica snapshot y <see cref="JobRunner"/> libera todo (paso 9). Cancelar
/// una en cola la quita y la termina como <see cref="RunState.Cancelled"/> sin haber empezado.</item>
/// <item>Al parar el servicio se cancela la ejecución en curso (<see cref="RunState.Cancelled"/>) y se descarta la cola.</item>
/// <item>Avisa del progreso (<see cref="Progress"/>, que el publicador limita a 4 por segundo), de cada ejecución terminada
/// (<see cref="Completed"/>) y de los cambios de la cola y del estado (<see cref="Changed"/>); <see cref="RunEventBroadcaster"/> y
/// <c>StatusBroadcaster</c> los envían a los clientes como <c>RunProgress</c>, <c>RunCompleted</c> y <c>StatusChanged</c>. Así la
/// cola no depende del servidor IPC, que a su vez depende de las operaciones que la usan.</item>
/// <item>El historial persistente y el Visor de eventos (paso 10 de §8.4, HU-21 y HU-22) se enganchan con
/// <see cref="IRunCompletionHandler"/>; hasta entonces, la última ejecución de cada trabajo solo vive en memoria.</item>
/// </list>
/// </remarks>
internal sealed partial class RunQueue : IJobActivity, IRunStatusSource, IHostedService, IDisposable
{
    private readonly IJobList _jobs;
    private readonly JobRunner _runner;
    private readonly TimeProvider _timeProvider;
    private readonly IReadOnlyList<IRunCompletionHandler> _handlers;
    private readonly ILogger<RunQueue> _logger;

    private readonly Lock _gate = new();
    private readonly List<Entry> _queue = [];
    private readonly Dictionary<Guid, RunRecord> _lastRuns = [];
    private readonly Dictionary<Guid, DateTimeOffset> _lastSuccess = [];
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _stopping = new();
    private Entry? _current;
    private RunProgressEvent? _progress;
    private long _lastRunId;
    private Task? _loop;
    private bool _disposed;

    public RunQueue(
        IJobList jobs,
        JobRunner runner,
        TimeProvider timeProvider,
        IEnumerable<IRunCompletionHandler> handlers,
        ILogger<RunQueue> logger)
    {
        _jobs = jobs;
        _runner = runner;
        _timeProvider = timeProvider;
        _handlers = [.. handlers];
        _logger = logger;
    }

    /// <summary>
    /// Lo que <see cref="StopAsync"/> espera a que el ejecutor termine y libere. Es menor que los 20 s del apagado del host
    /// (§8.1) para que <see cref="Nas.ShareConnectionShutdown"/> conserve margen para cerrar a la fuerza lo que siga colgado
    /// (revisión de seguridad de HU-14, M-A).
    /// </summary>
    internal TimeSpan StopWait { get; init; } = TimeSpan.FromSeconds(10);

    public event EventHandler? Changed;

    /// <summary>Progreso de la ejecución en curso (<see cref="RunEventBroadcaster"/> lo envía como <c>RunProgress</c>).</summary>
    public event EventHandler<RunProgressEvent>? Progress;

    /// <summary>Una ejecución ha terminado en cualquier estado (<see cref="RunEventBroadcaster"/> lo envía como <c>RunCompleted</c>).</summary>
    public event EventHandler<RunCompletedEvent>? Completed;

    public RunProgressEvent? CurrentRun
    {
        get
        {
            lock (_gate)
            {
                return _progress;
            }
        }
    }

    public IReadOnlyList<QueuedRun> Queue
    {
        get
        {
            lock (_gate)
            {
                return [.. _queue.Select(e => new QueuedRun { JobId = e.JobId, Kind = e.Kind, Trigger = e.Trigger, QueuedUtc = e.QueuedUtc })];
            }
        }
    }

    public RunRecord? LastRun(Guid jobId)
    {
        lock (_gate)
        {
            return _lastRuns.GetValueOrDefault(jobId);
        }
    }

    public DateTimeOffset? LastSuccessUtc(Guid jobId)
    {
        lock (_gate)
        {
            return _lastSuccess.TryGetValue(jobId, out var at) ? at : null;
        }
    }

    public JobActivity GetActivity(Guid jobId)
    {
        lock (_gate)
        {
            if (_current?.JobId == jobId)
            {
                return JobActivity.Running;
            }

            return _queue.Any(e => e.JobId == jobId) ? JobActivity.Queued : JobActivity.Idle;
        }
    }

    /// <summary>
    /// Pone una copia del trabajo en la cola (o la lanza ya, si no hay otra en curso). No espera a que termine.
    /// </summary>
    /// <returns>El identificador de la ejecución.</returns>
    /// <exception cref="BackboneException">
    /// <see cref="BackboneErrorCode.JobNotFound"/>; <see cref="BackboneErrorCode.JobAlreadyRunning"/> si el trabajo ya está en
    /// curso o en cola; <see cref="BackboneErrorCode.Interrupted"/> si el servicio se está parando.
    /// </exception>
    public long Enqueue(Guid jobId, RunTrigger trigger)
    {
        long runId;
        lock (_gate)
        {
            if (_disposed || _stopping.IsCancellationRequested)
            {
                throw new BackboneException(BackboneErrorCode.Interrupted, "The service is stopping.");
            }

            if (!_jobs.Jobs.Any(j => j.Id == jobId))
            {
                throw new BackboneException(BackboneErrorCode.JobNotFound, "The job does not exist.");
            }

            if (_current?.JobId == jobId || _queue.Any(e => e.JobId == jobId))
            {
                throw new BackboneException(BackboneErrorCode.JobAlreadyRunning, "The job is already running or queued.");
            }

            // La numeración la sustituirá el historial (HU-21); hasta entonces es de este proceso.
            runId = ++_lastRunId;
            _queue.Add(new Entry(runId, jobId, RunKind.Backup, trigger, _timeProvider.GetUtcNow()));
        }

        LogQueued(_logger, runId, jobId, trigger);
        _signal.Release();
        RaiseChanged();
        return runId;
    }

    /// <summary>
    /// Cancela la ejecución del trabajo, en curso o en cola. Idempotente: sin ninguna, no hace nada. No espera a que la
    /// ejecución termine de liberar sus recursos (la termina el ejecutor y emite <c>RunCompleted</c>).
    /// </summary>
    /// <exception cref="BackboneException"><see cref="BackboneErrorCode.JobNotFound"/> si el trabajo no existe.</exception>
    public void Cancel(Guid jobId)
    {
        Entry? queued = null;
        CancellationTokenSource? running = null;
        lock (_gate)
        {
            if (!_jobs.Jobs.Any(j => j.Id == jobId))
            {
                throw new BackboneException(BackboneErrorCode.JobNotFound, "The job does not exist.");
            }

            if (_current is { } current && current.JobId == jobId)
            {
                running = current.Cancellation;
            }
            else if (_queue.FindIndex(e => e.JobId == jobId) is >= 0 and var index)
            {
                queued = _queue[index];
                _queue.RemoveAt(index);
            }
        }

        if (running is not null)
        {
            LogCancelRequested(_logger, jobId);
            try
            {
                running.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // La ejecución terminó justo ahora: no hay nada que cancelar.
            }
        }
        else if (queued is not null)
        {
            LogCancelledInQueue(_logger, queued.RunId, jobId);
            _ = CompleteQueuedAsync(queued);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Start();
        return Task.CompletedTask;
    }

    /// <summary>Arranca el ejecutor (idempotente).</summary>
    public void Start()
    {
        lock (_gate)
        {
            _loop ??= Task.Run(() => LoopAsync(_stopping.Token), CancellationToken.None);
        }
    }

    /// <summary>
    /// Cancela la ejecución en curso, descarta la cola y espera a que el ejecutor libere todo (paso 9). Tiene que ir antes que
    /// el cierre del gestor de conexiones (<see cref="Nas.ShareConnectionShutdown"/>), que cierra a la fuerza lo que quede.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task? loop;
        lock (_gate)
        {
            _queue.Clear();
            loop = _loop;
        }

        await _stopping.CancelAsync().ConfigureAwait(false);
        if (loop is not null)
        {
            try
            {
                await loop.WaitAsync(StopWait, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                LogStopTimedOut(_logger);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _stopping.Cancel();
        if (_loop is null || _loop.IsCompleted)
        {
            _signal.Dispose();
            _stopping.Dispose();
        }
    }

    private async Task LoopAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            Entry entry;
            lock (_gate)
            {
                if (_queue.Count == 0)
                {
                    continue; // la que despertó al ejecutor se canceló mientras esperaba
                }

                entry = _queue[0];
                _queue.RemoveAt(0);
                entry.Cancellation = CancellationTokenSource.CreateLinkedTokenSource(stop);
                _current = entry;
                _progress = null;
            }

            RaiseChanged();
            try
            {
                await RunEntryAsync(entry).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // El ejecutor no puede morir: sin él no habría más copias hasta reiniciar el servicio.
                LogLoopFailed(_logger, ex, entry.RunId);
            }
            finally
            {
                lock (_gate)
                {
                    _current = null;
                    _progress = null;
                }

                entry.Cancellation?.Dispose();
                RaiseChanged();
            }
        }
    }

    private async Task RunEntryAsync(Entry entry)
    {
        var job = _jobs.Jobs.FirstOrDefault(j => j.Id == entry.JobId);
        RunOutcome outcome;
        if (job is null)
        {
            // DeleteJobAsync no borra un trabajo en curso ni en cola; solo una carrera podría llegar aquí.
            outcome = new RunOutcome(
                new RunRecord
                {
                    RunId = entry.RunId,
                    JobId = entry.JobId,
                    Kind = entry.Kind,
                    Trigger = entry.Trigger,
                    State = RunState.Failed,
                    StartedUtc = _timeProvider.GetUtcNow(),
                    CompletedUtc = _timeProvider.GetUtcNow(),
                    ErrorCode = BackboneErrorCode.JobNotFound,
                    ErrorMessage = "The job no longer exists.",
                },
                []);
        }
        else
        {
            try
            {
                outcome = await _runner.RunAsync(
                    new RunRequest(entry.RunId, job, entry.Kind, entry.Trigger, entry.QueuedUtc, OnProgress),
                    entry.Cancellation!.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // JobRunner devuelve los fallos de la ejecución como resultado; esto es un error de programación.
                LogRunnerFailed(_logger, ex, entry.RunId);
                outcome = new RunOutcome(
                    new RunRecord
                    {
                        RunId = entry.RunId,
                        JobId = job.Id,
                        JobName = job.Name,
                        Kind = entry.Kind,
                        Trigger = entry.Trigger,
                        State = RunState.Failed,
                        StartedUtc = _timeProvider.GetUtcNow(),
                        CompletedUtc = _timeProvider.GetUtcNow(),
                        ErrorCode = BackboneErrorCode.Internal,
                        ErrorMessage = "Unexpected error.",
                    },
                    []);
            }
            catch (OperationCanceledException)
            {
                outcome = new RunOutcome(
                    new RunRecord
                    {
                        RunId = entry.RunId,
                        JobId = job.Id,
                        JobName = job.Name,
                        Kind = entry.Kind,
                        Trigger = entry.Trigger,
                        State = RunState.Cancelled,
                        StartedUtc = _timeProvider.GetUtcNow(),
                        CompletedUtc = _timeProvider.GetUtcNow(),
                    },
                    []);
            }
        }

        await PublishCompletedAsync(outcome).ConfigureAwait(false);
    }

    /// <summary>Una ejecución en cola cancelada: no llegó a empezar, pero queda constancia y los clientes la ven terminar.</summary>
    private async Task CompleteQueuedAsync(Entry entry)
    {
        var job = _jobs.Jobs.FirstOrDefault(j => j.Id == entry.JobId);
        var now = _timeProvider.GetUtcNow();
        var outcome = new RunOutcome(
            new RunRecord
            {
                RunId = entry.RunId,
                JobId = entry.JobId,
                JobName = job?.Name ?? string.Empty,
                Kind = entry.Kind,
                Trigger = entry.Trigger,
                State = RunState.Cancelled,
                StartedUtc = now,
                CompletedUtc = now,
            },
            []);
        try
        {
            await PublishCompletedAsync(outcome).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogLoopFailed(_logger, ex, entry.RunId);
        }

        RaiseChanged();
    }

    private async Task PublishCompletedAsync(RunOutcome outcome)
    {
        var record = outcome.Record;
        lock (_gate)
        {
            _lastRuns[record.JobId] = record;
            if (record.State is RunState.Completed or RunState.CompletedWithWarnings && record.CompletedUtc is { } completed)
            {
                _lastSuccess[record.JobId] = completed;
            }
        }

        foreach (var handler in _handlers)
        {
            try
            {
                await handler.OnRunCompletedAsync(outcome, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogHandlerFailed(_logger, ex, handler.GetType().Name, record.RunId);
            }
        }

        Raise(Completed, new RunCompletedEvent { Run = record });
    }

    private void OnProgress(RunProgressEvent progress)
    {
        lock (_gate)
        {
            _progress = progress;
        }

        Raise(Progress, progress);
    }

    private void Raise<T>(EventHandler<T>? handlers, T args)
    {
        try
        {
            handlers?.Invoke(this, args);
        }
        catch (Exception ex)
        {
            LogChangedFailed(_logger, ex);
        }
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            LogChangedFailed(_logger, ex);
        }
    }

    private sealed class Entry(long runId, Guid jobId, RunKind kind, RunTrigger trigger, DateTimeOffset queuedUtc)
    {
        public long RunId { get; } = runId;

        public Guid JobId { get; } = jobId;

        public RunKind Kind { get; } = kind;

        public RunTrigger Trigger { get; } = trigger;

        public DateTimeOffset QueuedUtc { get; } = queuedUtc;

        /// <summary>Cancelación de la ejecución en curso (protegida por el cerrojo de la cola hasta que se publica).</summary>
        public CancellationTokenSource? Cancellation { get; set; }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Ejecución {RunId} del trabajo {JobId} en cola ({Trigger}).")]
    private static partial void LogQueued(ILogger logger, long runId, Guid jobId, RunTrigger trigger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Cancelación pedida para la ejecución en curso del trabajo {JobId}.")]
    private static partial void LogCancelRequested(ILogger logger, Guid jobId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Ejecución {RunId} del trabajo {JobId} cancelada en la cola, sin haber empezado.")]
    private static partial void LogCancelledInQueue(ILogger logger, long runId, Guid jobId);

    [LoggerMessage(Level = LogLevel.Error, Message = "El ejecutor de copias ha tenido un error al tratar la ejecución {RunId}; sigue con la siguiente.")]
    private static partial void LogLoopFailed(ILogger logger, Exception exception, long runId);

    [LoggerMessage(Level = LogLevel.Error, Message = "JobRunner ha lanzado una excepción en la ejecución {RunId}; se da como fallida.")]
    private static partial void LogRunnerFailed(ILogger logger, Exception exception, long runId);

    [LoggerMessage(Level = LogLevel.Error, Message = "El manejador {Handler} ha fallado al registrar la ejecución {RunId}.")]
    private static partial void LogHandlerFailed(ILogger logger, Exception exception, string handler, long runId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Un suscriptor de los cambios de la cola ha fallado.")]
    private static partial void LogChangedFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "La parada del servicio no esperó a que terminara la ejecución en curso.")]
    private static partial void LogStopTimedOut(ILogger logger);
}

/// <summary>
/// Envía a todos los clientes el progreso y el final de las ejecuciones de <see cref="RunQueue"/> (<c>RunProgress</c>, como
/// mucho 4 por segundo, y <c>RunCompleted</c>, §7.2).
/// </summary>
internal sealed class RunEventBroadcaster(RunQueue runs, IIpcEventPublisher events) : IHostedService, IDisposable
{
    private int _subscribed;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Subscribe();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Unsubscribe();
        return Task.CompletedTask;
    }

    public void Dispose() => Unsubscribe();

    /// <summary>Empieza a escuchar (idempotente).</summary>
    public void Subscribe()
    {
        if (Interlocked.Exchange(ref _subscribed, 1) == 0)
        {
            runs.Progress += OnProgress;
            runs.Completed += OnCompleted;
        }
    }

    private void Unsubscribe()
    {
        if (Interlocked.Exchange(ref _subscribed, 0) == 1)
        {
            runs.Progress -= OnProgress;
            runs.Completed -= OnCompleted;
        }
    }

    private void OnProgress(object? sender, RunProgressEvent e) => events.PublishRunProgress(e);

    private void OnCompleted(object? sender, RunCompletedEvent e) => events.PublishRunCompleted(e);
}
