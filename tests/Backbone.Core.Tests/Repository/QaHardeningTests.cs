using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Backbone.Core.Repository;
using Backbone.Core.Repository.Locking;
using Backbone.Core.Repository.Snapshots;
using Backbone.Core.Storage;
using Backbone.Core.Tests.Support;

namespace Backbone.Core.Tests.Repository;

/// <summary>
/// Pruebas de QA para HT-04 que cierran huecos detectados con mutaciones del código de producción:
/// orden vaciado-antes-de-renombrar (criterio 2, RNF-01), carreras de bloqueo entre procesos (criterio 4)
/// y el escenario completo sobre el sistema de archivos real, incluida una ruta de más de 260 caracteres.
/// </summary>
[Trait("Story", "HT-04")]
public sealed partial class QaHardeningTests
{
    private static readonly LockOwner Salon = new("PC-SALON", 1234);
    private static readonly LockOwner Estudio = new("PC-ESTUDIO", 42);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex(@"^\d+: (?<op>\w+) (?<path>\S*)(?: -> (?<dest>\S+))?$")]
    private static partial Regex LogLine();

    // ------------------------------------------------------------------ Criterio 2: durabilidad antes de publicar

    [Fact]
    public async Task HT04_2_Every_published_file_is_flushed_after_its_last_write_and_before_the_rename()
    {
        var storage = new InMemoryRepositoryStorage();
        var faults = new FaultInjectingStorage(storage);
        var time = new ManualTimeProvider();
        await BackupScenario.RunAsync(faults, time, Salon, run: 0, Ct);
        time.Advance(TimeSpan.FromHours(1));
        await BackupScenario.RunAsync(faults, time, Salon, run: 1, Ct);

        var lastOperation = new Dictionary<string, string>(StringComparer.Ordinal);
        var everFlushed = new HashSet<string>(StringComparer.Ordinal);
        var moves = 0;
        foreach (var line in faults.Log)
        {
            var match = LogLine().Match(line);
            Assert.True(match.Success, $"Línea de registro no reconocida: {line}");
            var op = match.Groups["op"].Value;
            var path = match.Groups["path"].Value;
            switch (op)
            {
                case "CreateNew":
                    lastOperation[path] = op;
                    break;
                case "Write" or "Flush":
                    lastOperation[path] = op;
                    if (op == "Flush")
                    {
                        everFlushed.Add(path);
                    }

                    break;
                case "Move":
                    moves++;
                    Assert.StartsWith("tmp/", path, StringComparison.Ordinal);
                    Assert.True(everFlushed.Contains(path), $"Se renombra sin vaciar nunca: {line}");
                    Assert.Equal("Flush", lastOperation[path]);
                    break;
            }
        }

        Assert.True(moves > 15, $"Debería haber renombrados de objetos, snapshots y bloqueos ({moves}).");
    }

    // ------------------------------------------------------------------ Criterio 4: carreras entre procesos

    public static TheoryData<RepositoryLockType, RepositoryLockType> IncompatiblePairs => new()
    {
        { RepositoryLockType.Exclusive, RepositoryLockType.Shared },
        { RepositoryLockType.Shared, RepositoryLockType.Exclusive },
        { RepositoryLockType.Exclusive, RepositoryLockType.Exclusive },
    };

    [Theory]
    [MemberData(nameof(IncompatiblePairs))]
    public async Task HT04_4_A_competitor_that_acquires_while_the_first_is_checking_never_gets_in_together(
        RepositoryLockType first,
        RepositoryLockType second)
    {
        // El segundo proceso hace toda su adquisición justo cuando el primero está listando locks/.
        var inner = new InMemoryRepositoryStorage();
        var time = new ManualTimeProvider();
        await BackupRepository.InitializeAsync(inner, new RepositoryInitializationOptions { CreatedBy = "x" }, time, Ct);
        var competitor = await BackupRepository.OpenAsync(inner, time, Ct);
        RepositoryLock? secondLock = null;
        RepositoryException? secondError = null;

        var hooked = new HookedStorage(inner)
        {
            BeforeFirstLockListing = async () =>
            {
                try
                {
                    secondLock = await competitor.AcquireLockAsync(second, Estudio, Ct);
                }
                catch (RepositoryException ex)
                {
                    secondError = ex;
                }
            },
        };
        var repository = await BackupRepository.OpenAsync(hooked, time, Ct);

        RepositoryLock? firstLock = null;
        RepositoryException? firstError = null;
        try
        {
            firstLock = await repository.AcquireLockAsync(first, Salon, Ct);
        }
        catch (RepositoryException ex)
        {
            firstError = ex;
        }

        try
        {
            Assert.True(firstLock is null || secondLock is null, "Los dos procesos obtuvieron un bloqueo incompatible.");
            Assert.All([firstError, secondError], e => Assert.True(e is null || e.Kind == RepositoryErrorKind.Locked));

            // Lo que no obtuvo bloqueo no deja archivo en locks/.
            var files = inner.FilePaths.Where(p => p.StartsWith("locks/", StringComparison.Ordinal)).Order().ToArray();
            var expected = new[] { firstLock is null ? null : "locks/PC-SALON-1234.lock", secondLock is null ? null : "locks/PC-ESTUDIO-42.lock" }
                .OfType<string>().Order().ToArray();
            Assert.Equal(expected, files);
        }
        finally
        {
            if (firstLock is not null)
            {
                await firstLock.DisposeAsync();
            }

            if (secondLock is not null)
            {
                await secondLock.DisposeAsync();
            }
        }
    }

    [Theory]
    [InlineData(RepositoryLockType.Shared, RepositoryLockType.Shared)]
    [InlineData(RepositoryLockType.Shared, RepositoryLockType.Exclusive)]
    [InlineData(RepositoryLockType.Exclusive, RepositoryLockType.Shared)]
    [InlineData(RepositoryLockType.Exclusive, RepositoryLockType.Exclusive)]
    public async Task HT04_4_Two_processes_racing_on_the_file_system_never_hold_incompatible_locks(
        RepositoryLockType first,
        RepositoryLockType second)
    {
        using var directory = new TempDirectory();
        var storage = FileSystemRepositoryStorage.ForLocalTesting(directory.Path);
        await BackupRepository.InitializeAsync(storage, new RepositoryInitializationOptions { CreatedBy = "x" }, TimeProvider.System, Ct);
        var compatible = first == RepositoryLockType.Shared && second == RepositoryLockType.Shared;

        for (var round = 0; round < 60; round++)
        {
            var repositoryA = await BackupRepository.OpenAsync(storage, TimeProvider.System, Ct);
            var repositoryB = await BackupRepository.OpenAsync(storage, TimeProvider.System, Ct);
            using var barrier = new Barrier(2);

            async Task<(RepositoryLock? Lock, RepositoryException? Error)> TryAcquireAsync(BackupRepository repository, RepositoryLockType type, LockOwner owner)
            {
                await Task.Yield();
                barrier.SignalAndWait(Ct);
                try
                {
                    return (await repository.AcquireLockAsync(type, owner, Ct), null);
                }
                catch (RepositoryException ex)
                {
                    return (null, ex);
                }
            }

            var taskA = Task.Run(() => TryAcquireAsync(repositoryA, first, Salon), Ct);
            var taskB = Task.Run(() => TryAcquireAsync(repositoryB, second, Estudio), Ct);
            var (lockA, errorA) = await taskA;
            var (lockB, errorB) = await taskB;
            try
            {
                if (compatible)
                {
                    Assert.NotNull(lockA);
                    Assert.NotNull(lockB);
                }
                else
                {
                    Assert.True(lockA is null || lockB is null, $"Ronda {round}: los dos obtuvieron un bloqueo incompatible.");
                }

                Assert.All([errorA, errorB], e => Assert.True(e is null || e.Kind == RepositoryErrorKind.Locked));
            }
            finally
            {
                if (lockA is not null)
                {
                    await lockA.DisposeAsync();
                }

                if (lockB is not null)
                {
                    await lockB.DisposeAsync();
                }
            }

            Assert.Empty(await RepositoryLock.ListAsync(storage, Ct));
        }
    }

    [Fact]
    public async Task HT04_4_A_holder_whose_stale_lock_was_taken_over_cannot_write_or_publish_anything()
    {
        // Titular A queda suspendido (sin latido > 15 min); la limpieza B toma el bloqueo exclusivo y borra el de A.
        var storage = new InMemoryRepositoryStorage();
        var time = new ManualTimeProvider();
        var repositoryA = await BackupRepository.InitializeAsync(storage, new RepositoryInitializationOptions { CreatedBy = "x" }, time, Ct);
        var repositoryB = await BackupRepository.OpenAsync(storage, time, Ct);
        var lockA = await repositoryA.AcquireLockAsync(RepositoryLockType.Shared, Salon, Ct);
        await using var writer = await repositoryA.CreateSnapshotAsync(
            new SnapshotStart { JobId = "1b2c3d4e-0000-0000-0000-000000000001", JobName = "x", Host = "PC-SALON", StartedUtc = time.UtcNow, UsedVss = false, Sources = BackupScenario.Sources },
            lockA,
            Ct);
        await WaitUntilAsync(() => time.ActiveTimerCount == 1);

        time.Jump(TimeSpan.FromMinutes(16));
        await using (var lockB = await repositoryB.AcquireLockAsync(RepositoryLockType.Exclusive, Estudio, Ct))
        {
            Assert.DoesNotContain("locks/PC-SALON-1234.lock", storage.FilePaths);

            // A despierta: lo detecta, no recrea su archivo y no publica nada.
            await WaitUntilAsync(() => lockA.IsLost);
            Assert.DoesNotContain("locks/PC-SALON-1234.lock", storage.FilePaths);
            var before = RepositoryInvariants.CaptureImmutableFiles(storage);

            var ex1 = await Assert.ThrowsAsync<RepositoryException>(() => lockA.EnsureHeldAsync(Ct));
            Assert.Equal(RepositoryErrorKind.LockLost, ex1.Kind);
            var ex2 = await Assert.ThrowsAsync<RepositoryException>(() =>
                repositoryA.WriteObjectAsync(new MemoryStream("x"u8.ToArray()), null, lockA, Ct));
            Assert.Equal(RepositoryErrorKind.LockLost, ex2.Kind);
            var ex3 = await Assert.ThrowsAsync<RepositoryException>(() =>
                writer.CompleteAsync(new SnapshotCompletion { Status = SnapshotStatus.Completed, Stats = new SnapshotStats() }, Ct));
            Assert.Equal(RepositoryErrorKind.LockLost, ex3.Kind);

            Assert.Equal(before.Keys.Order(), RepositoryInvariants.CaptureImmutableFiles(storage).Keys.Order());
            Assert.DoesNotContain(storage.FilePaths, p => p.StartsWith("snapshots/", StringComparison.Ordinal));
        }

        await lockA.DisposeAsync();
        Assert.DoesNotContain("locks/PC-SALON-1234.lock", storage.FilePaths);
    }

    [Theory]
    [InlineData(FaultMode.Cancel)]
    [InlineData(FaultMode.Transient)]
    public async Task HT04_4_Acquisition_interrupted_at_any_operation_leaves_no_lock_behind(FaultMode mode)
    {
        // Número de operaciones de una adquisición completa.
        int total;
        {
            var storage = new InMemoryRepositoryStorage();
            var time = new ManualTimeProvider();
            await BackupRepository.InitializeAsync(storage, new RepositoryInitializationOptions { CreatedBy = "x" }, time, Ct);
            var counter = new FaultInjectingStorage(storage);
            var repository = await BackupRepository.OpenAsync(counter, time, Ct);
            var baseline = counter.OperationCount;
            await (await repository.AcquireLockAsync(RepositoryLockType.Exclusive, Salon, Ct)).DisposeAsync();
            total = counter.OperationCount - baseline;
        }

        Assert.True(total >= 8, $"Una adquisición debería tener varias operaciones ({total}).");
        for (var n = 1; n <= total; n++)
        {
            var storage = new InMemoryRepositoryStorage();
            var time = new ManualTimeProvider();
            await BackupRepository.InitializeAsync(storage, new RepositoryInitializationOptions { CreatedBy = "x" }, time, Ct);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            var faults = new FaultInjectingStorage(storage) { Mode = mode, CancellationSource = cancellation };
            var repository = await BackupRepository.OpenAsync(faults, time, Ct);
            var failAt = faults.OperationCount + n;
            faults.FailAt = failAt;

            RepositoryLock? acquired = null;
            try
            {
                acquired = await repository.AcquireLockAsync(RepositoryLockType.Exclusive, Salon, cancellation.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            {
                // Esperado.
            }

            var leftovers = storage.FilePaths.Where(p => p.StartsWith("locks/", StringComparison.Ordinal)).ToArray();
            if (acquired is null)
            {
                if (leftovers.Length > 0)
                {
                    // Solo es admisible si el fallo fue el propio borrado del bloqueo (caduca a los 15 min).
                    Assert.Contains("Delete locks/", faults.Log[failAt - 1], StringComparison.Ordinal);
                }

                // Otro proceso puede adquirir el bloqueo pasados 15 minutos.
                time.Advance(TimeSpan.FromMinutes(16));
                var other = await BackupRepository.OpenAsync(storage, time, Ct);
                await using var recovered = await other.AcquireLockAsync(RepositoryLockType.Exclusive, Estudio, Ct);
            }
            else
            {
                await acquired.DisposeAsync();
                if (!faults.Log[failAt - 1].Contains("Delete locks/", StringComparison.Ordinal))
                {
                    Assert.DoesNotContain(storage.FilePaths, p => p.StartsWith("locks/", StringComparison.Ordinal));
                }
            }
        }
    }

    // ------------------------------------------------------------------ Sistema de archivos real

    [Fact]
    public async Task HT04_2_Full_backup_scenario_on_the_file_system_leaves_a_consistent_repository_and_no_temporaries()
    {
        using var directory = new TempDirectory();
        var storage = FileSystemRepositoryStorage.ForLocalTesting(directory.Path);
        var time = new ManualTimeProvider();

        await BackupScenario.RunAsync(storage, time, Salon, run: 0, Ct);
        time.Advance(TimeSpan.FromDays(1));
        var header = await BackupScenario.RunAsync(storage, time, Salon, run: 1, Ct);

        Assert.Empty(Directory.GetFiles(Path.Join(directory.Path, "tmp")));
        Assert.Empty(Directory.GetFiles(Path.Join(directory.Path, "locks")));
        Assert.Equal(2, Directory.GetFiles(Path.Join(directory.Path, "snapshots")).Length);
        Assert.Equal(BackupScenario.Files(1).Count, header.Stats.Files);

        // Cada objeto en disco coincide con su nombre y cada snapshot solo referencia objetos existentes.
        var repository = await BackupRepository.OpenAsync(storage, time, Ct);
        foreach (var id in await repository.ListSnapshotIdsAsync(Ct))
        {
            await using var reader = await repository.OpenSnapshotAsync(id, Ct);
            await foreach (var entry in reader.ReadEntriesAsync(Ct))
            {
                if (entry.IsDirectory)
                {
                    continue;
                }

                var hash = entry.Hash.GetValueOrDefault();
                Assert.True(await repository.ObjectExistsAsync(hash, Ct), $"Falta el objeto {hash} de {entry.Path}.");
                await using var stream = await repository.OpenObjectAsync(hash, Ct);
                var bytes = new MemoryStream();
                await stream.CopyToAsync(bytes, Ct);
                Assert.Equal(entry.Size, bytes.Length);
                Assert.Equal(hash.ToString(), Convert.ToHexStringLower(SHA256.HashData(bytes.ToArray())));
            }
        }
    }

    [Fact]
    public async Task HT04_1_Repository_under_a_path_longer_than_260_characters_initializes_and_stores_a_backup()
    {
        using var directory = new TempDirectory();
        var deep = directory.Path;
        while (deep.Length < 300)
        {
            deep = Path.Join(deep, "carpeta-muy-larga-para-superar-el-limite-de-ruta");
        }

        Directory.CreateDirectory(deep);
        var storage = FileSystemRepositoryStorage.ForLocalTesting(deep);

        var header = await BackupScenario.RunAsync(storage, new ManualTimeProvider(), Salon, run: 0, Ct);

        Assert.True(deep.Length > 260);
        Assert.Equal(BackupScenario.Files(0).Count, header.Stats.Files);
        var repository = await BackupRepository.OpenAsync(storage, TimeProvider.System, Ct);
        Assert.Single(await repository.ListSnapshotIdsAsync(Ct));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("La condición no se cumplió a tiempo.");
            }

            await Task.Delay(5, Ct);
        }
    }

    /// <summary>Ejecuta una acción justo antes de la primera enumeración de <c>locks/</c> (punto de intercalado de otro proceso).</summary>
    private sealed class HookedStorage(IRepositoryStorage inner) : IRepositoryStorage
    {
        private int fired;

        public Func<Task>? BeforeFirstLockListing { get; set; }

        public Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken) => inner.OpenReadAsync(path, cancellationToken);

        public Task<Stream> CreateNewAsync(string path, CancellationToken cancellationToken) => inner.CreateNewAsync(path, cancellationToken);

        public Task<bool> TryMoveAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken) =>
            inner.TryMoveAsync(sourcePath, destinationPath, overwrite, cancellationToken);

        public Task DeleteFileAsync(string path, CancellationToken cancellationToken) => inner.DeleteFileAsync(path, cancellationToken);

        public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken) => inner.CreateDirectoryAsync(path, cancellationToken);

        public Task<bool> DirectoryExistsAsync(string path, CancellationToken cancellationToken) => inner.DirectoryExistsAsync(path, cancellationToken);

        public Task<StorageEntry?> GetFileInfoAsync(string path, CancellationToken cancellationToken) => inner.GetFileInfoAsync(path, cancellationToken);

        public async IAsyncEnumerable<StorageEntry> EnumerateAsync(string directoryPath, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (directoryPath == RepositoryLayout.LocksDirectory && BeforeFirstLockListing is { } hook && Interlocked.Exchange(ref fired, 1) == 0)
            {
                await hook();
            }

            await foreach (var entry in inner.EnumerateAsync(directoryPath, cancellationToken))
            {
                yield return entry;
            }
        }
    }
}
