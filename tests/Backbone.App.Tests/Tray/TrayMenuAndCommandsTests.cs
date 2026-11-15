using Backbone.App.Ipc;
using Backbone.App.Tests.Ipc;
using Backbone.App.Tray;
using Backbone.Contracts;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;

namespace Backbone.App.Tests.Tray;

/// <summary>HU-18 criterio 2: menú contextual (Copiar todo ahora, Pausar F2, Abrir Backbone, Salir).</summary>
[Trait("Story", "HU-18")]
public sealed class TrayMenuAndCommandsTests
{
    private static JobStatus Job(Guid id, bool enabled = true, JobActivity activity = JobActivity.Idle) =>
        new() { JobId = id, JobName = "j", Enabled = enabled, Activity = activity };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void The_menu_has_the_four_items_in_order()
    {
        var items = TrayMenu.Build(new TrayPresentation(TrayIconKind.Ok, ConnectionState.Connected, null, false, 2));

        Assert.Equal([TrayCommand.RunAllNow, TrayCommand.Pause, TrayCommand.OpenApp, TrayCommand.Exit], items.Select(i => i.Command));
    }

    [Fact]
    public void Pause_is_shown_disabled_because_it_is_F2_and_Open_and_Exit_are_always_enabled()
    {
        var connected = TrayMenu.Build(new TrayPresentation(TrayIconKind.Ok, ConnectionState.Connected, null, false, 2));
        var stopped = TrayMenu.Build(new TrayPresentation(TrayIconKind.Error, ConnectionState.ServiceStopped, null, false, 0));

        Assert.False(connected[1].Enabled);
        Assert.True(connected[2].Enabled && connected[3].Enabled);
        Assert.True(stopped[2].Enabled && stopped[3].Enabled);
    }

    [Fact]
    public void Pause_is_enabled_only_when_available_and_connected()
    {
        Assert.True(TrayMenu.Build(new TrayPresentation(TrayIconKind.Ok, ConnectionState.Connected, null, false, 0), pauseAvailable: true)[1].Enabled);
        Assert.False(TrayMenu.Build(new TrayPresentation(TrayIconKind.Error, ConnectionState.ServiceStopped, null, false, 0), pauseAvailable: true)[1].Enabled);
    }

    [Theory]
    [InlineData(ConnectionState.Connected, 1, true)]
    [InlineData(ConnectionState.Connected, 0, false)]
    [InlineData(ConnectionState.Reconnecting, 1, false)]
    [InlineData(ConnectionState.ServiceStopped, 1, false)]
    public void RunAllNow_needs_a_connection_and_something_to_run(ConnectionState state, int runnable, bool enabled)
    {
        var items = TrayMenu.Build(new TrayPresentation(TrayIconKind.Ok, state, null, false, runnable));

        Assert.Equal(enabled, items[0].Enabled);
    }

    [Fact]
    public async Task RunAllNow_runs_every_enabled_idle_job_and_nothing_else()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var (service, fake) = FakeBackboneService.Create();
        var started = new List<Guid>();
        fake.On("GetStatusAsync", _ => Task.FromResult(new ServiceStatus
        {
            Jobs = [Job(a), Job(Guid.NewGuid(), enabled: false), Job(Guid.NewGuid(), activity: JobActivity.Running), Job(b)],
        }));
        fake.On("RunNowAsync", args => { started.Add((Guid)args[0]!); return Task.CompletedTask; });

        var result = await TrayCommands.RunAllNowAsync(service, Ct);

        Assert.Equal([a, b], started);
        Assert.Equal(new RunAllResult(2, 0, 0, false), result);
    }

    [Fact]
    public async Task RunAllNow_treats_JobAlreadyRunning_as_not_an_error_and_counts_other_failures()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var (service, fake) = FakeBackboneService.Create();
        fake.On("GetStatusAsync", _ => Task.FromResult(new ServiceStatus { Jobs = [Job(a), Job(b)] }));
        fake.On("RunNowAsync", args => (Guid)args[0]! == a
            ? Task.FromException(Remote(BackboneErrorCode.JobAlreadyRunning))
            : Task.FromException(Remote(BackboneErrorCode.JobNotFound)));

        var result = await TrayCommands.RunAllNowAsync(service, Ct);

        Assert.Equal(new RunAllResult(0, 1, 1, false), result);
    }

    [Fact]
    public async Task RunAllNow_reports_an_unavailable_service()
    {
        var (service, fake) = FakeBackboneService.Create();
        fake.On("GetStatusAsync", _ => Task.FromException<ServiceStatus>(new ServiceUnavailableException()));

        var result = await TrayCommands.RunAllNowAsync(service, Ct);

        Assert.True(result.Unavailable);
    }

    private static RemoteInvocationException Remote(BackboneErrorCode code) =>
        new("error", errorCode: (int)code, errorData: new BackboneErrorData { Code = code }, deserializedErrorData: new BackboneErrorData { Code = code });
}
