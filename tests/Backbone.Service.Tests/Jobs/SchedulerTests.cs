using Backbone.Contracts;
using Backbone.Service.Ipc;
using Backbone.Service.Jobs;
using Backbone.Service.Os;
using Backbone.Service.Persistence;
using Backbone.Service.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using static Backbone.Service.Tests.TestSupport.SchedulerRig;

namespace Backbone.Service.Tests.Jobs;

/// <summary>
/// HU-09 (RF-10, RF-11, RF-15), parte del servicio: el <see cref="Scheduler"/> con <c>FakeTimeProvider</c> y la zona
/// "Romance Standard Time" inyectada (nunca la del equipo): próxima ejecución publicada, puntos puntuales y perdidos,
/// recuperación tras el periodo de gracia, reinicio con <c>schedule.json</c>, recálculo al cambiar la configuración, pausa y
/// horario de verano. La cola FIFO real está en <see cref="SchedulerQueueTests"/>.
/// </summary>
/// <remarks>
/// Casi todas las pruebas hacen las pasadas a mano (<see cref="SchedulerRig.EvaluateAsync"/>), sin bucle, para que sean
/// deterministas; las del final arrancan el bucle de verdad con el reloj falso.
/// </remarks>
[Trait("Story", "HU-09")]
public sealed class SchedulerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- Criterio 2: próxima ejecución publicada ----

    [Fact]
    public async Task The_next_run_is_published_only_for_enabled_scheduled_jobs()
    {
        await using var rig = new SchedulerRig(Utc(2026, 11, 20, 12, 0));
        await rig.StartAsync();
        var daily = await rig.AddJobAsync("Diaria", Daily("02:00"));
        var interval = await rig.AddJobAsync("Cada seis", Every(6));
        var manual = await rig.AddJobAsync("Manual", new JobSchedule());
        var disabled = await rig.AddJobAsync("Apagado", Daily("02:00"), enabled: false);
        var changes = 0;
        rig.Scheduler.Changed += (_, _) => changes++;

        await rig.EvaluateAsync();

        Assert.Equal(Utc(2026, 11, 21, 1, 0), rig.Scheduler.NextRunUtc(daily.Id)); // 02:00 local (CET) = 01:00 UTC
        Assert.Equal(Utc(2026, 11, 20, 18, 0), rig.Scheduler.NextRunUtc(interval.Id)); // ahora + 6 h
        Assert.Null(rig.Scheduler.NextRunUtc(manual.Id));
        Assert.Null(rig.Scheduler.NextRunUtc(disabled.Id));
        Assert.Null(rig.Scheduler.NextRunUtc(Guid.NewGuid()));
        Assert.Equal(1, changes); // un solo aviso por pasada

        await rig.EvaluateAsync();
        Assert.Equal(1, changes); // sin cambios, sin aviso
    }

    [Fact]
    public async Task The_status_carries_the_next_run_and_StatusChanged_is_emitted_when_it_changes()
    {
        await using var rig = new SchedulerRig(Utc(2026, 11, 20, 12, 0));
        await rig.StartAsync();
        var status = new ServiceStatusProvider(rig.Jobs, new NoJobActivity(), new RepositoryStateTracker(), rig.Time, null, rig.Scheduler);
        var events = new RecordingEventPublisher();
        using var broadcaster = new StatusBroadcaster(rig.Jobs, status, events, null, rig.Scheduler);
        broadcaster.Subscribe();

        var job = await rig.AddJobAsync("Diaria", Daily("02:00"));
        Assert.Null(Assert.Single(status.GetStatus().Jobs).NextRunUtc); // aún no recalculado
        await rig.EvaluateAsync();

        var next = Utc(2026, 11, 21, 1, 0);
        Assert.Equal(next, Assert.Single(status.GetStatus().Jobs).NextRunUtc);
        Assert.Equal(next, events.Statuses.Last().Status.Jobs.Single(j => j.JobId == job.Id).NextRunUtc);

        // Al encolarse el punto, el siguiente cambia y los clientes lo ven.
        var before = events.Statuses.Count;
        await rig.AtAsync(Utc(2026, 11, 21, 1, 0, 20));
        Assert.True(events.Statuses.Count > before);
        Assert.Equal(Utc(2026, 11, 22, 1, 0), events.Statuses.Last().Status.Jobs.Single().NextRunUtc);

        // Un trabajo desactivado no muestra próxima ejecución, aunque el scheduler aún no se haya enterado.
        await rig.ChangeJobAsync(job.Id, j => j with { Enabled = false });
        Assert.Null(status.GetStatus().Jobs.Single().NextRunUtc);
    }

    // ---- Puntos puntuales (retraso menor de 2 minutos) ----

    [Fact]
    public async Task A_point_that_falls_due_is_enqueued_as_scheduled_and_consumed()
    {
        await using var rig = new SchedulerRig(Utc(2026, 11, 20, 12, 0));
        await rig.StartAsync();
        var job = await rig.AddJobAsync("Diaria", Daily("02:00"));
        await rig.EvaluateAsync();

        var delay = await rig.AtAsync(Utc(2026, 11, 21, 0, 59, 30));
        Assert.Empty(rig.Runs.Runs);
        Assert.Equal(TimeSpan.FromSeconds(30), delay); // despierta justo en el punto

        await rig.AtAsync(Utc(2026, 11, 21, 1, 0, 5));

        Assert.Equal(new EnqueuedRun(job.Id, RunTrigger.Scheduled), Assert.Single(rig.Runs.Runs));
        Assert.Equal(Utc(2026, 11, 22, 1, 0), rig.Scheduler.NextRunUtc(job.Id));
        Assert.Equal(Utc(2026, 11, 21, 1, 0), (await rig.SavedEntriesAsync())[job.Id]);

        await rig.AtAsync(Utc(2026, 11, 21, 1, 30));
        Assert.Single(rig.Runs.Runs); // un punto no se encola dos veces
    }

    [Fact]
    public async Task The_wait_never_exceeds_one_minute()
    {
        await using var rig = new SchedulerRig(Utc(2026, 11, 20, 12, 0));
        await rig.StartAsync();
        await rig.AddJobAsync("Diaria", Daily("02:00"));

        Assert.Equal(TimeSpan.FromMinutes(1), await rig.EvaluateAsync());
        Assert.Equal(Scheduler.MaxWait, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task A_point_detected_one_minute_59_seconds_late_is_on_time()
    {
        await using var rig = new SchedulerRig(Utc(2026, 11, 20, 12, 0));
        await rig.StartAsync();
        var job = await rig.AddJobAsync("Diaria", Daily("02:00"));
        await rig.EvaluateAsync();

        await rig.AtAsync(Utc(2026, 11, 21, 1, 1, 59));

        Assert.Equal(new EnqueuedRun(job.Id, RunTrigger.Scheduled), Assert.Single(rig.Runs.Runs));
    }

    [Fact]
    public async Task A_point_detected_two_minutes_late_is_a_missed_run_that_waits_for_the_grace_period()
    {
        await using var rig = new SchedulerRig(Utc(2026, 11, 20, 12, 0));
        await rig.StartAsync();
        var job = await rig.AddJobAsync("Diaria", Daily("02:00"));
        await rig.EvaluateAsync();

        await rig.AtAsync(Utc(2026, 11, 21, 1, 2, 0));
        Assert.Empty(rig.Runs.Runs); // detectado ahora: espera el periodo de gracia

        await rig.AtAsync(Utc(2026, 11, 21, 1, 6, 59));
        Assert.Empty(rig.Runs.Runs);
        await rig.AtAsync(Utc(2026, 11, 21, 1, 7, 0));
        Assert.Equal(new EnqueuedRun(job.Id, RunTrigger.CatchUp), Assert.Single(rig.Runs.Runs));
    }

    // ---- Criterio 3 y RF-11: ejecuciones perdidas ----

    /// <summary>
    /// Servicio parado (equipo apagado) desde las 00:30 UTC hasta las 07:00 UTC: se perdió el punto de las 02:00 locales
    /// (01:00 UTC), y la prueba parte del reinicio del servicio a las 07:00 UTC.
    /// </summary>
    private static async Task<(SchedulerRig Rig, JobDefinition Job)> RestartedAfterMissedPointAsync(bool catchUp = true, string times = "02:00")
    {
        var rig = new SchedulerRig(Utc(2026, 11, 20, 0, 30));
        await rig.StartAsync();
        var job = await rig.AddJobAsync("Diaria", Daily(times, catchUp: catchUp));
        await rig.EvaluateAsync(); // punto de partida: 00:30 UTC, guardado en schedule.json
        rig.Time.SetUtcNow(Utc(2026, 11, 20, 7, 0));
        await rig.RestartAsync();
        return (rig, job);
    }

    [Fact]
    public async Task A_daily_run_missed_with_the_machine_off_starts_five_minutes_after_the_service_starts()
    {
        var (rig, job) = await RestartedAfterMissedPointAsync();
        await using var disposable = rig;

        Assert.Empty(rig.Runs.Runs); // al arrancar no se encola en el acto: hay periodo de gracia
        await rig.AtAsync(Utc(2026, 11, 20, 7, 4, 59));
        Assert.Empty(rig.Runs.Runs);

        await rig.AtAsync(Utc(2026, 11, 20, 7, 5, 0));

        Assert.Equal(new EnqueuedRun(job.Id, RunTrigger.CatchUp), Assert.Single(rig.Runs.Runs));
        Assert.Equal(Utc(2026, 11, 20, 1, 0), (await rig.SavedEntriesAsync())[job.Id]); // el punto perdido queda consumido
        Assert.Equal(Utc(2026, 11, 21, 1, 0), rig.Scheduler.NextRunUtc(job.Id));

        await rig.AtAsync(Utc(2026, 11, 20, 9, 0));
        Assert.Single(rig.Runs.Runs); // una sola
    }

    [Fact]
    public async Task The_next_run_shown_while_a_missed_run_is_waiting_is_the_next_future_point()
    {
        var (rig, job) = await RestartedAfterMissedPointAsync();
        await using var disposable = rig;

        Assert.Equal(Utc(2026, 11, 21, 1, 0), rig.Scheduler.NextRunUtc(job.Id));
        Assert.Equal(TimeSpan.FromMinutes(1), await rig.EvaluateAsync());
    }

    [Fact]
    public async Task Restarting_the_service_again_during_the_grace_period_does_not_lose_the_missed_run()
    {
        var (rig, job) = await RestartedAfterMissedPointAsync();
        await using var disposable = rig;

        rig.Time.SetUtcNow(Utc(2026, 11, 20, 7, 2));
        await rig.RestartAsync(); // el punto no estaba consumido: se vuelve a detectar, con la gracia contada desde aquí

        await rig.AtAsync(Utc(2026, 11, 20, 7, 6, 59));
        Assert.Empty(rig.Runs.Runs);
        await rig.AtAsync(Utc(2026, 11, 20, 7, 7, 0));
        Assert.Equal(new EnqueuedRun(job.Id, RunTrigger.CatchUp), Assert.Single(rig.Runs.Runs));
    }

    [Fact]
    public async Task Without_recovery_the_missed_run_is_skipped_and_the_schedule_moves_on()
    {
        var (rig, job) = await RestartedAfterMissedPointAsync(catchUp: false);
        await using var disposable = rig;

        await rig.AtAsync(Utc(2026, 11, 20, 9, 0));

        Assert.Empty(rig.Runs.Runs);
        Assert.Equal(Utc(2026, 11, 20, 1, 0), (await rig.SavedEntriesAsync())[job.Id]); // consumido sin ejecutar
        Assert.Equal(Utc(2026, 11, 21, 1, 0), rig.Scheduler.NextRunUtc(job.Id));
        Assert.Contains(rig.Logs.Entries, e => e.Message.Contains("recuperación está desactivada", StringComparison.Ordinal));

        // El día siguiente sí se ejecuta, puntual.
        await rig.AtAsync(Utc(2026, 11, 21, 1, 0, 10));
        Assert.Equal(new EnqueuedRun(job.Id, RunTrigger.Scheduled), Assert.Single(rig.Runs.Runs));
    }

    [Fact]
    public async Task Many_missed_daily_points_give_a_single_catch_up_run()
    {
        await using var rig = new SchedulerRig(Utc(2026, 11, 20, 0, 30));
        await rig.StartAsync();
        var job = await rig.AddJobAsync("Dos al día", Daily("02:00,14:00"));
        await rig.EvaluateAsync();
        rig.Time.SetUtcNow(Utc(2026, 11, 23, 12, 0)); // tres días fuera: seis puntos perdidos
        await rig.RestartAsync();

        await rig.AtAsync(Utc(2026, 11, 23, 12, 5));
        await rig.AtAsync(Utc(2026, 11, 23, 12, 30));

        Assert.Equal(new EnqueuedRun(job.Id, RunTrigger.CatchUp), Assert.Single(rig.Runs.Runs));
        Assert.Equal(Utc(2026, 11, 23, 1, 0), (await rig.SavedEntriesAsync())[job.Id]); // el último punto perdido, 02:00 local de hoy
        Assert.Equal(Utc(2026, 11, 23, 13, 0), rig.Scheduler.NextRunUtc(job.Id)); // 14:00 local de hoy
    }

    [Fact]
    public async Task Many_missed_interval_points_give_a_single_catch_up_run_and_the_next_one_counts_from_the_last_point()
    {
        await using var rig = new SchedulerRig(Utc(2026, 11, 20, 0, 0));
        await rig.StartAsync();
        var job = await rig.AddJobAsync("Cada hora", Every(1));
        await rig.EvaluateAsync();
        rig.Time.SetUtcNow(Utc(2026, 11, 20, 10, 30));
        await rig.RestartAsync();

        Assert.Equal(Utc(2026, 11, 20, 11, 0), rig.Scheduler.NextRunUtc(job.Id)); // el último punto vencido fue las 10:00
        await rig.AtAsync(Utc(2026, 11, 20, 10, 35));

        Assert.Equal(new EnqueuedRun(job.Id, RunTrigger.CatchUp), Assert.Single(rig.Runs.Runs));
        Assert.Equal(Utc(2026, 11, 20, 10, 0), (await rig.SavedEntriesAsync())[job.Id]);
    }

    [Fact]
    public async Task A_run_that_was_done_before_the_restart_is_not_recovered()
    {
        await using var rig = new SchedulerRig(Utc(2026, 11, 20, 12, 0));
        await rig.StartAsync();
        var job = await rig.AddJobAsync("Diaria", Daily("02:00"));
        await rig.EvaluateAsync();
        await rig.AtAsync(Utc(2026, 11, 21, 1, 0, 10)); // se ejecuta a su hora
        Assert.Single(rig.Runs.Runs);

        rig.Time.SetUtcNow(Utc(2026, 11, 21, 7, 0));
        await rig.RestartAsync();
        await rig.AtAsync(Utc(2026, 11, 21, 8, 0));

        Assert.Single(rig.Runs.Runs); // sin ejecución de recuperación
        Assert.Equal(Utc(2026, 11, 22, 1, 0), rig.Scheduler.NextRunUtc(job.Id));
    }

    [Fact]
    public async Task A_point_that_falls_due_at_the_moment_the_service_starts_is_still_a_missed_run()
    {
        var (rig, job) = await RestartedAfterMissedPointAsync();
        await using var disposable = rig;
        rig.Time.SetUtcNow(Utc(2026, 11, 21, 1, 0, 30)); // 30 s después del siguiente punto: arranca el servicio
        await rig.RestartAsync();

        Assert.Empty(rig.Runs.Runs);
        await rig.AtAsync(Utc(2026, 11, 21, 1, 5, 30));
        Assert.Equal(new EnqueuedRun(job.Id, RunTrigger.CatchUp), Assert.Single(rig.Runs.Runs));
    }

    [Fact]
    public async Task A_job_without_an_entry_in_schedule_json_has_no_missed_runs()
    {
        // Primer arranque con trabajos ya guardados: se parte de "ahora", aunque el punto de hoy ya pasara.
        await using var rig = new SchedulerRig(Utc(2026, 11, 20, 7, 0));
        await rig.AddJobAsync("Diaria", Daily("02:00"));

        await rig.StartAsync();
        await rig.AtAsync(Utc(2026, 11, 20, 12, 0));

        Assert.Empty(rig.Runs.Runs);
    }

    [Fact]
    public async Task A_damaged_schedule_json_is_treated_as_empty()
    {
        await using var rig = new SchedulerRig(Utc(2026, 11, 20, 7, 0));
        await File.WriteAllTextAsync(rig.SchedulePath, "{ esto no es json", Ct);
        var job = await rig.AddJobAsync("Diaria", Daily("02:00"));

        await rig.StartAsync();
        await rig.AtAsync(Utc(2026, 11, 20, 12, 0));

        Assert.Empty(rig.Runs.Runs);
        Assert.Equal(Utc(2026, 11, 21, 1, 0), rig.Scheduler.NextRunUtc(job.Id));
        Assert.Equal(Utc(2026, 11, 20, 7, 0), (await rig.SavedEntriesAsync())[job.Id]); // se reescribe bien
    }

    [Fact]
    public async Task An_entry_in_the_future_after_the_clock_went_back_is_clamped_to_now()
    {
        await using var rig = new SchedulerRig(Utc(2026, 11, 20, 12, 0));
        var job = await rig.AddJobAsync("Cada seis", Every(6));
        await rig.Store.SaveAsync(new Dictionary<Guid, DateTimeOffset> { [job.Id] = Utc(2026, 12, 25, 0, 0) }, Ct);

        await rig.StartAsync();

        Assert.Equal(Utc(2026, 11, 20, 18, 0), rig.Scheduler.NextRunUtc(job.Id));
    }

    // ---- Intervalo ----

    [Fact]
    public async Task An_interval_job_starts_counting_from_now_and_keeps_the_start_across_restarts()
    {
        await using var rig = new SchedulerRig(Utc(2026, 11, 20, 12, 0));
        await rig.StartAsync();
        var job = await rig.AddJobAsync("Cada seis", Every(6));
        await rig.EvaluateAsync();
        Assert.Equal(Utc(2026, 11, 20, 18, 0), rig.Scheduler.NextRunUtc(job.Id));

        rig.Time.SetUtcNow(Utc(2026, 11, 20, 14, 0));
        await rig.RestartAsync();

        Assert.Equal(Utc(2026, 11, 20, 18, 0), rig.Scheduler.NextRunUtc(job.Id)); // no se retrasa por reiniciar
        Assert.Empty(rig.Runs.Runs);
    }

    [Fact]
    public async Task An_interval_job_counts_from_the_consumed_point_not_from_when_it_was_detected()
    {
        await using var rig = new SchedulerRig(Utc(2026, 11, 20, 12, 0));
        await rig.StartAsync();
        var job = await rig.AddJobAsync("Cada seis", Every(6));
        await rig.EvaluateAsync();

        await rig.AtAsync(Utc(2026, 11, 20, 18, 0, 40)); // detectado con 40 s de retraso

        Assert.Equal(new EnqueuedRun(job.Id, RunTrigger.Scheduled), Assert.Single(rig.Runs.Runs));
        Assert.Equal(Utc(2026, 11, 21, 0, 0), rig.Scheduler.NextRunUtc(job.Id)); // 18:00 + 6 h, no 18:00:40 + 6 h
    }

    [Fact]
    public async Task An_interval_does_not_follow_daylight_saving()
    {
        await using var rig = new SchedulerRig(Utc(2026, 3, 28, 22, 0));
        await rig.StartAsync();
        var job = await rig.AddJobAsync("Cada seis", Every(6));
        await rig.EvaluateAsync();

        await rig.AtAsync(Utc(2026, 3, 29, 4, 0, 1)); // cruza el salto de las 01:00 UTC

        Assert.Single(rig.Runs.Runs);
        Assert.Equal(Utc(2026, 3, 29, 10, 0), rig.Scheduler.NextRunUtc(job.Id));
    }

    // ---- Criterio 5: recálculo al cambiar la programación ----

    [Fact]
    public async Task Changing_the_schedule_recalculates_the_next_run_without_restarting()
    {
        await using var rig = new SchedulerRig(Utc(2026, 11, 20, 12, 0));
        await rig.StartAsync();
        var job = await rig.AddJobAsync("Diaria", Daily("02:00"));
        await rig.EvaluateAsync();
        Assert.Equal(Utc(2026, 11, 21, 1, 0), rig.Scheduler.NextRunUtc(job.Id));

        await rig.ChangeJobAsync(job.Id, j => j with { Schedule = Daily("03:00") });
        await rig.EvaluateAsync();
        Assert.Equal(Utc(2026, 11, 21, 2, 0), rig.Scheduler.NextRunUtc(job.Id));

        await rig.ChangeJobAsync(job.Id, j => j with { Schedule = Daily("03:00", DaysOfWeek.Monday) });
        await rig.EvaluateAsync();
        Assert.Equal(Utc(2026, 11, 23, 2, 0), rig.Scheduler.NextRunUtc(job.Id)); // el lunes 23

        await rig.ChangeJobAsync(job.Id, j => j with { Schedule = Every(3) });
        await rig.EvaluateAsync();
        Assert.Equal(Utc(2026, 11, 20, 15, 0), rig.Scheduler.NextRunUtc(job.Id));

        await rig.ChangeJobAsync(job.Id, j => j with { Schedule = new JobSchedule() });
        await rig.EvaluateAsync();
        Assert.Null(rig.Scheduler.NextRunUtc(job.Id));
        Assert.Empty(await rig.SavedEntriesAsync()); // un trabajo manual no deja entrada
    }

    [Fact]
    public async Task Changing_the_interval_restarts_the_count_from_now()
    {
        await using var rig = new SchedulerRig(Utc(2026, 11, 20, 12, 0));
        await rig.StartAsync();
        var job = await rig.AddJobAsync("Cada seis", Every(6));
        await rig.EvaluateAsync();

        rig.Time.SetUtcNow(Utc(2026, 11, 20, 13, 0));
        await rig.ChangeJobAsync(job.Id, j => j with { Schedule = Every(3) });
        await rig.EvaluateAsync();

        Assert.Equal(Utc(2026, 11, 20, 16, 0), rig.Scheduler.NextRunUtc(job.Id));
        Assert.Equal(Utc(2026, 11, 20, 13, 0), (await rig.SavedEntriesAsync())[job.Id]);
    }

    [Fact]
    public async Task Changing_a_daily_schedule_does_not_create_missed_runs_for_the_new_hours()
    {
        await using var rig = new SchedulerRig(Utc(2026, 11, 20, 12, 0));
        await rig.StartAsync();
        var job = await rig.AddJobAsync("Diaria", Daily("23:00"));
        await rig.EvaluateAsync();

        // A las 14:00 locales (13:00 UTC) se cambia a las 08:00 locales, que ya pasaron hoy.
        rig.Time.SetUtcNow(Utc(2026, 11, 20, 13, 0));
        await rig.ChangeJobAsync(job.Id, j => j with { Schedule = Daily("08:00") });
        await rig.EvaluateAsync();
        await rig.AtAsync(Utc(2026, 11, 20, 14, 0));

        Assert.Empty(rig.Runs.Runs);
        Assert.Equal(Utc(2026, 11, 21, 7, 0), rig.Scheduler.NextRunUtc(job.Id));
    }

    [Fact]
    public async Task Changing_only_the_recovery_flag_keeps_the_next_run_and_the_start_point()
    {
        await using var rig = new SchedulerRig(Utc(2026, 11, 20, 12, 0));
        await rig.StartAsync();
        var job = await rig.AddJobAsync("Cada seis", Every(6));
        await rig.EvaluateAsync();
        rig.Time.SetUtcNow(Utc(2026, 11, 20, 13, 0));

        await rig.ChangeJobAsync(job.Id, j => j with { Schedule = Every(6, catchUp: false) });
        await rig.EvaluateAsync();

        Assert.Equal(Utc(2026, 11, 20, 18, 0), rig.Scheduler.NextRunUtc(job.Id));
        Assert.Equal(Utc(2026, 11, 20, 12, 0), (await rig.SavedEntriesAsync())[job.Id]);
    }

    [Fact]
    public async Task A_disabled_job_has_no_next_run_no_entry_and_nothing_is_enqueued_and_it_restarts_from_now_when_enabled()
    {
        await using var rig = new SchedulerRig(Utc(2026, 11, 20, 12, 0));
        await rig.StartAsync();
        var job = await rig.AddJobAsync("Diaria", Daily("02:00"));
        await rig.EvaluateAsync();
        Assert.Contains(job.Id, (await rig.SavedEntriesAsync()).Keys);

        await rig.ChangeJobAsync(job.Id, j => j with { Enabled = false });
        await rig.AtAsync(Utc(2026, 11, 21, 1, 0, 10));
        Assert.Null(rig.Scheduler.NextRunUtc(job.Id));
        Assert.Empty(rig.Runs.Runs);
        Assert.Empty(await rig.SavedEntriesAsync());

        // Activado de nuevo días después: parte de "ahora", sin ejecuciones perdidas.
        rig.Time.SetUtcNow(Utc(2026, 11, 25, 12, 0));
        await rig.ChangeJobAsync(job.Id, j => j with { Enabled = true });
        await rig.EvaluateAsync();
        await rig.AtAsync(Utc(2026, 11, 25, 13, 0));
        Assert.Empty(rig.Runs.Runs);
        Assert.Equal(Utc(2026, 11, 26, 1, 0), rig.Scheduler.NextRunUtc(job.Id));
    }

    [Fact]
    public async Task A_deleted_job_loses_its_entry_and_is_never_enqueued()
    {
        await using var rig = new SchedulerRig(Utc(2026, 11, 20, 12, 0));
        await rig.StartAsync();
        var kept = await rig.AddJobAsync("Se queda", Daily("02:00"));
        var gone = await rig.AddJobAsync("Se va", Daily("02:00"));
        await rig.EvaluateAsync();

        await rig.RemoveJobAsync(gone.Id);
        await rig.AtAsync(Utc(2026, 11, 21, 1, 0, 10));

        Assert.Equal(kept.Id, Assert.Single(rig.Runs.Runs).JobId);
        Assert.Null(rig.Scheduler.NextRunUtc(gone.Id));
        Assert.Equal([kept.Id], (await rig.SavedEntriesAsync()).Keys);
    }

    // ---- Periodo de gracia configurable ----

    [Fact]
    public async Task Changing_the_grace_period_recalculates_when_the_missed_run_starts()
    {
        var (rig, job) = await RestartedAfterMissedPointAsync();
        await using var disposable = rig;
        await rig.AtAsync(Utc(2026, 11, 20, 7, 2));
        Assert.Empty(rig.Runs.Runs);

        rig.Settings.Update(TimeSpan.FromMinutes(1), paused: false); // la gracia contaba desde las 07:00: ya ha pasado
        await rig.EvaluateAsync();

        Assert.Equal(new EnqueuedRun(job.Id, RunTrigger.CatchUp), Assert.Single(rig.Runs.Runs));
    }

    [Fact]
    public async Task A_longer_grace_period_delays_the_missed_run()
    {
        var (rig, job) = await RestartedAfterMissedPointAsync();
        await using var disposable = rig;

        rig.Settings.Update(TimeSpan.FromMinutes(30), paused: false);
        await rig.AtAsync(Utc(2026, 11, 20, 7, 29, 59));
        Assert.Empty(rig.Runs.Runs);
        await rig.AtAsync(Utc(2026, 11, 20, 7, 30, 0));

        Assert.Equal(job.Id, Assert.Single(rig.Runs.Runs).JobId);
    }

    [Fact]
    public async Task A_zero_grace_period_recovers_as_soon_as_the_service_starts()
    {
        await using var rig = new SchedulerRig(Utc(2026, 11, 20, 0, 30));
        await rig.StartAsync();
        var job = await rig.AddJobAsync("Diaria", Daily("02:00"));
        await rig.EvaluateAsync();
        rig.Settings.Update(TimeSpan.Zero, paused: false);
        rig.Time.SetUtcNow(Utc(2026, 11, 20, 7, 0));

        await rig.RestartAsync();

        Assert.Equal(new EnqueuedRun(job.Id, RunTrigger.CatchUp), Assert.Single(rig.Runs.Runs));
    }

    // ---- Pausa ----

    [Fact]
    public async Task While_paused_nothing_is_enqueued_and_the_due_points_are_recovered_after_resuming()
    {
        await using var rig = new SchedulerRig(Utc(2026, 11, 20, 12, 0));
        await rig.StartAsync();
        var job = await rig.AddJobAsync("Diaria", Daily("02:00"));
        await rig.EvaluateAsync();
        rig.Settings.Update(ServiceSettingsDefaults.MissedRunGracePeriod, paused: true);

        await rig.AtAsync(Utc(2026, 11, 21, 1, 0, 10));
        await rig.AtAsync(Utc(2026, 11, 21, 6, 0));
        Assert.Empty(rig.Runs.Runs);
        Assert.Equal(Utc(2026, 11, 22, 1, 0), rig.Scheduler.NextRunUtc(job.Id));

        rig.Settings.Update(ServiceSettingsDefaults.MissedRunGracePeriod, paused: false);
        await rig.EvaluateAsync();
        Assert.Empty(rig.Runs.Runs); // se trata como perdida: gracia desde que se reanuda
        await rig.AtAsync(Utc(2026, 11, 21, 6, 4, 59));
        Assert.Empty(rig.Runs.Runs);
        await rig.AtAsync(Utc(2026, 11, 21, 6, 5, 0));

        Assert.Equal(new EnqueuedRun(job.Id, RunTrigger.CatchUp), Assert.Single(rig.Runs.Runs));
    }

    [Fact]
    public async Task While_paused_without_recovery_the_due_points_are_skipped()
    {
        await using var rig = new SchedulerRig(Utc(2026, 11, 20, 12, 0));
        await rig.StartAsync();
        await rig.AddJobAsync("Diaria", Daily("02:00", catchUp: false));
        await rig.EvaluateAsync();
        rig.Settings.Update(ServiceSettingsDefaults.MissedRunGracePeriod, paused: true);

        await rig.AtAsync(Utc(2026, 11, 21, 1, 0, 10));
        rig.Settings.Update(ServiceSettingsDefaults.MissedRunGracePeriod, paused: false);
        await rig.AtAsync(Utc(2026, 11, 21, 12, 0));

        Assert.Empty(rig.Runs.Runs);
    }

    // ---- Cola (parte del scheduler) ----

    [Fact]
    public async Task Jobs_that_fall_due_together_are_enqueued_in_the_order_of_jobs_json()
    {
        await using var rig = new SchedulerRig(Utc(2026, 11, 20, 12, 0));
        await rig.StartAsync();
        var first = await rig.AddJobAsync("Primero", Daily("02:00"));
        var second = await rig.AddJobAsync("Segundo", Every(13)); // 01:00 UTC del día siguiente
        var third = await rig.AddJobAsync("Tercero", Daily("02:00"));
        await rig.EvaluateAsync();

        await rig.AtAsync(Utc(2026, 11, 21, 1, 0, 5));

        Assert.Equal([first.Id, second.Id, third.Id], rig.Runs.Runs.Select(r => r.JobId));
        Assert.All(rig.Runs.Runs, r => Assert.Equal(RunTrigger.Scheduled, r.Trigger));
    }

    [Fact]
    public async Task Catch_up_runs_of_several_jobs_are_enqueued_in_the_order_of_jobs_json()
    {
        await using var rig = new SchedulerRig(Utc(2026, 11, 20, 0, 30));
        await rig.StartAsync();
        var first = await rig.AddJobAsync("Primero", Daily("02:00"));
        var second = await rig.AddJobAsync("Segundo", Daily("03:00"));
        await rig.EvaluateAsync();
        rig.Time.SetUtcNow(Utc(2026, 11, 20, 7, 0));
        await rig.RestartAsync();

        await rig.AtAsync(Utc(2026, 11, 20, 7, 5));

        Assert.Equal([first.Id, second.Id], rig.Runs.Runs.Select(r => r.JobId));
        Assert.All(rig.Runs.Runs, r => Assert.Equal(RunTrigger.CatchUp, r.Trigger));
    }

    [Fact]
    public async Task A_scheduled_point_of_a_job_already_queued_or_running_is_not_duplicated_but_is_consumed()
    {
        await using var rig = new SchedulerRig(Utc(2026, 11, 20, 12, 0));
        await rig.StartAsync();
        var job = await rig.AddJobAsync("Diaria", Daily("02:00"));
        await rig.EvaluateAsync();
        rig.Runs.Fail = _ => BackboneErrorCode.JobAlreadyRunning;

        await rig.AtAsync(Utc(2026, 11, 21, 1, 0, 10));

        Assert.Empty(rig.Runs.Runs);
        Assert.Equal(Utc(2026, 11, 21, 1, 0), (await rig.SavedEntriesAsync())[job.Id]);
        Assert.Equal(Utc(2026, 11, 22, 1, 0), rig.Scheduler.NextRunUtc(job.Id));
        rig.Runs.Fail = null;
        await rig.AtAsync(Utc(2026, 11, 21, 1, 30));
        Assert.Empty(rig.Runs.Runs); // consumido: no se reintenta
    }

    [Fact]
    public async Task A_catch_up_run_of_a_job_already_queued_or_running_is_not_duplicated_but_is_consumed()
    {
        var (rig, job) = await RestartedAfterMissedPointAsync();
        await using var disposable = rig;
        rig.Runs.Fail = _ => BackboneErrorCode.JobAlreadyRunning;

        await rig.AtAsync(Utc(2026, 11, 20, 7, 5));

        Assert.Empty(rig.Runs.Runs);
        Assert.Equal(Utc(2026, 11, 20, 1, 0), (await rig.SavedEntriesAsync())[job.Id]);
        rig.Runs.Fail = null;
        await rig.AtAsync(Utc(2026, 11, 20, 9, 0));
        Assert.Empty(rig.Runs.Runs);
    }

    [Fact]
    public async Task While_the_service_is_stopping_the_point_is_not_consumed_and_nothing_fails()
    {
        await using var rig = new SchedulerRig(Utc(2026, 11, 20, 12, 0));
        await rig.StartAsync();
        var job = await rig.AddJobAsync("Diaria", Daily("02:00"));
        await rig.EvaluateAsync();
        rig.Runs.Fail = _ => BackboneErrorCode.Interrupted;

        await rig.AtAsync(Utc(2026, 11, 21, 1, 0, 10));

        Assert.Empty(rig.Runs.Runs);
        Assert.Equal(Utc(2026, 11, 20, 12, 0), (await rig.SavedEntriesAsync())[job.Id]); // sin consumir
    }

    // ---- Persistencia ----

    [Fact]
    public async Task Without_a_data_folder_the_scheduler_keeps_working_in_memory_and_logs_a_warning()
    {
        await using var rig = new SchedulerRig(Utc(2026, 11, 20, 12, 0), createDataDirectory: false);
        await rig.StartAsync();
        var job = await rig.AddJobAsync("Diaria", Daily("02:00"));

        await rig.EvaluateAsync();
        await rig.AtAsync(Utc(2026, 11, 21, 1, 0, 10));

        Assert.Equal(new EnqueuedRun(job.Id, RunTrigger.Scheduled), Assert.Single(rig.Runs.Runs));
        Assert.Contains(rig.Logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("No se ha podido guardar", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_rejected_schedule_json_is_treated_as_empty_and_is_not_replaced()
    {
        await using var rig = new SchedulerRig(Utc(2026, 11, 20, 7, 0));
        Directory.CreateDirectory(rig.SchedulePath); // una carpeta ocupando el nombre: ADR-14 la rechaza
        var job = await rig.AddJobAsync("Diaria", Daily("02:00"));

        await rig.StartAsync();
        await rig.AtAsync(Utc(2026, 11, 21, 1, 0, 10));

        Assert.Equal(new EnqueuedRun(job.Id, RunTrigger.Scheduled), Assert.Single(rig.Runs.Runs));
        Assert.True(Directory.Exists(rig.SchedulePath));
        Assert.Contains(rig.Logs.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task The_file_is_camelCase_json_with_a_schema_version()
    {
        await using var rig = new SchedulerRig(Utc(2026, 11, 20, 12, 0));
        await rig.StartAsync();
        var job = await rig.AddJobAsync("Diaria", Daily("02:00"));
        await rig.EvaluateAsync();

        using var document = System.Text.Json.JsonDocument.Parse(await File.ReadAllBytesAsync(rig.SchedulePath, Ct));
        var root = document.RootElement;
        Assert.Equal(ScheduleStore.SchemaVersion, root.GetProperty("schemaVersion").GetInt32());
        var entry = Assert.Single(root.GetProperty("jobs").EnumerateArray());
        Assert.Equal(job.Id, entry.GetProperty("jobId").GetGuid());
        Assert.Equal(Utc(2026, 11, 20, 12, 0), entry.GetProperty("lastConsumedUtc").GetDateTimeOffset());
    }

    // ---- Horario de verano (criterio 6) ----

    [Fact]
    public async Task A_daily_time_inside_the_spring_gap_runs_once_at_the_first_valid_instant()
    {
        await using var rig = new SchedulerRig(Utc(2026, 3, 28, 12, 0));
        await rig.StartAsync();
        var job = await rig.AddJobAsync("Diaria", Daily("02:30"));
        await rig.EvaluateAsync();
        Assert.Equal(Utc(2026, 3, 29, 1, 0), rig.Scheduler.NextRunUtc(job.Id)); // 03:00 CEST

        await rig.AtAsync(Utc(2026, 3, 29, 0, 59, 59));
        Assert.Empty(rig.Runs.Runs);
        await rig.AtAsync(Utc(2026, 3, 29, 1, 0, 1));

        Assert.Single(rig.Runs.Runs);
        Assert.Equal(Utc(2026, 3, 30, 0, 30), rig.Scheduler.NextRunUtc(job.Id)); // 02:30 CEST del día siguiente
    }

    [Fact]
    public async Task A_daily_time_in_the_autumn_overlap_runs_once_at_its_first_occurrence()
    {
        await using var rig = new SchedulerRig(Utc(2026, 10, 24, 12, 0));
        await rig.StartAsync();
        var job = await rig.AddJobAsync("Diaria", Daily("02:30"));
        await rig.EvaluateAsync();
        Assert.Equal(Utc(2026, 10, 25, 0, 30), rig.Scheduler.NextRunUtc(job.Id)); // 02:30 CEST

        await rig.AtAsync(Utc(2026, 10, 25, 0, 30, 5));
        Assert.Single(rig.Runs.Runs);

        await rig.AtAsync(Utc(2026, 10, 25, 1, 30, 5)); // la segunda 02:30 (CET) no cuenta
        Assert.Single(rig.Runs.Runs);
        Assert.Equal(Utc(2026, 10, 26, 1, 30), rig.Scheduler.NextRunUtc(job.Id));
    }

    [Fact]
    public async Task A_daily_run_keeps_its_local_hour_across_the_change_to_summer_time()
    {
        await using var rig = new SchedulerRig(Utc(2026, 3, 27, 12, 0));
        await rig.StartAsync();
        var job = await rig.AddJobAsync("Diaria", Daily("08:00"));
        await rig.EvaluateAsync();
        Assert.Equal(Utc(2026, 3, 28, 7, 0), rig.Scheduler.NextRunUtc(job.Id)); // CET

        await rig.AtAsync(Utc(2026, 3, 28, 7, 0, 1));
        Assert.Equal(Utc(2026, 3, 29, 6, 0), rig.Scheduler.NextRunUtc(job.Id)); // ya CEST: una hora UTC antes
        await rig.AtAsync(Utc(2026, 3, 29, 6, 0, 1));

        Assert.Equal(2, rig.Runs.Runs.Count);
    }

    [Fact]
    public async Task The_scheduler_uses_the_zone_of_the_time_provider()
    {
        await using var rig = new SchedulerRig(Utc(2026, 11, 20, 12, 0), TimeZoneInfo.Utc);
        await rig.StartAsync();
        var job = await rig.AddJobAsync("Diaria", Daily("02:00"));

        await rig.EvaluateAsync();

        Assert.Equal(Utc(2026, 11, 21, 2, 0), rig.Scheduler.NextRunUtc(job.Id));
    }

    // ---- El bucle con el reloj falso ----

    [Fact]
    public async Task The_running_loop_recalculates_at_once_when_jobs_change()
    {
        await using var rig = new SchedulerRig(Utc(2026, 11, 20, 12, 0));
        await rig.Scheduler.StartAsync(Ct);

        var job = await rig.AddJobAsync("Diaria", Daily("02:00"));
        await WaitUntilAsync(() => rig.Scheduler.NextRunUtc(job.Id) == Utc(2026, 11, 21, 1, 0));

        await rig.ChangeJobAsync(job.Id, j => j with { Schedule = Daily("03:00") });
        await WaitUntilAsync(() => rig.Scheduler.NextRunUtc(job.Id) == Utc(2026, 11, 21, 2, 0));

        await rig.ChangeJobAsync(job.Id, j => j with { Enabled = false });
        await WaitUntilAsync(() => rig.Scheduler.NextRunUtc(job.Id) is null);
    }

    [Fact]
    public async Task The_running_loop_enqueues_a_point_when_the_clock_reaches_it()
    {
        await using var rig = new SchedulerRig(Utc(2026, 11, 21, 0, 59, 0));
        var job = await rig.AddJobAsync("Diaria", Daily("02:00"));
        await rig.Scheduler.StartAsync(Ct);
        await Task.Delay(100, Ct); // que el bucle registre su temporizador

        // Se avanza a pasos de 10 s hasta que el bucle reacciona (el punto está a 60 s).
        for (var i = 0; i < 11 && rig.Runs.Runs.Count == 0; i++)
        {
            rig.Time.Advance(TimeSpan.FromSeconds(10));
            await Task.Delay(30, Ct);
        }

        await WaitUntilAsync(() => rig.Runs.Runs.Count == 1);
        Assert.Equal(new EnqueuedRun(job.Id, RunTrigger.Scheduled), rig.Runs.Runs[0]);
    }

    [Fact]
    public async Task The_running_loop_notices_a_resume_from_suspension_and_recovers_after_the_grace_period()
    {
        await using var rig = new SchedulerRig(Utc(2026, 11, 20, 12, 0));
        var job = await rig.AddJobAsync("Diaria", Daily("02:00"));
        await rig.Scheduler.StartAsync(Ct);
        await Task.Delay(50, Ct); // que el bucle registre su temporizador

        // El equipo se suspende por la noche y reanuda a las 07:00 UTC: el reloj salta.
        var resumed = Utc(2026, 11, 21, 7, 0);
        rig.Time.SetUtcNow(resumed);
        for (var i = 0; i < 200 && rig.Runs.Runs.Count == 0; i++)
        {
            rig.Time.Advance(TimeSpan.FromMinutes(1));
            await Task.Delay(20, Ct);
        }

        await WaitUntilAsync(() => rig.Runs.Runs.Count >= 1);
        Assert.Equal(new EnqueuedRun(job.Id, RunTrigger.CatchUp), rig.Runs.Runs[0]);
        Assert.True(rig.Time.GetUtcNow() - resumed >= TimeSpan.FromMinutes(5), "La recuperación no puede empezar antes del periodo de gracia.");
        Assert.Single(rig.Runs.Runs);
    }

    [Fact]
    public async Task Stopping_the_scheduler_stops_the_loop_and_it_enqueues_nothing_more()
    {
        await using var rig = new SchedulerRig(Utc(2026, 11, 21, 0, 59, 0));
        await rig.AddJobAsync("Diaria", Daily("02:00"));
        await rig.Scheduler.StartAsync(Ct);

        await rig.Scheduler.StopAsync(Ct);
        rig.Time.Advance(TimeSpan.FromMinutes(3));
        await Task.Delay(100, Ct);

        Assert.Empty(rig.Runs.Runs);
    }
}
