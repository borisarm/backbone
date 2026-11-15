namespace Backbone.Core.Tests.Support;

/// <summary>
/// Reloj manual con temporizadores (equivalente mínimo de FakeTimeProvider, sin añadir paquetes):
/// el tiempo solo avanza con <see cref="Advance"/>, que dispara los temporizadores vencidos.
/// </summary>
public sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private readonly Lock gate = new();
    private readonly List<ManualTimer> timers = [];
    private DateTimeOffset now = start;

    public ManualTimeProvider()
        : this(new DateTimeOffset(2026, 11, 15, 2, 0, 0, TimeSpan.Zero))
    {
    }

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public int ActiveTimerCount
    {
        get
        {
            lock (gate)
            {
                return timers.Count(t => t.DueAt is not null);
            }
        }
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (gate)
        {
            return now;
        }
    }

    public DateTime UtcNow => GetUtcNow().UtcDateTime;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (gate)
        {
            timers.Add(timer);
        }

        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan delta)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delta, TimeSpan.Zero);
        DateTimeOffset target;
        lock (gate)
        {
            target = now + delta;
        }

        while (true)
        {
            ManualTimer? due;
            lock (gate)
            {
                due = timers
                    .Where(t => t.DueAt is { } at && at <= target)
                    .OrderBy(t => t.DueAt)
                    .FirstOrDefault();
                if (due is null)
                {
                    now = target;
                    return;
                }

                now = due.DueAt.GetValueOrDefault();
                due.DueAt = due.Period is { } p && p > TimeSpan.Zero && p != Timeout.InfiniteTimeSpan ? now + p : null;
            }

            due.Fire();
        }
    }

    /// <summary>
    /// Salto del reloj sin que los temporizadores se disparen por el camino (suspensión del equipo):
    /// al llegar, se disparan una vez los vencidos.
    /// </summary>
    public void Jump(TimeSpan delta)
    {
        List<ManualTimer> due;
        lock (gate)
        {
            now += delta;
            due = [.. timers.Where(t => t.DueAt is { } at && at <= now)];
            foreach (var timer in due)
            {
                timer.DueAt = timer.Period is { } p && p > TimeSpan.Zero && p != Timeout.InfiniteTimeSpan ? now + p : null;
            }
        }

        foreach (var timer in due)
        {
            timer.Fire();
        }
    }

    private void Remove(ManualTimer timer)
    {
        lock (gate)
        {
            timers.Remove(timer);
        }
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? DueAt { get; set; }

        public TimeSpan? Period { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner.gate)
            {
                DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner.now + dueTime;
                Period = period;
            }

            return true;
        }

        public void Fire() => callback(state);

        public void Dispose() => owner.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
