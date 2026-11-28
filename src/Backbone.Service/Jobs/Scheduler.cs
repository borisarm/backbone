using System.Collections.Immutable;
using Backbone.Contracts;
using Backbone.Service.Ipc;
using Backbone.Service.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Backbone.Service.Jobs;

/// <summary>Lo que el scheduler pide a la cola de ejecuciones (<see cref="RunQueue"/>); las pruebas lo sustituyen.</summary>
internal interface IRunEnqueuer
{
    /// <summary>Pone el trabajo en la cola de ejecuciones.</summary>
    /// <exception cref="BackboneException">
    /// <see cref="BackboneErrorCode.JobAlreadyRunning"/> si ya está en curso o en cola, <see cref="BackboneErrorCode.JobNotFound"/>,
    /// <see cref="BackboneErrorCode.Interrupted"/> si el servicio se está parando.
    /// </exception>
    long Enqueue(Guid jobId, RunTrigger trigger);
}

/// <summary>Próxima ejecución programada de cada trabajo, calculada por el <see cref="Scheduler"/> (<c>JobStatus.NextRunUtc</c>).</summary>
internal interface INextRunSource
{
    /// <summary>Próximo punto de programación (UTC); <see langword="null"/> si el trabajo es manual, está desactivado o no existe.</summary>
    DateTimeOffset? NextRunUtc(Guid jobId);

    /// <summary>Algún próximo punto ha cambiado (<c>StatusChanged</c>).</summary>
    event EventHandler? Changed;
}

/// <summary>
/// Ajustes que afectan a la programación (§8.2): el periodo de gracia de las ejecuciones perdidas
/// (<see cref="ServiceSettings.MissedRunGracePeriod"/>, RF-11) y la pausa global (F2, RF-12).
/// </summary>
internal interface IScheduleSettings
{
    /// <summary>Periodo de gracia antes de recuperar una ejecución perdida.</summary>
    TimeSpan MissedRunGracePeriod { get; }

    /// <summary>Pausa global activa: no se encola nada programado.</summary>
    bool IsPaused { get; }

    /// <summary>Algún ajuste ha cambiado: el scheduler recalcula al momento.</summary>
    event EventHandler? Changed;
}

/// <summary>
/// Ajustes de programación en memoria. Hasta que existan <c>GetSettingsAsync</c>/<c>SaveSettingsAsync</c> y la pausa, valen los
/// valores por defecto (5 minutos, sin pausa); quien los implemente llama a <see cref="Update"/>.
/// </summary>
internal sealed class ScheduleSettings : IScheduleSettings
{
    private readonly Lock _gate = new();
    private TimeSpan _grace = ServiceSettingsDefaults.MissedRunGracePeriod;
    private bool _paused;

    public event EventHandler? Changed;

    public TimeSpan MissedRunGracePeriod
    {
        get
        {
            lock (_gate)
            {
                return _grace;
            }
        }
    }

    public bool IsPaused
    {
        get
        {
            lock (_gate)
            {
                return _paused;
            }
        }
    }

