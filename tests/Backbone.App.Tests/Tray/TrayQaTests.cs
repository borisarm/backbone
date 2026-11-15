using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Backbone.App.Ipc;
using Backbone.App.Tests.Ipc;
using Backbone.App.Tray;
using Backbone.Contracts;
using Microsoft.Extensions.Time.Testing;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;

namespace Backbone.App.Tests.Tray;

/// <summary>Auditoría de QA de HU-18: recursos reales, prioridad de estados y casos límite de "Copiar todo ahora".</summary>
[Trait("Story", "HU-18")]
public sealed class TrayQaTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JobStatus Job(bool enabled = true, JobActivity activity = JobActivity.Idle, bool outdated = false) =>
        new() { JobId = Guid.NewGuid(), JobName = "j", Enabled = enabled, Activity = activity, IsOutdated = outdated };

    private static Dictionary<string, string> Resw(string culture)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Backbone.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var path = Path.Combine(dir.FullName, "src", "Backbone.App", "Strings", culture, "Resources.resw");
        return XDocument.Load(path).Root!.Elements("data")
            .ToDictionary(e => (string)e.Attribute("name")!, e => (string)e.Element("value")!, StringComparer.Ordinal);
    }

    private static string Placeholders(string s) =>
        string.Join(',', Regex.Matches(s, @"\{\d+\}").Select(m => m.Value).Order(StringComparer.Ordinal));

    private static RemoteInvocationException Remote(BackboneErrorCode code) =>
        new("error", errorCode: (int)code, errorData: new BackboneErrorData { Code = code }, deserializedErrorData: new BackboneErrorData { Code = code });

    [Fact]
    public void Es_and_en_resource_files_have_exactly_the_same_keys_and_placeholders()
    {
        var es = Resw("es-ES");
        var en = Resw("en-US");

        Assert.Equal(es.Keys.Order(StringComparer.Ordinal), en.Keys.Order(StringComparer.Ordinal));
        foreach (var key in es.Keys)
        {
            Assert.False(string.IsNullOrWhiteSpace(es[key]), key);
            Assert.False(string.IsNullOrWhiteSpace(en[key]), key);
            Assert.Equal(Placeholders(es[key]), Placeholders(en[key]));
        }
    }

    [Theory]
    [InlineData("es-ES")]
    [InlineData("en-US")]
    public void With_the_real_strings_every_state_fits_in_the_tooltip_without_truncation(string culture)
    {
        var strings = Resw(culture);
        var ci = CultureInfo.GetCultureInfo(culture);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 12, 31, 23, 59, 0, TimeSpan.Zero));
        time.SetLocalTimeZone(TimeZoneInfo.Utc);
        var formatter = new TrayTooltipFormatter(k => strings[k], time, ci);
        var lastWeek = new DateTimeOffset(2026, 12, 24, 23, 58, 0, TimeSpan.Zero);

        foreach (var connection in Enum.GetValues<ConnectionState>())
        {
            foreach (var kind in Enum.GetValues<TrayIconKind>())
            {
                foreach (var paused in new[] { false, true })
                {
                    var p = new TrayPresentation(kind, connection, lastWeek, paused, 1);
                    var text = formatter.Format(p);

                    Assert.True(text.Length <= TrayTooltipFormatter.MaxLength, $"{culture} {kind}/{connection}/{paused}: {text.Length}");
                    Assert.DoesNotContain('…', text);
                    Assert.Contains(strings[TrayTooltipFormatter.StateKey(p)], text, StringComparison.Ordinal);
                    Assert.Contains("\n", text, StringComparison.Ordinal);
                }
            }
        }
    }

    [Fact]
    public void A_truncated_tooltip_ends_with_an_ellipsis()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        time.SetLocalTimeZone(TimeZoneInfo.Utc);
        var formatter = new TrayTooltipFormatter(_ => new string('x', 200), time, CultureInfo.InvariantCulture);

        var text = formatter.Format(TrayPresentation.Initial);

        Assert.EndsWith("…", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Last_success_just_after_local_midnight_is_today_in_a_zone_ahead_of_utc()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("t+2", TimeSpan.FromHours(2), "t", "t");
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 3, 29, 22, 30, 0, TimeSpan.Zero)); // 00:30 del día 30 local
        time.SetLocalTimeZone(zone);
        var formatter = new TrayTooltipFormatter(
            k => k == "Tray_TooltipFormat" ? "{0}|{1}" : k.EndsWith("Format", StringComparison.Ordinal) ? "{0}" : k,
            time,
            CultureInfo.GetCultureInfo("es-ES"));
        var p = new TrayPresentation(TrayIconKind.Ok, ConnectionState.Connected, new DateTimeOffset(2026, 3, 29, 22, 10, 0, TimeSpan.Zero), false, 0);

        var text = formatter.Format(p);

        Assert.Contains("0:10", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Losing_the_service_while_copying_turns_the_icon_red_not_blue()
    {
        var tracker = new TrayStatusTracker();
        tracker.SetConnection(ConnectionState.Connected);
        tracker.SetStatus(new ServiceStatus { Health = HealthState.Ok, Jobs = [Job(activity: JobActivity.Running)] });
        Assert.Equal(TrayIconKind.Copying, tracker.Presentation.Kind);

        tracker.SetConnection(ConnectionState.ServiceStopped);

        Assert.Equal(TrayIconKind.Error, tracker.Presentation.Kind);
        Assert.Equal(0, tracker.Presentation.RunnableJobs);
    }

    [Fact]
    public void Recovering_the_connection_returns_to_the_last_known_health()
    {
        var tracker = new TrayStatusTracker();
        tracker.SetConnection(ConnectionState.Connected);
        tracker.SetStatus(new ServiceStatus { Health = HealthState.Warning, Jobs = [Job()] });
        tracker.SetConnection(ConnectionState.ServiceStopped);
        tracker.SetConnection(ConnectionState.Connected);

        Assert.Equal(TrayIconKind.Warning, tracker.Presentation.Kind);
    }

    [Fact]
    public void A_queued_job_alone_does_not_make_the_icon_blue_and_is_not_runnable()
    {
        var tracker = new TrayStatusTracker();
        tracker.SetConnection(ConnectionState.Connected);
        tracker.SetStatus(new ServiceStatus { Health = HealthState.Ok, Jobs = [Job(activity: JobActivity.Queued)] });

        Assert.Equal(TrayIconKind.Ok, tracker.Presentation.Kind);
        Assert.Equal(0, tracker.Presentation.RunnableJobs);
    }

    [Fact]
    public void A_status_without_jobs_is_ok_and_has_no_last_success()
    {
        var tracker = new TrayStatusTracker();
        tracker.SetConnection(ConnectionState.Connected);
        tracker.SetStatus(new ServiceStatus { Health = HealthState.Ok, Jobs = [] });

        Assert.Equal(TrayIconKind.Ok, tracker.Presentation.Kind);
        Assert.Null(tracker.Presentation.LastSuccessUtc);
    }

    [Fact]
    public async Task RefreshAsync_propagates_cancellation()
    {
        var (service, fake) = FakeBackboneService.Create();
        using var cts = new CancellationTokenSource();
        fake.On("GetStatusAsync", _ => { cts.Cancel(); return Task.FromCanceled<ServiceStatus>(cts.Token); });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new TrayStatusTracker().RefreshAsync(service, cts.Token));
    }

    [Fact]
    public async Task RunAllNow_skips_queued_jobs_and_does_nothing_without_runnable_jobs()
    {
        var (service, fake) = FakeBackboneService.Create();
        var calls = 0;
        fake.On("GetStatusAsync", _ => Task.FromResult(new ServiceStatus { Jobs = [Job(activity: JobActivity.Queued)] }));
        fake.On("RunNowAsync", _ => { calls++; return Task.CompletedTask; });

        var result = await TrayCommands.RunAllNowAsync(service, Ct);

        Assert.Equal(0, calls);
        Assert.Equal(new RunAllResult(0, 0, 0, false), result);
    }

    [Fact]
    public async Task RunAllNow_reports_unavailable_if_the_service_drops_midway_and_keeps_what_was_started()
    {
        var first = Job();
        var second = Job();
        var (service, fake) = FakeBackboneService.Create();
        fake.On("GetStatusAsync", _ => Task.FromResult(new ServiceStatus { Jobs = [first, second] }));
        fake.On("RunNowAsync", args => (Guid)args[0]! == first.JobId
            ? Task.CompletedTask
            : Task.FromException(new ServiceUnavailableException()));

        var result = await TrayCommands.RunAllNowAsync(service, Ct);

        Assert.True(result.Unavailable);
        Assert.Equal(1, result.Started);
    }

    [Fact]
    public async Task RunAllNow_counts_a_service_error_when_reading_the_status()
    {
        var (service, fake) = FakeBackboneService.Create();
        fake.On("GetStatusAsync", _ => Task.FromException<ServiceStatus>(Remote(BackboneErrorCode.NotAuthorized)));

        var result = await TrayCommands.RunAllNowAsync(service, Ct);

        Assert.Equal(1, result.Failed);
        Assert.False(result.Unavailable);
    }

    [Fact]
    public async Task RunAllNow_propagates_cancellation()
    {
        var (service, fake) = FakeBackboneService.Create();
        using var cts = new CancellationTokenSource();
        fake.On("GetStatusAsync", _ => Task.FromResult(new ServiceStatus { Jobs = [Job()] }));
        fake.On("RunNowAsync", _ => { cts.Cancel(); return Task.FromCanceled(cts.Token); });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TrayCommands.RunAllNowAsync(service, cts.Token));
    }

    [Fact]
    public void Menu_command_ids_are_stable_and_non_zero()
    {
        // El identificador del elemento nativo es el valor del enum: 0 significa "sin selección" en TrackPopupMenu.
        Assert.Equal([1, 2, 3, 4], new[] { TrayCommand.RunAllNow, TrayCommand.Pause, TrayCommand.OpenApp, TrayCommand.Exit }.Select(c => (int)c));
    }
}
