using Backbone.Contracts;
using Backbone.Service.Ipc;
using Microsoft.Extensions.Time.Testing;

namespace Backbone.Service.Tests.Ipc;

/// <summary>HT-03 criterio 3: <c>RunProgress</c> no supera 4 mensajes por segundo y el último valor siempre llega.</summary>
[Trait("Story", "HT-03")]
public sealed class RunProgressThrottleTests : IDisposable
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero));
    private readonly List<(TimeSpan At, RunProgressEvent Event)> _sent = [];
    private readonly DateTimeOffset _start;
    private readonly RunProgressThrottle _throttle;

    public RunProgressThrottleTests()
    {
        _start = _time.GetUtcNow();
        _throttle = new RunProgressThrottle(_time, new IpcOptions().RunProgressInterval, e => _sent.Add((_time.GetUtcNow() - _start, e)));
    }

    public void Dispose() => _throttle.Dispose();

    [Fact]
    public void The_default_interval_allows_four_messages_per_second()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(250), new IpcOptions().RunProgressInterval);
        Assert.Equal(4, BackboneIpc.MaxRunProgressEventsPerSecond);
    }

    [Fact]
    public void A_burst_of_progress_never_exceeds_four_messages_in_any_one_second_window()
    {
        // 1 000 eventos en 10 s (uno cada 10 ms).
        for (var i = 1; i <= 1000; i++)
        {
            _throttle.Publish(Progress(i));
            _time.Advance(TimeSpan.FromMilliseconds(10));
        }

        _time.Advance(TimeSpan.FromSeconds(1));

        Assert.InRange(_sent.Count, 38, 41);
        for (var i = 0; i < _sent.Count; i++)
        {
            var inWindow = _sent.Count(s => s.At >= _sent[i].At && s.At < _sent[i].At + TimeSpan.FromSeconds(1));
            Assert.True(inWindow <= BackboneIpc.MaxRunProgressEventsPerSecond, $"{inWindow} mensajes en el segundo que empieza en {_sent[i].At}.");
        }
    }

    [Fact]
    public void The_first_event_is_sent_immediately_and_the_last_one_is_never_lost()
    {
        _throttle.Publish(Progress(1));
        Assert.Single(_sent);

        _throttle.Publish(Progress(2));
        _throttle.Publish(Progress(3));
        Assert.Single(_sent);

        _time.Advance(TimeSpan.FromMilliseconds(250));

        Assert.Equal([1L, 3L], _sent.Select(s => s.Event.FilesProcessed));
    }

    [Fact]
    public void After_a_quiet_period_an_event_is_sent_immediately()
    {
        _throttle.Publish(Progress(1));
        _time.Advance(TimeSpan.FromSeconds(5));

        _throttle.Publish(Progress(2));

        Assert.Equal([1L, 2L], _sent.Select(s => s.Event.FilesProcessed));
    }

    [Fact]
    public void Pending_progress_of_a_finished_run_is_discarded()
    {
        _throttle.Publish(Progress(1));
        _throttle.Publish(Progress(2));

        _throttle.Discard(runId: 42);
        _time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal([1L], _sent.Select(s => s.Event.FilesProcessed));
    }

    [Fact]
    public void Discard_of_another_run_keeps_the_pending_progress()
    {
        _throttle.Publish(Progress(1));
        _throttle.Publish(Progress(2));

        _throttle.Discard(runId: 41);
        _time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal([1L, 2L], _sent.Select(s => s.Event.FilesProcessed));
    }

    private static RunProgressEvent Progress(long files) => new() { RunId = 42, FilesProcessed = files };
}
