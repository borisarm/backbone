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
