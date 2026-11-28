using System.Globalization;
using Backbone.App.Ipc;
using Backbone.App.Tests.Ipc;
using Backbone.App.ViewModels;
using Backbone.Contracts;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Backbone.App.Tests.ViewModels;

/// <summary>HU-19: casos límite del panel de inicio (estimador, reconexión, tarjetas, texto no fiable).</summary>
public sealed class HomePanelEdgeTests
{
    private static readonly Guid JobId = Guid.NewGuid();
    private static readonly Dictionary<string, string> Resources = ResourceCoverageTests.LoadResources("es-ES");
    private const long Mb = 1024 * 1024;

    private static string Str(string key) => Resources[key];

    private static FakeTimeProvider NewTime() => new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private static RunProgressEvent Ev(long bytes, long? total = null, long files = 1, long? filesTotal = null, double speed = 0, TimeSpan? remaining = null, string? path = null, long runId = 1, RunPhase phase = RunPhase.Copying) => new()
    {
        RunId = runId,
        JobId = JobId,
        Phase = phase,
        FilesProcessed = files,
        FilesTotal = filesTotal,
        BytesProcessed = bytes,
        BytesTotal = total,
        BytesPerSecond = speed,
        EstimatedRemaining = remaining,
        CurrentPath = path,
    };

    private static JobRunState NewRun(FakeTimeProvider time)
    {
        var (service, _) = FakeBackboneService.Create();
        return new JobRunState(service, Str, JobId, time);
    }

    // --- Criterio 2: estimador ---

    [Fact]
    [Trait("Story", "HU-19")]
    public void Criterion2_EstimatorWithOneSampleOrSameInstantHasNoRate()
    {
        var time = NewTime();
        var e = new RunRateEstimator(time);
        Assert.Null(e.BytesPerSecond);
        e.Add(1, 100);
        Assert.Null(e.BytesPerSecond);
        e.Add(1, 200); // mismo instante: sin división por cero
        Assert.Null(e.BytesPerSecond);
    }

    [Fact]
    [Trait("Story", "HU-19")]
    public void Criterion2_EstimatorClockJumpingBackwardsNeverGivesNegativeOrInfiniteRate()
    {
        var time = new SettableTime { Now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) };
        var e = new RunRateEstimator(time);
        e.Add(1, 0);
        time.Now = time.Now.AddSeconds(4);
        e.Add(1, 4000);
        time.Now = time.Now.AddHours(-1); // el reloj salta hacia atrás
        e.Add(1, 8000);

