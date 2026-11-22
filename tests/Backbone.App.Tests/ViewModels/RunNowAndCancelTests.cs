using Backbone.App.Tests.Ipc;
using Backbone.App.Tray;
using Backbone.App.ViewModels;
using Backbone.Contracts;
using StreamJsonRpc;
using Xunit;

namespace Backbone.App.Tests.ViewModels;

/// <summary>HU-14: Copiar ahora y Cancelar en Inicio y Trabajos.</summary>
public sealed class RunNowAndCancelTests
{
    private static readonly Guid JobId = Guid.NewGuid();

    private static string Str(string key) => key == "Run_ProgressFormat" ? "{0}: {1} files, {2}" : key;

    private static ServiceStatus Status(JobActivity activity, RunProgressEvent? current = null, bool enabled = true) =>
        new() { Jobs = [new JobStatus { JobId = JobId, JobName = "Fotos", Enabled = enabled, Activity = activity }], CurrentRun = current };

    private static RemoteInvocationException Remote(BackboneErrorCode code) =>
        new("error", errorCode: (int)code, errorData: new BackboneErrorData { Code = code }, deserializedErrorData: new BackboneErrorData { Code = code });

    private static async Task<(HomeViewModel Home, FakeBackboneService Fake, JobRunState Run)> HomeWith(IBackboneService service, FakeBackboneService fake, JobActivity activity)
    {
        fake.On("GetStatusAsync", _ => Task.FromResult(Status(activity)));
        var home = new HomeViewModel(service, Str);
        await home.LoadAsync(TestContext.Current.CancellationToken);
        return (home, fake, Assert.Single(home.Cards).Run);
    }

    [Theory]
    [Trait("Story", "HU-14")]
    [InlineData(JobActivity.Idle, true, false)]
    [InlineData(JobActivity.Queued, false, true)]
    [InlineData(JobActivity.Running, false, true)]
    public async Task Buttons_FollowActivity(JobActivity activity, bool canRun, bool showCancel)
    {
        var (service, fake) = FakeBackboneService.Create();
        var (home, _, run) = await HomeWith(service, fake, activity);
        using var _ = home;

        Assert.Equal(canRun, run.CanRunNow);
        Assert.Equal(canRun, run.ShowRunNow);
        Assert.Equal(showCancel, run.ShowCancel);
        Assert.Equal(showCancel, run.CanCancel);
    }

    [Fact]
    [Trait("Story", "HU-14")]
    public async Task RunNow_CallsServiceAndStatusChangedSwitchesToCancel()
    {
        var (service, fake) = FakeBackboneService.Create();
        var (home, _, run) = await HomeWith(service, fake, JobActivity.Idle);
        using var _ = home;
        Guid? started = null;
        fake.On("RunNowAsync", a => { started = (Guid)a[0]!; return Task.CompletedTask; });

        await run.RunNowAsync();
        fake.Raise("StatusChanged", new StatusChangedEvent { Status = Status(JobActivity.Queued) });

        Assert.Equal(JobId, started);
        Assert.False(run.CanRunNow);
        Assert.True(run.ShowCancel);
        Assert.False(run.HasError);
    }

    [Fact]
    [Trait("Story", "HU-14")]
    public async Task RunNow_JobAlreadyRunning_IsASoftNoticeNotAnError()
    {
        var (service, fake) = FakeBackboneService.Create();
        var (home, _, run) = await HomeWith(service, fake, JobActivity.Idle);
        using var _ = home;
        fake.On("RunNowAsync", _ => Task.FromException(Remote(BackboneErrorCode.JobAlreadyRunning)));

        await run.RunNowAsync();

        Assert.True(run.HasNotice);
        Assert.False(run.HasError);
    }

    [Fact]
    [Trait("Story", "HU-14")]
    public async Task RunNow_OtherFailure_ShowsError()
    {
        var (service, fake) = FakeBackboneService.Create();
        var (home, _, run) = await HomeWith(service, fake, JobActivity.Idle);
        using var _ = home;
        fake.On("RunNowAsync", _ => Task.FromException(Remote(BackboneErrorCode.JobNotFound)));

        await run.RunNowAsync();

        Assert.True(run.HasError);
        Assert.Equal("Error_JobNotFound", run.Error);
        Assert.False(run.HasNotice);
    }

    [Fact]
    [Trait("Story", "HU-14")]
    public async Task Cancel_CallsServiceShowsCancellingAndRunCompletedReturnsToIdle()
    {
        var (service, fake) = FakeBackboneService.Create();
        var (home, _, run) = await HomeWith(service, fake, JobActivity.Running);
        using var _ = home;
        Guid? cancelled = null;
        fake.On("CancelRunAsync", a => { cancelled = (Guid)a[0]!; return Task.CompletedTask; });

        await run.CancelAsync();

        Assert.Equal(JobId, cancelled);
        Assert.True(run.IsCancelling);
        Assert.Equal("Run_Cancelling", run.CancelText);
        Assert.False(run.CanCancel);

        fake.Raise("RunCompleted", new RunCompletedEvent { Run = new RunRecord { JobId = JobId, State = RunState.Cancelled } });

        Assert.False(run.IsCancelling);
        Assert.True(run.CanRunNow);
        Assert.False(run.ShowCancel);
        Assert.False(run.HasProgress);
        Assert.False(run.HasError);
    }

