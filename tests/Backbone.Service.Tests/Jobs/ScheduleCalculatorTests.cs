using Backbone.Contracts;
using Backbone.Service.Jobs;
using Backbone.Service.Tests.TestSupport;

namespace Backbone.Service.Tests.Jobs;

/// <summary>
/// HU-09 criterios 1 y 6 (RF-10): el cálculo del próximo punto de programación. Diaria en hora local con las reglas del horario
/// de verano de la zona inyectada ("Romance Standard Time": UTC+1 en invierno, UTC+2 en verano; salto el 29-03-2026 a las 02:00
/// y retroceso el 25-10-2026 a las 03:00), intervalo en UTC. Nunca depende de la zona del equipo de pruebas.
/// </summary>
[Trait("Story", "HU-09")]
public sealed class ScheduleCalculatorTests
{
    private static readonly TimeZoneInfo Madrid = SchedulerRig.Madrid;

    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute, int second = 0) =>
        SchedulerRig.Utc(year, month, day, hour, minute, second);

    private static DateTimeOffset? Next(JobSchedule schedule, DateTimeOffset after, TimeZoneInfo? zone = null) =>
        ScheduleCalculator.Next(schedule, after, zone ?? Madrid);

    // ---- Tipos (criterio 1) ----

    [Fact]
    public void A_manual_schedule_has_no_next_point()
    {
        Assert.Null(Next(new JobSchedule { Kind = ScheduleKind.Manual }, Utc(2026, 11, 20, 12, 0)));
    }

    [Fact]
    public void A_daily_schedule_without_times_or_days_has_no_next_point()
    {
        Assert.Null(Next(new JobSchedule { Kind = ScheduleKind.Daily, TimesOfDay = [], Days = DaysOfWeek.All }, Utc(2026, 11, 20, 12, 0)));
        Assert.Null(Next(SchedulerRig.Daily("02:00", DaysOfWeek.None), Utc(2026, 11, 20, 12, 0)));
    }

    [Fact]
    public void Daily_uses_the_local_time_of_the_zone_not_utc()
    {
        // 20-11-2026 es invierno (UTC+1): las 02:00 locales son las 01:00 UTC.
        Assert.Equal(Utc(2026, 11, 21, 1, 0), Next(SchedulerRig.Daily("02:00"), Utc(2026, 11, 20, 12, 0)));
        Assert.Equal(Utc(2026, 11, 20, 1, 0), Next(SchedulerRig.Daily("02:00"), Utc(2026, 11, 19, 12, 0)));
    }

    [Fact]
    public void Daily_depends_on_the_injected_zone_only()
    {
        var utc = TimeZoneInfo.Utc;
        var newYork = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

        Assert.Equal(Utc(2026, 11, 21, 2, 0), Next(SchedulerRig.Daily("02:00"), Utc(2026, 11, 20, 12, 0), utc));
        Assert.Equal(Utc(2026, 11, 21, 7, 0), Next(SchedulerRig.Daily("02:00"), Utc(2026, 11, 20, 12, 0), newYork));
    }

    [Fact]
    public void Daily_takes_the_nearest_of_several_times_and_the_point_is_strictly_later_than_the_reference()
    {
        var schedule = SchedulerRig.Daily("08:00,13:30,20:00");

        Assert.Equal(Utc(2026, 11, 20, 12, 30), Next(schedule, Utc(2026, 11, 20, 10, 0))); // 13:30 local
        Assert.Equal(Utc(2026, 11, 20, 19, 0), Next(schedule, Utc(2026, 11, 20, 12, 30))); // estrictamente posterior: 20:00 local
        Assert.Equal(Utc(2026, 11, 21, 7, 0), Next(schedule, Utc(2026, 11, 20, 19, 0))); // 08:00 local del día siguiente
    }

    [Fact]
    public void Daily_only_runs_on_the_selected_days()
    {
        // 20-11-2026 es viernes. Solo lunes y miércoles: el siguiente es el lunes 23-11.
        var schedule = SchedulerRig.Daily("02:00", DaysOfWeek.Monday | DaysOfWeek.Wednesday);

        Assert.Equal(Utc(2026, 11, 23, 1, 0), Next(schedule, Utc(2026, 11, 20, 12, 0)));
        Assert.Equal(Utc(2026, 11, 25, 1, 0), Next(schedule, Utc(2026, 11, 23, 1, 0)));
        Assert.Equal(Utc(2026, 11, 30, 1, 0), Next(schedule, Utc(2026, 11, 25, 1, 0)));
    }

    [Fact]
    public void Daily_with_a_single_day_finds_it_a_week_later()
    {
        // Solo viernes, y ya ha pasado el de hoy.
        Assert.Equal(Utc(2026, 11, 27, 1, 0), Next(SchedulerRig.Daily("02:00", DaysOfWeek.Friday), Utc(2026, 11, 20, 1, 0)));
    }

    [Fact]
    public void Daily_day_is_the_local_day_not_the_utc_day()
    {
        // 00:30 local del lunes 23-11 son las 23:30 UTC del domingo 22-11: cuenta como lunes.
        Assert.Equal(Utc(2026, 11, 22, 23, 30), Next(SchedulerRig.Daily("00:30", DaysOfWeek.Monday), Utc(2026, 11, 20, 12, 0)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(24)]
    public void Interval_is_the_reference_plus_n_hours_in_utc(int hours)
    {
        var after = Utc(2026, 11, 20, 12, 15, 30);

        Assert.Equal(after.AddHours(hours), Next(SchedulerRig.Every(hours), after));
    }

    [Fact]
    public void Interval_is_not_affected_by_daylight_saving()
    {
        // Cada 6 h a través del salto del 29-03-2026 (01:00 UTC): siempre exactamente 6 h de reloj.
        Assert.Equal(Utc(2026, 3, 29, 4, 0), Next(SchedulerRig.Every(6), Utc(2026, 3, 28, 22, 0)));
        // Y a través del retroceso del 25-10-2026.
        Assert.Equal(Utc(2026, 10, 25, 3, 0), Next(SchedulerRig.Every(6), Utc(2026, 10, 24, 21, 0)));
    }

    // ---- Horario de verano (criterio 6) ----

    [Fact]
    public void A_daily_time_that_does_not_exist_runs_at_the_first_valid_instant_after_the_jump()
    {
        // 29-03-2026: a las 02:00 locales (01:00 UTC) el reloj salta a las 03:00. 02:30 no existe: se ejecuta a las 03:00 locales
        // (01:00 UTC), el primer instante válido tras el salto.
        Assert.Equal(Utc(2026, 3, 29, 1, 0), Next(SchedulerRig.Daily("02:30"), Utc(2026, 3, 28, 12, 0)));
        Assert.Equal(Utc(2026, 3, 29, 1, 0), Next(SchedulerRig.Daily("02:00"), Utc(2026, 3, 28, 12, 0)));
        Assert.Equal(Utc(2026, 3, 29, 1, 0), Next(SchedulerRig.Daily("02:59:30"), Utc(2026, 3, 28, 12, 0)));
    }

    [Fact]
    public void A_nonexistent_time_and_the_first_valid_time_do_not_run_twice_and_the_next_day_is_normal()
    {
        var schedule = SchedulerRig.Daily("02:30,03:00");

        Assert.Equal(Utc(2026, 3, 29, 1, 0), Next(schedule, Utc(2026, 3, 28, 12, 0)));
        // Tras ese punto, lo siguiente es el día 30 a las 02:30 locales, ya en horario de verano (UTC+2): 00:30 UTC.
        Assert.Equal(Utc(2026, 3, 30, 0, 30), Next(schedule, Utc(2026, 3, 29, 1, 0)));
    }

    [Fact]
    public void Times_around_the_jump_keep_their_local_hour_on_both_sides()
    {
        var schedule = SchedulerRig.Daily("01:30,04:00");

        Assert.Equal(Utc(2026, 3, 29, 0, 30), Next(schedule, Utc(2026, 3, 28, 12, 0))); // 01:30 CET
        Assert.Equal(Utc(2026, 3, 29, 2, 0), Next(schedule, Utc(2026, 3, 29, 0, 30))); // 04:00 CEST
    }

    [Fact]
    public void A_daily_time_after_the_jump_is_one_utc_hour_earlier_than_before_it()
    {
        // 02:00 local: 01:00 UTC en invierno y 00:00 UTC en verano.
        Assert.Equal(Utc(2026, 3, 28, 1, 0), Next(SchedulerRig.Daily("02:00"), Utc(2026, 3, 27, 12, 0)));
        Assert.Equal(Utc(2026, 3, 30, 0, 0), Next(SchedulerRig.Daily("02:00"), Utc(2026, 3, 29, 12, 0)));
    }

    [Fact]
    public void An_ambiguous_time_runs_once_at_its_first_occurrence()
    {
        // 25-10-2026: a las 03:00 locales (01:00 UTC) el reloj retrocede a las 02:00. 02:30 ocurre dos veces: 00:30 UTC (CEST) y
        // 01:30 UTC (CET). Solo cuenta la primera.
        var schedule = SchedulerRig.Daily("02:30");

        var first = Next(schedule, Utc(2026, 10, 24, 12, 0));
        Assert.Equal(Utc(2026, 10, 25, 0, 30), first);

        var afterFirst = Next(schedule, first!.Value);
        Assert.Equal(Utc(2026, 10, 26, 1, 30), afterFirst); // la segunda aparición del día 25 (01:30 UTC) no cuenta: el siguiente es el día 26 en CET
    }

    [Fact]
    public void A_time_before_the_ambiguous_hour_and_a_time_after_it_are_unaffected()
    {
        var schedule = SchedulerRig.Daily("01:30,03:30");

        Assert.Equal(Utc(2026, 10, 24, 23, 30), Next(schedule, Utc(2026, 10, 24, 12, 0))); // 01:30 CEST
        Assert.Equal(Utc(2026, 10, 25, 2, 30), Next(schedule, Utc(2026, 10, 24, 23, 30))); // 03:30 CET (tras el retroceso)
    }

    [Fact]
    public void A_far_overdue_reference_still_finds_the_next_point()
    {
        Assert.Equal(Utc(2027, 1, 2, 1, 0), Next(SchedulerRig.Daily("02:00", DaysOfWeek.Saturday), Utc(2026, 12, 31, 12, 0)));
    }
}