    /// <summary>Cambia los ajustes y avisa al scheduler si alguno es distinto.</summary>
    public void Update(TimeSpan missedRunGracePeriod, bool paused)
    {
        lock (_gate)
        {
            if (_grace == missedRunGracePeriod && _paused == paused)
            {
                return;
            }

            _grace = missedRunGracePeriod;
            _paused = paused;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>
/// Programación de las copias (HU-09, RF-10, RF-11, RF-15; §8.2 "Programación"): calcula el próximo punto de cada trabajo
/// activado, lo encola en <see cref="RunQueue"/> cuando vence y recupera las ejecuciones perdidas.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Un único bucle con un temporizador de <see cref="TimeProvider"/> que despierta en el próximo punto más cercano y, como
/// mucho, cada <see cref="MaxWait"/>; compara siempre con el reloj, así que la suspensión, la reanudación y los cambios de hora
/// se detectan sin eventos. Cambiar <c>jobs.json</c> o los ajustes despierta el bucle al momento.</item>
/// <item>Puntual (retraso menor de <see cref="OnTimeWindow"/>): se encola con <see cref="RunTrigger.Scheduled"/>. Perdido (más
/// tarde, o cualquier punto vencido al arrancar): con <see cref="JobSchedule.CatchUpMissedRuns"/> se encola <b>una sola</b>
/// ejecución por trabajo con <see cref="RunTrigger.CatchUp"/> cuando pasa el periodo de gracia desde que se detecta; sin ella
/// se consume sin ejecutar. Con la pausa activa no se encola nada: los puntos vencidos cuentan como perdidos al reanudar.</item>
/// <item>Un punto se consume al encolarlo, al descartarlo porque el trabajo ya está en cola o en curso, o al saltarlo (sin
/// recuperación). Se guarda en <c>data\schedule.json</c> <b>después</b> de encolar: si el servicio muriera entre medias, la
/// ejecución se recuperaría de más, nunca se perdería. Los puntos perdidos pendientes de recuperar no se consumen hasta que
/// la recuperación se encola.</item>
/// <item>Un trabajo nuevo, que se activa o cuya programación cambia (tipo, horas, días, intervalo) empieza desde "ahora": se
/// guarda "ahora" como punto de partida (así un reinicio no lo retrasa ni inventa ejecuciones perdidas). Los trabajos
/// manuales, desactivados o eliminados no tienen entrada.</item>
/// <item>El estado interno solo lo toca el bucle (y <see cref="EvaluateAsync"/>, que llaman las pruebas sin bucle); lo que ven
/// los demás (<see cref="NextRunUtc"/>) es una instantánea inmutable.</item>
/// </list>
/// </remarks>
internal sealed partial class Scheduler(
    IJobList jobs,
    IRunEnqueuer runs,
    IScheduleSettings settings,
    ScheduleStore store,
    TimeProvider timeProvider,
    ILogger<Scheduler> logger) : INextRunSource, IHostedService, IDisposable
{
    /// <summary>Un punto vencido se encola en el acto si se detecta con menos retraso que esto; si no, es una ejecución perdida.</summary>
    internal static readonly TimeSpan OnTimeWindow = TimeSpan.FromMinutes(2);

    /// <summary>El bucle despierta como mucho cada tanto, para notar la suspensión y los cambios de hora.</summary>
    internal static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(1);

    /// <summary>Tope de puntos que se recorren de una pasada (p. ej. tras saltar el reloj años hacia delante).</summary>
    private const int MaxPointsPerPass = 100_000;

    private readonly Dictionary<Guid, Plan> _plans = [];
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _gate = new();
    private Dictionary<Guid, DateTimeOffset> _entries = [];
    private ImmutableDictionary<Guid, DateTimeOffset> _published = ImmutableDictionary<Guid, DateTimeOffset>.Empty;
    private CancellationTokenSource? _wake;
    private bool _dirty;
    private bool _started;
    private bool _disposed;
    private Task? _loop;

    public event EventHandler? Changed;

    public DateTimeOffset? NextRunUtc(Guid jobId) => _published.TryGetValue(jobId, out var next) ? next : null;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var delay = await InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (delay is null)
        {
            return;
        }

        var stop = _stopping.Token;
        _loop = Task.Run(() => LoopAsync(delay.Value, stop), CancellationToken.None);
    }

    /// <summary>
    /// Lee <c>schedule.json</c>, empieza a escuchar los cambios y hace la primera pasada, con el servicio recién arrancado: todo
    /// punto vencido es una ejecución perdida (RF-11). Sin bucle: lo arranca <see cref="StartAsync"/>; las pruebas lo usan
    /// para llamar a <see cref="EvaluateAsync"/> a mano.
    /// </summary>
    /// <returns>Lo que hay que esperar hasta la siguiente pasada; <see langword="null"/> si ya estaba inicializado.</returns>
    internal async Task<TimeSpan?> InitializeAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_started)
            {
                return null;
            }

            _started = true;
        }

