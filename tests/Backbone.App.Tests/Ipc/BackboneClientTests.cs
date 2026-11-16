using System.Collections.Concurrent;
using System.Diagnostics;
using Backbone.App.Ipc;
using Backbone.Contracts;
using Microsoft.Extensions.Time.Testing;
using StreamJsonRpc;

namespace Backbone.App.Tests.Ipc;

/// <summary>HT-03 criterio 5: reconexión automática (§9.1: 1 s, 2 s, 5 s, 10 s y luego cada 10 s) y estado observable.</summary>
[Trait("Story", "HT-03")]
public sealed class BackboneClientTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private readonly CountingTimeProvider _time = new();
    private readonly ConcurrentQueue<ConnectionState> _states = new();
    private readonly List<BackboneClient> _clients = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
        {
            await client.DisposeAsync();
        }
    }

    private BackboneClient NewClient(FakeConnector connector, Action<Action>? post = null)
    {
        var client = new BackboneClient(connector, _time, post);
        client.StateChanged += (_, s) => _states.Enqueue(s);
        _clients.Add(client);
        return client;
    }

    [Fact]
    public async Task It_connects_and_exposes_the_service_info()
    {
        var session = FakeBackboneSession.Create(new ServiceInfo { ServiceVersion = "1.2.3", ContractVersion = "1.0" });
        var client = NewClient(new FakeConnector().Then(session));

        client.Start();
        await Eventually(() => client.State == ConnectionState.Connected);

        Assert.Equal("1.2.3", client.ServiceInfo?.ServiceVersion);
        Assert.Equal([ConnectionState.Connected], _states);
    }

    [Fact]
    public async Task Without_a_service_it_retries_after_1_2_5_10_and_then_every_10_seconds()
    {
        var connector = new FakeConnector().Then(new TimeoutException());
        var client = NewClient(connector);

        client.Start();
        await Eventually(() => client.State == ConnectionState.ServiceStopped);
        Assert.Equal(1, connector.Attempts);

        var expected = new[] { 1, 2, 5, 10, 10, 10 };
        for (var i = 0; i < expected.Length; i++)
        {
            await _time.WaitForTimersAsync(i + 1);
            _time.Advance(TimeSpan.FromSeconds(expected[i]) - TimeSpan.FromMilliseconds(1));
            await Task.Delay(50, Ct);
            Assert.Equal(i + 1, connector.Attempts);

            _time.Advance(TimeSpan.FromMilliseconds(1));
            await Eventually(() => connector.Attempts == i + 2);
        }
    }

    [Fact]
    public async Task Losing_a_working_connection_shows_Reconnecting_and_reconnects_after_one_second()
    {
        var first = FakeBackboneSession.Create();
        var second = FakeBackboneSession.Create();
        var connector = new FakeConnector().Then(first).Then(second);
        var client = NewClient(connector);
        client.Start();
        await Eventually(() => client.State == ConnectionState.Connected);

        first.Drop();
        await Eventually(() => client.State == ConnectionState.Reconnecting);
        Assert.Null(client.ServiceInfo);
        Assert.True(first.Disposed);
        await _time.WaitForTimersAsync(1);
        _time.Advance(TimeSpan.FromMilliseconds(999));
        await Task.Delay(50, Ct);
        Assert.Equal(1, connector.Attempts);

        _time.Advance(TimeSpan.FromMilliseconds(1));
        await Eventually(() => client.State == ConnectionState.Connected);

        Assert.Equal(
            [ConnectionState.Connected, ConnectionState.Reconnecting, ConnectionState.Connected],
            _states);
    }

    [Fact]
    public async Task After_four_failed_reconnections_the_state_becomes_ServiceStopped()
    {
        var first = FakeBackboneSession.Create();
        var connector = new FakeConnector().Then(first).Then(new TimeoutException());
        var client = NewClient(connector);
        client.Start();
        await Eventually(() => client.State == ConnectionState.Connected);
        first.Drop();
        await Eventually(() => client.State == ConnectionState.Reconnecting);

        // Esperas de 1 s, 2 s y 5 s: los dos primeros intentos fallidos siguen siendo "reconectando"; el tercero, no.
        var delays = new[] { 1, 2, 5 };
        for (var i = 0; i < delays.Length; i++)
        {
            await _time.WaitForTimersAsync(i + 1);
            _time.Advance(TimeSpan.FromSeconds(delays[i]));
            await Eventually(() => connector.Attempts == i + 2);
            if (i < delays.Length - 1)
            {
                await Task.Delay(50, Ct);
                Assert.Equal(ConnectionState.Reconnecting, client.State);
            }
            else
            {
                await Eventually(() => client.State == ConnectionState.ServiceStopped);
            }
        }
    }

    [Fact]
    public async Task A_rejected_user_is_reported_as_Unauthorized_and_recovers_when_authorized()
    {
        var session = FakeBackboneSession.Create();
        var connector = new FakeConnector().Then(new UnauthorizedAccessException()).Then(session);
        var client = NewClient(connector);

        client.Start();
        await Eventually(() => client.State == ConnectionState.Unauthorized);
        await _time.WaitForTimersAsync(1);
        _time.Advance(TimeSpan.FromSeconds(1));

        await Eventually(() => client.State == ConnectionState.Connected);
    }

    [Fact]
    public async Task An_incompatible_contract_is_reported_with_the_service_version()
    {
        var connector = new FakeConnector().Then(new ContractIncompatibleException("2.0"));
        var client = NewClient(connector);

        client.Start();
        await Eventually(() => client.State == ConnectionState.IncompatibleContract);

        Assert.Equal("2.0", client.IncompatibleContractVersion);
    }

    [Fact]
    public async Task RetryNow_skips_the_wait()
    {
        var session = FakeBackboneSession.Create();
        var connector = new FakeConnector().Then(new TimeoutException()).Then(session);
        var client = NewClient(connector);
        client.Start();
        await Eventually(() => client.State == ConnectionState.ServiceStopped);
        await _time.WaitForTimersAsync(1);

        client.RetryNow();

        await Eventually(() => client.State == ConnectionState.Connected);
        Assert.Equal(2, connector.Attempts);
    }

    [Fact]
    public async Task Service_events_are_forwarded_through_the_post_delegate_and_stop_after_disconnecting()
    {
        var session = FakeBackboneSession.Create();
        var posted = 0;
        var client = NewClient(new FakeConnector().Then(session), action =>
        {
            Interlocked.Increment(ref posted);
            action();
        });
        var progress = new ConcurrentQueue<RunProgressEvent>();
        var status = new ConcurrentQueue<StatusChangedEvent>();
        client.RunProgress += (_, e) => progress.Enqueue(e);
        client.StatusChanged += (_, e) => status.Enqueue(e);
        client.Start();
        await Eventually(() => client.State == ConnectionState.Connected);
        await Eventually(() => Volatile.Read(ref posted) == 1); // La notificación del cambio a Connected.
        var before = Volatile.Read(ref posted);

        session.Fake.Raise(nameof(IBackboneService.RunProgress), new RunProgressEvent { RunId = 9, FilesProcessed = 3 });
        session.Fake.Raise(nameof(IBackboneService.StatusChanged), new StatusChangedEvent());

        Assert.Equal(9, Assert.Single(progress).RunId);
        Assert.Single(status);
        Assert.Equal(before + 2, Volatile.Read(ref posted));
        Assert.Equal(1, session.Fake.HandlerCount(nameof(IBackboneService.RunProgress)));

        session.Drop();
        await Eventually(() => client.State == ConnectionState.Reconnecting);
        Assert.Equal(0, session.Fake.HandlerCount(nameof(IBackboneService.RunProgress)));
        Assert.Equal(0, session.Fake.HandlerCount(nameof(IBackboneService.StatusChanged)));
    }

    [Fact]
    public async Task Calls_are_forwarded_to_the_current_connection_and_fail_clearly_without_one()
    {
        var session = FakeBackboneSession.Create();
        var id = Guid.NewGuid();
        session.Fake.On(nameof(IBackboneService.RunNowAsync), args =>
        {
            Assert.Equal(id, args[0]);
            return Task.CompletedTask;
        });
        var client = NewClient(new FakeConnector().Then(session));

        var before = await Assert.ThrowsAsync<ServiceUnavailableException>(() => client.RunNowAsync(id, Ct));
        Assert.Equal(ConnectionState.Connecting, before.State);

        client.Start();
        await Eventually(() => client.State == ConnectionState.Connected);
        await client.RunNowAsync(id, Ct);
        Assert.Contains(nameof(IBackboneService.RunNowAsync), session.Fake.Calls);

        session.Drop();
        await Eventually(() => client.State == ConnectionState.Reconnecting);
        var after = await Assert.ThrowsAsync<ServiceUnavailableException>(() => client.RunNowAsync(id, Ct));
        Assert.Equal(ConnectionState.Reconnecting, after.State);
    }

    [Fact]
    public async Task A_connection_lost_during_a_call_becomes_ServiceUnavailable_and_business_errors_pass_through()
    {
        var session = FakeBackboneSession.Create();
        session.Fake.On(nameof(IBackboneService.RunNowAsync), _ => Task.FromException(new ConnectionLostException()));
        session.Fake.On(nameof(IBackboneService.CancelRunAsync), _ => Task.FromException(new RemoteInvocationException("x", (int)BackboneErrorCode.JobNotFound, null, null)));
        var client = NewClient(new FakeConnector().Then(session));
        client.Start();
        await Eventually(() => client.State == ConnectionState.Connected);

        await Assert.ThrowsAsync<ServiceUnavailableException>(() => client.RunNowAsync(Guid.NewGuid(), Ct));
        var business = await Assert.ThrowsAsync<RemoteInvocationException>(() => client.CancelRunAsync(Guid.NewGuid(), Ct));
        Assert.True(ConnectionFailures.TryGetErrorCode(business, out var code, out _));
        Assert.Equal(BackboneErrorCode.JobNotFound, code);
    }

    [Fact]
    public async Task Disposing_stops_the_reconnection_loop_and_the_current_session()
    {
        var session = FakeBackboneSession.Create();
        var connector = new FakeConnector().Then(session);
        var client = NewClient(connector);
        client.Start();
        await Eventually(() => client.State == ConnectionState.Connected);

        await client.DisposeAsync();

        Assert.True(session.Disposed);
        Assert.Equal(1, connector.Attempts);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < Wait, "The expected condition was not reached in time.");
            await Task.Delay(10, Ct);
        }
    }

    /// <summary>Reloj falso que cuenta los temporizadores creados, para saber cuándo el cliente ya está esperando.</summary>
    private sealed class CountingTimeProvider : FakeTimeProvider
    {
        private int _timers;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            // Se cuenta después de registrar el temporizador: si no, Advance podría ejecutarse entre ambos pasos y no dispararlo.
            var timer = base.CreateTimer(callback, state, dueTime, period);
            Interlocked.Increment(ref _timers);
            return timer;
        }

        public async Task WaitForTimersAsync(int count)
        {
            var watch = Stopwatch.StartNew();
            while (Volatile.Read(ref _timers) < count)
            {
                Assert.True(watch.Elapsed < Wait, "The client did not start waiting for the next attempt.");
                await Task.Delay(10, Ct);
            }
        }
    }
}
