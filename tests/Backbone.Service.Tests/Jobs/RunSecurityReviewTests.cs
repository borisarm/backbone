using System.Collections.Concurrent;
using Backbone.Contracts;
using Backbone.Core.Engine;
using Backbone.Core.Storage;
using Backbone.Service.Jobs;
using Backbone.Service.Nas;
using Backbone.Service.Os;
using Backbone.Service.Persistence;
using Backbone.Service.Sources;
using Backbone.Service.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backbone.Service.Tests.Jobs;

/// <summary>
/// Correcciones de la revisión de seguridad de la parte de servicio de HU-14: M-A (sin E/S en el NAS tras cerrar la conexión a
/// la fuerza ni tras soltarla) y B-5 (los plazos de la limpieza acotan de verdad), M-B (un origen sin resolver no se abre
/// nunca) y B-4 (cancelada, no se empieza a conectar).
/// </summary>
[Trait("Story", "HU-14")]
public sealed class RunSecurityReviewTests : IAsyncLifetime
{
    private static readonly TimeSpan Real = TimeSpan.FromSeconds(10);

    private RunRig _rig = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _rig.SaveCredentialAsync();
        _rig.Sources.Source.AddFile(@"C:\Datos\notas.txt", "hola, mundo");
    }

    public ValueTask DisposeAsync() => _rig.DisposeAsync();

    /// <summary>Avanza el reloj de la prueba a pasos hasta que la tarea termina; devuelve lo avanzado.</summary>
    private async Task<TimeSpan> AdvanceUntilAsync(Task task, TimeSpan step, TimeSpan limit)
    {
        var advanced = TimeSpan.Zero;
        while (!task.IsCompleted && advanced < limit)
        {
            await Task.Delay(30, CancellationToken.None);
            if (task.IsCompleted)
            {
                break;
            }

            _rig.Time.Advance(step);
            advanced += step;
        }

        await task.WaitAsync(Real, Ct);
        return advanced;
    }

    // ---- M-A: parada con la copia colgada en E/S del NAS ----

    [Fact]
    public async Task Stop_waits_at_most_10_seconds_and_after_the_forced_close_the_woken_up_run_does_no_more_io_on_the_share()
    {
        var job = await _rig.AddJobAsync();
        var inner = _rig.Memory();
        var storage = new HookRepositoryStorage(inner);
        _rig.Storages.Set(RunRig.Destination, storage);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hang = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reached = new ConcurrentQueue<string>();
        storage.Before = async (operation, path, _) =>
        {
            reached.Enqueue(operation + ":" + path);
            if (operation == "CreateNew" && path.StartsWith("tmp/", StringComparison.Ordinal) && !_rig.Sources.Created.IsEmpty)
            {
                entered.TrySetResult();
                await hang.Task; // E/S del NAS colgada que no atiende la cancelación
            }
        };

        try
        {
            _rig.Runs.Enqueue(job.Id, RunTrigger.Manual);
            await entered.Task.WaitAsync(Real, Ct);

            var stop = _rig.Runs.StopAsync(CancellationToken.None);
            await Task.Delay(300, CancellationToken.None); // deja que la parada registre su plazo antes de mover el reloj
            _rig.Time.Advance(TimeSpan.FromSeconds(9));
            await Task.Delay(100, CancellationToken.None);
            Assert.False(stop.IsCompleted, "La parada tiene que esperar hasta 10 s a la ejecución.");

            await AdvanceUntilAsync(stop, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)); // tope propio de 10 s: deja margen a ShareConnectionShutdown dentro de los 20 s del host

            // ShareConnectionShutdown: cierra a la fuerza lo que siga abierto.
            _rig.Connections.Close();
            await _rig.Connections.ForceDisconnectAllAsync(CancellationToken.None);
            Assert.Contains(RunRig.Share, _rig.Connector.ForcedDisconnects, StringComparer.OrdinalIgnoreCase);

            // La E/S colgada despierta: ninguna operación posterior (escritura del flujo, borrado del bloqueo) llega al almacenamiento.
            var before = reached.Count;
            hang.SetResult();
            await RunRig.WaitUntilAsync(() => _rig.Runs.GetActivity(job.Id) == JobActivity.Idle);
            await RunRig.WaitUntilAsync(() => _rig.Connector.OpenConnections == 0);

            Assert.Empty(reached.Skip(before));
            Assert.Contains(inner.FilePaths, p => p.StartsWith("locks/", StringComparison.Ordinal)); // el bloqueo caducará solo
            Assert.Empty(RepositoryCheck.Snapshots(inner));
            Assert.Equal(1, _rig.Sources.Source.Disposals);
        }
        finally
        {
            hang.TrySetResult();
        }
    }

    // ---- B-5: la limpieza no pasa de su plazo aunque el NAS no responda ----

    [Fact]
    public async Task A_hung_lock_release_does_not_hold_the_run_beyond_the_cleanup_step_timeout_and_the_connection_is_still_released()
    {
        var job = await _rig.AddJobAsync();
        var inner = _rig.Memory();
        var storage = new HookRepositoryStorage(inner);
        _rig.Storages.Set(RunRig.Destination, storage);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hang = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        storage.Before = async (operation, path, _) =>
        {
            if (operation == "Delete" && path.StartsWith("locks/", StringComparison.Ordinal))
            {
                entered.TrySetResult();
                await hang.Task; // el borrado del bloqueo no vuelve y no atiende el token
            }
        };

        try
        {
            _rig.Runs.Enqueue(job.Id, RunTrigger.Manual);
            await entered.Task.WaitAsync(Real, Ct);

            var completed = _rig.NextCompletedAsync();
            var advanced = await AdvanceUntilAsync(completed, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(10));
            var run = await completed;

            Assert.True(advanced <= TimeSpan.FromSeconds(5), $"La limpieza tardó {advanced} (plazo por paso: 2 s).");
            Assert.Equal(RunState.Completed, run.State);
            await RunRig.WaitUntilAsync(() => _rig.Connector.OpenConnections == 0);
            Assert.Empty(_rig.Connections.ActiveShareRoots);
            Assert.Equal(1, _rig.Sources.Source.Disposals);
        }
        finally
        {
            hang.TrySetResult();
        }
    }

    // ---- M-B: un origen que el paso 2 no pudo resolver no se abre nunca ----

    [Fact]
    public async Task A_source_unreadable_at_step_2_is_passed_marked_unreadable_and_never_opened_even_if_it_becomes_readable_before_opening()
    {
        using var api = new FakeSourceFileApi();
        api.AddFile("a.txt", [1, 2, 3]);
        _rig.Snapshotter.CreateError = new VolumeSnapshotException("VSS no disponible"); // la lectura de la prueba es en vivo
        _rig.Inspector.AddStatus(FakeSourceFileApi.ReadPath, LocalFolderStatus.NotFound);
        var job = await _rig.AddJobAsync(sources: [("Datos", RunRig.DataFolder), ("Src", FakeSourceFileApi.ReadPath)]);

        _rig.Runs.Enqueue(job.Id, RunTrigger.Manual);
        await _rig.NextCompletedAsync();

        // Solo el origen sin resolver va marcado, con la ruta configurada; el resuelto, con su ruta final y sin marcar.
        var sources = Assert.Single(_rig.Sources.Created);
        Assert.Equal([1], Assert.Single(_rig.Sources.UnreadableSources));
        Assert.Equal(FakeSourceFileApi.ReadPath, sources[1].ReadPath);

        // Entre el paso 2 y la apertura la carpeta pasa a ser legible (aquí lo es para la API): la lectura no abre nada.
        using var fs = new SourceFileSystem(
            sources,
            api,
            TimeProvider.System,
            SourceReadOptions.Default,
            NullLogger<SourceFileSystem>.Instance,
            unreadableSources: [1]);
        var ex = await Assert.ThrowsAsync<SourceAccessException>(async () =>
        {
            await foreach (var unused in fs.EnumerateAsync(FakeSourceFileApi.ReadPath, Ct))
            {
            }
        });
        Assert.Equal(RunIssueCode.ReadError, ex.Code);
        Assert.Empty(api.VolumeOpens);
        Assert.Empty(api.Opens);
        Assert.Equal(0, api.OpenHandles);

        // Control: sin la marca, la misma raíz sí se habría abierto y recorrido.
        using var unmarked = new SourceFileSystem(sources, api, TimeProvider.System, SourceReadOptions.Default, NullLogger<SourceFileSystem>.Instance);
        var names = new List<string>();
        await foreach (var entry in unmarked.EnumerateAsync(FakeSourceFileApi.ReadPath, Ct))
        {
            names.Add(entry.Name);
        }

        Assert.Equal(["a.txt"], names);
        Assert.NotEmpty(api.VolumeOpens);
    }

    [Fact]
    public async Task A_resolved_source_is_not_marked_unreadable()
    {
        var job = await _rig.AddJobAsync();

        _rig.Runs.Enqueue(job.Id, RunTrigger.Manual);
        await _rig.NextCompletedAsync();

        Assert.Empty(Assert.Single(_rig.Sources.UnreadableSources));
    }

    // ---- B-4: cancelada, no se empieza a conectar ----

    [Fact]
    public async Task A_run_cancelled_after_reading_the_credential_never_starts_the_connection_and_disposes_the_credential()
    {
        await _rig.DisposeAsync();
        var jobId = Guid.Empty;
        CancelAfterReadStore? store = null;
        _rig = new RunRig(wrapCredentials: inner => store = new CancelAfterReadStore(inner, () => _rig!.Runs.Cancel(jobId)));
        await InitializeAsync();
        var job = await _rig.AddJobAsync();
        jobId = job.Id;

        _rig.Runs.Enqueue(job.Id, RunTrigger.Manual);
        var run = await _rig.NextCompletedAsync();

        Assert.Equal(RunState.Cancelled, run.State);
        Assert.NotNull(store?.Last);
        Assert.True(FakeShareConnector.IsDisposed(store.Last));
        Assert.Empty(_rig.Connector.Connections); // WNetAddConnection2 ni se empezó
        Assert.Empty(_rig.Sources.Created);
        Assert.Equal(0, _rig.Connector.OpenConnections);
    }

    private sealed class CancelAfterReadStore(ICredentialStore inner, Action afterRead) : ICredentialStore
    {
        public NasCredential? Last { get; private set; }

        public Task<CredentialInfo> SaveAsync(string shareRoot, CredentialInput credential, CancellationToken ct) => inner.SaveAsync(shareRoot, credential, ct);

        public Task<CredentialInfo?> GetInfoAsync(string shareRoot, CancellationToken ct) => inner.GetInfoAsync(shareRoot, ct);

        public async Task<NasCredential?> GetAsync(string shareRoot, CancellationToken ct)
        {
            Last = await inner.GetAsync(shareRoot, ct).ConfigureAwait(false);
            afterRead();
            return Last;
        }

        public Task<bool> DeleteAsync(string shareRoot, CancellationToken ct) => inner.DeleteAsync(shareRoot, ct);
    }
}

