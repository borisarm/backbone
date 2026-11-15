using Backbone.Core.Repository;
using Backbone.Core.Repository.Locking;
using Backbone.Core.Storage;
using Backbone.Core.Tests.Support;

namespace Backbone.Core.Tests.Repository;

/// <summary>
/// HT-04 criterio 2 y RNF-01: un fallo en cualquier operación de almacenamiento (recorriendo todas las N)
/// no deja archivos incompletos en objects/ ni en snapshots/, ningún snapshot referencia objetos
/// inexistentes o corruptos, lo ya publicado no cambia y la ejecución siguiente termina bien.
/// </summary>
[Trait("Story", "HT-04")]
[Trait("Requirement", "RNF-01")]
public sealed class FaultInjectionTests
{
    private static readonly LockOwner FirstProcess = new("PC-PRUEBA", 100);
    private static readonly LockOwner RestartedProcess = new("PC-PRUEBA", 200);

    public static TheoryData<FaultMode> Modes => [FaultMode.Crash, FaultMode.Transient, FaultMode.Cancel];

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task HT04_2_First_backup_fails_at_every_operation_and_the_repository_stays_consistent(FaultMode mode)
    {
        var total = await CountOperationsAsync(prepare: null);
        Assert.True(total > 50, $"El escenario debería tener bastantes operaciones ({total}).");

        for (var n = 1; n <= total; n++)
        {
            await RunWithFaultAsync(n, mode, prepare: null);
        }
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task HT04_2_Incremental_backup_fails_at_every_operation_and_previous_snapshot_is_preserved(FaultMode mode)
    {
        static async Task Prepare(InMemoryRepositoryStorage storage, ManualTimeProvider time)
        {
            await BackupScenario.RunAsync(storage, time, FirstProcess, run: 0, TestContext.Current.CancellationToken);
            time.Advance(TimeSpan.FromDays(1));
        }

        var total = await CountOperationsAsync(Prepare);
        for (var n = 1; n <= total; n++)
        {
            await RunWithFaultAsync(n, mode, Prepare);
        }
    }

    [Fact]
    public async Task HT04_2_Fault_free_run_counts_reads_writes_and_renames()
    {
        var storage = new InMemoryRepositoryStorage();
        var faults = new FaultInjectingStorage(storage);
        await BackupScenario.RunAsync(faults, new ManualTimeProvider(), FirstProcess, run: 0, TestContext.Current.CancellationToken);

        Assert.Contains(faults.Log, l => l.Contains("Move tmp/", StringComparison.Ordinal) && l.Contains("-> snapshots/", StringComparison.Ordinal));
        Assert.Contains(faults.Log, l => l.Contains("-> objects/", StringComparison.Ordinal));
        Assert.Contains(faults.Log, l => l.Contains("-> repo.json", StringComparison.Ordinal));

        // Ningún archivo de objects/, snapshots/ ni repo.json se crea directamente: solo por renombrado (§6.4.1).
        Assert.DoesNotContain(faults.Log, l => l.Contains("CreateNew ", StringComparison.Ordinal) && !l.Contains("CreateNew tmp/", StringComparison.Ordinal));
        Assert.DoesNotContain(storage.FilePaths, p => p.StartsWith("tmp/", StringComparison.Ordinal));
        Assert.DoesNotContain(storage.FilePaths, p => p.StartsWith("locks/", StringComparison.Ordinal));
        await RepositoryInvariants.AssertConsistentAsync(storage);
    }

    [Fact]
    public async Task Invariant_checker_detects_a_partial_object()
    {
        var storage = new InMemoryRepositoryStorage();
        await BackupScenario.RunAsync(storage, new ManualTimeProvider(), FirstProcess, run: 0, TestContext.Current.CancellationToken);
        var objectPath = storage.FilePaths.First(p => p.StartsWith("objects/", StringComparison.Ordinal) && storage.GetFileContent(p).Length > 1000);
        storage.SetFileContent(objectPath, storage.GetFileContent(objectPath).AsSpan(0, 1000));

        await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(() => RepositoryInvariants.AssertConsistentAsync(storage));
    }

    [Fact]
    public async Task Invariant_checker_detects_a_snapshot_referencing_a_missing_object()
    {
        var storage = new InMemoryRepositoryStorage();
        await BackupScenario.RunAsync(storage, new ManualTimeProvider(), FirstProcess, run: 0, TestContext.Current.CancellationToken);
        var objectPath = storage.FilePaths.First(p => p.StartsWith("objects/", StringComparison.Ordinal));
        await storage.DeleteFileAsync(objectPath, TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(() => RepositoryInvariants.AssertConsistentAsync(storage));
    }

    [Fact]
    public async Task Invariant_checker_detects_a_temporary_file_in_snapshots()
    {
        var storage = new InMemoryRepositoryStorage();
        await BackupScenario.RunAsync(storage, new ManualTimeProvider(), FirstProcess, run: 0, TestContext.Current.CancellationToken);
        storage.SetFileContent("snapshots/0123.tmp", "{"u8);

        await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(() => RepositoryInvariants.AssertConsistentAsync(storage));
    }

    private static async Task<int> CountOperationsAsync(Func<InMemoryRepositoryStorage, ManualTimeProvider, Task>? prepare)
    {
        var storage = new InMemoryRepositoryStorage();
        var time = new ManualTimeProvider();
        if (prepare is not null)
        {
            await prepare(storage, time);
        }

        var faults = new FaultInjectingStorage(storage);
        await BackupScenario.RunAsync(faults, time, FirstProcess, run: 1, TestContext.Current.CancellationToken);
        return faults.OperationCount;
    }

    private static async Task RunWithFaultAsync(int failAt, FaultMode mode, Func<InMemoryRepositoryStorage, ManualTimeProvider, Task>? prepare)
    {
        var storage = new InMemoryRepositoryStorage();
        var time = new ManualTimeProvider();
        if (prepare is not null)
        {
            await prepare(storage, time);
        }

        var published = RepositoryInvariants.CaptureImmutableFiles(storage);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var faults = new FaultInjectingStorage(storage) { FailAt = failAt, Mode = mode, CancellationSource = cancellation };

        try
        {
            await BackupScenario.RunAsync(faults, time, FirstProcess, run: 1, cancellation.Token);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or RepositoryException)
        {
            // Esperado: la ejecución falla. Lo que importa es el estado que deja.
        }

        Assert.True(faults.Tripped, $"La operación {failAt} no llegó a ejecutarse.");
        var context = $"modo {mode}, fallo en {failAt}: {faults.Log[failAt - 1]}";

        try
        {
            await RepositoryInvariants.AssertConsistentAsync(storage);
            RepositoryInvariants.AssertPreserved(published, storage);

            // La siguiente ejecución (otro proceso, más tarde) termina bien y deja todo consistente.
            time.Advance(TimeSpan.FromHours(1));
            var before = (await ListSnapshotsAsync(storage)).Count;
            var header = await BackupScenario.RunAsync(storage, time, RestartedProcess, run: 2, TestContext.Current.CancellationToken);
            await RepositoryInvariants.AssertConsistentAsync(storage);
            RepositoryInvariants.AssertPreserved(published, storage);
            Assert.Equal(before + 1, (await ListSnapshotsAsync(storage)).Count);
            Assert.Equal(BackupScenario.Files(2).Count, header.Stats.Files);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new Xunit.Sdk.XunitException($"{context}\n{ex}");
        }
    }

    private static async Task<IReadOnlyList<string>> ListSnapshotsAsync(InMemoryRepositoryStorage storage)
    {
        var probe = await BackupRepository.ProbeAsync(storage, TestContext.Current.CancellationToken);
        if (probe.State != RepositoryState.Compatible)
        {
            return [];
        }

        var repository = await BackupRepository.OpenAsync(storage, TimeProvider.System, TestContext.Current.CancellationToken);
        return await repository.ListSnapshotIdsAsync(TestContext.Current.CancellationToken);
    }
}
