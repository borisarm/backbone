using Backbone.App.Ipc;
using Backbone.App.Tests.Ipc;
using Backbone.App.Tray;
using Backbone.Contracts;

namespace Backbone.App.Tests.Tray;

/// <summary>HU-18 criterio 1 (cuatro estados de RF-50) y estados de conexión de §9.3.</summary>
[Trait("Story", "HU-18")]
public sealed class TrayStatusTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);

    private static JobStatus Job(bool enabled = true, JobActivity activity = JobActivity.Idle, bool outdated = false, DateTimeOffset? lastSuccess = null) =>
        new() { JobId = Guid.NewGuid(), JobName = "j", Enabled = enabled, Activity = activity, IsOutdated = outdated, LastSuccessUtc = lastSuccess };

    private static TrayStatusTracker Connected(ServiceStatus status)
    {
        var tracker = new TrayStatusTracker();
        tracker.SetConnection(ConnectionState.Connected);
        tracker.SetStatus(status);
        return tracker;
    }

    [Theory]
    [InlineData(HealthState.Ok, TrayIconKind.Ok)]
    [InlineData(HealthState.Warning, TrayIconKind.Warning)]
    [InlineData(HealthState.Error, TrayIconKind.Error)]
    public void Connected_shows_the_service_health(HealthState health, TrayIconKind expected)
    {
        var tracker = Connected(new ServiceStatus { Health = health, Jobs = [Job()] });

        Assert.Equal(expected, tracker.Presentation.Kind);
    }

    [Fact]
    public void A_running_job_or_current_run_shows_Copying_even_if_health_is_error()
    {
        Assert.Equal(TrayIconKind.Copying, Connected(new ServiceStatus { Health = HealthState.Error, Jobs = [Job(activity: JobActivity.Running)] }).Presentation.Kind);
        Assert.Equal(TrayIconKind.Copying, Connected(new ServiceStatus { CurrentRun = new RunProgressEvent() }).Presentation.Kind);
    }

    [Fact]
    public void An_outdated_enabled_job_turns_an_ok_icon_into_a_warning_but_a_disabled_one_does_not()
    {
        Assert.Equal(TrayIconKind.Warning, Connected(new ServiceStatus { Jobs = [Job(outdated: true)] }).Presentation.Kind);
        Assert.Equal(TrayIconKind.Ok, Connected(new ServiceStatus { Jobs = [Job(enabled: false, outdated: true)] }).Presentation.Kind);
    }

    [Theory]
    [InlineData(ConnectionState.ServiceStopped)]
    [InlineData(ConnectionState.Unauthorized)]
    [InlineData(ConnectionState.IncompatibleContract)]
    public void Without_a_usable_service_the_icon_is_red_even_with_a_healthy_last_status(ConnectionState state)
    {
        var tracker = Connected(new ServiceStatus { Health = HealthState.Ok, Jobs = [Job()] });

        tracker.SetConnection(state);

        Assert.Equal(TrayIconKind.Error, tracker.Presentation.Kind);
        Assert.Equal(0, tracker.Presentation.RunnableJobs);
    }

    [Theory]
    [InlineData(ConnectionState.Connecting)]
    [InlineData(ConnectionState.Reconnecting)]
    public void While_connecting_or_reconnecting_the_icon_is_yellow(ConnectionState state)
    {
        var tracker = new TrayStatusTracker();

        tracker.SetConnection(state);

        Assert.Equal(TrayIconKind.Warning, tracker.Presentation.Kind);
    }

    [Fact]
    public void Connected_without_a_status_yet_is_yellow_until_the_status_arrives()
    {
        var tracker = new TrayStatusTracker();
        tracker.SetConnection(ConnectionState.Connected);
        Assert.Equal(TrayIconKind.Warning, tracker.Presentation.Kind);

        tracker.SetStatus(new ServiceStatus());

        Assert.Equal(TrayIconKind.Ok, tracker.Presentation.Kind);
    }

    [Fact]
    public void LastSuccess_is_the_latest_of_all_jobs_and_is_kept_when_the_service_stops()
    {
        var tracker = Connected(new ServiceStatus { Jobs = [Job(lastSuccess: T0), Job(lastSuccess: T0.AddHours(3)), Job()] });

        tracker.SetConnection(ConnectionState.ServiceStopped);

        Assert.Equal(T0.AddHours(3), tracker.Presentation.LastSuccessUtc);
    }

    [Fact]
    public void Pause_and_runnable_jobs_are_reported()
    {
        var tracker = Connected(new ServiceStatus
        {
            Pause = new PauseState { SinceUtc = T0 },
            Jobs = [Job(), Job(activity: JobActivity.Queued), Job(enabled: false)],
        });

        Assert.True(tracker.Presentation.Paused);
        Assert.Equal(1, tracker.Presentation.RunnableJobs);
    }

    [Fact]
    public void Changed_is_raised_only_when_the_presentation_changes()
    {
        var tracker = new TrayStatusTracker();
        var count = 0;
        tracker.Changed += (_, _) => count++;

        tracker.SetConnection(ConnectionState.ServiceStopped);
        tracker.SetConnection(ConnectionState.ServiceStopped);
        tracker.SetConnection(ConnectionState.Unauthorized);

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task RefreshAsync_reads_the_status_and_ignores_a_failure()
    {
        var (service, fake) = FakeBackboneService.Create();
        fake.On("GetStatusAsync", _ => Task.FromResult(new ServiceStatus { Health = HealthState.Error }));
        var tracker = new TrayStatusTracker();
        tracker.SetConnection(ConnectionState.Connected);

        await tracker.RefreshAsync(service, TestContext.Current.CancellationToken);
        Assert.Equal(TrayIconKind.Error, tracker.Presentation.Kind);

        fake.On("GetStatusAsync", _ => Task.FromException<ServiceStatus>(new ServiceUnavailableException()));
        await tracker.RefreshAsync(service, TestContext.Current.CancellationToken);
        Assert.Equal(TrayIconKind.Error, tracker.Presentation.Kind);
    }
}
