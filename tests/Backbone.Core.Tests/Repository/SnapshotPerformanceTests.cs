using System.Diagnostics;
using Backbone.Core.Repository;
using Backbone.Core.Repository.Locking;
using Backbone.Core.Repository.Snapshots;
using Backbone.Core.Storage;
using Backbone.Core.Tests.Support;

namespace Backbone.Core.Tests.Repository;

/// <summary>
/// HT-04 criterio 3 (prueba de rendimiento opcional): un manifiesto de 500 000 entradas se escribe y se lee
/// con menos de 400 MB de memoria (RNF-05, R3). Sobre disco real, para no contar el manifiesto en memoria.
/// </summary>
[Trait("Story", "HT-04")]
[Trait("Category", "Perf")]
public sealed class SnapshotPerformanceTests(ITestOutputHelper output)
{
    private const int EntryCount = 500_000;
    private const long MemoryLimit = 400L * 1024 * 1024;

    [Fact]
    public async Task HT04_3_Manifest_with_500000_entries_is_written_and_read_under_400_MB()
    {
        var ct = TestContext.Current.CancellationToken;
        using var directory = new TempDirectory();
        var storage = FileSystemRepositoryStorage.ForLocalTesting(directory.Path);
        var repository = await BackupRepository.InitializeAsync(storage, new RepositoryInitializationOptions { CreatedBy = "perf" }, TimeProvider.System, ct);
        await using var repositoryLock = await repository.AcquireLockAsync(RepositoryLockType.Shared, new LockOwner("PERF", 1), ct);

        using var process = Process.GetCurrentProcess();
        GC.Collect();
        var baseline = process.PrivateMemorySize64;
        long peak = 0;
        void Sample()
        {
            process.Refresh();
            peak = Math.Max(peak, process.PrivateMemorySize64 - baseline);
        }

        var stopwatch = Stopwatch.StartNew();
        string id;
        await using (var writer = await repository.CreateSnapshotAsync(
            new SnapshotStart
            {
                JobId = "perf",
                JobName = "Rendimiento",
                Host = "PERF",
                StartedUtc = DateTime.UtcNow,
                UsedVss = false,
                Sources = [new("Documentos", @"C:\Documentos"), new("Fotos", @"D:\Fotos")],
            },
            repositoryLock,
            ct))
        {
            var i = 0;
            foreach (var entry in SnapshotTests.ManyEntries(EntryCount))
            {
                await writer.AddAsync(entry, ct);
                if (++i % 10_000 == 0)
                {
                    Sample();
                }
            }

            id = (await writer.CompleteAsync(new SnapshotCompletion { Status = SnapshotStatus.Completed }, ct)).Id;
        }

        var writeTime = stopwatch.Elapsed;
        Sample();
        var size = new FileInfo(Path.Join(directory.Path, "snapshots", id + ".json")).Length;

        stopwatch.Restart();
        long count = 0;
        await using (var reader = await repository.OpenSnapshotAsync(id, ct))
        {
            await foreach (var _ in reader.ReadEntriesAsync(ct))
            {
                if (++count % 10_000 == 0)
                {
                    Sample();
                }
            }
        }

        Sample();
        output.WriteLine($"Manifiesto: {size / (1024 * 1024)} MB; escritura {writeTime}; lectura {stopwatch.Elapsed}; pico de memoria adicional {peak / (1024 * 1024)} MB.");
        Assert.Equal(EntryCount, count);
        Assert.True(peak < MemoryLimit, $"Pico de memoria {peak / (1024 * 1024)} MB.");
    }
}
