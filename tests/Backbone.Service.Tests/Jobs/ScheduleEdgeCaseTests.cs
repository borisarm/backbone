using Backbone.Contracts;
using Backbone.Service.Jobs;
using Backbone.Service.Tests.TestSupport;
using static Backbone.Service.Tests.TestSupport.SchedulerRig;

namespace Backbone.Service.Tests.Jobs;

/// <summary>
/// HU-09, ampliación de QA (criterios 1, 3, 5 y 6): límites del cálculo (instante exacto, días sin valor, hemisferio sur,
/// intervalo fuera de rango) y del <see cref="Scheduler"/> (cambios del reloj del sistema, entradas raras en
/// <c>schedule.json</c>, fallo de escritura, parada con una recuperación pendiente).
/// </summary>
[Trait("Story", "HU-09")]
public sealed class ScheduleEdgeCaseTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TimeZoneInfo Auckland = TimeZoneInfo.FindSystemTimeZoneById("New Zealand Standard Time");

    // ---- Cálculo ----

    [Fact]
    public void A_daily_time_exactly_at_the_reference_instant_is_not_returned_but_the_next_day_is()
    {
        // 02:00 local de Madrid = 01:00 UTC; "estrictamente posterior".
        Assert.Equal(Utc(2026, 11, 21, 1, 0), ScheduleCalculator.Next(Daily("02:00"), Utc(2026, 11, 20, 1, 0), SchedulerRig.Madrid));
        Assert.Equal(Utc(2026, 11, 20, 1, 0), ScheduleCalculator.Next(Daily("02:00"), Utc(2026, 11, 20, 0, 59, 59), SchedulerRig.Madrid));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0x80)]
    [InlineData(0x100)]
    public void A_daily_schedule_with_no_valid_day_has_no_next_point(int rawDays)
    {
        Assert.Null(ScheduleCalculator.Next(Daily("02:00", (DaysOfWeek)rawDays), Utc(2026, 11, 20, 12, 0), SchedulerRig.Madrid));
    }

    [Fact]
    public void Repeated_times_do_not_produce_two_points()
    {
        var schedule = Daily("02:00,02:00,02:00");
        var first = ScheduleCalculator.Next(schedule, Utc(2026, 11, 20, 0, 0), SchedulerRig.Madrid);
        var second = ScheduleCalculator.Next(schedule, first!.Value, SchedulerRig.Madrid);

        Assert.Equal(Utc(2026, 11, 20, 1, 0), first);
        Assert.Equal(Utc(2026, 11, 21, 1, 0), second);
    }

    [Theory]
    [InlineData(-5, 1)]
    [InlineData(0, 1)]
    [InlineData(25, 24)]
    [InlineData(int.MaxValue, 24)]
    public void An_interval_out_of_range_is_clamped_and_never_throws(int hours, int expectedHours)
    {
        var after = Utc(2026, 11, 20, 12, 0);

        Assert.Equal(after.AddHours(expectedHours), ScheduleCalculator.Next(Every(hours), after, SchedulerRig.Madrid));
    }

    [Fact]
    public void A_24_hour_interval_across_the_spring_change_is_24_clock_hours_not_the_same_local_time()
    {
        // 28-03-2026 13:00 local (12:00 UTC) + 24 h = 29-03 12:00 UTC = 14:00 local (el reloj local saltó una hora).
        var next = ScheduleCalculator.Next(Every(24), Utc(2026, 3, 28, 12, 0), SchedulerRig.Madrid)!.Value;

        Assert.Equal(Utc(2026, 3, 29, 12, 0), next);
        Assert.Equal(14, TimeZoneInfo.ConvertTime(next, SchedulerRig.Madrid).Hour);
    }

    [Fact]
    public void Southern_hemisphere_spring_gap_runs_at_the_first_valid_instant()
    {
        // Auckland: 27-09-2026 a las 02:00 NZST (26-09 14:00 UTC) el reloj salta a las 03:00 NZDT. 02:30 no existe.
        Assert.Equal(Utc(2026, 9, 26, 14, 0), ScheduleCalculator.Next(Daily("02:30"), Utc(2026, 9, 26, 6, 0), Auckland));
        // El día siguiente, 02:30 NZDT (UTC+13) = 27-09 13:30 UTC.
        Assert.Equal(Utc(2026, 9, 27, 13, 30), ScheduleCalculator.Next(Daily("02:30"), Utc(2026, 9, 26, 14, 0), Auckland));
    }

    [Fact]
    public void Southern_hemisphere_autumn_overlap_runs_once_at_the_first_occurrence()
    {
        // Auckland: 05-04-2026 a las 03:00 NZDT (04-04 14:00 UTC) el reloj vuelve a las 02:00 NZST. 02:30 ocurre dos veces.
        var first = ScheduleCalculator.Next(Daily("02:30"), Utc(2026, 4, 4, 6, 0), Auckland)!.Value;
        Assert.Equal(Utc(2026, 4, 4, 13, 30), first); // primera aparición, NZDT (UTC+13)
        var next = ScheduleCalculator.Next(Daily("02:30"), first, Auckland)!.Value;
        Assert.Equal(Utc(2026, 4, 5, 14, 30), next); // no la segunda aparición (04-04 14:30 UTC): el día siguiente, NZST (UTC+12)
    }

    // ---- Scheduler: reloj del sistema ----

    [Fact]
    public async Task The_system_clock_jumping_forward_over_a_point_is_a_missed_run_recovered_after_the_grace_period()
    {
        await using var rig = new SchedulerRig(Utc(2026, 11, 20, 12, 0));
        await rig.StartAsync();
        var job = await rig.AddJobAsync("Diaria", Daily("02:00"));
        await rig.EvaluateAsync();

        await rig.AtAsync(Utc(2026, 11, 23, 12, 0)); // el usuario adelanta el reloj tres días
        Assert.Empty(rig.Runs.Runs);
        await rig.AtAsync(Utc(2026, 11, 23, 12, 5));

        Assert.Equal(new EnqueuedRun(job.Id, RunTrigger.CatchUp), Assert.Single(rig.Runs.Runs));
        Assert.Equal(Utc(2026, 11, 24, 1, 0), rig.Scheduler.NextRunUtc(job.Id));
    }

    [Fact]
    public async Task The_system_clock_going_back_does_not_repeat_a_point_already_consumed()
    {
        await using var rig = new SchedulerRig(Utc(2026, 11, 20, 12, 0));
        await rig.StartAsync();
        var job = await rig.AddJobAsync("Diaria", Daily("02:00"));
        await rig.EvaluateAsync();
        await rig.AtAsync(Utc(2026, 11, 21, 1, 0, 10));
        Assert.Single(rig.Runs.Runs);

        rig.Time.AdjustTime(Utc(2026, 11, 20, 23, 0)); // el reloj retrocede 2 horas
        await rig.EvaluateAsync();
        await rig.AtAsync(Utc(2026, 11, 21, 1, 0, 30)); // vuelve a pasar por las 02:00 locales

        Assert.Single(rig.Runs.Runs);
        Assert.Equal(Utc(2026, 11, 22, 1, 0), rig.Scheduler.NextRunUtc(job.Id));
    }

    [Fact(Skip = "Falla: con el servicio en marcha, si el reloj retrocede, un trabajo por Intervalo cuenta desde un punto consumido que está en el futuro (solo se recorta al arrancar) — pendiente de backbone-service")]
    public async Task The_system_clock_going_back_does_not_push_an_interval_job_beyond_the_interval_from_now()
    {
        await using var rig = new SchedulerRig(Utc(2026, 11, 20, 12, 0));
        await rig.StartAsync();
        var job = await rig.AddJobAsync("Cada 4 h", Every(4));
        await rig.EvaluateAsync();
        await rig.AtAsync(Utc(2026, 11, 20, 16, 0, 5));
        Assert.Single(rig.Runs.Runs);

        rig.Time.AdjustTime(Utc(2026, 11, 18, 16, 0)); // el reloj retrocede dos días
        await rig.EvaluateAsync();

        // El siguiente punto no debería quedar a dos días más cuatro horas: como mucho, ahora + intervalo.
        Assert.True(rig.Scheduler.NextRunUtc(job.Id) <= Utc(2026, 11, 18, 20, 0));
    }

    // ---- Scheduler: schedule.json ----

    [Fact]
    public async Task Entries_of_nonexistent_jobs_are_removed_and_duplicate_ids_do_not_break_the_start()
    {
        await using var rig = new SchedulerRig(Utc(2026, 11, 20, 7, 0));
        var job = await rig.AddJobAsync("Diaria", Daily("02:00"));
        var ghost = Guid.NewGuid();
        await File.WriteAllTextAsync(
            rig.SchedulePath,
            $$"""{ "schemaVersion": 1, "jobs": [ { "jobId": "{{ghost}}", "lastConsumedUtc": "2026-11-19T01:00:00Z" }, { "jobId": "{{job.Id}}", "lastConsumedUtc": "2026-11-10T01:00:00Z" }, { "jobId": "{{job.Id}}", "lastConsumedUtc": "2026-11-20T01:00:00Z" } ] }""",
            Ct);

        await rig.StartAsync();

        var saved = await rig.SavedEntriesAsync();
        Assert.False(saved.ContainsKey(ghost));
        Assert.Equal(Utc(2026, 11, 20, 1, 0), saved[job.Id]); // gana la última
        Assert.Empty(rig.Runs.Runs); // el punto del 20-11 ya está hecho: no hay recuperación
        Assert.Equal(Utc(2026, 11, 21, 1, 0), rig.Scheduler.NextRunUtc(job.Id));
    }

    [Fact]
    public async Task A_write_failure_in_schedule_json_keeps_the_schedule_working_without_duplicates_and_leaves_the_old_file()
    {
        await using var rig = new SchedulerRig(Utc(2026, 11, 20, 12, 0));
        await rig.StartAsync();
        var job = await rig.AddJobAsync("Diaria", Daily("02:00"));
        await rig.EvaluateAsync();
        var before = await File.ReadAllBytesAsync(rig.SchedulePath, Ct);

        using (new FileStream(rig.SchedulePath, FileMode.Open, FileAccess.Read, FileShare.None)) // bloquea el reemplazo
        {
            await rig.AtAsync(Utc(2026, 11, 21, 1, 0, 10));
        }

        var after = await File.ReadAllBytesAsync(rig.SchedulePath, Ct);
        Assert.Equal(new EnqueuedRun(job.Id, RunTrigger.Scheduled), Assert.Single(rig.Runs.Runs));
        Assert.Equal(before, after); // el archivo anterior intacto: ni a medias ni vacío
        Assert.Equal([rig.SchedulePath], Directory.EnumerateFileSystemEntries(rig.DataDirectory)); // sin temporales
        Assert.Equal(Utc(2026, 11, 22, 1, 0), rig.Scheduler.NextRunUtc(job.Id));

        await rig.AtAsync(Utc(2026, 11, 21, 12, 0));
        Assert.Single(rig.Runs.Runs);
    }

    // ---- Scheduler: parada con una recuperación pendiente ----

    [Fact]
    public async Task Stopping_with_a_pending_recovery_enqueues_nothing_and_the_next_start_detects_it_again()
    {
        var rig = new SchedulerRig(Utc(2026, 11, 20, 0, 30));
        await using var disposable = rig;
        await rig.StartAsync();
        var job = await rig.AddJobAsync("Diaria", Daily("02:00"));
        await rig.EvaluateAsync();
        rig.Time.SetUtcNow(Utc(2026, 11, 20, 7, 0));
        await rig.RestartAsync();
        Assert.Empty(rig.Runs.Runs); // en gracia

        await rig.Scheduler.StopAsync(Ct);
        rig.Time.SetUtcNow(Utc(2026, 11, 20, 7, 10));
        Assert.Empty(rig.Runs.Runs); // parado: ya no se encola nada
        Assert.Equal(Utc(2026, 11, 20, 0, 30), (await rig.SavedEntriesAsync())[job.Id]); // sin consumir

        await rig.RestartAsync();
        await rig.AtAsync(Utc(2026, 11, 20, 7, 15));
        Assert.Equal(new EnqueuedRun(job.Id, RunTrigger.CatchUp), Assert.Single(rig.Runs.Runs));
    }
}
