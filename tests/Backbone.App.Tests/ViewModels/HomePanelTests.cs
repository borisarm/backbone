using System.ComponentModel;
using System.Globalization;
using Backbone.App.Ipc;
using Backbone.App.Tests.Ipc;
using Backbone.App.ViewModels;
using Backbone.Contracts;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Backbone.App.Tests.ViewModels;

/// <summary>HU-19: panel de inicio con progreso en vivo (RF-51, §9.2, §9.3).</summary>
public sealed class HomePanelTests
{
    private static readonly Guid JobId = Guid.NewGuid();
    private static readonly Dictionary<string, string> Resources = ResourceCoverageTests.LoadResources("es-ES");

    // Con los textos reales de es-ES: una clave que falte en el .resw rompe la prueba.
    private static string Str(string key) => Resources[key];

    private static JobStatus Job(Action<JobStatusBuilder>? configure = null)
    {
        var b = new JobStatusBuilder();
        configure?.Invoke(b);
        return new JobStatus
        {
            JobId = JobId,
            JobName = "Fotos",
            Enabled = b.Enabled,
            Activity = b.Activity,
            Health = b.Health,
            IsOutdated = b.IsOutdated,
            LastRun = b.LastRun,
            NextRunUtc = b.NextRunUtc,
        };
    }