        _entries = await store.LoadAsync(ct).ConfigureAwait(false);
        jobs.Changed += OnConfigurationChanged;
        settings.Changed += OnConfigurationChanged;
        return await EvaluateAsync(startup: true, ct).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        jobs.Changed -= OnConfigurationChanged;
        settings.Changed -= OnConfigurationChanged;
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try
            {
                await _loop.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // La parada del host se agotó; el bucle ya tiene la cancelación y terminará solo.
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

        jobs.Changed -= OnConfigurationChanged;
        settings.Changed -= OnConfigurationChanged;
        _stopping.Cancel();
        if (_loop is null || _loop.IsCompleted)
        {
            _stopping.Dispose();
        }
    }

    /// <summary>
    /// Una pasada: sincroniza los planes con <c>jobs.json</c>, encola lo que vence, aplica la recuperación de lo perdido,
    /// guarda <c>schedule.json</c> y publica los próximos puntos. Lo llama el bucle; las pruebas la llaman directamente. No es
    /// reentrante.
    /// </summary>
    /// <param name="startup">Primera pasada tras arrancar el servicio: todo punto vencido es perdido.</param>
    /// <returns>Cuánto esperar hasta la próxima pasada (como mucho <see cref="MaxWait"/>).</returns>
    internal async Task<TimeSpan> EvaluateAsync(bool startup, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().ToUniversalTime();
        var zone = timeProvider.LocalTimeZone;
        var paused = settings.IsPaused;
        var grace = settings.MissedRunGracePeriod;
        var entriesChanged = false;

        var live = new HashSet<Guid>();
        foreach (var job in jobs.Jobs)
        {
            if (!job.Enabled || job.Schedule.Kind == ScheduleKind.Manual)
            {
                continue;
            }

            live.Add(job.Id);
            if (!_plans.TryGetValue(job.Id, out var plan) || !plan.SameShape(job.Schedule))
            {
                DateTimeOffset cursor;
                if (plan is null && startup && _entries.TryGetValue(job.Id, out var saved))
                {
                    // Un punto guardado en el futuro (el reloj se retrasó) no puede retrasar la programación.
                    cursor = saved > now ? now : saved;
                    if (saved > now)
                    {
                        _entries[job.Id] = now;
                        entriesChanged = true;
                    }
                }
                else
                {
                    // Trabajo nuevo, activado ahora o con otra programación: empieza desde "ahora" (sin puntos perdidos).
                    cursor = now;
                    _entries[job.Id] = now;
                    entriesChanged = true;
                }

                plan = new Plan(job.Schedule, cursor);
                _plans[job.Id] = plan;
            }

            plan.Schedule = job.Schedule;
            entriesChanged |= await AdvanceAsync(job, plan, now, zone, startup, paused, grace, ct).ConfigureAwait(false);
        }

        foreach (var gone in _plans.Keys.Where(id => !live.Contains(id)).ToList())
        {
            _plans.Remove(gone);
        }

        foreach (var gone in _entries.Keys.Where(id => !live.Contains(id)).ToList())
        {
            _entries.Remove(gone);
            entriesChanged = true;
        }

        if (entriesChanged)
        {
            await SaveEntriesAsync(ct).ConfigureAwait(false);
        }

        Publish();
        return NextWake(now, grace);
    }

