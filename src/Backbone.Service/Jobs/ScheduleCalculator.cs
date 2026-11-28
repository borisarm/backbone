using Backbone.Contracts;

namespace Backbone.Service.Jobs;

/// <summary>
/// Cálculo del próximo punto de programación de un trabajo (RF-10, §8.2 "Programación"). Es una función pura: no lee el
/// reloj ni la zona horaria del equipo, se los pasa quien llama (<see cref="TimeProvider.LocalTimeZone"/>).
/// </summary>
internal static class ScheduleCalculator
{
    /// <summary>
    /// Primer punto de programación estrictamente posterior a <paramref name="afterUtc"/> (el último punto consumido o "ahora").
    /// </summary>
    /// <returns>El instante en UTC; <see langword="null"/> si el trabajo es manual o la programación diaria está incompleta.</returns>
    public static DateTimeOffset? Next(JobSchedule schedule, DateTimeOffset afterUtc, TimeZoneInfo zone) => schedule.Kind switch
    {
        ScheduleKind.Interval => afterUtc.ToUniversalTime().AddHours(Math.Clamp(schedule.IntervalHours, JobDefaults.MinIntervalHours, JobDefaults.MaxIntervalHours)),
        ScheduleKind.Daily => NextDaily(schedule, afterUtc.ToUniversalTime(), zone),
        _ => null,
    };

    private static DateTimeOffset? NextDaily(JobSchedule schedule, DateTimeOffset afterUtc, TimeZoneInfo zone)
    {
        if (schedule.TimesOfDay.Count == 0 || (schedule.Days & DaysOfWeek.All) == DaysOfWeek.None)
        {
            return null;
        }

        var afterLocalDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(afterUtc, zone).DateTime);
        DateTimeOffset? best = null;

        // Una semana completa más un día de margen por cada lado: los desfases de la zona (hasta 14 h) pueden hacer que la
        // fecha local anterior a la de "después" ya caiga por delante en UTC.
        for (var offset = -1; offset <= 8; offset++)
        {
            var date = afterLocalDate.AddDays(offset);
            if (!Includes(schedule.Days, date.DayOfWeek))
            {
                continue;
            }

            foreach (var time in schedule.TimesOfDay)
            {
                var candidate = ToUtc(date, time, zone);
                if (candidate > afterUtc && (best is null || candidate < best))
                {
                    best = candidate;
                }
            }
        }

        return best;
    }

    private static bool Includes(DaysOfWeek days, DayOfWeek day) => (days & ToFlag(day)) != DaysOfWeek.None;

    private static DaysOfWeek ToFlag(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => DaysOfWeek.Monday,
        DayOfWeek.Tuesday => DaysOfWeek.Tuesday,
        DayOfWeek.Wednesday => DaysOfWeek.Wednesday,
        DayOfWeek.Thursday => DaysOfWeek.Thursday,
        DayOfWeek.Friday => DaysOfWeek.Friday,
        DayOfWeek.Saturday => DaysOfWeek.Saturday,
        _ => DaysOfWeek.Sunday,
    };

    /// <summary>
    /// Instante UTC de una hora local. Hora inexistente (salto hacia delante): el primer instante válido tras el salto. Hora
    /// ambigua (retroceso): la primera aparición.
    /// </summary>
    private static DateTimeOffset ToUtc(DateOnly date, TimeOnly time, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
        {
            // El salto acaba en un minuto exacto: se avanza minuto a minuto desde el inicio del minuto hasta la primera hora válida.
            var probe = new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, 0, DateTimeKind.Unspecified);
            while (zone.IsInvalidTime(probe))
            {
                probe = probe.AddMinutes(1);
            }

            return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(probe, zone), TimeSpan.Zero);
        }

        if (zone.IsAmbiguousTime(local))
        {
            // La primera aparición es la del desfase mayor (la hora de verano, que va por delante de la estándar).
            var firstOffset = zone.GetAmbiguousTimeOffsets(local).Max();
            return new DateTimeOffset(DateTime.SpecifyKind(local - firstOffset, DateTimeKind.Utc));
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
    }
}