    private static async Task<(HomeViewModel Home, FakeBackboneService Fake, FakeTimeProvider Time)> Open(
        JobStatus job,
        ScheduleKind kind = ScheduleKind.Daily,
        FakeConnection? connection = null,
        FakeStarter? starter = null,
        RunProgressEvent? current = null)
    {
        var (service, fake) = FakeBackboneService.Create();
        fake.On("GetStatusAsync", _ => Task.FromResult(new ServiceStatus { Jobs = [job], CurrentRun = current }));
        fake.On("GetJobsAsync", _ => Task.FromResult<IReadOnlyList<JobDefinition>>([new JobDefinition { Id = job.JobId, Name = job.JobName, Schedule = new JobSchedule { Kind = kind } }]));
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero));
        var home = new HomeViewModel(service, Str, connection, starter, time);
        await home.LoadAsync(TestContext.Current.CancellationToken);
        return (home, fake, time);
    }

    private static string Local(DateTimeOffset utc) => utc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    // --- Criterio 1: estado, última ejecución con resultado, próxima ejecución, botón ---

    [Fact]
    [Trait("Story", "HU-19")]
    public async Task Criterion1_CardShowsStateLastRunWithResultAndNextRun()
    {
        var done = new DateTimeOffset(2026, 2, 28, 22, 5, 0, TimeSpan.Zero);
        var next = new DateTimeOffset(2026, 3, 1, 22, 0, 0, TimeSpan.Zero);
        var (home, _, _) = await Open(Job(b =>
        {
            b.LastRun = new RunRecord { State = RunState.CompletedWithWarnings, StartedUtc = done.AddMinutes(-5), CompletedUtc = done };
            b.NextRunUtc = next;
        }));
        using var _ = home;

        var card = Assert.Single(home.Cards);

        Assert.Equal("Al día", card.StateText);
        Assert.Contains(Local(done), card.LastRunText, StringComparison.Ordinal);
        Assert.Contains("con avisos", card.LastRunText, StringComparison.Ordinal);
        Assert.Contains(Local(next), card.NextRunText, StringComparison.Ordinal);
        Assert.True(card.Run.ShowRunNow);
        Assert.False(card.Run.ShowCancel);
        Assert.Contains("Fotos", card.AutomationName, StringComparison.Ordinal);
        Assert.Contains("Fotos", card.RunNowAutomationName, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Story", "HU-19")]
    public async Task Criterion1_NeverRunJobSaysSo()
    {
        var (home, _, _) = await Open(Job());
        using var _ = home;

        Assert.Equal("Última ejecución: ninguna todavía", Assert.Single(home.Cards).LastRunText);
    }

    [Theory]
    [Trait("Story", "HU-19")]
    [InlineData(ScheduleKind.Manual, "Manual")]
    [InlineData(ScheduleKind.Daily, "—")]
    public async Task Criterion1_NextRunWithoutDateShowsManualOrDash(ScheduleKind kind, string expected)
    {
        var (home, _, _) = await Open(Job(), kind);
        using var _ = home;

        Assert.EndsWith(expected, Assert.Single(home.Cards).NextRunText, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Story", "HU-19")]
    public async Task Criterion1_DisabledJobShowsDashEvenIfManual()
    {
        var (home, _, _) = await Open(Job(b => b.Enabled = false), ScheduleKind.Manual);
        using var _ = home;

        var card = Assert.Single(home.Cards);
        Assert.Equal("Desactivado", card.StateText);
        Assert.EndsWith("—", card.NextRunText, StringComparison.Ordinal);
    }

    [Theory]
    [Trait("Story", "HU-19")]
    [InlineData(HealthState.Error, false, "Con error en la última copia")]
    [InlineData(HealthState.Warning, false, "Con avisos")]
    [InlineData(HealthState.Ok, true, "Sin copia reciente")]
    [InlineData(HealthState.Ok, false, "Al día")]
    public async Task Criterion1_StateTextFollowsHealth(HealthState health, bool outdated, string expected)
    {
        var (home, _, _) = await Open(Job(b => { b.Health = health; b.IsOutdated = outdated; }));
        using var _ = home;

        Assert.Equal(expected, Assert.Single(home.Cards).StateText);
    }

    [Fact]
    [Trait("Story", "HU-19")]
    public async Task Criterion1_RunningJobShowsCancelAndStatusChangedUpdatesCardInPlace()
    {
        var (home, fake, _) = await Open(Job());
        using var _ = home;
        var card = Assert.Single(home.Cards);

        fake.Raise("StatusChanged", new StatusChangedEvent { Status = new ServiceStatus { Jobs = [Job(b => b.Activity = JobActivity.Running)] } });

        Assert.Same(card, Assert.Single(home.Cards));
        Assert.Equal("Copiando ahora", card.StateText);
        Assert.True(card.Run.ShowCancel);
        Assert.False(card.Run.ShowRunNow);
    }

    [Fact]
    [Trait("Story", "HU-19")]
    public async Task Criterion1_CardsAreSyncedAddedRemovedAndOrdered()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var (home, fake, _) = await Open(Job());
        using var _ = home;
        var original = home.Cards[0];

        fake.Raise("StatusChanged", new StatusChangedEvent
        {
            Status = new ServiceStatus
            {
                Jobs = [new JobStatus { JobId = a, JobName = "A" }, new JobStatus { JobId = JobId, JobName = "Fotos" }, new JobStatus { JobId = b, JobName = "B" }],
            },
        });

        Assert.Equal(["A", "Fotos", "B"], home.Cards.Select(c => c.Name));
        Assert.Same(original, home.Cards[1]);

        fake.Raise("StatusChanged", new StatusChangedEvent { Status = new ServiceStatus { Jobs = [new JobStatus { JobId = b, JobName = "B" }] } });
        Assert.Equal(["B"], home.Cards.Select(c => c.Name));
    }

    [Fact]
    [Trait("Story", "HU-19")]
    public async Task Criterion1_OverallCardSummarizesJobs()
    {
        var (service, fake) = FakeBackboneService.Create();
        fake.On("GetJobsAsync", _ => Task.FromResult<IReadOnlyList<JobDefinition>>([]));
        fake.On("GetStatusAsync", _ => Task.FromResult(new ServiceStatus
        {
            Health = HealthState.Error,
            Jobs =
            [
                new JobStatus { JobId = Guid.NewGuid(), Enabled = true, Health = HealthState.Error },
                new JobStatus { JobId = Guid.NewGuid(), Enabled = true, Health = HealthState.Ok, IsOutdated = true },
                new JobStatus { JobId = Guid.NewGuid(), Enabled = true, Activity = JobActivity.Running },
            ],
        }));
        using var home = new HomeViewModel(service, Str);

        await home.LoadAsync(TestContext.Current.CancellationToken);

        Assert.True(home.ShowOverall);
        Assert.Equal("Hay copias con error", home.OverallTitle);
        Assert.Contains("3 trabajo(s).", home.OverallDetail, StringComparison.Ordinal);
        Assert.Contains("1 en marcha.", home.OverallDetail, StringComparison.Ordinal);
        Assert.Contains("1 con error.", home.OverallDetail, StringComparison.Ordinal);
        Assert.Contains("1 con avisos", home.OverallDetail, StringComparison.Ordinal);
    }

    // --- Criterio 2: progreso en vivo ---

    private static RunProgressEvent Progress(Action<ProgressBuilder>? configure = null)
    {
        var b = new ProgressBuilder();
        configure?.Invoke(b);
        return new RunProgressEvent
        {
            RunId = 1,
            JobId = JobId,
            Phase = RunPhase.Copying,
            FilesProcessed = b.Files,
            FilesTotal = b.FilesTotal,
            BytesProcessed = b.Bytes,
            BytesTotal = b.BytesTotal,
            BytesPerSecond = b.Speed,
            EstimatedRemaining = b.Remaining,
            CurrentPath = b.Path,
        };
    }

    private static async Task<(HomeViewModel Home, FakeBackboneService Fake, FakeTimeProvider Time, JobRunState Run)> Running()
    {
        var (home, fake, time) = await Open(Job(b => b.Activity = JobActivity.Running));
        return (home, fake, time, Assert.Single(home.Cards).Run);
    }

    [Fact]
    [Trait("Story", "HU-19")]
    public async Task Criterion2_ServiceProvidedSpeedAndEstimateAreShown()
    {
        var (home, fake, _, run) = await Running();
        using var _ = home;

        fake.Raise("RunProgress", Progress(b =>
        {
            b.Files = 1500;
            b.FilesTotal = 3000;
            b.Bytes = 512L * 1024 * 1024;
            b.BytesTotal = 2048L * 1024 * 1024;
            b.Speed = 10 * 1024 * 1024;
            b.Remaining = TimeSpan.FromMinutes(2.4);
            b.Path = @"\Documentos\informe.docx";
        }));

        Assert.True(run.HasDetails);
        Assert.Contains(1500.ToString("N0", CultureInfo.CurrentCulture), run.FilesText, StringComparison.Ordinal);
        Assert.Contains(3000.ToString("N0", CultureInfo.CurrentCulture), run.FilesText, StringComparison.Ordinal);
        Assert.Contains("512 MB de 2 GB", run.BytesText, StringComparison.Ordinal);
        Assert.Contains("10 MB/s", run.SpeedText, StringComparison.Ordinal);
        Assert.Contains("2 min", run.RemainingText, StringComparison.Ordinal);
        Assert.Contains("informe.docx", run.CurrentPathText, StringComparison.Ordinal);
        Assert.False(run.IsIndeterminate);
        Assert.Equal(25, run.Percent, 3);
    }

    [Fact]
    [Trait("Story", "HU-19")]
    public async Task Criterion2_WithoutServiceSpeedItIsComputedFromAMovingAverage()
    {
        var (home, fake, time, run) = await Running();
        using var _ = home;
        const long mb = 1024 * 1024;

        fake.Raise("RunProgress", Progress(b => { b.Bytes = 0; b.BytesTotal = 1000 * mb; }));
        Assert.False(run.HasSpeed);
        Assert.False(run.HasRemaining);

        time.Advance(TimeSpan.FromSeconds(2));
        fake.Raise("RunProgress", Progress(b => { b.Bytes = 20 * mb; b.BytesTotal = 1000 * mb; }));

        Assert.True(run.HasSpeed);
        Assert.Contains("10 MB/s", run.SpeedText, StringComparison.Ordinal);
        // Quedan 980 MB a 10 MB/s: 98 s, es decir 2 min redondeados.
        Assert.True(run.HasRemaining);
        Assert.Contains("2 min", run.RemainingText, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Story", "HU-19")]
    public void Criterion2_MovingAverageForgetsOldSamples()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var estimator = new RunRateEstimator(time);
        estimator.Add(1, 0);
        time.Advance(TimeSpan.FromSeconds(5));
        estimator.Add(1, 5000);
        Assert.Equal(1000, estimator.BytesPerSecond!.Value, 3);

        // Tras 20 s parado, la ventana de 10 s ya solo contiene la parada.
        time.Advance(TimeSpan.FromSeconds(10));
        estimator.Add(1, 5000);
        time.Advance(TimeSpan.FromSeconds(10));
        estimator.Add(1, 5000);
        Assert.Equal(0, estimator.BytesPerSecond!.Value, 3);

        // Otra ejecución reinicia la ventana.
        estimator.Add(2, 10);
        Assert.Null(estimator.BytesPerSecond);
    }

    [Fact]
    [Trait("Story", "HU-19")]
    public async Task Criterion2_UnknownTotalHidesEstimateAndShowsIndeterminateBar()
    {
        var (home, fake, time, run) = await Running();
        using var _ = home;

        fake.Raise("RunProgress", Progress(b => b.Bytes = 1000));
        time.Advance(TimeSpan.FromSeconds(1));
        fake.Raise("RunProgress", Progress(b => b.Bytes = 5000));

        Assert.True(run.HasSpeed);
        Assert.False(run.HasRemaining);
        Assert.True(run.IsIndeterminate);
        Assert.DoesNotContain(" de ", run.BytesText, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Story", "HU-19")]
    public async Task Criterion2_CurrentPathIsSanitized()
    {
        var (home, fake, _, run) = await Running();
        using var _ = home;

        fake.Raise("RunProgress", Progress(b => b.Path = "\\a\u202Eb\nc.txt"));

        Assert.DoesNotContain('\n', run.CurrentPathText);
        Assert.DoesNotContain('\u202E', run.CurrentPathText);
    }

    [Fact]
    [Trait("Story", "HU-19")]
    public async Task Criterion2_PreparationPhaseHasNoCountersAndCompletionClearsEverything()
    {
        var (home, fake, _, run) = await Running();
        using var _ = home;

        fake.Raise("RunProgress", new RunProgressEvent { RunId = 1, JobId = JobId, Phase = RunPhase.Connecting });
        Assert.True(run.HasProgress);
        Assert.False(run.HasDetails);
        Assert.Equal("Conectando con el NAS", run.PhaseText);

        fake.Raise("RunProgress", Progress(b => { b.Files = 3; b.Bytes = 3000; }));
        Assert.True(run.HasDetails);

        fake.Raise("StatusChanged", new StatusChangedEvent { Status = new ServiceStatus { Jobs = [Job()] } });

        Assert.False(run.HasProgress);
        Assert.False(run.HasDetails);
        Assert.False(run.HasSpeed);
        Assert.False(run.HasRemaining);
        Assert.Equal(string.Empty, run.PhaseText);
    }

    [Fact]
    [Trait("Story", "HU-19")]
    public async Task Criterion2_ReloadRestoresProgressFromCurrentRun()
    {
        var (home, _, _) = await Open(Job(b => b.Activity = JobActivity.Running), current: Progress(b => { b.Files = 10; b.Bytes = 4096; b.BytesTotal = 8192; }));
        using var _ = home;

        var run = Assert.Single(home.Cards).Run;

        Assert.True(run.HasDetails);
        Assert.Equal(50, run.Percent, 3);
    }

    [Fact]
    [Trait("Story", "HU-19")]
    public async Task Criterion4_PhaseTextOnlyChangesWithThePhaseSoNarratorIsNotFlooded()
    {
        var (home, fake, time, run) = await Running();
        using var _ = home;
        fake.Raise("RunProgress", Progress(b => b.Bytes = 1));
        var phaseChanges = 0;
        run.PropertyChanged += (_, e) => phaseChanges += e.PropertyName == nameof(JobRunState.PhaseText) ? 1 : 0;

        for (var i = 2; i < 20; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(250));
            fake.Raise("RunProgress", Progress(b => b.Bytes = i * 1000));
        }

        Assert.Equal(0, phaseChanges);
        fake.Raise("RunProgress", new RunProgressEvent { RunId = 1, JobId = JobId, Phase = RunPhase.WritingSnapshot, BytesProcessed = 1 });
        Assert.Equal(1, phaseChanges);
    }

    // --- Criterio 3: estados especiales ---

    [Fact]
    [Trait("Story", "HU-19")]
    public async Task Criterion3_ServiceStoppedShowsBannerHidesCardsAndHidesEmptyState()
    {
        var connection = new FakeConnection(ConnectionState.Connected);
        var (home, _, _) = await Open(Job(), connection: connection);
        using var _ = home;
        Assert.True(home.Connection.ShowContent);

        connection.Set(ConnectionState.ServiceStopped);

        Assert.True(home.Connection.ShowServiceStopped);
        Assert.False(home.Connection.ShowContent);
        Assert.False(home.Connection.ShowReconnecting);
        Assert.False(home.IsEmpty);
        Assert.False(home.ShowOverall);
    }

    [Fact]
    [Trait("Story", "HU-19")]
    public async Task Criterion3_ReconnectingKeepsLastCardsAndIsIndependentFromServiceStopped()
    {
        var connection = new FakeConnection(ConnectionState.Connected);
        var (home, _, _) = await Open(Job(), connection: connection);
        using var _ = home;

        connection.Set(ConnectionState.Reconnecting);

        Assert.True(home.Connection.ShowReconnecting);
        Assert.False(home.Connection.ShowServiceStopped);
        Assert.True(home.Connection.ShowContent);
        Assert.Single(home.Cards);
    }

    [Fact]
    [Trait("Story", "HU-19")]
    public async Task Criterion3_StartServiceLaunchesStarterThenRetriesImmediately()
    {
        var connection = new FakeConnection(ConnectionState.ServiceStopped);
        var starter = new FakeStarter();
        var (home, _, _) = await Open(Job(), connection: connection, starter: starter);
        using var _ = home;

        await home.Connection.StartServiceCommand.ExecuteAsync(null);

        Assert.Equal(1, starter.Calls);
        Assert.Equal(1, connection.RetryCalls);
        Assert.False(home.Connection.IsStartingService);
    }

    [Fact]
    [Trait("Story", "HU-19")]
    public async Task Criterion3_StartServiceStillRetriesWhenUserRefusesElevation()
    {
        var connection = new FakeConnection(ConnectionState.ServiceStopped);
        var starter = new FakeStarter { Result = false };
        var (home, _, _) = await Open(Job(), connection: connection, starter: starter);
        using var _ = home;

        await home.Connection.StartServiceCommand.ExecuteAsync(null);

        Assert.Equal(1, connection.RetryCalls);
    }

    [Fact]
    [Trait("Story", "HU-19")]
    public async Task Criterion3_StartServiceCannotRunTwiceAtOnce()
    {
        var connection = new FakeConnection(ConnectionState.ServiceStopped);
        var starter = new FakeStarter { Gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously) };
        var (home, _, _) = await Open(Job(), connection: connection, starter: starter);
        using var _ = home;

        var first = home.Connection.StartServiceCommand.ExecuteAsync(null);

        Assert.False(home.Connection.StartServiceCommand.CanExecute(null));
        starter.Gate.SetResult(true);
        await first;
        Assert.True(home.Connection.StartServiceCommand.CanExecute(null));
    }

    [Fact]
    [Trait("Story", "HU-19")]
    public async Task Criterion3_UnauthorizedShowsExplanationWithAccount()
    {
        var connection = new FakeConnection(ConnectionState.Unauthorized);
        var (service, _) = FakeBackboneService.Create();
        using var home = new HomeViewModel(service, Str, connection);
        await Task.CompletedTask;

        Assert.True(home.Connection.ShowUnauthorized);
        Assert.False(home.Connection.ShowContent);
        Assert.Contains(home.Connection.Account, home.Connection.UnauthorizedInstructions, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Story", "HU-19")]
    public async Task Criterion3_IncompatibleContractShowsBothVersions()
    {
        var connection = new FakeConnection(ConnectionState.Connected) { IncompatibleVersion = "2.0" };
        var (service, _) = FakeBackboneService.Create();
        using var home = new HomeViewModel(service, Str, connection);
        await Task.CompletedTask;

        connection.Set(ConnectionState.IncompatibleContract);

        Assert.True(home.Connection.ShowIncompatible);
        Assert.Contains("2.0", home.Connection.IncompatibleText, StringComparison.Ordinal);
        Assert.Contains(ContractVersion.Value, home.Connection.IncompatibleText, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Story", "HU-19")]
    public async Task Criterion3_NoJobsShowsEmptyStateOnlyWhenConnected()
    {
        var connection = new FakeConnection(ConnectionState.Connected);
        var (service, fake) = FakeBackboneService.Create();
        fake.On("GetStatusAsync", _ => Task.FromResult(new ServiceStatus()));
        fake.On("GetJobsAsync", _ => Task.FromResult<IReadOnlyList<JobDefinition>>([]));
        using var home = new HomeViewModel(service, Str, connection);

        await home.LoadAsync(TestContext.Current.CancellationToken);

        Assert.True(home.IsEmpty);
        Assert.False(home.ShowOverall);
        var raised = false;
        home.CreateJobRequested += (_, _) => raised = true;
        home.CreateJobCommand.Execute(null);
        Assert.True(raised);

        connection.Set(ConnectionState.ServiceStopped);
        Assert.False(home.IsEmpty);
    }

    [Fact]
    [Trait("Story", "HU-19")]
    public void Criterion3_EveryConnectionStateHasExactlyOneVisiblePresentation()
    {
        var (service, _) = FakeBackboneService.Create();
        foreach (var state in Enum.GetValues<ConnectionState>())
        {
            using var home = new HomeViewModel(service, Str, new FakeConnection(state));
            var c = home.Connection;
            var shown = new[] { c.ShowConnecting, c.ShowReconnecting, c.ShowServiceStopped, c.ShowUnauthorized, c.ShowIncompatible, c.IsConnected }.Count(x => x);
            Assert.Equal(1, shown);
        }
    }

    // --- Recursos ---

    [Fact]
    [Trait("Story", "HU-19")]
    public void Resources_EveryHomeKeyExistsInBothLanguages()
    {
        var es = ResourceCoverageTests.LoadResources("es-ES");
        var en = ResourceCoverageTests.LoadResources("en-US");
        foreach (var key in es.Keys.Where(k => k.StartsWith("Home_", StringComparison.Ordinal) || k.StartsWith("Conn_", StringComparison.Ordinal) || k.StartsWith("Run_", StringComparison.Ordinal)))
        {
            Assert.True(en.ContainsKey(key), $"en-US: {key}");
        }

        foreach (var state in Enum.GetValues<RunState>())
        {
            Assert.True(es.ContainsKey("Home_Result_" + state) && en.ContainsKey("Home_Result_" + state), state.ToString());
        }
    }

    private sealed class JobStatusBuilder
    {
        public bool Enabled { get; set; } = true;

        public JobActivity Activity { get; set; }

        public HealthState Health { get; set; }

        public bool IsOutdated { get; set; }

        public RunRecord? LastRun { get; set; }

        public DateTimeOffset? NextRunUtc { get; set; }
    }

    private sealed class ProgressBuilder
    {
        public long Files { get; set; }

        public long? FilesTotal { get; set; }

        public long Bytes { get; set; }

        public long? BytesTotal { get; set; }

        public double Speed { get; set; }

        public TimeSpan? Remaining { get; set; }

        public string? Path { get; set; }
    }

    private sealed class FakeConnection : IConnectionMonitor
    {
        public FakeConnection(ConnectionState state) => State = state;

        public ConnectionState State { get; private set; }

        public ServiceInfo? ServiceInfo { get; set; }

        public string? IncompatibleVersion { get; set; }

        public string? IncompatibleContractVersion => IncompatibleVersion;

        public int RetryCalls { get; private set; }

        public event EventHandler<ConnectionState>? StateChanged;

        public void RetryNow() => RetryCalls++;

        public void Set(ConnectionState state)
        {
            State = state;
            StateChanged?.Invoke(this, state);
        }
    }

    private sealed class FakeStarter : IServiceStarter
    {
        public int Calls { get; private set; }

        public bool Result { get; set; } = true;

        public TaskCompletionSource<bool>? Gate { get; set; }

        public async Task<bool> StartAsync(CancellationToken ct = default)
        {
            Calls++;
            return Gate is null ? Result : await Gate.Task;
        }
    }
}
