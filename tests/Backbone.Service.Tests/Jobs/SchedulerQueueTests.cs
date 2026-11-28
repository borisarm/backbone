using System.Collections.Immutable;
using Backbone.Contracts;
using Backbone.Service.Jobs;
using Backbone.Service.Persistence;
using Backbone.Service.Tests.TestSupport;
using Microsoft.Extensions.Logging;

namespace Backbone.Service.Tests.Jobs;

/// <summary>
/// HU-09 criterio 4 y RF-15: el <see cref="Scheduler"/> real encolando en la <see cref="RunQueue"/> y el
/// <see cref="JobRunner"/> reales (dobles del sistema, <see cref="RunRig"/>): dos trabajos que coinciden se ejecutan uno detrás
/// de otro en orden de llegada; un trabajo ya en curso no se duplica; las ejecuciones manuales no mueven la programación.
/// </summary>
[Trait("Story", "HU-09")]
public sealed class SchedulerQueueTests : IAsyncLifetime
{
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(20);
    private readonly RunRig _rig = new();
    private readonly TempDirectory _temp = new();
    private readonly ILoggerFactory _loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace));
    private Scheduler _scheduler = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateTimeOffset Utc(int day, int hour, int minute, int second = 0) => new(2026, 11, day, hour, minute, second, TimeSpan.Zero);

    public async ValueTask InitializeAsync()
    {
        await _rig.SaveCredentialAsync();
        _rig.Sources.Source.AddFile(@"C:\Datos\notas.txt", "hola, mundo");
        Directory.CreateDirectory(_temp.Combine("data"));
        var store = new ScheduleStore(_temp.Combine("data", "schedule.json"), TestFileTrust.CurrentUser, _loggerFactory.CreateLogger<ScheduleStore>());
        _scheduler = new Scheduler(_rig.Jobs, _rig.Runs, new ScheduleSettings(), store, _rig.Time, _loggerFactory.CreateLogger<Scheduler>());
    }

    public async ValueTask DisposeAsync()
    {
        _scheduler.Dispose();
        await _rig.DisposeAsync();
        _loggerFactory.Dispose();
        _temp.Dispose();
    }

    // RunRig.Time arranca el 22-11-2026 a las 09:00 UTC (su zona local es UTC).
    private async Task<JobDefinition> ScheduleAsync(string name, JobSchedule schedule)
    {
        var job = await _rig.AddJobAsync(name, RunRig.Share + @"\" + name.ToLowerInvariant());
        await _rig.Jobs.UpdateAsync(list => (list.Select(j => j.Id == job.Id ? j with { Schedule = schedule } : j).ToImmutableArray(), 0), Ct);
        return job;
    }

    [Fact]
    public async Task Two_jobs_that_coincide_run_one_after_the_other_in_order_of_arrival()
    {
        var first = await ScheduleAsync("Primero", new JobSchedule { Kind = ScheduleKind.Daily, TimesOfDay = [new TimeOnly(10, 0)] });
        var second = await ScheduleAsync("Segundo", new JobSchedule { Kind = ScheduleKind.Daily, TimesOfDay = [new TimeOnly(10, 0)] });
        await _scheduler.InitializeAsync(Ct);
        var (entered, release) = _rig.HoldAtFirstFile();

        _rig.Time.SetUtcNow(Utc(22, 10, 0, 10));
        await _scheduler.EvaluateAsync(startup: false, Ct);
        await entered.WaitAsync(Long, Ct);

        Assert.Equal((JobActivity.Running, JobActivity.Queued), (_rig.Runs.GetActivity(first.Id), _rig.Runs.GetActivity(second.Id)));
        var queued = Assert.Single(_rig.Runs.Queue);
        Assert.Equal((second.Id, RunTrigger.Scheduled), (queued.JobId, queued.Trigger));
        Assert.Equal(first.Id, _rig.Runs.CurrentRun?.JobId);

        release.SetResult();
        var firstRun = await _rig.NextCompletedAsync();
        var secondRun = await _rig.NextCompletedAsync();
        Assert.Equal([first.Id, second.Id], [firstRun.JobId, secondRun.JobId]);
        Assert.All([firstRun, secondRun], r => Assert.Equal((RunState.Completed, RunTrigger.Scheduled), (r.State, r.Trigger)));
        Assert.Equal(0, _rig.Connector.OpenConnections);
    }

    [Fact]
    public async Task A_point_of_a_job_that_is_still_running_is_not_queued_again()
    {
        var job = await ScheduleAsync("Largo", new JobSchedule { Kind = ScheduleKind.Interval, IntervalHours = 6 });
        await _scheduler.InitializeAsync(Ct);
        Assert.Equal(Utc(22, 15, 0), _scheduler.NextRunUtc(job.Id));
        var (entered, release) = _rig.HoldAtFirstFile();

        // Una ejecución manual en curso no mueve la programación (no consume el punto de las 15:00).
        _rig.Runs.Enqueue(job.Id, RunTrigger.Manual);
        await entered.WaitAsync(Long, Ct);
        await _scheduler.EvaluateAsync(startup: false, Ct);
        Assert.Equal(Utc(22, 15, 0), _scheduler.NextRunUtc(job.Id));

        // Llega el punto con la copia aún en curso: se descarta, sin duplicar, y la programación sigue.
        _rig.Time.SetUtcNow(Utc(22, 15, 0, 5));
        await _scheduler.EvaluateAsync(startup: false, Ct);

        Assert.Empty(_rig.Runs.Queue);
        Assert.Equal(Utc(22, 21, 0), _scheduler.NextRunUtc(job.Id));
        release.SetResult();
        Assert.Equal(RunTrigger.Manual, (await _rig.NextCompletedAsync()).Trigger);
        Assert.DoesNotContain(_rig.CompletedEvents, e => e.Run.Trigger != RunTrigger.Manual);
    }

    [Fact]
    public async Task A_missed_run_enters_the_same_queue_as_a_catch_up_run()
    {
        var job = await ScheduleAsync("Diaria", new JobSchedule { Kind = ScheduleKind.Daily, TimesOfDay = [new TimeOnly(2, 0)] });
        await _scheduler.InitializeAsync(Ct); // baseline 09:00 del 22
        _rig.Time.SetUtcNow(Utc(23, 8, 0)); // el equipo estuvo apagado a las 02:00 del 23
        await _scheduler.EvaluateAsync(startup: false, Ct);
        _rig.Time.SetUtcNow(Utc(23, 8, 5));

        await _scheduler.EvaluateAsync(startup: false, Ct);

        var run = await _rig.NextCompletedAsync();
        Assert.Equal((job.Id, RunTrigger.CatchUp, RunState.Completed), (run.JobId, run.Trigger, run.State));
    }
}
