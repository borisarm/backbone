using Backbone.App.Tests.Ipc;
using Backbone.App.ViewModels;
using Backbone.Contracts;
using Xunit;

namespace Backbone.App.Tests.ViewModels;

/// <summary>HU-07 criterio 1: el trabajo guardado aparece en Trabajos y en Inicio.</summary>
public sealed class JobsAndHomeTests
{
    [Fact]
    [Trait("Story", "HU-07")]
    public async Task Criterion1_SavedJobAppearsInJobsListAndHome()
    {
        var (service, fake) = FakeBackboneService.Create();
        var jobs = new List<JobDefinition>();
        fake.On("GetJobsAsync", _ => Task.FromResult<IReadOnlyList<JobDefinition>>([.. jobs]));
        fake.On("GetStatusAsync", _ => Task.FromResult(new ServiceStatus { Jobs = [.. jobs.Select(j => new JobStatus { JobId = j.Id, JobName = j.Name })] }));
        fake.On("ValidateJobAsync", _ => Task.FromResult(new ValidationResult()));
        fake.On("SaveJobAsync", a =>
        {
            var saved = ((JobDefinition)a[0]!) with { Id = Guid.NewGuid() };
            jobs.Add(saved);
            return Task.FromResult(saved);
        });
        using var jobsVm = new JobsViewModel(service, new FakePicker(), k => k);
        using var home = new HomeViewModel(service, k => k);
        await jobsVm.LoadAsync(TestContext.Current.CancellationToken);
        await home.LoadAsync(TestContext.Current.CancellationToken);
        Assert.True(jobsVm.IsEmpty);
        Assert.True(home.IsEmpty);

        jobsVm.NewJobCommand.Execute(null);
        var editor = jobsVm.Editor!;
        editor.Name = "Fotos";
        editor.Destination = @"\\nas\b\f";
        await editor.SaveAsync();
        await home.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Null(jobsVm.Editor);
        Assert.Equal("Fotos", Assert.Single(jobsVm.Jobs).Name);
        Assert.Equal("Fotos", Assert.Single(home.Cards).Name);
        Assert.False(jobsVm.IsEmpty);
    }

    [Fact]
    [Trait("Story", "HU-07")]
    public void Home_CreateRequestedRaisesEvent()
    {
        var (service, _) = FakeBackboneService.Create();
        using var home = new HomeViewModel(service, k => k);
        var raised = false;
        home.CreateJobRequested += (_, _) => raised = true;

        home.CreateJobCommand.Execute(null);

        Assert.True(raised);
    }
}

/// <summary>HU-06: aviso de solo lectura (RNF-14) y error del destino que no es un repositorio.</summary>
public sealed class ReadOnlyRepositoryTests
{
    private static readonly Guid JobId = Guid.NewGuid();

    private static ServiceStatus StatusWith(RepositoryState state) =>
        new() { Jobs = [new JobStatus { JobId = JobId, JobName = "Fotos", RepositoryState = state }] };

    private static StatusChangedEvent Changed(RepositoryState state) => new() { Status = StatusWith(state) };

    [Theory]
    [Trait("Story", "HU-06")]
    [InlineData(RepositoryState.ReadOnlyNewerFormat, true)]
    [InlineData(RepositoryState.Unknown, false)]
    [InlineData(RepositoryState.ReadWrite, false)]
    public async Task Home_WarningOnlyForReadOnlyNewerFormat(RepositoryState state, bool expected)
    {
        var (service, fake) = FakeBackboneService.Create();
        fake.On("GetStatusAsync", _ => Task.FromResult(StatusWith(state)));
        using var home = new HomeViewModel(service, k => k);

        await home.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expected, Assert.Single(home.Cards).IsReadOnly);
    }

    [Fact]
    [Trait("Story", "HU-06")]
    public async Task Home_WarningFollowsStatusChanged()
    {
        var (service, fake) = FakeBackboneService.Create();
        fake.On("GetStatusAsync", _ => Task.FromResult(StatusWith(RepositoryState.Unknown)));
        using var home = new HomeViewModel(service, k => k);
        await home.LoadAsync(TestContext.Current.CancellationToken);
        Assert.False(Assert.Single(home.Cards).IsReadOnly);

        fake.Raise("StatusChanged", Changed(RepositoryState.ReadOnlyNewerFormat));
        Assert.True(Assert.Single(home.Cards).IsReadOnly);

        fake.Raise("StatusChanged", Changed(RepositoryState.ReadWrite));
        Assert.False(Assert.Single(home.Cards).IsReadOnly);
    }

    [Fact]
    [Trait("Story", "HU-06")]
    public async Task Jobs_WarningFromGetStatusAndStatusChanged()
    {
        var job = new JobDefinition { Id = JobId, Name = "Fotos", Destination = @"\\nas\b\f" };
        var (service, fake) = FakeBackboneService.Create();
        fake.On("GetJobsAsync", _ => Task.FromResult<IReadOnlyList<JobDefinition>>([job]));
        fake.On("GetStatusAsync", _ => Task.FromResult(StatusWith(RepositoryState.ReadOnlyNewerFormat)));
        using var vm = new JobsViewModel(service, new FakePicker(), k => k);

        await vm.LoadAsync(TestContext.Current.CancellationToken);
        Assert.True(Assert.Single(vm.Jobs).IsReadOnly);

        fake.Raise("StatusChanged", Changed(RepositoryState.ReadWrite));
        Assert.False(Assert.Single(vm.Jobs).IsReadOnly);
    }

    [Fact]
    [Trait("Story", "HU-06")]
    public async Task Editor_DestinationNotRepositoryShowsUnderDestination()
    {
        var (service, fake) = FakeBackboneService.Create();
        fake.On("ValidateJobAsync", _ => Task.FromResult(new ValidationResult
        {
            Errors = [new ValidationIssue { Field = "Destination", Code = ValidationIssueCode.DestinationNotRepository }],
        }));
        using var editor = new JobEditorViewModel(service, new FakePicker(), k => k);
        editor.Name = "Fotos";
        editor.Destination = @"\\nas\b\f";

        await editor.SaveAsync();

        Assert.Equal("Validation_DestinationNotRepository", editor.DestinationError);
        Assert.True(editor.HasDestinationError);
    }
}