    /// <returns><see langword="true"/> si ha cambiado alguna entrada de <c>schedule.json</c>.</returns>
    private async Task<bool> AdvanceAsync(
        JobDefinition job, Plan plan, DateTimeOffset now, TimeZoneInfo zone, bool startup, bool paused, TimeSpan grace, CancellationToken ct)
    {
        var changed = false;
        var schedule = plan.Schedule;
        var next = ScheduleCalculator.Next(schedule, plan.Cursor, zone);
        for (var passes = 0; next is { } point && point <= now; passes++)
        {
            if (passes >= MaxPointsPerPass)
            {
                // El reloj ha saltado una barbaridad: se descarta el resto y se parte de "ahora".
                plan.Cursor = now;
                plan.Missed = schedule.CatchUpMissedRuns;
                if (!schedule.CatchUpMissedRuns)
                {
                    _entries[job.Id] = now;
                    changed = true;
                }

                next = ScheduleCalculator.Next(schedule, now, zone);
                break;
            }

            plan.Cursor = point;
            var late = now - point;
            if (!paused && !plan.Missed && !startup && late < OnTimeWindow)
            {
                // Puntual: se encola en el acto (un trabajo ya en cola o en curso no se duplica, pero el punto se consume).
                if (TryEnqueue(job.Id, RunTrigger.Scheduled, point))
                {
                    _entries[job.Id] = point;
                    changed = true;
                }
            }
            else if (schedule.CatchUpMissedRuns)
            {
                plan.Missed = true; // un solo punto perdido pendiente por trabajo, por muchos que se acumulen
            }
            else
            {
                LogSkipped(logger, job.Id, point);
                _entries[job.Id] = point;
                changed = true;
            }

            next = ScheduleCalculator.Next(schedule, point, zone);
        }

        plan.Next = next;

        if (plan.Missed)
        {
            if (!schedule.CatchUpMissedRuns)
            {
                // Se desactivó la recuperación mientras esperaba: se consume sin ejecutar.
                LogSkipped(logger, job.Id, plan.Cursor);
                _entries[job.Id] = plan.Cursor;
                plan.Missed = false;
                plan.DetectedUtc = null;
                changed = true;
            }
            else if (paused)
            {
                plan.DetectedUtc = null; // el periodo de gracia empieza al reanudar la pausa
            }
            else
            {
                plan.DetectedUtc ??= now;
                if (now >= plan.DetectedUtc + grace)
                {
                    if (TryEnqueue(job.Id, RunTrigger.CatchUp, plan.Cursor))
                    {
                        _entries[job.Id] = plan.Cursor;
                        plan.Missed = false;
                        plan.DetectedUtc = null;
                        changed = true;
                    }
                }
            }
        }

        return changed;
    }

    /// <returns>
    /// <see langword="true"/> si el punto queda consumido (encolado, o ya en cola o en curso); <see langword="false"/> si no se
    /// pudo encolar y hay que reintentarlo (el servicio se está parando).
    /// </returns>
    private bool TryEnqueue(Guid jobId, RunTrigger trigger, DateTimeOffset point)
    {
        try
        {
            var runId = runs.Enqueue(jobId, trigger);
            LogEnqueued(logger, jobId, trigger, point, runId);
            return true;
        }
        catch (BackboneException ex) when (ex.Code == BackboneErrorCode.JobAlreadyRunning)
        {
            LogAlreadyRunning(logger, jobId, trigger, point);
            return true;
        }
        catch (BackboneException ex) when (ex.Code == BackboneErrorCode.JobNotFound)
        {
            // Borrado entre la lectura de la lista y aquí: la siguiente pasada limpia su plan.
            return true;
        }
        catch (BackboneException ex) when (ex.Code == BackboneErrorCode.Interrupted)
        {
            return false;
        }
    }

    private async Task SaveEntriesAsync(CancellationToken ct)
    {
        try
        {
            await store.SaveAsync(_entries, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is UntrustedFileException or IOException or UnauthorizedAccessException)
        {
            // La programación sigue en memoria; lo que no se guarda solo se pierde si el servicio se reinicia.
            LogSaveFailed(logger, ex, store.FilePath);
        }
    }

    private void Publish()
    {
        var builder = ImmutableDictionary.CreateBuilder<Guid, DateTimeOffset>();
        foreach (var (id, plan) in _plans)
        {
            if (plan.Next is { } next)
            {
                builder[id] = next;
            }
        }

        var updated = builder.ToImmutable();
        var previous = _published;
        _published = updated;
        if (previous.Count == updated.Count && previous.All(p => updated.TryGetValue(p.Key, out var v) && v == p.Value))
        {
            return;
        }

        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            LogChangedFailed(logger, ex);
        }
    }

    private TimeSpan NextWake(DateTimeOffset now, TimeSpan grace)
    {
        var wait = MaxWait;
        foreach (var plan in _plans.Values)
        {
            if (plan.Next is { } next && next - now < wait)
            {
                wait = next - now;
            }

            if (plan.Missed && plan.DetectedUtc is { } detected && detected + grace - now < wait)
            {
                wait = detected + grace - now;
            }
        }

        return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
    }

