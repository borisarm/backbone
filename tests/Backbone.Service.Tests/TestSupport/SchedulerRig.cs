using System.Collections.Concurrent;
using System.Collections.Immutable;
using Backbone.Contracts;
using Backbone.Service.Hosting;
using Backbone.Service.Ipc;
using Backbone.Service.Jobs;
using Backbone.Service.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Backbone.Service.Tests.TestSupport;

/// <summary>Doble de <see cref="IRunEnqueuer"/>: anota lo que el scheduler encola y puede simular que el trabajo ya está en cola.</summary>
internal sealed class FakeRunEnqueuer : IRunEnqueuer
{
    private readonly ConcurrentQueue<EnqueuedRun> _runs = new();

    /// <summary>Si devuelve un código, <see cref="Enqueue"/> lanza esa excepción (sin anotar nada).</summary>
    public Func<Guid, BackboneErrorCode?>? Fail { get; set; }

    public IReadOnlyList<EnqueuedRun> Runs => [.. _runs];

    public long Enqueue(Guid jobId, RunTrigger trigger)
    {
        if (Fail?.Invoke(jobId) is { } code)
        {
            throw new BackboneException(code, "simulated");
        }

        _runs.Enqueue(new EnqueuedRun(jobId, trigger));
        return _runs.Count;
    }
}

internal sealed record EnqueuedRun(Guid JobId, RunTrigger Trigger);

/// <summary>Publicador de eventos que guarda los <c>StatusChanged</c>.</summary>
internal sealed class RecordingEventPublisher : IIpcEventPublisher
{
    public ConcurrentQueue<StatusChangedEvent> Statuses { get; } = new();

    public void PublishStatusChanged(StatusChangedEvent e) => Statuses.Enqueue(e);

    public void PublishRunProgress(RunProgressEvent e)
    {
    }

    public void PublishRunCompleted(RunCompletedEvent e)
    {
    }

    public void PublishRestoreProgress(string ownerSid, RestoreProgressEvent e)
    {
    }

    public void PublishRestoreConflict(string ownerSid, RestoreConflictEvent e)
    {
    }
}

/// <summary>
/// <see cref="Scheduler"/> real con reloj falso (zona local inyectada, nunca la del equipo), <c>jobs.json</c> y
/// <c>schedule.json</c> reales sobre una carpeta temporal y una cola falsa. Las pruebas avanzan el reloj y llaman a
/// <see cref="Scheduler.EvaluateAsync"/> a mano (sin bucle), salvo las que prueban el bucle.
/// </summary>
internal sealed class SchedulerRig : IAsyncDisposable
{
    /// <summary>Zona con horario de verano (UTC+1 en invierno, UTC+2 en verano).</summary>
    public static readonly TimeZoneInfo Madrid = TimeZoneInfo.FindSystemTimeZoneById("Romance Standard Time");

    private readonly TempDirectory _temp = new();
    private readonly ILoggerFactory _loggerFactory;

    public SchedulerRig(DateTimeOffset startUtc, TimeZoneInfo? zone = null, bool createDataDirectory = true)
    {
        Time = new FakeTimeProvider(startUtc);
        Time.SetLocalTimeZone(zone ?? Madrid);
        _loggerFactory = LoggerFactory.Create(b => b.AddProvider(Logs).SetMinimumLevel(LogLevel.Trace));
        Directory.CreateDirectory(_temp.Combine("config"));
        if (createDataDirectory)
        {
            Directory.CreateDirectory(_temp.Combine("data"));
        }

        Jobs = new JobStore(JobsPath, TestFileTrust.CurrentUser, _loggerFactory.CreateLogger<JobStore>());
        Store = new ScheduleStore(SchedulePath, TestFileTrust.CurrentUser, _loggerFactory.CreateLogger<ScheduleStore>());
        Scheduler = NewScheduler();
    }

    public FakeTimeProvider Time { get; }

    public JobStore Jobs { get; }

    public ScheduleStore Store { get; }

    public FakeRunEnqueuer Runs { get; } = new();

    public ScheduleSettings Settings { get; } = new();

