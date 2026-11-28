using System.Diagnostics;
using Backbone.Contracts;
using Backbone.Core.Repository;
using Backbone.Core.Storage;
using Backbone.Service.Ipc;
using Backbone.Service.Tests.TestSupport;

namespace Backbone.Service.Tests.Jobs;

/// <summary>
/// HU-14, criterios 1 a 3 (parte del servicio): "Copiar ahora" lanza o encola la ejecución (una a la vez, sin duplicar);
/// "Cancelar" la detiene en menos de 5 s, queda <c>Cancelled</c> y sin snapshot nuevo; tras cancelar, el repositorio pasa
/// <c>check</c> y la siguiente ejecución termina bien. Con <see cref="RunQueue"/> y <see cref="JobRunner"/> reales y dobles del
/// sistema.
/// </summary>
[Trait("Story", "HU-14")]
public sealed class RunNowAndCancelTests : IAsyncLifetime
{
    private static readonly TimeSpan Fast = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(20);
    private readonly RunRig _rig = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _rig.SaveCredentialAsync();
        for (var i = 1; i <= 5; i++)
        {
            _rig.Sources.Source.AddFile($@"C:\Datos\carpeta\archivo{i}.bin", RandomBytes(i, 4000 + (i * 100)));
        }

        _rig.Sources.Source.AddFile(@"C:\Datos\notas.txt", "hola, mundo");
    }

    public ValueTask DisposeAsync() => _rig.DisposeAsync();

    private static byte[] RandomBytes(int seed, int length)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    // ---- Criterio 1: "Copiar ahora" lanza o encola ----

    [Fact]
    public async Task Run_now_runs_the_job_publishes_the_snapshot_and_releases_everything()
    {
        var job = await _rig.AddJobAsync();

        _rig.Runs.Enqueue(job.Id, RunTrigger.Manual);
        var run = await _rig.NextCompletedAsync();

        Assert.Equal((RunState.Completed, RunKind.Backup, RunTrigger.Manual, job.Id, "Documentos"), (run.State, run.Kind, run.Trigger, run.JobId, run.JobName));
        Assert.NotNull(run.SnapshotId);
        Assert.Equal((6L, 0L), (run.Stats.Files, run.Stats.Skipped));
        Assert.True(run.CompletedUtc >= run.StartedUtc);
        Assert.Null(run.ErrorCode);
        Assert.True(run.UsedVss); // HU-15: se lee de la instantánea (el doble de VSS crea una por defecto)

        Assert.Equal(run.SnapshotId + ".json", Assert.Single(RepositoryCheck.Snapshots(_rig.Memory())).Split('/')[1]);
        await RepositoryCheck.AssertConsistentAsync(_rig.Memory());
        Assert.Equal(Contracts.RepositoryState.ReadWrite, _rig.States.Get(job.Id));

        // Paso 9: nada queda tomado.
        Assert.Equal(0, _rig.Connector.OpenConnections);
        Assert.Equal(1, _rig.Sources.Source.Disposals);
        Assert.True(FakeShareConnector.IsDisposed(_rig.Connector.LastCredential!));
        var connection = Assert.Single(_rig.Connector.Connections);
        Assert.Equal((RunRig.Share, RunRig.User), (connection.ShareRoot, connection.UserName));
        await RunRig.WaitUntilAsync(() => _rig.Runs.GetActivity(job.Id) == JobActivity.Idle);
        Assert.Equal(run, _rig.Runs.LastRun(job.Id));
        Assert.Equal(run.CompletedUtc, _rig.Runs.LastSuccessUtc(job.Id));
    }

    [Fact]
    public async Task Run_now_works_with_the_job_disabled()
    {
        var job = await _rig.AddJobAsync();
        await _rig.Jobs.UpdateAsync(list => (list.SetItem(0, job with { Enabled = false }), true), Ct);

        _rig.Runs.Enqueue(job.Id, RunTrigger.Manual);

        Assert.Equal(RunState.Completed, (await _rig.NextCompletedAsync()).State);
    }

    [Fact]
    public async Task Run_now_of_a_job_already_running_or_queued_does_not_duplicate_it()
    {
        var job = await _rig.AddJobAsync();
        var other = await _rig.AddJobAsync("Otro", RunRig.Share + @"\otro");
        var (entered, release) = _rig.HoldAtFirstFile();

        _rig.Runs.Enqueue(job.Id, RunTrigger.Manual);
        await entered.WaitAsync(Long, Ct);
        _rig.Runs.Enqueue(other.Id, RunTrigger.Manual);

        // En curso y en cola: los dos dan JobAlreadyRunning y no entra ninguna ejecución más.
        Assert.Equal(BackboneErrorCode.JobAlreadyRunning, Assert.Throws<BackboneException>(() => _rig.Runs.Enqueue(job.Id, RunTrigger.Manual)).Code);
        Assert.Equal(BackboneErrorCode.JobAlreadyRunning, Assert.Throws<BackboneException>(() => _rig.Runs.Enqueue(other.Id, RunTrigger.Manual)).Code);
        Assert.Equal(other.Id, Assert.Single(_rig.Runs.Queue).JobId);

        release.SetResult();
        Assert.Equal(job.Id, (await _rig.NextCompletedAsync()).JobId);
        Assert.Equal(other.Id, (await _rig.NextCompletedAsync()).JobId);
        await RunRig.WaitUntilAsync(() => _rig.Runs.GetActivity(other.Id) == JobActivity.Idle);
        Assert.Equal(2, _rig.CompletedEvents.Count);
    }

    [Fact]
    public async Task Only_one_run_at_a_time_and_the_rest_queue_in_order_of_arrival()
    {
        var first = await _rig.AddJobAsync("Primero");
        var second = await _rig.AddJobAsync("Segundo", RunRig.Share + @"\segundo");
        var third = await _rig.AddJobAsync("Tercero", RunRig.Share + @"\tercero");
        var (entered, release) = _rig.HoldAtFirstFile();

        _rig.Runs.Enqueue(first.Id, RunTrigger.Manual);
        await entered.WaitAsync(Long, Ct);
        _rig.Runs.Enqueue(second.Id, RunTrigger.Manual);
        _rig.Runs.Enqueue(third.Id, RunTrigger.Scheduled);

        Assert.Equal(
            [(first.Id, JobActivity.Running), (second.Id, JobActivity.Queued), (third.Id, JobActivity.Queued)],
            new[] { first, second, third }.Select(j => (j.Id, _rig.Runs.GetActivity(j.Id))));
        Assert.Equal(
            [(second.Id, RunTrigger.Manual), (third.Id, RunTrigger.Scheduled)],
            _rig.Runs.Queue.Select(q => (q.JobId, q.Trigger)));
        Assert.All(_rig.Runs.Queue, q => Assert.Equal(_rig.Time.GetUtcNow(), q.QueuedUtc));
        Assert.Equal(first.Id, _rig.Runs.CurrentRun?.JobId); // el progreso de la ejecución en curso (para el cliente que conecta a mitad)

        release.SetResult();
        var order = new[] { (await _rig.NextCompletedAsync()).JobId, (await _rig.NextCompletedAsync()).JobId, (await _rig.NextCompletedAsync()).JobId };

        Assert.Equal([first.Id, second.Id, third.Id], order);

        // Nunca dos conexiones a la vez: cada ejecución conecta y desconecta antes de que empiece la siguiente.
        Assert.Equal(
            ["connect", "disconnect", "connect", "disconnect", "connect", "disconnect"],
            _rig.Connector.Events.Select(e => e[..e.IndexOf(':')]).Where(e => !e.StartsWith("disconnect-", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Run_now_and_cancel_of_an_unknown_job_are_JobNotFound_and_cancel_of_an_idle_job_does_nothing()
    {
        var job = await _rig.AddJobAsync();

        Assert.Equal(BackboneErrorCode.JobNotFound, Assert.Throws<BackboneException>(() => _rig.Runs.Enqueue(Guid.NewGuid(), RunTrigger.Manual)).Code);
        Assert.Equal(BackboneErrorCode.JobNotFound, Assert.Throws<BackboneException>(() => _rig.Runs.Cancel(Guid.NewGuid())).Code);

        _rig.Runs.Cancel(job.Id);
        _rig.Runs.Cancel(job.Id); // idempotente

        Assert.Empty(_rig.CompletedEvents);
        Assert.Empty(_rig.Runs.Queue);
        Assert.Equal(JobActivity.Idle, _rig.Runs.GetActivity(job.Id));
    }

    [Fact]
    public async Task Progress_goes_through_the_phases_and_counts_files_and_bytes()
    {
        var job = await _rig.AddJobAsync();

        var runId = _rig.Runs.Enqueue(job.Id, RunTrigger.Manual);
        await _rig.NextCompletedAsync();

        var phases = _rig.ProgressEvents.Select(p => p.Phase).Distinct().ToList();
        Assert.Equal([RunPhase.Starting, RunPhase.Connecting, RunPhase.Copying, RunPhase.Finishing], phases);
        Assert.All(_rig.ProgressEvents, p => Assert.Equal((runId, job.Id, RunKind.Backup, RunTrigger.Manual), (p.RunId, p.JobId, p.Kind, p.Trigger)));
        var last = _rig.ProgressEvents.Last(p => p.Phase == RunPhase.Copying);
        Assert.Equal(6, last.FilesProcessed);
        Assert.Equal(4100 + 4200 + 4300 + 4400 + 4500 + "hola, mundo".Length, last.BytesProcessed);
        Assert.NotNull(last.CurrentPath);
    }

    [Fact]
    public async Task Status_reports_the_current_run_the_queue_and_the_last_run()
    {
        var job = await _rig.AddJobAsync();
        var other = await _rig.AddJobAsync("Otro", RunRig.Share + @"\otro");
        var status = new Backbone.Service.Jobs.ServiceStatusProvider(_rig.Jobs, _rig.Runs, _rig.States, _rig.Time, _rig.Runs);
        var (entered, release) = _rig.HoldAtFirstFile();

        _rig.Runs.Enqueue(job.Id, RunTrigger.Manual);
        await entered.WaitAsync(Long, Ct);
        _rig.Runs.Enqueue(other.Id, RunTrigger.Manual);
        var during = status.GetStatus();

        Assert.Equal(job.Id, during.CurrentRun?.JobId);
        Assert.Equal(other.Id, Assert.Single(during.Queue).JobId);
        Assert.Equal(
            [JobActivity.Running, JobActivity.Queued],
            during.Jobs.Select(j => j.Activity));

        release.SetResult();
        await _rig.NextCompletedAsync();
        await _rig.NextCompletedAsync();
        await RunRig.WaitUntilAsync(() => _rig.Runs.GetActivity(other.Id) == JobActivity.Idle);
        var after = status.GetStatus();

        Assert.Null(after.CurrentRun);
        Assert.Empty(after.Queue);
        Assert.All(after.Jobs, j => Assert.Equal((JobActivity.Idle, RunState.Completed, HealthState.Ok), (j.Activity, j.LastRun!.State, j.Health)));
        Assert.All(after.Jobs, j => Assert.NotNull(j.LastSuccessUtc));
        Assert.True(_rig.Changes >= 6, "StatusChanged al entrar, empezar y terminar cada ejecución.");
    }

    // ---- Criterio 2: Cancelar detiene la ejecución en menos de 5 s, sin snapshot ----

    public enum CancelPoint
    {
        /// <summary>Al abrir el tercer archivo del origen.</summary>
        ReadingSource,

        /// <summary>Durante la subida de objetos: al crear el segundo temporal en el repositorio.</summary>
        UploadingObjects,

        /// <summary>Justo antes de publicar el snapshot.</summary>
        BeforeWritingSnapshot,
    }

    [Theory]
    [InlineData(CancelPoint.ReadingSource)]
    [InlineData(CancelPoint.UploadingObjects)]
    [InlineData(CancelPoint.BeforeWritingSnapshot)]
    public async Task Cancel_stops_in_under_5_seconds_leaves_no_snapshot_and_the_repository_and_the_next_run_are_fine(CancelPoint point)
    {
        var job = await _rig.AddJobAsync();
        var inner = _rig.Memory();
        var storage = new HookRepositoryStorage(inner);
        _rig.Storages.Set(RunRig.Destination, storage);
        var stopwatch = new Stopwatch();
        var uploads = 0;
        void CancelNow()
        {
            stopwatch.Start();
            _rig.Runs.Cancel(job.Id);
        }

        switch (point)
        {
            case CancelPoint.ReadingSource:
                _rig.Sources.Source.OnOpenRead = (path, _) =>
                {
                    if (path.EndsWith("archivo3.bin", StringComparison.Ordinal))
                    {
                        CancelNow();
                    }

                    return Task.CompletedTask;
                };
                break;
            case CancelPoint.UploadingObjects:
                storage.Before = (operation, path, _) =>
                {
                    // Solo cuenta lo que se sube después de empezar a leer los orígenes (antes se inicializa el repositorio).
                    if (operation == "CreateNew" && path.StartsWith("tmp/", StringComparison.Ordinal) && !_rig.Sources.Created.IsEmpty && ++uploads == 2)
                    {
                        CancelNow();
                    }

                    return Task.CompletedTask;
                };
                break;
            default:
                storage.Before = (operation, path, _) =>
                {
                    if (operation == "Move" && path.StartsWith("snapshots/", StringComparison.Ordinal))
                    {
                        CancelNow();
                    }

                    return Task.CompletedTask;
                };
                break;
        }

        _rig.Runs.Enqueue(job.Id, RunTrigger.Manual);
        var run = await _rig.NextCompletedAsync();
        stopwatch.Stop();

        Assert.True(stopwatch.ElapsedTicks > 0, "La prueba no llegó a pedir la cancelación.");
        Assert.True(stopwatch.Elapsed < Fast, $"Cancelar tardó {stopwatch.Elapsed}.");
        Assert.Equal(RunState.Cancelled, run.State);
        Assert.Null(run.SnapshotId);
        Assert.Null(run.ErrorCode);
        Assert.Equal(0, run.Stats.Files);
        Assert.Empty(RepositoryCheck.Snapshots(inner)); // sin snapshot nuevo
        await RepositoryCheck.AssertConsistentAsync(inner); // y sin bloqueo ni objetos a medias
        Assert.Equal(0, _rig.Connector.OpenConnections);
        Assert.Equal(1, _rig.Sources.Source.Disposals);
        await RunRig.WaitUntilAsync(() => _rig.Runs.GetActivity(job.Id) == JobActivity.Idle);
        Assert.Equal(RunState.Cancelled, _rig.Runs.LastRun(job.Id)!.State);
        Assert.Null(_rig.Runs.LastSuccessUtc(job.Id));

        // Criterio 3: la siguiente ejecución termina bien y el repositorio sigue pasando el check.
        _rig.Sources.Source.OnOpenRead = null;
        storage.Before = null;
        _rig.Time.Advance(TimeSpan.FromMinutes(1));
        _rig.Runs.Enqueue(job.Id, RunTrigger.Manual);
        var next = await _rig.NextCompletedAsync();

        Assert.Equal((RunState.Completed, 6L), (next.State, next.Stats.Files));
        Assert.Equal(next.SnapshotId + ".json", Assert.Single(RepositoryCheck.Snapshots(inner)).Split('/')[1]);
        await RepositoryCheck.AssertConsistentAsync(inner);
        Assert.Equal(0, _rig.Connector.OpenConnections);
    }

    [Fact]
    public async Task Cancel_after_a_published_snapshot_keeps_it_and_the_next_cancelled_run_adds_none()
    {
        var job = await _rig.AddJobAsync();
        _rig.Runs.Enqueue(job.Id, RunTrigger.Manual);
        var first = await _rig.NextCompletedAsync();
        _rig.Time.Advance(TimeSpan.FromMinutes(1));
        _rig.Sources.Source.AddFile(@"C:\Datos\nuevo.txt", "contenido nuevo");
        _rig.Sources.Source.OnOpenRead = (path, _) =>
        {
            if (path.EndsWith("nuevo.txt", StringComparison.Ordinal))
            {
                _rig.Runs.Cancel(job.Id);
            }

            return Task.CompletedTask;
        };

        _rig.Runs.Enqueue(job.Id, RunTrigger.Manual);
        var second = await _rig.NextCompletedAsync();

        Assert.Equal(RunState.Cancelled, second.State);
        Assert.Equal(first.SnapshotId + ".json", Assert.Single(RepositoryCheck.Snapshots(_rig.Memory())).Split('/')[1]);
        await RepositoryCheck.AssertConsistentAsync(_rig.Memory());
    }

    [Fact]
    public async Task Cancelling_a_queued_run_removes_it_without_touching_the_nas()
    {
        var running = await _rig.AddJobAsync("En curso");
        var queued = await _rig.AddJobAsync("En cola", RunRig.Share + @"\cola");
        var (entered, release) = _rig.HoldAtFirstFile();
        _rig.Runs.Enqueue(running.Id, RunTrigger.Manual);
        await entered.WaitAsync(Long, Ct);
        _rig.Runs.Enqueue(queued.Id, RunTrigger.Manual);

        _rig.Runs.Cancel(queued.Id);
        var cancelled = await _rig.NextCompletedAsync();

        Assert.Equal((queued.Id, RunState.Cancelled, null), (cancelled.JobId, cancelled.State, cancelled.SnapshotId));
        Assert.Equal(JobActivity.Idle, _rig.Runs.GetActivity(queued.Id));
        Assert.Empty(_rig.Runs.Queue);
        Assert.Equal(JobActivity.Running, _rig.Runs.GetActivity(running.Id)); // la que estaba en curso sigue

        release.SetResult();
        var finished = await _rig.NextCompletedAsync();
        Assert.Equal((running.Id, RunState.Completed), (finished.JobId, finished.State));
        Assert.Single(_rig.Connector.Connections); // la cancelada nunca llegó a conectar
        Assert.Empty(RepositoryCheck.Snapshots(_rig.Memory(RunRig.Share + @"\cola")));
    }

    [Fact]
    public async Task Cancelling_while_connecting_returns_at_once_and_the_late_connection_is_released()
    {
        var job = await _rig.AddJobAsync();
        var gate = _rig.Connector.ConnectGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _rig.Runs.Enqueue(job.Id, RunTrigger.Manual);
        await RunRig.WaitUntilAsync(() => !_rig.Connector.Connections.IsEmpty);

        var stopwatch = Stopwatch.StartNew();
        _rig.Runs.Cancel(job.Id);
        var run = await _rig.NextCompletedAsync();

        Assert.True(stopwatch.Elapsed < Fast);
        Assert.Equal(RunState.Cancelled, run.State);
        Assert.Empty(_rig.Sources.Created); // sin leer nada
        Assert.Empty(_rig.Memory().FilePaths); // y sin escribir nada

        // WNetAddConnection2 no se interrumpe: cuando vuelve, la conexión se suelta y la contraseña se pone a cero.
        gate.SetResult();
        await RunRig.WaitUntilAsync(() => _rig.Connector.OpenConnections == 0 && !_rig.Connector.Created.IsEmpty && _rig.Connector.Created.All(c => c.IsDisposed));
        await RunRig.WaitUntilAsync(() => FakeShareConnector.IsDisposed(_rig.Connector.LastCredential!));
        Assert.Empty(_rig.Connections.ActiveShareRoots);
    }

    [Fact]
    public async Task Stopping_the_service_cancels_the_run_in_progress_and_releases_everything()
    {
        var job = await _rig.AddJobAsync();
        var queued = await _rig.AddJobAsync("En cola", RunRig.Share + @"\cola");
        _rig.Sources.Source.OnOpenRead = async (_, ct) => await Task.Delay(Timeout.Infinite, ct); // se queda leyendo hasta que se cancele
        _rig.Runs.Enqueue(job.Id, RunTrigger.Manual);
        _rig.Runs.Enqueue(queued.Id, RunTrigger.Manual);
        await RunRig.WaitUntilAsync(() => !_rig.Sources.Source.Opened.IsEmpty);

        await _rig.Runs.StopAsync(Ct).WaitAsync(Fast, Ct);

        var run = await _rig.NextCompletedAsync();
        Assert.Equal((job.Id, RunState.Cancelled), (run.JobId, run.State));
        Assert.Equal(0, _rig.Connector.OpenConnections);
        Assert.Equal(1, _rig.Sources.Source.Disposals);
        await RepositoryCheck.AssertConsistentAsync(_rig.Memory());
        Assert.Single(_rig.Connector.Connections); // la que estaba en cola no empezó
        Assert.Equal(BackboneErrorCode.Interrupted, Assert.Throws<BackboneException>(() => _rig.Runs.Enqueue(job.Id, RunTrigger.Manual)).Code);
    }
}
