using Backbone.Contracts;

namespace Backbone.Service.Ipc;

/// <summary>
/// Eventos push del contrato (§7.2) para el resto del servicio (scheduler, JobRunner, restauración).
/// </summary>
internal interface IIpcEventPublisher
{
    /// <summary>A todos los clientes.</summary>
    void PublishStatusChanged(StatusChangedEvent e);

    /// <summary>A todos los clientes, como máximo <see cref="BackboneIpc.MaxRunProgressEventsPerSecond"/> por segundo: si llegan más, se envía el último.</summary>
    void PublishRunProgress(RunProgressEvent e);

    /// <summary>A todos los clientes. Descarta el progreso pendiente de esa ejecución.</summary>
    void PublishRunCompleted(RunCompletedEvent e);

    /// <summary>Solo a las conexiones de <paramref name="ownerSid"/>, el SID que inició la restauración (§7.3 A-2).</summary>
    void PublishRestoreProgress(string ownerSid, RestoreProgressEvent e);

    /// <summary>Solo a las conexiones de <paramref name="ownerSid"/> (§7.3 A-2).</summary>
    void PublishRestoreConflict(string ownerSid, RestoreConflictEvent e);
}

/// <summary><see cref="IIpcEventPublisher"/> sobre las conexiones del <see cref="IpcConnectionManager"/>.</summary>
internal sealed class IpcEventPublisher : IIpcEventPublisher, IDisposable
{
    private readonly IpcConnectionManager _connections;
    private readonly RunProgressThrottle _runProgress;

    public IpcEventPublisher(IpcConnectionManager connections, IpcOptions options, TimeProvider timeProvider)
    {
        _connections = connections;
        _runProgress = new RunProgressThrottle(
            timeProvider,
            options.RunProgressInterval,
            e => _connections.Broadcast(nameof(IBackboneService.RunProgress), e));
    }

    public void PublishStatusChanged(StatusChangedEvent e) =>
        _connections.Broadcast(nameof(IBackboneService.StatusChanged), Require(e));

    public void PublishRunProgress(RunProgressEvent e) => _runProgress.Publish(Require(e));

    public void PublishRunCompleted(RunCompletedEvent e)
    {
        _runProgress.Discard(Require(e).Run.RunId);
        _connections.Broadcast(nameof(IBackboneService.RunCompleted), e);
    }

    public void PublishRestoreProgress(string ownerSid, RestoreProgressEvent e) =>
        _connections.SendToSid(ownerSid, nameof(IBackboneService.RestoreProgress), Require(e));

    public void PublishRestoreConflict(string ownerSid, RestoreConflictEvent e) =>
        _connections.SendToSid(ownerSid, nameof(IBackboneService.RestoreConflict), Require(e));

    public void Dispose() => _runProgress.Dispose();

    private static T Require<T>(T e)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(e);
        return e;
    }
}

/// <summary>
/// Limita <c>RunProgress</c> a un envío por intervalo (250 ms = 4 por segundo). Un evento que llega antes de tiempo queda
/// pendiente y se envía al cumplirse el intervalo; si llegan varios, solo el último (se agrupan).
/// </summary>
internal sealed class RunProgressThrottle : IDisposable
{
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _interval;
    private readonly Action<RunProgressEvent> _send;
    private readonly Lock _gate = new();
    private readonly ITimer _timer;
    private RunProgressEvent? _pending;
    private long _lastSent = long.MinValue;
    private bool _timerArmed;

    public RunProgressThrottle(TimeProvider timeProvider, TimeSpan interval, Action<RunProgressEvent> send)
    {
        _timeProvider = timeProvider;
        _interval = interval;
        _send = send;
        _timer = timeProvider.CreateTimer(static s => ((RunProgressThrottle)s!).OnTimer(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Publish(RunProgressEvent e)
    {
        RunProgressEvent? sendNow = null;
        lock (_gate)
        {
            var now = _timeProvider.GetTimestamp();
            if (!_timerArmed && (_lastSent == long.MinValue || _timeProvider.GetElapsedTime(_lastSent, now) >= _interval))
            {
                _lastSent = now;
                sendNow = e;
            }
            else
            {
                _pending = e;
                if (!_timerArmed)
                {
                    _timerArmed = true;
                    var due = _interval - _timeProvider.GetElapsedTime(_lastSent, now);
                    _timer.Change(due > TimeSpan.Zero ? due : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
                }
            }
        }

        if (sendNow is not null)
        {
            _send(sendNow);
        }
    }

    /// <summary>Descarta el progreso pendiente de una ejecución que ha terminado.</summary>
    public void Discard(long runId)
    {
        lock (_gate)
        {
            if (_pending is { } pending && pending.RunId == runId)
            {
                _pending = null;
            }
        }
    }

    public void Dispose() => _timer.Dispose();

    private void OnTimer()
    {
        RunProgressEvent? e;
        lock (_gate)
        {
            _timerArmed = false;
            e = _pending;
            _pending = null;
            if (e is not null)
            {
                _lastSent = _timeProvider.GetTimestamp();
            }
        }

        if (e is not null)
        {
            _send(e);
        }
    }
}