    private void OnConfigurationChanged(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            _dirty = true;
            try
            {
                _wake?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // El bucle ya terminó la espera: la marca basta para que la siguiente no se haga.
            }
        }
    }

    private async Task LoopAsync(TimeSpan delay, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            CancellationTokenSource wake;
            bool skipWait;
            lock (_gate)
            {
                wake = CancellationTokenSource.CreateLinkedTokenSource(stop);
                _wake = wake;
                skipWait = _dirty;
                _dirty = false;
            }

            try
            {
                if (!skipWait)
                {
                    await Task.Delay(delay, timeProvider, wake.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Una orden de recalcular, o la parada (se comprueba abajo).
            }
            finally
            {
                lock (_gate)
                {
                    _wake = null;
                }

                wake.Dispose();
            }

            if (stop.IsCancellationRequested)
            {
                return;
            }

            try
            {
                delay = await EvaluateAsync(startup: false, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // El bucle no puede morir: sin él no habría más copias programadas hasta reiniciar el servicio.
                LogLoopFailed(logger, ex);
                delay = MaxWait;
            }
        }
    }

    /// <summary>Estado de programación de un trabajo activado (solo lo toca el bucle).</summary>
    private sealed class Plan(JobSchedule schedule, DateTimeOffset cursor)
    {
        /// <summary>Programación actual (se refresca en cada pasada, p. ej. <see cref="JobSchedule.CatchUpMissedRuns"/>).</summary>
        public JobSchedule Schedule { get; set; } = schedule;

        /// <summary>Último punto procesado en memoria (consumido, o perdido y pendiente de recuperar).</summary>
        public DateTimeOffset Cursor { get; set; } = cursor;

        /// <summary>Próximo punto de programación, posterior a <see cref="Cursor"/>.</summary>
        public DateTimeOffset? Next { get; set; }

        /// <summary>Hay puntos perdidos pendientes de recuperar con una sola ejecución.</summary>
        public bool Missed { get; set; }

        /// <summary>Cuándo se detectó lo perdido (arranque, reanudación o fin de la pausa): el periodo de gracia cuenta desde aquí.</summary>
        public DateTimeOffset? DetectedUtc { get; set; }

        /// <summary>Misma programación a efectos del cálculo (no cuenta <see cref="JobSchedule.CatchUpMissedRuns"/>).</summary>
        public bool SameShape(JobSchedule other)
        {
            var current = Schedule;
            if (current.Kind != other.Kind)
            {
                return false;
            }

            return current.Kind switch
            {
                ScheduleKind.Interval => current.IntervalHours == other.IntervalHours,
                ScheduleKind.Daily => current.Days == other.Days
                    && current.TimesOfDay.Distinct().Order().SequenceEqual(other.TimesOfDay.Distinct().Order()),
                _ => true,
            };
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Programación: trabajo {JobId} encolado ({Trigger}) por el punto {Point:o}, ejecución {RunId}.")]
    private static partial void LogEnqueued(ILogger logger, Guid jobId, RunTrigger trigger, DateTimeOffset point, long runId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Programación: el trabajo {JobId} ya está en cola o en curso; se descarta el punto {Point:o} ({Trigger}).")]
    private static partial void LogAlreadyRunning(ILogger logger, Guid jobId, RunTrigger trigger, DateTimeOffset point);

    [LoggerMessage(Level = LogLevel.Information, Message = "Programación: se salta la ejecución perdida del trabajo {JobId} (punto {Point:o}); la recuperación está desactivada.")]
    private static partial void LogSkipped(ILogger logger, Guid jobId, DateTimeOffset point);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se ha podido guardar {Path}; la programación sigue en memoria.")]
    private static partial void LogSaveFailed(ILogger logger, Exception exception, string path);

    [LoggerMessage(Level = LogLevel.Error, Message = "La pasada del scheduler ha fallado; se reintenta en un minuto.")]
    private static partial void LogLoopFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Un suscriptor de los cambios de programación ha fallado.")]
    private static partial void LogChangedFailed(ILogger logger, Exception exception);
}
