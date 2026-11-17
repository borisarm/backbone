using System.Collections.Concurrent;
using System.Text.Json;
using Backbone.Contracts;
using Backbone.Service.Persistence;
using Backbone.Service.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backbone.Service.Tests.Ipc;

/// <summary>
/// HU-07 por IPC (§7.2, §7.5): <c>GetJobsAsync</c>, <c>SaveJobAsync</c>, <c>DeleteJobAsync</c>, <c>ValidateJobAsync</c> y
/// <c>GetStatusAsync</c>, con la validación en el servicio (criterio 4), la persistencia en <c>jobs.json</c> y
/// <c>StatusChanged</c>.
/// </summary>
[Trait("Story", "HU-07")]
public sealed class JobIpcTests : IAsyncLifetime
{
    private const string Documents = @"C:\Users\ana\Documentos";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly IpcTestHarness _server = new();
    private TestConnection _ana = null!; // Asignado en InitializeAsync.

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _server.FileSystem.AddFolder(Documents).AddFolder(@"C:\Users\ana\Imágenes");
        await _server.Jobs.LoadAsync(Ct);
        await _server.AuthorizeAsync(TestSids.Ana, TestSids.Luis);
        _ana = await _server.ConnectAuthorizedAsync(TestSids.Identity(TestSids.Ana));
        await _ana.Proxy.SaveCredentialAsync(@"\\nas\backups", new CredentialInput { UserName = "ana", Password = "Contraseña-HU07" }, Ct);
    }

    public ValueTask DisposeAsync() => _server.DisposeAsync();

    private static JobDefinition NewJob(string name = "Documentos", string destination = @"\\NAS\Backups\PC-Salon\") =>
        JobDefaults.NewJob() with
        {
            Name = name,
            Sources = [new JobSource { Alias = name, Path = Documents }],
            Destination = destination,
        };

    [Fact]
    public async Task Saving_a_new_job_assigns_an_id_normalizes_the_destination_and_lists_it()
    {
        var saved = await _ana.Proxy.SaveJobAsync(NewJob(), Ct);

        Assert.NotEqual(Guid.Empty, saved.Id);
        Assert.Equal(@"\\nas\backups\PC-Salon", saved.Destination);
        var listed = Assert.Single(await _ana.Proxy.GetJobsAsync(Ct));
        Assert.Equal(saved.Id, listed.Id);
        Assert.Equal("Documentos", listed.Name);
        Assert.Equal(JobDefaults.Exclusions, listed.Exclusions);
    }

    [Fact]
    public async Task A_saved_job_is_in_jobs_json_and_survives_a_restart_of_the_store()
    {
        var saved = await _ana.Proxy.SaveJobAsync(NewJob(), Ct);

        using var restarted = new JobStore(Path.Combine(_server.DataRoot, "config", "jobs.json"), TestFileTrust.CurrentUser, NullLogger<JobStore>.Instance);
        await restarted.LoadAsync(Ct);

        Assert.Equal(saved.Id, Assert.Single(restarted.Jobs).Id);
    }

    [Fact]
    public async Task Saving_and_deleting_emit_StatusChanged_with_the_jobs_to_every_client()
    {
        var luis = await _server.ConnectAuthorizedAsync(TestSids.Identity(TestSids.Luis));
        var events = new ConcurrentQueue<StatusChangedEvent>();
        var received = new SemaphoreSlim(0);
        luis.Proxy.StatusChanged += (_, e) =>
        {
            events.Enqueue(e);
            received.Release();
        };

        var saved = await _ana.Proxy.SaveJobAsync(NewJob(), Ct);
        Assert.True(await received.WaitAsync(Wait, Ct));
        await _ana.Proxy.DeleteJobAsync(saved.Id, Ct);
        Assert.True(await received.WaitAsync(Wait, Ct));

        var list = events.ToList();
        var job = Assert.Single(list[0].Status.Jobs);
        Assert.Equal((saved.Id, "Documentos", true, JobActivity.Idle), (job.JobId, job.JobName, job.Enabled, job.Activity));
        Assert.Empty(list[1].Status.Jobs);
    }

    [Fact]
    public async Task GetStatus_lists_the_saved_jobs()
    {
        var saved = await _ana.Proxy.SaveJobAsync(NewJob(), Ct);

        var status = await _ana.Proxy.GetStatusAsync(Ct);

        Assert.Equal(saved.Id, Assert.Single(status.Jobs).JobId);
        Assert.Equal(_server.Time.GetUtcNow(), status.TimestampUtc);
        Assert.Equal(TimeSpan.Zero, status.TimestampUtc.Offset);
    }

    [Fact]
    public async Task Updating_an_existing_job_replaces_it_in_place()
    {
        var first = await _ana.Proxy.SaveJobAsync(NewJob(), Ct);
        var second = await _ana.Proxy.SaveJobAsync(NewJob("Imágenes", @"\\nas\backups\fotos"), Ct);

        await _ana.Proxy.SaveJobAsync(first with { Name = "Documentos de Ana", Enabled = false }, Ct);

        var jobs = await _ana.Proxy.GetJobsAsync(Ct);
        Assert.Equal([first.Id, second.Id], jobs.Select(j => j.Id));
        Assert.Equal(("Documentos de Ana", false), (jobs[0].Name, jobs[0].Enabled));
    }

    [Fact]
    public async Task Saving_or_deleting_an_unknown_id_is_JobNotFound()
    {
        await RpcAssert.FailsWithAsync(BackboneErrorCode.JobNotFound, () => _ana.Proxy.SaveJobAsync(NewJob() with { Id = Guid.NewGuid() }, Ct));
        await RpcAssert.FailsWithAsync(BackboneErrorCode.JobNotFound, () => _ana.Proxy.DeleteJobAsync(Guid.NewGuid(), Ct));
        Assert.Empty(await _ana.Proxy.GetJobsAsync(Ct));
    }

    [Theory]
    [InlineData(JobActivity.Running)]
    [InlineData(JobActivity.Queued)]
    public async Task Deleting_a_running_or_queued_job_is_JobAlreadyRunning(JobActivity activity)
    {
        var saved = await _ana.Proxy.SaveJobAsync(NewJob(), Ct);
        _server.Activity.Set(saved.Id, activity);

        await RpcAssert.FailsWithAsync(BackboneErrorCode.JobAlreadyRunning, () => _ana.Proxy.DeleteJobAsync(saved.Id, Ct));

        Assert.Single(await _ana.Proxy.GetJobsAsync(Ct));
    }

    [Fact]
    public async Task Deleting_does_not_touch_the_credential_of_the_share()
    {
        var saved = await _ana.Proxy.SaveJobAsync(NewJob(), Ct);

        await _ana.Proxy.DeleteJobAsync(saved.Id, Ct);

        Assert.NotNull(await _ana.Proxy.GetCredentialInfoAsync(@"\\nas\backups", Ct));
    }

    [Fact]
    public async Task Saving_without_a_credential_for_the_share_is_CredentialRequired()
    {
        await RpcAssert.FailsWithAsync(BackboneErrorCode.CredentialRequired, () => _ana.Proxy.SaveJobAsync(NewJob(destination: @"\\otro-nas\copias"), Ct));

        Assert.Empty(await _ana.Proxy.GetJobsAsync(Ct));
    }

    [Fact]
    public async Task ValidateJob_returns_every_error_with_its_field_and_saves_nothing()
    {
        var job = NewJob() with
        {
            Sources =
            [
                new JobSource { Alias = "Docs", Path = Documents },
                new JobSource { Alias = "docs", Path = @"C:\no\existe" },
            ],
            Destination = @"D:\copias",
        };

        var result = await _ana.Proxy.ValidateJobAsync(job, Ct);

        Assert.Equal(
            [
                ("Sources[1].Alias", ValidationIssueCode.AliasDuplicate),
                ("Sources[1].Path", ValidationIssueCode.SourceNotFound),
                ("Destination", ValidationIssueCode.DestinationNotUnc),
            ],
            result.Errors.Select(e => (e.Field, e.Code)));
        Assert.Empty(await _ana.Proxy.GetJobsAsync(Ct));
        Assert.False(File.Exists(Path.Combine(_server.DataRoot, "config", "jobs.json")));
    }

    [Fact]
    public async Task Saving_an_invalid_job_fails_with_ValidationFailed_and_the_whole_result()
    {
        var job = NewJob() with { Name = " ", Destination = "nas" };

        var data = await RpcAssert.FailsWithAsync(BackboneErrorCode.ValidationFailed, () => _ana.Proxy.SaveJobAsync(job, Ct));

        Assert.NotNull(data.Validation);
        Assert.Equal(
            [("Name", ValidationIssueCode.NameRequired), ("Destination", ValidationIssueCode.DestinationNotUnc)],
            data.Validation.Errors.Select(e => (e.Field, e.Code)));
        Assert.Empty(await _ana.Proxy.GetJobsAsync(Ct));
    }

    [Fact]
    public async Task A_second_job_with_the_same_destination_is_rejected()
    {
        await _ana.Proxy.SaveJobAsync(NewJob(), Ct);

        await RpcAssert.ValidationFailsAsync(
            "Destination", ValidationIssueCode.DestinationUsedByOtherJob, () => _ana.Proxy.SaveJobAsync(NewJob("Otro", @"\\nas\backups\pc-salon"), Ct));
    }

    [Fact]
    public async Task Criterion_6_the_data_folder_is_rejected_on_its_source_and_a_containing_source_is_saved_with_a_warning()
    {
        var dataRoot = _server.DataRoot;
        var parent = Path.GetDirectoryName(dataRoot) ?? throw new InvalidOperationException("DataRoot has no parent.");
        _server.FileSystem.AddFolder(dataRoot).AddFolder(parent);
        var inside = NewJob() with { Sources = [new JobSource { Alias = "Datos", Path = dataRoot }] };
        var containing = NewJob() with { Sources = [new JobSource { Alias = "Todo", Path = parent }] };

        var validation = await _ana.Proxy.ValidateJobAsync(inside, Ct);
        await RpcAssert.ValidationFailsAsync("Sources[0].Path", ValidationIssueCode.SourceNotAllowed, () => _ana.Proxy.SaveJobAsync(inside, Ct));
        var saved = await _ana.Proxy.SaveJobAsync(containing, Ct);
        var warning = Assert.Single((await _ana.Proxy.ValidateJobAsync(saved, Ct)).Warnings);

        Assert.Equal(("Sources[0].Path", ValidationIssueCode.SourceNotAllowed), (Assert.Single(validation.Errors).Field, validation.Errors[0].Code));
        Assert.Equal(("Sources[0].Path", ValidationIssueCode.SourceContainsDataFolder), (warning.Field, warning.Code));
    }

    [Fact]
    public async Task The_returned_job_does_not_carry_unknown_or_secret_fields()
    {
        var saved = await _ana.Proxy.SaveJobAsync(NewJob(), Ct);

        var raw = await _ana.Client.Rpc.InvokeWithCancellationAsync<JsonElement>(nameof(Contracts.IBackboneService.GetJobsAsync), [], Ct);

        Assert.Equal(saved.Id, raw[0].GetProperty("Id").GetGuid());
        Assert.DoesNotContain("Contraseña-HU07", raw.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Writes_are_audited_without_arguments()
    {
        await _ana.Proxy.SaveJobAsync(NewJob(), Ct);

        var all = _server.Logs.AllText();
        Assert.Contains(nameof(Contracts.IBackboneService.SaveJobAsync), all, StringComparison.Ordinal);
        Assert.DoesNotContain(Documents, all, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"\nas\", all, StringComparison.OrdinalIgnoreCase);
    }
}