/// <summary>
/// M-A: la revocación de <see cref="ShareLease"/> y el almacenamiento que la respeta
/// (<see cref="ShareLease.Protect"/>).
/// </summary>
[Trait("Story", "HU-14")]
public sealed class ShareLeaseRevocationTests : IDisposable
{
    private const string Root = @"\\nas\backups";

    private readonly FakeShareConnector _connector = new();
    private readonly ShareConnectionManager _manager;
    private readonly HookRepositoryStorage _storage;
    private readonly ConcurrentQueue<string> _reached = new();

    public ShareLeaseRevocationTests()
    {
        _manager = new ShareConnectionManager(_connector, NullLogger<ShareConnectionManager>.Instance);
        _storage = new HookRepositoryStorage(new InMemoryRepositoryStorage())
        {
            Before = (operation, path, _) =>
            {
                _reached.Enqueue(operation + ":" + path);
                return Task.CompletedTask;
            },
        };
    }

    public void Dispose()
    {
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static NasCredential Credential()
    {
        var buffer = GC.AllocateArray<char>(6, pinned: true);
        "clave".CopyTo(buffer);
        return new NasCredential(Root, "ana", DateTimeOffset.UnixEpoch, buffer, 5, isExplicit: false);
    }

    private async Task<ShareLease> AcquireAsync()
    {
        using var credential = Credential();
        return await _manager.AcquireAsync(credential, Ct);
    }

    private async Task AssertAllOperationsInterruptedAsync(IRepositoryStorage storage)
    {
        var before = _reached.Count;
        await AssertInterruptedAsync(() => storage.OpenReadAsync("a", Ct));
        await AssertInterruptedAsync(() => storage.CreateNewAsync("tmp/x", Ct));
        await AssertInterruptedAsync(() => storage.TryMoveAsync("tmp/x", "y", false, Ct));
        await AssertInterruptedAsync(() => storage.DeleteFileAsync("locks/a.lock", Ct));
        await AssertInterruptedAsync(() => storage.CreateDirectoryAsync("d", Ct));
        await AssertInterruptedAsync(() => storage.DirectoryExistsAsync("d", Ct));
        await AssertInterruptedAsync(() => storage.GetFileInfoAsync("a", Ct));
        await AssertInterruptedAsync(async () =>
        {
            await foreach (var unused in storage.EnumerateAsync(string.Empty, Ct))
            {
            }
        });
        Assert.Equal(before, _reached.Count); // ninguna llegó al almacenamiento de la red
    }

    private static async Task AssertInterruptedAsync(Func<Task> operation)
    {
        var ex = await Assert.ThrowsAsync<ShareException>(operation);
        Assert.Equal(BackboneErrorCode.Interrupted, ex.Code);
    }

    [Fact]
    public async Task Disposing_the_lease_revokes_it_before_the_connection_is_cancelled_and_the_storage_does_no_more_io()
    {
        var lease = await AcquireAsync();
        var storage = lease.Protect(_storage);
        var revokedAtDisconnect = false;
        _connector.BeforeDisconnect = () => revokedAtDisconnect = lease.IsRevoked;
        await storage.CreateDirectoryAsync("d", Ct);
        Assert.Single(_reached);

        await lease.DisposeAsync();

        Assert.True(revokedAtDisconnect, "La concesión tiene que estar revocada antes de WNetCancelConnection2.");
        await AssertAllOperationsInterruptedAsync(storage);
    }

    [Fact]
    public async Task A_forced_close_revokes_every_live_lease_before_touching_the_network_and_open_streams_stop_too()
    {
        var first = await AcquireAsync();
        var second = await AcquireAsync(); // misma conexión (credencial guardada idéntica)
        var storage = first.Protect(_storage);
        var other = second.Protect(_storage);
        await storage.CreateDirectoryAsync("tmp", Ct);
        await using var stream = await storage.CreateNewAsync("tmp/x", Ct);
        await stream.WriteAsync(new byte[] { 1, 2, 3 }, Ct);
        var revokedAtForce = new List<bool>();
        _connector.BeforeDisconnect = () => revokedAtForce.Add(first.IsRevoked && second.IsRevoked);

        await _manager.ForceDisconnectAllAsync(CancellationToken.None);

        Assert.Equal([true], revokedAtForce);
        await AssertAllOperationsInterruptedAsync(storage);
        await AssertAllOperationsInterruptedAsync(other);
        var before = _reached.Count;
        await AssertInterruptedAsync(() => stream.WriteAsync(new byte[] { 4 }, Ct).AsTask());
        await AssertInterruptedAsync(() => stream.FlushAsync(Ct));
        Assert.Equal(before, _reached.Count);
        await first.DisposeAsync();
        await second.DisposeAsync();
    }

    [Fact]
    public async Task Releasing_one_lease_does_not_revoke_the_other_one_sharing_the_connection()
    {
        var first = await AcquireAsync();
        var second = await AcquireAsync();
        var other = second.Protect(_storage);

        await first.DisposeAsync();

        Assert.False(second.IsRevoked);
        await other.CreateDirectoryAsync("d", Ct);
        await second.DisposeAsync();
    }

    [Fact]
    public async Task A_lease_that_connects_after_the_forced_close_is_born_revoked()
    {
        var gate = _connector.ConnectGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var credential = Credential();
        var acquiring = _manager.AcquireAsync(credential, Ct);

        await _manager.ForceDisconnectAllAsync(CancellationToken.None);
        gate.SetResult();
        var lease = await acquiring.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.True(lease.IsRevoked);
        await AssertAllOperationsInterruptedAsync(lease.Protect(_storage));
        await lease.DisposeAsync();
    }
}