        var rate = e.BytesPerSecond;
        Assert.True(rate is null || (rate >= 0 && double.IsFinite(rate.Value)), $"rate={rate}");
    }

    [Fact]
    [Trait("Story", "HU-19")]
    public void Criterion2_EstimatorClockJumpingForwardKeepsLastTwoSamples()
    {
        var time = NewTime();
        var e = new RunRateEstimator(time);
        e.Add(1, 0);
        time.Advance(TimeSpan.FromDays(2));
        e.Add(1, 1000);

        Assert.NotNull(e.BytesPerSecond);
        Assert.True(double.IsFinite(e.BytesPerSecond!.Value));
    }

    [Fact]
    [Trait("Story", "HU-19")]
    public void Criterion2_EstimatorCounterGoingBackwardsRestartsWindow()
    {
        var time = NewTime();
        var e = new RunRateEstimator(time);
        e.Add(1, 5000);
        time.Advance(TimeSpan.FromSeconds(1));
        e.Add(1, 1000); // mismo RunId pero retrocede
        Assert.Null(e.BytesPerSecond);
        time.Advance(TimeSpan.FromSeconds(1));
        e.Add(1, 3000);
        Assert.Equal(2000, e.BytesPerSecond!.Value, 3);
    }

    [Fact]
    [Trait("Story", "HU-19")]
    public void Criterion2_EstimatorResetForgetsEverything()
    {
        var time = NewTime();
        var e = new RunRateEstimator(time);
        e.Add(1, 0);
        time.Advance(TimeSpan.FromSeconds(1));
        e.Add(1, 10);
        e.Reset();
        Assert.Null(e.BytesPerSecond);
    }

    [Fact]
    [Trait("Story", "HU-19")]
    public void Criterion2_StalledCopyShowsNoSpeedAndNoEstimateInsteadOfInfinity()
    {
        var time = NewTime();
        var run = NewRun(time);
        run.ApplyProgress(Ev(10 * Mb, 100 * Mb));
        time.Advance(TimeSpan.FromSeconds(3));
        run.ApplyProgress(Ev(10 * Mb, 100 * Mb));

        Assert.False(run.HasSpeed);
        Assert.False(run.HasRemaining);
        Assert.Equal(10, run.Percent, 3);
    }

    [Fact]
    [Trait("Story", "HU-19")]
    public void Criterion2_ServiceSpeedZeroFallsBackToMovingAverage()
    {
        var time = NewTime();
        var run = NewRun(time);
        run.ApplyProgress(Ev(0, 100 * Mb, speed: 0));
        time.Advance(TimeSpan.FromSeconds(1));
        run.ApplyProgress(Ev(5 * Mb, 100 * Mb, speed: 0));

        Assert.Contains("5 MB/s", run.SpeedText, StringComparison.Ordinal);
    }

    [Theory]
    [Trait("Story", "HU-19")]
    [InlineData(double.NaN)]
    [InlineData(-5.0)]
    public void Criterion2_InvalidServiceSpeedIsIgnored(double speed)
    {
        var run = NewRun(NewTime());
        run.ApplyProgress(Ev(Mb, 100 * Mb, speed: speed));

        Assert.False(run.HasSpeed);
        Assert.DoesNotContain("NaN", run.SpeedText, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Story", "HU-19")]
    public void Criterion2_InfiniteServiceSpeedDoesNotProduceGarbageText()
    {
        var run = NewRun(NewTime());
        run.ApplyProgress(Ev(Mb, 100 * Mb, speed: double.PositiveInfinity));

        Assert.DoesNotContain("∞", run.SpeedText, StringComparison.Ordinal);
        Assert.DoesNotContain("Infinity", run.SpeedText, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Story", "HU-19")]
    public void Criterion2_BytesBeyondTotalDoNotOverflowPercentOrShowNegativeEstimate()
    {
        var run = NewRun(NewTime());
        run.ApplyProgress(Ev(200, 100, speed: 10));

        Assert.InRange(run.Percent, 0, 100);
        Assert.DoesNotContain(" de ", run.BytesText, StringComparison.Ordinal);
        Assert.False(run.HasRemaining);
    }

    [Fact]
    [Trait("Story", "HU-19")]
    public void Criterion2_NegativeServiceEstimateIsHidden()
    {
        var run = NewRun(NewTime());
        run.ApplyProgress(Ev(Mb, 100 * Mb, remaining: TimeSpan.FromSeconds(-5)));

        Assert.False(run.HasRemaining);
    }

    [Fact]
    [Trait("Story", "HU-19")]
    public void Criterion2_AbsurdEstimateOverThirtyDaysIsHidden()
    {
        var run = NewRun(NewTime());
        run.ApplyProgress(Ev(1, long.MaxValue / 2, speed: 1));

        Assert.False(run.HasRemaining);
    }

    [Fact]
    [Trait("Story", "HU-19")]
    public void Criterion2_ZeroByteFilesWithKnownFileTotalUseFilesForTheBar()
    {
        var run = NewRun(NewTime());
        run.ApplyProgress(Ev(0, 0, files: 5, filesTotal: 10));

        Assert.False(run.IsIndeterminate);
        Assert.Equal(50, run.Percent, 3);
        Assert.False(run.HasRemaining);
    }

    [Fact]
    [Trait("Story", "HU-19")]
    public void Criterion2_FinishedAllBytesShowsLessThanAMinuteNotNegative()
    {
        var run = NewRun(NewTime());
        run.ApplyProgress(Ev(100, 100, speed: 10));

        Assert.Equal(100, run.Percent, 3);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, Str("Run_RemainingFormat"), Str("Run_Duration_LessThanMinute")), run.RemainingText);
    }

    [Fact]
    [Trait("Story", "HU-19")]
    public void Criterion2_NewRunDoesNotInheritPreviousRunRate()
    {
        var time = NewTime();
        var run = NewRun(time);
        run.ApplyProgress(Ev(0, 100 * Mb, runId: 1));
        time.Advance(TimeSpan.FromSeconds(1));
        run.ApplyProgress(Ev(50 * Mb, 100 * Mb, runId: 1));
        run.Complete();

        run.ApplyProgress(Ev(1 * Mb, 100 * Mb, runId: 2));

        Assert.False(run.HasSpeed);
    }

    [Fact]
    [Trait("Story", "HU-19")]
    public void Criterion2_LongRunOverAnHourFormatsHoursAndMinutes()
    {
        var run = NewRun(NewTime());
        run.ApplyProgress(Ev(Mb, 1000 * Mb, remaining: TimeSpan.FromMinutes(135)));

        Assert.Equal(string.Format(CultureInfo.CurrentCulture, Str("Run_RemainingFormat"), string.Format(CultureInfo.CurrentCulture, Str("Run_Duration_HoursMinutes"), 2, 15)), run.RemainingText);
    }

    [Fact]
    [Trait("Story", "HU-19")]
    public void Criterion2_SubSecondEventsEachRefreshTheCounters()
    {
        var time = NewTime();
        var run = NewRun(time);
        var texts = new List<string>();
        for (var i = 1; i <= 8; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(250));
            run.ApplyProgress(Ev(i * Mb, 100 * Mb, files: i));
            texts.Add(run.BytesText);
        }

        Assert.Equal(8, texts.Distinct().Count());
    }

    // --- Archivo actual no fiable ---

    [Fact]
    [Trait("Story", "HU-19")]
    public void Criterion2_CurrentPathEscapesControlsAndTruncates()
    {
        var run = NewRun(NewTime());
        var hostile = "\\" + new string('a', 5000) + "\u202E\r\n\0";
        run.ApplyProgress(Ev(1, 10, path: hostile));

        Assert.True(run.CurrentPathText.Length < 400, run.CurrentPathText.Length.ToString(CultureInfo.InvariantCulture));
        Assert.All(run.CurrentPathText, c => Assert.False(char.IsControl(c)));

        run.ApplyProgress(Ev(2, 10, path: "\\x\u202Ey\r\n.txt"));
        Assert.Contains("\\u202e", run.CurrentPathText, StringComparison.Ordinal);
        Assert.DoesNotContain('\u202E', run.CurrentPathText);
    }

    [Fact]
    [Trait("Story", "HU-19")]
    public void Criterion2_CurrentPathEmptyOrNullHidesTheLine()
    {
        var run = NewRun(NewTime());
        run.ApplyProgress(Ev(1, 10, path: "\\a.txt"));
        Assert.True(run.HasCurrentPath);

        run.ApplyProgress(Ev(2, 10, path: null));
        Assert.False(run.HasCurrentPath);
        run.ApplyProgress(Ev(3, 10, path: string.Empty));
        Assert.False(run.HasCurrentPath);
    }

    [Fact]
    [Trait("Story", "HU-19")]
    public void Criterion2_UnicodePathIsShownIntact()
    {
        var run = NewRun(NewTime());
        run.ApplyProgress(Ev(1, 10, path: "\\Fotos\\niño 日本語 😀.jpg"));

        Assert.Contains("niño 日本語 😀.jpg", run.CurrentPathText, StringComparison.Ordinal);
    }

    // --- Criterio 1: tarjetas en sitio y cambios de estado ---

    private static async Task<(HomeViewModel Home, FakeBackboneService Fake, FakeTimeProvider Time)> Open(JobStatus job, FakeConnection? connection = null)
    {
        var (service, fake) = FakeBackboneService.Create();
        fake.On("GetStatusAsync", _ => Task.FromResult(new ServiceStatus { Jobs = [job] }));
        fake.On("GetJobsAsync", _ => Task.FromResult<IReadOnlyList<JobDefinition>>([new JobDefinition { Id = job.JobId, Name = job.JobName }]));
        var time = NewTime();
        var home = new HomeViewModel(service, Str, connection, null, time);
        await home.LoadAsync(TestContext.Current.CancellationToken);
        return (home, fake, time);
    }

    [Fact]
    [Trait("Story", "HU-19")]
    public async Task Criterion1_StatusChangedUpdatesTextsInPlaceAndRaisesOnlyChangedProperties()
    {
        var (home, fake, _) = await Open(new JobStatus { JobId = JobId, JobName = "Fotos", Enabled = true });
        using var _ = home;
        var card = Assert.Single(home.Cards);
        var changed = new List<string?>();
        card.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        fake.Raise("StatusChanged", new StatusChangedEvent { Status = new ServiceStatus { Jobs = [new JobStatus { JobId = JobId, JobName = "Fotos", Enabled = true }] } });

        Assert.DoesNotContain(nameof(JobCard.StateText), changed);
        Assert.DoesNotContain(nameof(JobCard.LastRunText), changed);
        Assert.DoesNotContain(nameof(JobCard.NextRunText), changed);

        fake.Raise("StatusChanged", new StatusChangedEvent { Status = new ServiceStatus { Jobs = [new JobStatus { JobId = JobId, JobName = "Fotos", Enabled = true, Health = HealthState.Error }] } });
        Assert.Contains(nameof(JobCard.StateText), changed);
    }

    [Fact]
    [Trait("Story", "HU-19")]
    public async Task Criterion1_RenamedJobKeepsCardAndUpdatesAccessibleNames()
    {
        var (home, fake, _) = await Open(new JobStatus { JobId = JobId, JobName = "Fotos", Enabled = true });
        using var _ = home;
        var card = Assert.Single(home.Cards);

        fake.Raise("StatusChanged", new StatusChangedEvent { Status = new ServiceStatus { Jobs = [new JobStatus { JobId = JobId, JobName = "Vídeos", Enabled = true }] } });

        Assert.Same(card, Assert.Single(home.Cards));
        Assert.Contains("Vídeos", card.RunNowAutomationName, StringComparison.Ordinal);
        Assert.Contains("Vídeos", card.CancelAutomationName, StringComparison.Ordinal);
        Assert.Contains("Vídeos", card.ProgressAutomationName, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Story", "HU-19")]
    public async Task Criterion1_QueuedJobShowsCancelAndNoProgress()
    {
        var (home, _, _) = await Open(new JobStatus { JobId = JobId, JobName = "Fotos", Enabled = true, Activity = JobActivity.Queued });
        using var _ = home;
        var card = Assert.Single(home.Cards);

        Assert.True(card.Run.ShowCancel);
        Assert.False(card.Run.ShowRunNow);
        Assert.False(card.Run.HasProgress);
    }

    [Fact]
    [Trait("Story", "HU-19")]
    public async Task Criterion1_RunCompletedReturnsToRunNowAndClearsCancelling()
    {
        var (home, fake, _) = await Open(new JobStatus { JobId = JobId, JobName = "Fotos", Enabled = true, Activity = JobActivity.Running });
        using var _ = home;
        var run = Assert.Single(home.Cards).Run;
        fake.Raise("RunProgress", Ev(Mb, 10 * Mb));
        run.IsCancelling = true;

        fake.Raise("RunCompleted", new RunCompletedEvent { Run = new RunRecord { JobId = JobId, State = RunState.Cancelled } });

        Assert.True(run.ShowRunNow);
        Assert.False(run.ShowCancel);
        Assert.False(run.IsCancelling);
        Assert.False(run.HasProgress);
    }

    [Fact]
    [Trait("Story", "HU-19")]
    public async Task Criterion1_ProgressForUnknownJobDoesNotThrowOrBreakOtherCards()
    {
        var (home, fake, _) = await Open(new JobStatus { JobId = JobId, JobName = "Fotos", Enabled = true });
        using var _ = home;

        fake.Raise("RunProgress", new RunProgressEvent { RunId = 9, JobId = Guid.NewGuid(), Phase = RunPhase.Copying, BytesProcessed = 5 });

        Assert.Single(home.Cards);
        Assert.False(home.Cards[0].Run.HasProgress);
    }

    // --- Criterio 3: reconexión y arranque ---

    [Fact]
    [Trait("Story", "HU-19")]
    public async Task Criterion3_ReconnectionSequenceWalksThroughEveryStateAndRestoresContent()
    {
        var connection = new FakeConnection(ConnectionState.Connected);
        var (home, _, _) = await Open(new JobStatus { JobId = JobId, JobName = "Fotos", Enabled = true }, connection);
        using var _ = home;
        var c = home.Connection;

        connection.Set(ConnectionState.Reconnecting);
        Assert.True(c.ShowReconnecting);
        Assert.True(c.ShowContent);
        Assert.Single(home.Cards);

        connection.Set(ConnectionState.ServiceStopped);
        Assert.True(c.ShowServiceStopped);
        Assert.False(c.ShowReconnecting);
        Assert.False(c.ShowContent);

        connection.Set(ConnectionState.Connecting);
        Assert.True(c.ShowConnecting);
        Assert.False(c.ShowServiceStopped);

        connection.Set(ConnectionState.Connected);
        Assert.True(c.IsConnected);
        Assert.True(c.ShowContent);
        Assert.False(c.ShowServiceStopped || c.ShowReconnecting || c.ShowConnecting);
        Assert.True(home.ShowOverall);
    }

    [Theory]
    [Trait("Story", "HU-19")]
    [InlineData(ConnectionState.Unauthorized)]
    [InlineData(ConnectionState.IncompatibleContract)]
    [InlineData(ConnectionState.ServiceStopped)]
    public async Task Criterion3_SpecialStatesHideCardsAndOverallAndEmptyState(ConnectionState state)
    {
        var connection = new FakeConnection(ConnectionState.Connected);
        var (home, _, _) = await Open(new JobStatus { JobId = JobId, JobName = "Fotos", Enabled = true }, connection);
        using var _ = home;

        connection.Set(state);

        Assert.False(home.Connection.ShowContent);
        Assert.False(home.ShowOverall);
        Assert.False(home.IsEmpty);
    }

    [Fact]
    [Trait("Story", "HU-19")]
    public void Criterion3_IncompatibleContractWithoutKnownVersionShowsPlaceholder()
    {
        var (service, _) = FakeBackboneService.Create();
        using var home = new HomeViewModel(service, Str, new FakeConnection(ConnectionState.IncompatibleContract));

        Assert.Contains("?", home.Connection.IncompatibleText, StringComparison.Ordinal);
        Assert.Contains(ContractVersion.Value, home.Connection.IncompatibleText, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Story", "HU-19")]
    public async Task Criterion3_StartServiceWhenStarterThrowsStillRetriesAndReenablesButton()
    {
        var connection = new FakeConnection(ConnectionState.ServiceStopped);
        var (service, _) = FakeBackboneService.Create();
        using var home = new HomeViewModel(service, Str, connection, new ThrowingStarter());

        await Assert.ThrowsAsync<InvalidOperationException>(() => home.Connection.StartServiceCommand.ExecuteAsync(null));

        Assert.Equal(1, connection.RetryCalls);
        Assert.False(home.Connection.IsStartingService);
        Assert.True(home.Connection.StartServiceCommand.CanExecute(null));
    }

    [Fact]
    [Trait("Story", "HU-19")]
    public async Task Criterion3_StartServiceWithoutStarterJustRetries()
    {
        var connection = new FakeConnection(ConnectionState.ServiceStopped);
        var (service, _) = FakeBackboneService.Create();
        using var home = new HomeViewModel(service, Str, connection, null);

        await home.Connection.StartServiceCommand.ExecuteAsync(null);

        Assert.Equal(1, connection.RetryCalls);
    }

    [Fact]
    [Trait("Story", "HU-19")]
    public async Task Criterion3_DisposedHomeNoLongerReactsToConnectionOrStatus()
    {
        var connection = new FakeConnection(ConnectionState.Connected);
        var (home, fake, _) = await Open(new JobStatus { JobId = JobId, JobName = "Fotos", Enabled = true }, connection);
        home.Dispose();

        connection.Set(ConnectionState.ServiceStopped);
        fake.Raise("StatusChanged", new StatusChangedEvent { Status = new ServiceStatus() });

        Assert.True(home.Connection.IsConnected);
        Assert.Single(home.Cards);
    }

    [Fact]
    [Trait("Story", "HU-19")]
    public async Task Criterion3_LoadFailureShowsErrorAndKeepsPageUsable()
    {
        var (service, fake) = FakeBackboneService.Create();
        fake.On("GetStatusAsync", _ => throw new InvalidOperationException("boom"));
        using var home = new HomeViewModel(service, Str);

        await home.LoadAsync(TestContext.Current.CancellationToken);

        Assert.True(home.HasError);
    }

    // --- Criterio 4: Narrador y teclado (lo comprobable sin UI) ---

    [Fact]
    [Trait("Story", "HU-19")]
    public async Task Criterion4_EveryCardAutomationNameIsNonEmptyAndPerJob()
    {
        var (home, _, _) = await Open(new JobStatus { JobId = JobId, JobName = "Fotos", Enabled = true });
        using var _ = home;
        var card = home.Cards[0];

        foreach (var name in new[] { card.AutomationName, card.RunNowAutomationName, card.CancelAutomationName, card.ProgressAutomationName })
        {
            Assert.False(string.IsNullOrWhiteSpace(name));
            Assert.Contains("Fotos", name, StringComparison.Ordinal);
        }
    }

    [Fact]
    [Trait("Story", "HU-19")]
    public async Task Criterion4_CountersChangingDoNotRaiseCardStateOrAutomationName()
    {
        var (home, fake, time) = await Open(new JobStatus { JobId = JobId, JobName = "Fotos", Enabled = true, Activity = JobActivity.Running });
        using var _ = home;
        var card = home.Cards[0];
        fake.Raise("RunProgress", Ev(1));
        var changed = new List<string?>();
        card.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        for (var i = 2; i < 10; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(200));
            fake.Raise("RunProgress", Ev(i * 1000, 100_000));
        }

        Assert.Empty(changed);
    }

    [Fact]
    [Trait("Story", "HU-19")]
    public void Criterion4_ActionButtonsStateIsReachableOnlyWhenRelevant()
    {
        var run = NewRun(NewTime());
        Assert.True(run.CanRunNow);
        Assert.False(run.CanCancel);

        run.Activity = JobActivity.Running;
        Assert.False(run.CanRunNow);
        Assert.True(run.CanCancel);

        run.IsCancelling = true;
        Assert.False(run.CanCancel);
        Assert.Equal(Str("Run_Cancelling"), run.CancelText);
    }

    // --- Recursos sin usar ---

    [Fact]
    [Trait("Story", "HU-19")]
    public void Resources_HomeJobKeysUnusedByCodeAreDocumented()
    {
        // Documenta el estado: estas dos claves existen en los .resw pero ningún código ni XAML las usa.
        var src = Path.Combine(FindRepoRoot(), "src", "Backbone.App");
        var sep = Path.DirectorySeparatorChar;
        var used = Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".xaml", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{sep}obj{sep}", StringComparison.Ordinal) && !f.Contains($"{sep}bin{sep}", StringComparison.Ordinal))
            .Select(File.ReadAllText)
            .Any(t => t.Contains("Home_Job_NeverRun", StringComparison.Ordinal) || t.Contains("Home_Job_LastSuccessFormat", StringComparison.Ordinal));

        Assert.False(used, "Si ahora se usan, retirar esta prueba.");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Backbone.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("No se encontró Backbone.slnx");
    }

    private sealed class SettableTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; }

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class ThrowingStarter : IServiceStarter
    {
        public Task<bool> StartAsync(CancellationToken ct = default) => throw new InvalidOperationException("fallo");
    }

    private sealed class FakeConnection : IConnectionMonitor
    {
        public FakeConnection(ConnectionState state) => State = state;

        public ConnectionState State { get; private set; }

        public ServiceInfo? ServiceInfo { get; set; }

        public string? IncompatibleContractVersion { get; set; }

        public int RetryCalls { get; private set; }

        public event EventHandler<ConnectionState>? StateChanged;

        public void RetryNow() => RetryCalls++;

        public void Set(ConnectionState state)
        {
            State = state;
            StateChanged?.Invoke(this, state);
        }
    }
}
