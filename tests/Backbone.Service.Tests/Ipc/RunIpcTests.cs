using System.Collections.Concurrent;
using Backbone.Contracts;
using Backbone.Service.Persistence;
using Backbone.Service.Tests.TestSupport;

namespace Backbone.Service.Tests.Ipc;

/// <summary>
/// HU-14 por IPC (§7.2, §7.5): <c>RunNowAsync</c> y <c>CancelRunAsync</c> (criterio 1: lo que usan "Copiar ahora" de Inicio, de la
/// bandeja y de Trabajos), con <c>RunProgress</c>, <c>RunCompleted</c> y <c>StatusChanged</c> hacia el cliente.
/// </summary>
[Trait("Story", "HU-14")]
public sealed class RunIpcTests : IAsyncLifetime
{
    private const string Datos = @"C:\Datos";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly IpcTestHarness _server = new();
    private readonly ConcurrentQueue<RunCompletedEvent> _completed = new();
    private readonly ConcurrentQueue<RunProgressEvent> _progress = new();
    private readonly ConcurrentQueue<StatusChangedEvent> _statuses = new();
    private TestConnection _ana = null!; // Asignado en InitializeAsync.
    private JobDefinition _job = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _server.FileSystem.AddFolder(Datos);
        _server.SourceFactory.Source.AddFile(@"C:\Datos\a.txt", "hola").AddFile(@"C:\Datos\b.txt", "mundo");
        await _server.Jobs.LoadAsync(Ct);
        await _server.AuthorizeAsync(TestSids.Ana, TestSids.Luis);
        _ana = await _server.ConnectAuthorizedAsync(TestSids.Identity(TestSids.Ana));
        _ana.Proxy.RunCompleted += (_, e) => _completed.Enqueue(e);
        _ana.Proxy.RunProgress += (_, e) => _progress.Enqueue(e);
        _ana.Proxy.StatusChanged += (_, e) => _statuses.Enqueue(e);
        await _ana.Proxy.SaveCredentialAsync(@"\\nas\backups", new CredentialInput { UserName = "ana", Password = "Contraseña-HU14" }, Ct);
        _job = await _ana.Proxy.SaveJobAsync(
            JobDefaults.NewJob() with { Name = "Documentos", Sources = [new JobSource { Alias = "Datos", Path = Datos }], Destination = @"\\nas\backups\pc" },
            Ct);
    }

    public ValueTask DisposeAsync() => _server.DisposeAsync();

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 1000 && !condition(); i++)
        {
            await Task.Delay(10, CancellationToken.None);
        }

        Assert.True(condition(), "La condición no se cumplió a tiempo.");
    }

    [Fact]
    public async Task RunNowAsync_runs_the_job_and_the_client_receives_progress_the_end_and_the_status_changes()
    {
        _statuses.Clear();

        await _ana.Proxy.RunNowAsync(_job.Id, Ct);
        await WaitUntilAsync(() => !_completed.IsEmpty);

        var run = Assert.Single(_completed).Run;
        Assert.Equal((RunState.Completed, RunTrigger.Manual, _job.Id, 2L), (run.State, run.Trigger, run.JobId, run.Stats.Files));
        Assert.NotNull(run.SnapshotId);
        Assert.All(_progress, p => Assert.Equal((run.RunId, _job.Id), (p.RunId, p.JobId)));
        Assert.Contains(_progress, p => p.Phase == RunPhase.Starting);

        // StatusChanged al empezar (Running) y al terminar (Idle, con la última ejecución).
        await WaitUntilAsync(() => _statuses.Any(s => s.Status.Jobs.Any(j => j.Activity == JobActivity.Idle && j.LastRun?.State == RunState.Completed)));
        Assert.Contains(_statuses, s => s.Status.Jobs.Any(j => j.JobId == _job.Id && j.Activity == JobActivity.Running));
        var status = await _ana.Proxy.GetStatusAsync(Ct);
        Assert.Equal((RunState.Completed, JobActivity.Idle), (status.Jobs.Single().LastRun!.State, status.Jobs.Single().Activity));
        Assert.Null(status.CurrentRun);
    }

    [Fact]
    public async Task RunNowAsync_of_a_job_already_running_or_queued_is_JobAlreadyRunning_and_of_an_unknown_one_JobNotFound()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _server.SourceFactory.Source.OnOpenRead = async (_, ct) =>
        {
            entered.TrySetResult();
            await gate.Task.WaitAsync(ct);
        };

        await _ana.Proxy.RunNowAsync(_job.Id, Ct);
        await entered.Task.WaitAsync(Wait, Ct);

        await RpcAssert.FailsWithAsync(BackboneErrorCode.JobAlreadyRunning, () => _ana.Proxy.RunNowAsync(_job.Id, Ct));
        await RpcAssert.FailsWithAsync(BackboneErrorCode.JobNotFound, () => _ana.Proxy.RunNowAsync(Guid.NewGuid(), Ct));
        Assert.Equal(JobActivity.Running, (await _ana.Proxy.GetStatusAsync(Ct)).Jobs.Single().Activity);

        gate.SetResult();
        await WaitUntilAsync(() => !_completed.IsEmpty);
        Assert.Single(_completed);
    }

    [Fact]
    public async Task CancelRunAsync_cancels_the_run_and_is_idempotent_and_JobNotFound_for_an_unknown_job()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _server.SourceFactory.Source.OnOpenRead = async (_, ct) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        };

        await _ana.Proxy.CancelRunAsync(_job.Id, Ct); // sin ejecución: no hace nada
        Assert.Empty(_completed);
        await _ana.Proxy.RunNowAsync(_job.Id, Ct);
        await entered.Task.WaitAsync(Wait, Ct);

        await _ana.Proxy.CancelRunAsync(_job.Id, Ct);
        await WaitUntilAsync(() => !_completed.IsEmpty);
        await _ana.Proxy.CancelRunAsync(_job.Id, Ct); // otra vez, ya terminada

        var run = Assert.Single(_completed).Run;
        Assert.Equal((RunState.Cancelled, null), (run.State, run.SnapshotId));
        await RpcAssert.FailsWithAsync(BackboneErrorCode.JobNotFound, () => _ana.Proxy.CancelRunAsync(Guid.NewGuid(), Ct));
        Assert.Equal(0, _server.ShareConnector.OpenConnections);
    }

    [Fact]
    public async Task A_client_that_is_not_authorized_cannot_run_or_cancel()
    {
        var eva = _server.Connect(TestSids.Identity(TestSids.Eva));

        await RpcAssert.FailsWithAsync(BackboneErrorCode.NotAuthorized, () => eva.Proxy.RunNowAsync(_job.Id, Ct));
        await RpcAssert.FailsWithAsync(BackboneErrorCode.NotAuthorized, () => eva.Proxy.CancelRunAsync(_job.Id, Ct));
        Assert.Empty(_completed);
    }
}
