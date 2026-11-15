using System.Collections.Concurrent;
using Backbone.Contracts;
using Backbone.Service.Tests.TestSupport;

namespace Backbone.Service.Tests.Ipc;

/// <summary>
/// HT-03 criterio 3 y §7.3 A-2: <c>StatusChanged</c>, <c>RunProgress</c> y <c>RunCompleted</c> llegan a todos los clientes
/// autorizados; los eventos de restauración, solo a las conexiones del SID propietario.
/// </summary>
[Trait("Story", "HT-03")]
public sealed class EventDeliveryTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private readonly IpcTestHarness _server = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => new(_server.AuthorizeAsync(TestSids.Ana, TestSids.Luis));

    public ValueTask DisposeAsync() => _server.DisposeAsync();

    [Fact]
    public async Task StatusChanged_reaches_every_connected_client()
    {
        var clients = new[]
        {
            await _server.ConnectAuthorizedAsync(TestSids.Identity(TestSids.Ana)),
            await _server.ConnectAuthorizedAsync(TestSids.Identity(TestSids.Ana)),
            await _server.ConnectAuthorizedAsync(TestSids.Identity(TestSids.Luis)),
        };
        var received = clients.Select(c => Subscribe<StatusChangedEvent>(h => c.Proxy.StatusChanged += h)).ToList();
        var sent = new StatusChangedEvent { Status = new ServiceStatus { Health = HealthState.Warning, TimestampUtc = _server.Time.GetUtcNow() } };

        _server.Events.PublishStatusChanged(sent);

        foreach (var r in received)
        {
            Assert.Equal(sent, await r.Task.WaitAsync(Wait, Ct), StatusEquality.Instance);
        }
    }

    [Fact]
    public async Task RunProgress_and_RunCompleted_reach_every_connected_client()
    {
        var ana = await _server.ConnectAuthorizedAsync(TestSids.Identity(TestSids.Ana));
        var luis = await _server.ConnectAuthorizedAsync(TestSids.Identity(TestSids.Luis));
        var progress = new[] { ana, luis }.Select(c => Subscribe<RunProgressEvent>(h => c.Proxy.RunProgress += h)).ToList();
        var completed = new[] { ana, luis }.Select(c => Subscribe<RunCompletedEvent>(h => c.Proxy.RunCompleted += h)).ToList();

        _server.Events.PublishRunProgress(new RunProgressEvent { RunId = 7, FilesProcessed = 10 });
        _server.Events.PublishRunCompleted(new RunCompletedEvent { Run = new RunRecord { RunId = 7, State = RunState.Completed } });

        foreach (var p in progress)
        {
            Assert.Equal(10, (await p.Task.WaitAsync(Wait, Ct)).FilesProcessed);
        }

        foreach (var c in completed)
        {
            Assert.Equal(RunState.Completed, (await c.Task.WaitAsync(Wait, Ct)).Run.State);
        }
    }

    [Fact]
    public async Task A_burst_of_RunProgress_reaches_the_client_at_most_four_times_per_second_ending_with_the_last_value()
    {
        var client = await _server.ConnectAuthorizedAsync(TestSids.Identity(TestSids.Ana));
        var received = new ConcurrentQueue<RunProgressEvent>();
        client.Proxy.RunProgress += (_, e) => received.Enqueue(e);

        // 100 eventos en 990 ms de reloj simulado.
        for (var i = 1; i <= 100; i++)
        {
            _server.Events.PublishRunProgress(new RunProgressEvent { RunId = 1, FilesProcessed = i });
            if (i < 100)
            {
                _server.Time.Advance(TimeSpan.FromMilliseconds(10));
            }
        }

        await WaitUntilAsync(() => received.Count >= 4);
        await Task.Delay(200, Ct);
        Assert.Equal(4, received.Count); // t = 0, 250, 500 y 750 ms

        _server.Time.Advance(TimeSpan.FromMilliseconds(250));
        await WaitUntilAsync(() => received.Count >= 5);
        Assert.Equal(100, received.Last().FilesProcessed);
    }

    [Fact]
    public async Task Restore_events_only_reach_the_connections_of_the_owner()
    {
        var ana1 = await _server.ConnectAuthorizedAsync(TestSids.Identity(TestSids.Ana));
        var ana2 = await _server.ConnectAuthorizedAsync(TestSids.Identity(TestSids.Ana, elevated: true));
        var luis = await _server.ConnectAuthorizedAsync(TestSids.Identity(TestSids.Luis));
        var anaProgress = new[] { ana1, ana2 }.Select(c => Subscribe<RestoreProgressEvent>(h => c.Proxy.RestoreProgress += h)).ToList();
        var anaConflict = Subscribe<RestoreConflictEvent>(h => ana1.Proxy.RestoreConflict += h);
        var luisEvents = new ConcurrentQueue<object>();
        luis.Proxy.RestoreProgress += (_, e) => luisEvents.Enqueue(e);
        luis.Proxy.RestoreConflict += (_, e) => luisEvents.Enqueue(e);
        var restoreId = Guid.NewGuid();

        _server.Events.PublishRestoreProgress(TestSids.Ana, new RestoreProgressEvent { RestoreId = restoreId, State = RestoreState.Running });
        _server.Events.PublishRestoreConflict(TestSids.Ana, new RestoreConflictEvent { RestoreId = restoreId, SnapshotPath = "Docs/a.txt" });

        foreach (var p in anaProgress)
        {
            Assert.Equal(restoreId, (await p.Task.WaitAsync(Wait, Ct)).RestoreId);
        }

        Assert.Equal("Docs/a.txt", (await anaConflict.Task.WaitAsync(Wait, Ct)).SnapshotPath);

        // Un evento general posterior sirve de barrera: si Luis hubiera recibido algo de la restauración, ya habría llegado.
        var barrier = Subscribe<StatusChangedEvent>(h => luis.Proxy.StatusChanged += h);
        _server.Events.PublishStatusChanged(new StatusChangedEvent());
        await barrier.Task.WaitAsync(Wait, Ct);
        Assert.Empty(luisEvents);
    }

    [Fact]
    public async Task Clients_that_have_not_been_authorized_receive_no_events()
    {
        var authorized = await _server.ConnectAuthorizedAsync(TestSids.Identity(TestSids.Ana));
        var silent = _server.Connect(TestSids.Identity(TestSids.Luis)); // conectado, sin ningún mensaje todavía
        var rejected = _server.Connect(TestSids.Identity(TestSids.Eva)); // no es administradora
        await RpcAssert.FailsWithAsync(BackboneErrorCode.NotAuthorized, () => rejected.Proxy.GetServiceInfoAsync(Ct));
        var leaked = new ConcurrentQueue<StatusChangedEvent>();
        silent.Proxy.StatusChanged += (_, e) => leaked.Enqueue(e);
        rejected.Proxy.StatusChanged += (_, e) => leaked.Enqueue(e);
        var received = Subscribe<StatusChangedEvent>(h => authorized.Proxy.StatusChanged += h);

        _server.Events.PublishStatusChanged(new StatusChangedEvent());

        await received.Task.WaitAsync(Wait, Ct);
        await Task.Delay(200, Ct);
        Assert.Empty(leaked);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < Wait, "La condición no se ha cumplido a tiempo.");
            await Task.Delay(10, Ct);
        }
    }

    private static TaskCompletionSource<T> Subscribe<T>(Action<EventHandler<T>> subscribe)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        subscribe((_, e) => tcs.TrySetResult(e));
        return tcs;
    }

    private sealed class StatusEquality : IEqualityComparer<StatusChangedEvent>
    {
        public static readonly StatusEquality Instance = new();

        public bool Equals(StatusChangedEvent? x, StatusChangedEvent? y) =>
            x?.Status.Health == y?.Status.Health && x?.Status.TimestampUtc == y?.Status.TimestampUtc;

        public int GetHashCode(StatusChangedEvent obj) => obj.Status.Health.GetHashCode();
    }
}
