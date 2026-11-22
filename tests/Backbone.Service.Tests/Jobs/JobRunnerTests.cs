using System.Text;
using Backbone.Contracts;
using Backbone.Core.Repository;
using Backbone.Core.Repository.Locking;
using Backbone.Core.Storage;
using Backbone.Service.Jobs;
using Backbone.Service.Nas;
using Backbone.Service.Os;
using Backbone.Service.Sources;
using Backbone.Service.Tests.TestSupport;

namespace Backbone.Service.Tests.Jobs;

/// <summary>
/// HU-14, requisitos del <c>JobRunner</c> (§8.4): paso 2 (repetir la validación de orígenes y credencial antes de conectar ni
/// leer nada), <c>ReadPath</c> con la ruta final de la resolución, examen del repositorio en cada ejecución (RNF-14), hilos
/// abandonados (R12), liberación de todo en cada camino de error y traducción de fallos (tabla de §8.4).
/// </summary>
[Trait("Story", "HU-14")]
public sealed class JobRunnerTests : IAsyncLifetime
{
    private readonly RunRig _rig = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _rig.SaveCredentialAsync();
        _rig.Sources.Source.AddFile(@"C:\Datos\notas.txt", "hola, mundo");
    }

    public ValueTask DisposeAsync() => _rig.DisposeAsync();

    private async Task<RunRecord> RunAsync(JobDefinition job)
    {
        _rig.Runs.Enqueue(job.Id, RunTrigger.Manual);
        return await _rig.NextCompletedAsync();
    }

    private void AssertNothingConnectedOrRead()
    {
        Assert.Empty(_rig.Connector.Connections);
        Assert.Empty(_rig.Sources.Created);
        Assert.Empty(_rig.Memory().FilePaths);
        Assert.Equal(0, _rig.Connector.OpenConnections);
    }

    private async Task AssertReleasedAsync()
    {
        Assert.Equal(0, _rig.Connector.OpenConnections);
        Assert.Empty(_rig.Connections.ActiveShareRoots);
        Assert.Equal(_rig.Sources.Created.Count, _rig.Sources.Source.Disposals);
        if (_rig.Memory().FilePaths.Contains("repo.json"))
        {
            Assert.DoesNotContain(_rig.Memory().FilePaths, p => p.StartsWith("locks/", StringComparison.Ordinal));
        }

        await RunRig.WaitUntilAsync(() => _rig.Runs.GetActivity(_rig.Jobs.Jobs[0].Id) == JobActivity.Idle);
    }

    // ---- Paso 2: los orígenes se vuelven a resolver al empezar cada ejecución ----

    [Fact]
    public async Task A_live_copy_reads_from_the_final_path_of_the_resolution_never_from_the_configured_one()
    {
        // Un junction legítimo: C:\Datos\enlace lleva a D:\Real\Datos, un volumen local (ADR-20).
        _rig.Inspector.AddFolder(@"C:\Datos\enlace", finalPath: @"D:\Real\Datos");
        _rig.Sources.Source.AddFile(@"D:\Real\Datos\dentro.txt", "contenido");
        var job = await _rig.AddJobAsync(sources: [("Enlace", @"C:\Datos\enlace")]);

        var run = await RunAsync(job);

        Assert.Equal((RunState.Completed, 1L), (run.State, run.Stats.Files));
        var source = Assert.Single(Assert.Single(_rig.Sources.Created));
        Assert.Equal(("Enlace", @"C:\Datos\enlace", @"D:\Real\Datos"), (source.Alias, source.Path, source.ReadPath));
        Assert.DoesNotContain(@"C:\Datos\enlace", _rig.Sources.Source.Enumerated);
        Assert.Contains(@"D:\Real\Datos", _rig.Sources.Source.Enumerated);
    }

    [Fact]
    public async Task A_volume_without_a_drive_letter_is_read_through_its_volume_guid_path()
    {
        const string Guid = @"\\?\Volume{11111111-2222-3333-4444-555555555555}\Datos";
        _rig.Inspector.AddFolder(@"C:\Datos\montado", finalPath: @"Volume{11111111-2222-3333-4444-555555555555}\Datos", guidPath: Guid);
        _rig.Sources.Source.AddFile(Guid + @"\a.txt", "a");
        var job = await _rig.AddJobAsync(sources: [("Montado", @"C:\Datos\montado")]);

        var run = await RunAsync(job);

        Assert.Equal(RunState.Completed, run.State);
        Assert.Equal(Guid, Assert.Single(Assert.Single(_rig.Sources.Created)).ReadPath);
    }

    [Fact]
    public async Task The_sources_are_resolved_again_in_every_run_and_a_link_planted_after_saving_is_caught()
    {
        var job = await _rig.AddJobAsync();
        Assert.Equal(RunState.Completed, (await RunAsync(job)).State);
        Assert.Equal(1, _rig.Inspector.Resolved.Count(p => p == @"C:\Datos"));

        // Después de guardar el trabajo, la carpeta se sustituye por un enlace que lleva fuera del equipo (ADR-20).
        _rig.Inspector.AddStatus(@"C:\Datos", LocalFolderStatus.NotLocal);
        _rig.Time.Advance(TimeSpan.FromMinutes(1));
        var created = _rig.Sources.Created.Count;
        var second = await RunAsync(job);

        Assert.Equal(2, _rig.Inspector.Resolved.Count(p => p == @"C:\Datos"));
        Assert.Equal((RunState.Failed, BackboneErrorCode.ValidationFailed, null), (second.State, second.ErrorCode, second.SnapshotId));
        Assert.Equal(created, _rig.Sources.Created.Count); // no leyó nada
        Assert.Single(RepositoryCheck.Snapshots(_rig.Memory())); // y el único snapshot sigue siendo el primero
    }

    [Theory]
    [InlineData("NotLocal")]
    [InlineData("NotNtfs")]
    [InlineData("Remote")]
    [InlineData("InvalidPath")]
    [InlineData("NotNormalized")]
    [InlineData("Unc")]
    public async Task A_source_that_is_not_valid_fails_with_ValidationFailed_before_connecting_or_reading_anything(string problem)
    {
        var path = problem switch
        {
            "NotLocal" => @"C:\Red\enlace",
            "NotNtfs" => @"F:\Memoria",
            "Remote" => @"Z:\Unidad",
            "InvalidPath" => @"C:\Datos\falla",
            "NotNormalized" => @"C:\Datos\..\Windows",
            _ => @"\\otro\recurso",
        };
        switch (problem)
        {
            case "NotLocal":
                _rig.Inspector.AddStatus(path, LocalFolderStatus.NotLocal);
                break;
            case "NotNtfs":
                _rig.Inspector.AddFolder(path, fileSystem: "FAT32");
                break;
            case "Remote":
                _rig.Inspector.AddFolder(path, volume: FakeVolumeKind.Remote);
                break;
            case "InvalidPath":
                _rig.Inspector.AddStatus(path, LocalFolderStatus.InvalidPath);
                break;
        }

        var job = await _rig.AddJobAsync(sources: [("Datos", path)]);

        var run = await RunAsync(job);

        Assert.Equal((RunState.Failed, BackboneErrorCode.ValidationFailed, null), (run.State, run.ErrorCode, run.SnapshotId));
        Assert.Equal(1, run.ErrorCount);
        AssertNothingConnectedOrRead();
        await AssertReleasedAsync();
    }

    [Fact]
    public async Task A_source_inside_the_data_folder_fails_with_ValidationFailed_without_connecting()
    {
        var dataRoot = _rig.DataRoot;
        _rig.Inspector.AddFolder(dataRoot);
        var job = await _rig.AddJobAsync(sources: [("Datos", dataRoot)]);

        var run = await RunAsync(job);

        Assert.Equal((RunState.Failed, BackboneErrorCode.ValidationFailed), (run.State, run.ErrorCode));
        AssertNothingConnectedOrRead();
    }

    [Fact]
    public async Task A_source_that_contains_the_data_folder_passes_it_to_the_engine_as_a_mandatory_exclusion()
    {
        var programData = Path.GetDirectoryName(_rig.DataRoot)!;
        _rig.Inspector.AddFolder(programData);
        _rig.Sources.Source.AddFile(Path.Combine(programData, "otro", "x.txt"), "x");
        _rig.Sources.Source.AddFile(Path.Combine(_rig.DataRoot, "credentials", "secreto.bin"), "no se copia nunca");
        var job = await _rig.AddJobAsync(sources: [("PD", programData)]);

        var run = await RunAsync(job);

        Assert.Equal(RunState.Completed, run.State);
        var source = Assert.Single(Assert.Single(_rig.Sources.Created));
        Assert.Equal([Path.GetFileName(_rig.DataRoot)], source.MandatoryExclusions);
        Assert.DoesNotContain(_rig.Sources.Source.Opened, p => p.Contains("secreto", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("NotFound")]
    [InlineData("Unreadable")]
    public async Task A_source_that_is_not_found_or_cannot_be_checked_is_an_unreadable_root_and_the_others_are_copied(string status)
    {
        _rig.Inspector.AddStatus(@"C:\Falta", Enum.Parse<LocalFolderStatus>(status));
        var job = await _rig.AddJobAsync(sources: [("Datos", @"C:\Datos"), ("Falta", @"C:\Falta")]);

        var run = await RunAsync(job);

        Assert.Equal((RunState.CompletedWithWarnings, 1L), (run.State, run.Stats.Files));
        Assert.True(run.WarningCount >= 1);
        Assert.Equal(2, Assert.Single(_rig.Sources.Created).Count);
        await RepositoryCheck.AssertConsistentAsync(_rig.Memory());
    }

    [Fact]
    public async Task Without_a_saved_credential_the_run_fails_with_CredentialRequired_without_connecting()
    {
        await _rig.Credentials.DeleteAsync(RunRig.Share, Ct);
        var job = await _rig.AddJobAsync();

        var run = await RunAsync(job);

        Assert.Equal((RunState.Failed, BackboneErrorCode.CredentialRequired), (run.State, run.ErrorCode));
        Assert.Empty(_rig.Connector.Connections);
        Assert.Empty(_rig.Sources.Created);
        Assert.Empty(_rig.Memory().FilePaths);
        Assert.Contains(@"C:\Datos", _rig.Inspector.Resolved); // el orden de §8.4 paso 2: primero los orígenes, después la credencial
    }

    // ---- RepositoryStateTracker es orientativo (RNF-14) ----

    [Fact]
    public async Task A_repository_of_a_newer_format_fails_without_writing_even_if_the_tracker_says_read_write()
    {
        await _rig.Memory().CreateDirectoryAsync(string.Empty, Ct);
        _rig.Memory().SetFileContent("repo.json", Encoding.UTF8.GetBytes("""{ "formatVersion": 7, "algo": [1, 2, { "nuevo": true }], "repositoryId": 5 }"""));
        var job = await _rig.AddJobAsync();
        _rig.States.Set(job.Id, Contracts.RepositoryState.ReadWrite); // estado antiguo: no autoriza nada
        var before = Dump(_rig.Memory());

        var run = await RunAsync(job);

        Assert.Equal((RunState.Failed, BackboneErrorCode.RepositoryIncompatible, null), (run.State, run.ErrorCode, run.SnapshotId));
        Assert.Equal(before, Dump(_rig.Memory()));
        Assert.Equal(Contracts.RepositoryState.ReadOnlyNewerFormat, _rig.States.Get(job.Id));
        Assert.Empty(_rig.Sources.Created);
        await AssertReleasedAsync();
    }

    [Fact]
    public async Task Every_run_examines_the_repository_again_and_updates_the_tracker()
    {
        var job = await _rig.AddJobAsync();
        Assert.Equal(Contracts.RepositoryState.Unknown, _rig.States.Get(job.Id));
        Assert.Equal(RunState.Completed, (await RunAsync(job)).State);
        Assert.Equal(Contracts.RepositoryState.ReadWrite, _rig.States.Get(job.Id));

        // Otro equipo ha migrado el repositorio a un formato superior entre las dos ejecuciones.
        _rig.Memory().SetFileContent("repo.json", Encoding.UTF8.GetBytes("""{ "formatVersion": 2 }"""));
        _rig.Time.Advance(TimeSpan.FromMinutes(1));
        var before = Dump(_rig.Memory());
        var second = await RunAsync(job);

        Assert.Equal((RunState.Failed, BackboneErrorCode.RepositoryIncompatible), (second.State, second.ErrorCode));
        Assert.Equal(before, Dump(_rig.Memory()));
        Assert.Equal(Contracts.RepositoryState.ReadOnlyNewerFormat, _rig.States.Get(job.Id));
    }

    [Fact]
    public async Task A_destination_with_foreign_files_is_ValidationFailed_and_a_damaged_repository_is_RepositoryCorrupt_without_writing()
    {
        await _rig.Memory().CreateDirectoryAsync(string.Empty, Ct);
        _rig.Memory().SetFileContent("ajeno.txt", "no es un repositorio"u8);
        var job = await _rig.AddJobAsync();
        var before = Dump(_rig.Memory());

        var foreign = await RunAsync(job);

        Assert.Equal((RunState.Failed, BackboneErrorCode.ValidationFailed), (foreign.State, foreign.ErrorCode));
        Assert.Equal(before, Dump(_rig.Memory()));

        _rig.Memory().SetFileContent("repo.json", "{ esto no es json"u8);
        before = Dump(_rig.Memory());
        var damaged = await RunAsync(job);

        Assert.Equal((RunState.Failed, BackboneErrorCode.RepositoryCorrupt), (damaged.State, damaged.ErrorCode));
        Assert.Equal(before, Dump(_rig.Memory()));
        await AssertReleasedAsync();
    }

    [Fact]
    public async Task An_empty_destination_is_initialized_by_the_first_run()
    {
        var job = await _rig.AddJobAsync();
        Assert.Empty(_rig.Memory().FilePaths);

        var run = await RunAsync(job);

        Assert.Equal(RunState.Completed, run.State);
        Assert.Contains("repo.json", _rig.Memory().FilePaths);
        await RepositoryCheck.AssertConsistentAsync(_rig.Memory());
    }

    // ---- R12: hilos abandonados ----

    [Fact]
    public async Task With_four_abandoned_threads_the_run_fails_with_Internal_without_resolving_connecting_or_reading_and_event_1005_is_written_once()
    {
        for (var i = 0; i < AbandonedSourceThreads.Limit; i++)
        {
            _rig.Abandoned.OnAbandoned(@"C:\Colgada");
        }

        var job = await _rig.AddJobAsync();
        var run = await RunAsync(job);

        Assert.Equal((RunState.Failed, BackboneErrorCode.Internal), (run.State, run.ErrorCode));
        Assert.Empty(_rig.Inspector.Resolved); // sin resolver ni leer nada
        AssertNothingConnectedOrRead();
        Assert.Single(_rig.EventLog.Entries, e => e.EventId == BackboneEventIds.SourceReadingStopped);

        // Una segunda ejecución falla igual y el evento no se repite (solo al volver a llegar a 4).
        _rig.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(BackboneErrorCode.Internal, (await RunAsync(job)).ErrorCode);
        Assert.Single(_rig.EventLog.Entries, e => e.EventId == BackboneEventIds.SourceReadingStopped);
    }

    [Fact]
    public async Task A_resolution_of_the_JobRunner_that_does_not_return_counts_in_the_same_counter_as_the_source_reading()
    {
        // Tres hilos abandonados de lectura de orígenes; con el de la resolución del JobRunner, cuatro (mismo contador).
        for (var i = 0; i < AbandonedSourceThreads.Limit - 1; i++)
        {
            _rig.Abandoned.OnAbandoned(@"C:\Colgada");
        }

        _rig.Inspector.Hanging[@"C:\Datos"] = true;
        _rig.Inspector.IgnoreCancellation = true; // un controlador que no atiende la cancelación
        var job = await _rig.AddJobAsync();
        _rig.Runs.Enqueue(job.Id, RunTrigger.Manual);
        Assert.True(await _rig.Inspector.HangEntered.WaitAsync(TimeSpan.FromSeconds(10), Ct));

        // 60 s del plazo y 30 s más tras cancelarla: el hilo se abandona y la ejecución termina.
        var completion = _rig.NextCompletedAsync(30);
        for (var i = 0; i < 400 && !completion.IsCompleted; i++)
        {
            await Task.Delay(10, CancellationToken.None);
            _rig.Time.Advance(TimeSpan.FromSeconds(1));
        }

        var run = await completion;
        Assert.Equal((RunState.Failed, BackboneErrorCode.Internal), (run.State, run.ErrorCode));
        Assert.Equal(AbandonedSourceThreads.Limit, _rig.Abandoned.Count);
        Assert.True(_rig.Abandoned.IsExhausted);
        Assert.Single(_rig.EventLog.Entries, e => e.EventId == BackboneEventIds.SourceReadingStopped);
        AssertNothingConnectedOrRead();

        _rig.Inspector.ReleaseHung();
        await RunRig.WaitUntilAsync(() => _rig.Abandoned.Count == AbandonedSourceThreads.Limit - 1);
    }

    [Fact]
    public async Task The_resolution_of_the_JobRunner_is_not_limited_to_four_validations_at_once()
    {
        Assert.Equal(4, _rig.Validation.AvailableSlots);
        var job = await _rig.AddJobAsync();

        // Las 4 plazas de validación ocupadas no frenan a la ejecución (§8.3 "Sin bloquear el hilo RPC").
        var gates = Enumerable.Range(0, 4).Select(_ => new ManualResetEventSlim()).ToList();
        var tasks = gates.Select(g => _rig.Validation.RunAsync(_ => { g.Wait(); return 0; }, limited: true, Ct)).ToList();
        await RunRig.WaitUntilAsync(() => _rig.Validation.AvailableSlots == 0);

        var run = await RunAsync(job);

        Assert.Equal(RunState.Completed, run.State);
        foreach (var gate in gates)
        {
            gate.Set();
        }

        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task An_operation_abandoned_during_the_copy_fails_the_run_with_Internal_even_if_the_engine_did_not_see_it()
    {
        var job = await _rig.AddJobAsync();
        _rig.Sources.Source.OnOpenRead = (_, _) =>
        {
            _rig.Sources.Source.IsAbandoned = true;
            return Task.CompletedTask;
        };

        var run = await RunAsync(job);

        Assert.Equal((RunState.Failed, BackboneErrorCode.Internal), (run.State, run.ErrorCode));
        await AssertReleasedAsync();
    }

    [Theory]
    [InlineData("Stopped")]
    [InlineData("Unavailable")]
    public async Task When_the_source_reading_cannot_start_the_run_fails_with_Internal_and_releases_the_lock_and_the_connection(string reason)
    {
        _rig.Sources.CreateError = reason == "Stopped" ? new SourceReadingStoppedException() : new SourceReadingUnavailableException();
        var job = await _rig.AddJobAsync();

        var run = await RunAsync(job);

        Assert.Equal((RunState.Failed, BackboneErrorCode.Internal, null), (run.State, run.ErrorCode, run.SnapshotId));
        await RepositoryCheck.AssertConsistentAsync(_rig.Memory());
        await AssertReleasedAsync();
    }

    // ---- Liberación en cada camino de error y traducción de fallos ----

    [Fact]
    public async Task A_connection_error_fails_with_its_code_and_nothing_stays_connected()
    {
        _rig.Connector.ConnectError = new ShareException(BackboneErrorCode.CredentialsRejected, ShareErrors.ErrorLogonFailure);
        var job = await _rig.AddJobAsync();

        var run = await RunAsync(job);

        Assert.Equal((RunState.Failed, BackboneErrorCode.CredentialsRejected), (run.State, run.ErrorCode));
        Assert.True(FakeShareConnector.IsDisposed(_rig.Connector.LastCredential!));
        Assert.DoesNotContain(RunRig.Password, run.ErrorMessage ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(RunRig.Password, _rig.Logs.AllText(), StringComparison.Ordinal);
        Assert.Empty(_rig.Sources.Created);
        await AssertReleasedAsync();
    }

    [Fact]
    public async Task A_connection_that_lost_its_use_record_fails_with_NasUnreachable_without_writing()
    {
        var gate = _rig.Connector.Share.Gate("UseRecord");
        var job = await _rig.AddJobAsync();
        _rig.Runs.Enqueue(job.Id, RunTrigger.Manual);
        await RunRig.WaitUntilAsync(() => !_rig.Connector.Connections.IsEmpty);
        _rig.Connector.UseRecords.TryRemove(RunRig.Share, out _); // una desconexión pendiente se lo llevó (§8.2 B-1)
        gate.SetResult();

        var run = await _rig.NextCompletedAsync();

        Assert.Equal((RunState.Failed, BackboneErrorCode.NasUnreachable), (run.State, run.ErrorCode));
        Assert.Empty(_rig.Memory().FilePaths);
        await AssertReleasedAsync();
    }

    [Fact]
    public async Task A_repository_locked_by_another_computer_fails_with_RepositoryLocked_and_releases_everything()
    {
        await _rig.Memory().CreateDirectoryAsync(string.Empty, Ct);
        var other = await BackupRepository.InitializeAsync(_rig.Memory(), new RepositoryInitializationOptions { CreatedBy = "otro" }, _rig.Time, Ct);
        await using var exclusive = await other.AcquireLockAsync(RepositoryLockType.Exclusive, new LockOwner("OTRO-PC", 77), Ct);
        var job = await _rig.AddJobAsync();

        var run = await RunAsync(job);

        Assert.Equal((RunState.Failed, BackboneErrorCode.RepositoryLocked, null), (run.State, run.ErrorCode, run.SnapshotId));
        Assert.Equal(["locks/OTRO-PC-77.lock"], _rig.Memory().FilePaths.Where(p => p.StartsWith("locks/", StringComparison.Ordinal)));
        Assert.Empty(_rig.Sources.Created);
        Assert.Equal(0, _rig.Connector.OpenConnections);
    }

    [Theory]
    [InlineData("access", BackboneErrorCode.AccessDenied)]
    [InlineData("network", BackboneErrorCode.NasUnreachable)]
    [InlineData("other", BackboneErrorCode.Internal)]
    public async Task A_storage_failure_is_translated_and_the_lock_and_the_connection_are_released(string kind, BackboneErrorCode expected)
    {
        var storage = new HookRepositoryStorage(_rig.Memory());
        _rig.Storages.Set(RunRig.Destination, storage);
        var job = await _rig.AddJobAsync();
        storage.Before = (operation, path, _) =>
        {
            if (operation == "CreateNew" && path.StartsWith("tmp/", StringComparison.Ordinal) && !_rig.Sources.Created.IsEmpty)
            {
                throw kind switch
                {
                    "access" => new UnauthorizedAccessException("sin permiso"),
                    "network" => new IOException("red caída", unchecked((int)0x80070035)), // ERROR_BAD_NETPATH
                    _ => new InvalidOperationException("error de programación"),
                };
            }

            return Task.CompletedTask;
        };

        var run = await RunAsync(job);

        Assert.Equal((RunState.Failed, expected, null), (run.State, run.ErrorCode, run.SnapshotId));
        Assert.Empty(RepositoryCheck.Snapshots(_rig.Memory()));
        await RepositoryCheck.AssertConsistentAsync(_rig.Memory());
        await AssertReleasedAsync();
    }

    [Theory]
    [InlineData(RepositoryErrorKind.SnapshotTooLarge, BackboneErrorCode.SnapshotTooLarge)]
    [InlineData(RepositoryErrorKind.Corrupt, BackboneErrorCode.RepositoryCorrupt)]
    [InlineData(RepositoryErrorKind.SnapshotNotFound, BackboneErrorCode.RepositoryCorrupt)]
    [InlineData(RepositoryErrorKind.ObjectNotFound, BackboneErrorCode.RepositoryCorrupt)]
    [InlineData(RepositoryErrorKind.Incompatible, BackboneErrorCode.RepositoryIncompatible)]
    [InlineData(RepositoryErrorKind.Locked, BackboneErrorCode.RepositoryLocked)]
    [InlineData(RepositoryErrorKind.LockLost, BackboneErrorCode.NasUnreachable)]
    [InlineData(RepositoryErrorKind.NotARepository, BackboneErrorCode.ValidationFailed)]
    [InlineData(RepositoryErrorKind.SnapshotIdConflict, BackboneErrorCode.Internal)]
    public void Engine_failures_map_to_the_error_code_of_the_run(RepositoryErrorKind kind, BackboneErrorCode expected) =>
        Assert.Equal(expected, JobRunner.CodeOf(kind));

    [Fact]
    public async Task An_engine_failure_ends_the_run_Failed_with_its_code_and_without_a_snapshot()
    {
        var storage = new HookRepositoryStorage(_rig.Memory());
        _rig.Storages.Set(RunRig.Destination, storage);
        var job = await _rig.AddJobAsync();
        storage.Before = (operation, path, _) =>
            operation == "Move" && path.StartsWith("snapshots/", StringComparison.Ordinal)
                ? throw new RepositoryException(RepositoryErrorKind.SnapshotTooLarge, "demasiadas entradas")
                : Task.CompletedTask;

        var run = await RunAsync(job);

        Assert.Equal((RunState.Failed, BackboneErrorCode.SnapshotTooLarge, null), (run.State, run.ErrorCode, run.SnapshotId));
        Assert.Empty(RepositoryCheck.Snapshots(_rig.Memory()));
        await AssertReleasedAsync();
    }

    [Fact]
    public async Task A_failure_in_a_completion_handler_does_not_affect_the_run()
    {
        await using var rig = new RunRig(handlers: [new ThrowingHandler()]);
        await rig.SaveCredentialAsync();
        rig.Sources.Source.AddFile(@"C:\Datos\a.txt", "a");
        var job = await rig.AddJobAsync();

        rig.Runs.Enqueue(job.Id, RunTrigger.Manual);
        var run = await rig.NextCompletedAsync();

        Assert.Equal(RunState.Completed, run.State);
    }

    private sealed class ThrowingHandler : IRunCompletionHandler
    {
        public Task OnRunCompletedAsync(RunOutcome outcome, CancellationToken ct) => throw new InvalidOperationException("historial caído");
    }

    private static IReadOnlyList<string> Dump(InMemoryRepositoryStorage storage) =>
    [
        .. storage.DirectoryPaths.Select(d => "D " + d),
        .. storage.FilePaths.Select(f => "F " + f + " " + Convert.ToHexString(storage.GetFileContent(f))),
    ];
}