    [Fact]
    [Trait("Story", "HU-14")]
    public async Task Cancel_Failure_RestoresButtonAndShowsError()
    {
        var (service, fake) = FakeBackboneService.Create();
        var (home, _, run) = await HomeWith(service, fake, JobActivity.Running);
        using var _ = home;
        fake.On("CancelRunAsync", _ => Task.FromException(Remote(BackboneErrorCode.JobNotFound)));

        await run.CancelAsync();

        Assert.False(run.IsCancelling);
        Assert.True(run.CanCancel);
        Assert.True(run.HasError);
    }

    [Fact]
    [Trait("Story", "HU-14")]
    public async Task RunProgress_ShowsPhaseFilesAndBytesWithoutPercentage()
    {
        var (service, fake) = FakeBackboneService.Create();
        var (home, _, run) = await HomeWith(service, fake, JobActivity.Running);
        using var _ = home;

        fake.Raise("RunProgress", new RunProgressEvent { JobId = JobId, Phase = RunPhase.Copying, FilesProcessed = 42, BytesProcessed = 2048 });

        Assert.True(run.HasProgress);
        Assert.Contains("42", run.ProgressText, StringComparison.Ordinal);
        Assert.Contains("KB", run.ProgressText, StringComparison.Ordinal);

        fake.Raise("RunProgress", new RunProgressEvent { JobId = JobId, Phase = RunPhase.Connecting });
        Assert.Equal("Run_Phase_Connecting", run.ProgressText);

        fake.Raise("StatusChanged", new StatusChangedEvent { Status = Status(JobActivity.Idle) });
        Assert.False(run.HasProgress);
    }

    [Fact]
    [Trait("Story", "HU-14")]
    public async Task Reload_RestoresProgressFromCurrentRun()
    {
        var (service, fake) = FakeBackboneService.Create();
        fake.On("GetStatusAsync", _ => Task.FromResult(Status(JobActivity.Running, new RunProgressEvent { JobId = JobId, Phase = RunPhase.Copying, FilesProcessed = 7 })));
        using var home = new HomeViewModel(service, Str);

        await home.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Contains("7", Assert.Single(home.Cards).Run.ProgressText, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Story", "HU-14")]
    public async Task JobsList_ItemsShareRunStateAndFollowActivity()
    {
        var (service, fake) = FakeBackboneService.Create();
        var job = new JobDefinition { Id = JobId, Name = "Fotos", Destination = @"\\nas\b\f" };
        fake.On("GetJobsAsync", _ => Task.FromResult<IReadOnlyList<JobDefinition>>([job]));
        fake.On("GetStatusAsync", _ => Task.FromResult(Status(JobActivity.Idle)));
        using var vm = new JobsViewModel(service, new FakePicker(), k => k);
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        var run = Assert.Single(vm.Jobs).Run;
        Assert.True(run.CanRunNow);
        Guid? started = null;
        fake.On("RunNowAsync", a => { started = (Guid)a[0]!; return Task.CompletedTask; });

        vm.Jobs[0].Run.RunNowCommand.Execute(null);
        fake.Raise("StatusChanged", new StatusChangedEvent { Status = Status(JobActivity.Running) });

        Assert.Equal(JobId, started);
        Assert.True(Assert.Single(vm.Jobs).Run.ShowCancel);
        Assert.False(run.CanRunNow);
    }

    [Fact]
    [Trait("Story", "HU-14")]
    public async Task TrayRunAll_RunsEnabledJobsAndNotDisabledOnes()
    {
        var (service, fake) = FakeBackboneService.Create();
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), disabled = Guid.NewGuid();
        fake.On("GetStatusAsync", _ => Task.FromResult(new ServiceStatus
        {
            Jobs =
            [
                new JobStatus { JobId = a, Enabled = true },
                new JobStatus { JobId = disabled, Enabled = false },
                new JobStatus { JobId = b, Enabled = true },
            ],
        }));
        var started = new List<Guid>();
        fake.On("RunNowAsync", args => { started.Add((Guid)args[0]!); return b == (Guid)args[0]! ? Task.FromException(Remote(BackboneErrorCode.JobAlreadyRunning)) : Task.CompletedTask; });

        var result = await TrayCommands.RunAllNowAsync(service, TestContext.Current.CancellationToken);

        Assert.Equal([a, b], started);
        Assert.Equal(new RunAllResult(1, 1, 0, false), result);
    }

    [Theory]
    [Trait("Story", "HU-14")]
    [InlineData("es-ES")]
    [InlineData("en-US")]
    public void RunTextsExistInBothLanguages(string culture)
    {
        var res = ResourceCoverageTests.LoadResources(culture);
        foreach (var key in new[] { "Run_RunNow.Content", "Run_Cancel", "Run_Cancelling", "Run_AlreadyRunning", "Run_ProgressFormat" })
        {
            Assert.True(res.ContainsKey(key), $"{culture}: {key}");
        }

        foreach (var phase in Enum.GetValues<RunPhase>())
        {
            Assert.True(res.ContainsKey("Run_Phase_" + phase), $"{culture}: {phase}");
        }
    }
}
