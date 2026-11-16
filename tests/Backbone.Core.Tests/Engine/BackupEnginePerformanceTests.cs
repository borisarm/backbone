using System.Diagnostics;
using System.Runtime.CompilerServices;
using Backbone.Core.Engine;
using Backbone.Core.Repository;
using Backbone.Core.Repository.Locking;
using Backbone.Core.Repository.Snapshots;
using Backbone.Core.Storage;
using Backbone.Core.Tests.Support;

namespace Backbone.Core.Tests.Engine;

/// <summary>
/// HU-13 (RNF-03, RNF-05): copia incremental sin cambios de 500 000 archivos, sin abrir ninguno, con menos de 400 MB de
/// memoria adicional y a un ritmo de 100 000 archivos en menos de 60 s. El origen es sintético (no ocupa memoria) y el
/// repositorio está en el disco real, para no contar los manifiestos en memoria.
/// </summary>
[Trait("Story", "HU-13")]
[Trait("Category", "Perf")]
public sealed class BackupEnginePerformanceTests(ITestOutputHelper output)
{
    private const int Directories = 500;
    private const int FilesPerDirectory = 1000;
    private const int FileCount = Directories * FilesPerDirectory;
    private const long MemoryLimit = 400L * 1024 * 1024;
    private const string Root = @"R:\Datos";
    private static readonly byte[] Content = "contenido de prueba"u8.ToArray();
    private static readonly DateTime Modified = new(2026, 1, 31, 10, 22, 1, DateTimeKind.Utc);

    [Fact]
    public async Task HU13_Unchanged_incremental_backup_of_500000_files_reads_nothing_and_stays_under_400_MB()
    {
        var ct = TestContext.Current.CancellationToken;
        using var directory = new TempDirectory();
        var storage = FileSystemRepositoryStorage.ForLocalTesting(directory.Path);
        var time = TimeProvider.System;
        var request = EngineHarness.Request([new BackupSource("Datos", Root, Root)]);

        // Snapshot padre con las mismas entradas, escrito directamente (subir 500 000 objetos tardaría demasiado).
        var repository = await BackupRepository.InitializeAsync(storage, new RepositoryInitializationOptions { CreatedBy = "perf" }, time, ct);
        string parentId;
        await using (var parentLock = await repository.AcquireLockAsync(RepositoryLockType.Shared, new LockOwner("PERF", 1), ct))
        {
            using var content = new MemoryStream(Content);
            var stored = await repository.WriteObjectAsync(content, null, parentLock, ct);
            await using var writer = await repository.CreateSnapshotAsync(
                new SnapshotStart
                {
                    JobId = request.JobId,
                    JobName = request.JobName,
                    Host = request.Host,
                    StartedUtc = time.GetUtcNow().UtcDateTime.AddDays(-1),
                    UsedVss = false,
                    Sources = [new SnapshotSource("Datos", Root)],
                },
                parentLock,
                ct);
            for (var d = 0; d < Directories; d++)
            {
                for (var f = 0; f < FilesPerDirectory; f++)
                {
                    await writer.AddAsync(SnapshotEntry.File($"Datos/{DirectoryName(d)}/{FileName(f)}", stored.Hash, Content.Length, Modified, FileAttributes.Archive), ct);
                }
            }

            parentId = (await writer.CompleteAsync(new SnapshotCompletion { Status = SnapshotStatus.Completed }, ct)).Id;
            await parentLock.ReleaseAsync(ct);
        }

        var source = new SyntheticSource();
        using var process = Process.GetCurrentProcess();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        process.Refresh();
        var baseline = process.PrivateMemorySize64;
        long peak = 0;
        var progress = new SamplingProgress(() =>
        {
            process.Refresh();
            peak = Math.Max(peak, process.PrivateMemorySize64 - baseline);
        });

        var stopwatch = Stopwatch.StartNew();
        var result = await EngineHarness.RunAsync(storage, time, source, request, progress: progress, cancellationToken: ct);
        stopwatch.Stop();
        progress.Sample();

        output.WriteLine($"{FileCount} archivos sin cambios en {stopwatch.Elapsed}; pico de memoria adicional {peak / (1024 * 1024)} MB.");
        Assert.Equal(parentId, result.Snapshot.ParentId);
        Assert.Equal(FileCount, result.Snapshot.Stats.Files);
        Assert.Equal(0, result.Snapshot.Stats.NewObjects);
        Assert.Equal(0, source.OpenCount);
        Assert.True(peak < MemoryLimit, $"Pico de memoria {peak / (1024 * 1024)} MB.");
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(60.0 * FileCount / 100_000),
            $"{stopwatch.Elapsed} para {FileCount} archivos (RNF-03: 100 000 en menos de 60 s).");
    }

    private static string DirectoryName(int d) => $"carpeta {d:D3}";

    private static string FileName(int f) => $"archivo {f:D4}.txt";

    /// <summary>Muestrea cada 10 000 archivos.</summary>
    private sealed class SamplingProgress(Action sample) : IProgress<BackupProgress>
    {
        public void Report(BackupProgress value)
        {
            if (value.FilesProcessed % 10_000 == 0)
            {
                sample();
            }
        }

        public void Sample() => sample();
    }

    /// <summary>Origen generado al vuelo: <see cref="Directories"/> carpetas con <see cref="FilesPerDirectory"/> archivos.</summary>
    private sealed class SyntheticSource : ISourceFileSystem
    {
        private int opens;

        public int OpenCount => Volatile.Read(ref opens);

        public async IAsyncEnumerable<SourceEntry> EnumerateAsync(string directoryPath, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            if (directoryPath == Root)
            {
                for (var d = 0; d < Directories; d++)
                {
                    yield return new SourceEntry(DirectoryName(d), Root + @"\" + DirectoryName(d), true, 0, Modified, FileAttributes.Directory);
                }

                yield break;
            }

            for (var f = 0; f < FilesPerDirectory; f++)
            {
                yield return new SourceEntry(FileName(f), directoryPath + @"\" + FileName(f), false, Content.Length, Modified, FileAttributes.Archive);
            }
        }

        public Task<Stream> OpenReadAsync(string filePath, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref opens);
            return Task.FromResult<Stream>(new MemoryStream(Content));
        }

        public Task<SourceEntry?> GetFileInfoAsync(string filePath, CancellationToken cancellationToken) =>
            Task.FromResult<SourceEntry?>(new SourceEntry(Path.GetFileName(filePath), filePath, false, Content.Length, Modified, FileAttributes.Archive));
    }
}
