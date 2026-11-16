using System.Diagnostics;
using Backbone.App.Ipc;
using Backbone.App.Tests.Ipc;
using Backbone.App.Tray;
using Microsoft.Extensions.Time.Testing;

namespace Backbone.App.Tests.Tray;

/// <summary>
/// HU-18 criterio 5: servicio detenido → icono rojo en menos de 15 s. Cliente real con tiempo simulado: la pérdida de la conexión
/// cuenta como primer fallo; los reintentos esperan 1 s, 2 s y 5 s y el tercero fallido pasa a "servicio detenido" (8 s de esperas).
/// Cada intento fallido tarda como mucho <c>ConnectTimeout</c> (1 s) en el conector real: peor caso 8 + 3 = 11 s.
/// </summary>
[Trait("Story", "HU-18")]
public sealed class TrayServiceStoppedTests
{
    private static readonly int[] Delays = [1, 2, 5];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_icon_turns_red_after_eight_seconds_of_waits_and_not_before()
    {
        var time = new TimerCountingTimeProvider();
        var first = FakeBackboneSession.Create();
        var connector = new FakeConnector().Then(first).Then(new TimeoutException());
        var tracker = new TrayStatusTracker();
        var client = new BackboneClient(connector, time);
        client.StateChanged += (_, s) => tracker.SetConnection(s);
        try
        {
            client.Start();
            await Eventually(() => client.State == ConnectionState.Connected);
            first.Drop();
            await Eventually(() => client.State == ConnectionState.Reconnecting);
            Assert.Equal(TrayIconKind.Warning, tracker.Presentation.Kind);

            var elapsed = TimeSpan.Zero;
            foreach (var (delay, index) in Delays.Select((d, i) => (TimeSpan.FromSeconds(d), i)))
            {
                await time.WaitForTimersAsync(index + 1);
                Assert.NotEqual(TrayIconKind.Error, tracker.Presentation.Kind);
                time.Advance(delay);
                elapsed += delay;
                await Eventually(() => connector.Attempts == index + 2);
            }

            await Eventually(() => tracker.Presentation.Kind == TrayIconKind.Error);
            Assert.True(elapsed + TimeSpan.FromSeconds(3) < TimeSpan.FromSeconds(15), "Peor caso (esperas + 3 s de ConnectTimeout) debe ser < 15 s.");
            Assert.Equal(ConnectionState.ServiceStopped, tracker.Presentation.Connection);
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task At_startup_without_a_service_the_icon_is_red_after_the_first_failed_attempt()
    {
        var tracker = new TrayStatusTracker();
        var client = new BackboneClient(new FakeConnector().Then(new TimeoutException()), new FakeTimeProvider());
        client.StateChanged += (_, s) => tracker.SetConnection(s);
        try
        {
            client.Start();
            await Eventually(() => tracker.Presentation.Kind == TrayIconKind.Error);
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    private static async Task Eventually(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "The expected condition was not reached in time.");
            await Task.Delay(10, Ct);
        }
    }

    private sealed class TimerCountingTimeProvider : FakeTimeProvider
    {
        private int _timers;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Interlocked.Increment(ref _timers);
            return base.CreateTimer(callback, state, dueTime, period);
        }

        public Task WaitForTimersAsync(int count) => Eventually(() => Volatile.Read(ref _timers) >= count);
    }
}
