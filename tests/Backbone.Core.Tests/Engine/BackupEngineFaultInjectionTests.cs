using Backbone.Core.Repository;
using Backbone.Core.Repository.Locking;
using Backbone.Core.Storage;
using Backbone.Core.Tests.Support;
using static Backbone.Core.Tests.Support.EngineHarness;

namespace Backbone.Core.Tests.Engine;

/// <summary>
/// HU-13 y RNF-01 con el motor completo: un fallo, un corte o una cancelación en cualquier operación del almacenamiento del
/// repositorio (recorriendo todas las N) no deja un snapshot que referencie objetos inexistentes ni objetos cuyo contenido no
/// coincida con su nombre, no cambia lo ya publicado, y la ejecución siguiente termina bien y describe exactamente el
/// origen. Un error del repositorio nunca se confunde con un archivo de origen ilegible.
/// </summary>
[Trait("Story", "HU-13")]
[Trait("Requirement", "RNF-01")]
public sealed class BackupEngineFaultInjectionTests
{
    private static readonly LockOwner FirstProcess = new("PC-PRUEBA", 100);
    private static readonly LockOwner RestartedProcess = new("PC-PRUEBA", 200);

    public static TheoryData<FaultMode> Modes => [FaultMode.Crash, FaultMode.Transient, FaultMode.Cancel];

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task HU13_First_backup_fails_at_every_repository_operation_and_the_repository_stays_consistent(FaultMode mode)
    {
        var scenario = new Scenario(incremental: false);
        var total = await scenario.CountOperationsAsync();
        Assert.True(total > 40, $"El escenario debería tener bastantes operaciones ({total}).");

        for (var n = 1; n <= total; n++)
        {
            await scenario.RunWithFaultAsync(n, mode);
        }
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task HU13_Incremental_backup_fails_at_every_repository_operation_and_the_previous_snapshot_is_preserved(FaultMode mode)
    {
        var scenario = new Scenario(incremental: true);
        var total = await scenario.CountOperationsAsync();
        for (var n = 1; n <= total; n++)
        {
            await scenario.RunWithFaultAsync(n, mode);
        }
    }

    [Fact]
    public async Task HU13_The_sweep_covers_the_size_check_of_existing_objects_and_each_failure_of_it_is_a_repository_error()
    {
        var ct = TestContext.Current.CancellationToken;
        var storage = new InMemoryRepositoryStorage();
        var time = new ManualTimeProvider();
        var source = SampleSource();
        source.AddFile(DocumentsRoot + @"\copia de notas.txt", "hola, mundo"u8.ToArray()); // mismo contenido: objeto ya existente
        await OpenOrInitializeAsync(storage, time, ct);
        var probe = new FaultInjectingStorage(storage);
        await RunAsync(probe, time, source, owner: FirstProcess);
        var sizeChecks = probe.Log.Select((entry, index) => (entry, index)).Where(x => x.entry.Contains(": GetFileInfo objects/", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(sizeChecks); // el barrido de los otros tests incluye esta operacion

        foreach (var (entry, index) in sizeChecks)
        {
            foreach (var mode in new[] { FaultMode.Transient, FaultMode.Crash })
            {
                var fresh = new InMemoryRepositoryStorage();
                var freshTime = new ManualTimeProvider();
                await OpenOrInitializeAsync(fresh, freshTime, ct);
                var faults = new FaultInjectingStorage(fresh) { FailAt = index + 1, Mode = mode };

                var ex = await Record.ExceptionAsync(() => RunAsync(faults, freshTime, source, owner: FirstProcess));

                Assert.True(ex is IOException or RepositoryException, $"{entry}, {mode}: {ex?.GetType().Name ?? "sin excepcion"}");
                Assert.Empty(await ListSnapshotsAsync(fresh)); // nada publicado y no se convirtio en aviso de origen
                await RepositoryInvariants.AssertConsistentAsync(fresh);
                freshTime.Advance(TimeSpan.FromHours(1));
                var next = await RunAsync(fresh, freshTime, source, owner: RestartedProcess);
                Assert.Empty(next.Warnings);
                AssertMatchesSource(await ReadEntriesAsync(fresh, next.Snapshot.Id), source);
            }
        }
    }

    /// <summary>Repositorio y origen preparados igual en cada repetición.</summary>
    private sealed class Scenario(bool incremental)
    {
        public async Task<int> CountOperationsAsync()
        {
            var (storage, time, source) = await PrepareAsync();
            var faults = new FaultInjectingStorage(storage);
            await RunAsync(faults, time, source, owner: FirstProcess);
            return faults.OperationCount;
        }

        public async Task RunWithFaultAsync(int failAt, FaultMode mode)
        {
            var (storage, time, source) = await PrepareAsync();
            var published = RepositoryInvariants.CaptureImmutableFiles(storage);
            var snapshotsBefore = await ListSnapshotsAsync(storage);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            var faults = new FaultInjectingStorage(storage) { FailAt = failAt, Mode = mode, CancellationSource = cancellation };

            Backbone.Core.Engine.BackupResult? completed = null;
            try
            {
                completed = await RunAsync(faults, time, source, owner: FirstProcess, cancellationToken: cancellation.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or RepositoryException)
            {
                // Esperado: la ejecución falla. Lo que importa es el estado que deja.
            }

            Assert.True(faults.Tripped, $"La operación {failAt} no llegó a ejecutarse.");
            var context = $"modo {mode}, fallo en {failAt}: {faults.Log[failAt - 1]}";
            try
            {
                // Un fallo del repositorio nunca se registra como archivo de origen ilegible (que daría un snapshot con avisos).
                if (completed is not null)
                {
                    Assert.Empty(completed.Warnings);
                }

                await RepositoryInvariants.AssertConsistentAsync(storage);
                RepositoryInvariants.AssertPreserved(published, storage);

                // Si no terminó, no hay snapshot nuevo (salvo que el fallo fuera posterior a la publicación).
                var snapshotsAfterFault = await ListSnapshotsAsync(storage);
                Assert.InRange(snapshotsAfterFault.Count, snapshotsBefore.Count, snapshotsBefore.Count + 1);

                // La siguiente ejecución (otro proceso, más tarde) termina bien y describe exactamente el origen.
                time.Advance(TimeSpan.FromHours(1));
                var next = await RunAsync(storage, time, source, owner: RestartedProcess);
                Assert.Empty(next.Warnings);
                AssertMatchesSource(await ReadEntriesAsync(storage, next.Snapshot.Id), source);
                await RepositoryInvariants.AssertConsistentAsync(storage);
                RepositoryInvariants.AssertPreserved(published, storage);
                Assert.Equal(snapshotsAfterFault.Count + 1, (await ListSnapshotsAsync(storage)).Count);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new Xunit.Sdk.XunitException($"{context}\n{ex}");
            }
        }

        private async Task<(InMemoryRepositoryStorage Storage, ManualTimeProvider Time, InMemorySourceFileSystem Source)> PrepareAsync()
        {
            var ct = TestContext.Current.CancellationToken;
            var storage = new InMemoryRepositoryStorage();
            var time = new ManualTimeProvider();
            var source = SampleSource();
            source.AddFile(DocumentsRoot + @"\copia de notas.txt", "hola, mundo"u8.ToArray()); // deduplicado
            await OpenOrInitializeAsync(storage, time, ct);

            if (incremental)
            {
                await RunAsync(storage, time, source, owner: FirstProcess);
                source.Modify(DocumentsRoot + @"\notas.txt", "hola, mundo (cambiado)"u8.ToArray());
                source.Delete(DocumentsRoot + @"\vacío.txt");
                source.AddFile(PhotosRoot + @"\nueva.jpg", [1, 2, 3, 4, 5]);
                source.AddFile(PhotosRoot + @"\misma que notas.txt", "hola, mundo (cambiado)"u8.ToArray());
                time.Advance(TimeSpan.FromDays(1));
            }

            return (storage, time, source);
        }
    }
}