    public CapturingLoggerProvider Logs { get; } = new();

    /// <summary>El scheduler en uso; <see cref="Restart"/> lo sustituye por uno nuevo sobre los mismos archivos.</summary>
    public Scheduler Scheduler { get; private set; }

    public string JobsPath => _temp.Combine("config", "jobs.json");

    public string SchedulePath => _temp.Combine("data", "schedule.json");

    public string DataDirectory => _temp.Combine("data");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public Scheduler NewScheduler() => new(Jobs, Runs, Settings, Store, Time, _loggerFactory.CreateLogger<Scheduler>());

    /// <summary>Hora local de Madrid como instante UTC (para escribir las pruebas en la hora que lee el usuario).</summary>
    public static DateTimeOffset Utc(int year, int month, int day, int hour, int minute, int second = 0) =>
        new(year, month, day, hour, minute, second, TimeSpan.Zero);

    public static JobSchedule Daily(string times, DaysOfWeek days = DaysOfWeek.All, bool catchUp = true) =>
        new()
        {
            Kind = ScheduleKind.Daily,
            TimesOfDay = [.. times.Split(',').Select(t => TimeOnly.Parse(t, System.Globalization.CultureInfo.InvariantCulture))],
            Days = days,
            CatchUpMissedRuns = catchUp,
        };

    public static JobSchedule Every(int hours, bool catchUp = true) =>
        new() { Kind = ScheduleKind.Interval, IntervalHours = hours, CatchUpMissedRuns = catchUp };

    public async Task<JobDefinition> AddJobAsync(string name, JobSchedule schedule, bool enabled = true)
    {
        var job = JobDefaults.NewJob() with
        {
            Id = Guid.NewGuid(),
            Name = name,
            Enabled = enabled,
            Sources = [new JobSource { Alias = name, Path = $@"C:\Users\ana\{name}" }],
            Destination = $@"\\nas\backups\{name.ToLowerInvariant()}",
            Schedule = schedule,
        };
        await Jobs.UpdateAsync(list => (list.Add(job), 0), Ct);
        return job;
    }

    public Task ChangeJobAsync(Guid id, Func<JobDefinition, JobDefinition> change) =>
        Jobs.UpdateAsync(list => (list.Select(j => j.Id == id ? change(j) : j).ToImmutableArray(), 0), Ct);

    public Task RemoveJobAsync(Guid id) =>
        Jobs.UpdateAsync(list => (list.RemoveAll(j => j.Id == id), 0), Ct);

    /// <summary>Lee <c>schedule.json</c> tal como lo vería un reinicio.</summary>
    public Task<Dictionary<Guid, DateTimeOffset>> SavedEntriesAsync() => Store.LoadAsync(Ct);

    /// <summary>Arranca el scheduler sin bucle (primera pasada con el servicio recién arrancado).</summary>
    public Task<TimeSpan?> StartAsync() => Scheduler.InitializeAsync(Ct);

    /// <summary>Hace una pasada con el reloj donde esté.</summary>
    public Task<TimeSpan> EvaluateAsync() => Scheduler.EvaluateAsync(startup: false, Ct);

    /// <summary>Pone el reloj en <paramref name="utc"/> y hace una pasada.</summary>
    public Task<TimeSpan> AtAsync(DateTimeOffset utc)
    {
        Time.SetUtcNow(utc);
        return EvaluateAsync();
    }

    /// <summary>Simula reiniciar el servicio: un scheduler nuevo sobre los mismos archivos, con su primera pasada.</summary>
    public async Task RestartAsync()
    {
        Scheduler.Dispose();
        Scheduler = NewScheduler();
        await StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await Scheduler.StopAsync(CancellationToken.None);
        Scheduler.Dispose();
        Jobs.Dispose();
        _loggerFactory.Dispose();
        _temp.Dispose();
    }

    /// <summary>Espera a que se cumpla la condición (hasta 10 s de reloj real).</summary>
    public static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 1000 && !condition(); i++)
        {
            await Task.Delay(10, CancellationToken.None);
        }

        Assert.True(condition(), "La condición no se cumplió a tiempo.");
    }
}
