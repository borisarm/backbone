using Backbone.Core.Repository;
using Backbone.Core.Repository.Locking;
using Backbone.Core.Repository.Snapshots;
using Backbone.Core.Storage;

namespace Backbone.Core.Tests.Support;

/// <summary>
/// Secuencia mínima de una copia sobre el formato (lo que hará HU-13): abrir o inicializar,
/// bloqueo compartido, objetos, manifiesto y liberación. Sirve para recorrer todos los puntos de fallo.
/// </summary>
public static class BackupScenario
{
    public static readonly SnapshotSource[] Sources = [new("Documentos", @"C:\Users\ana\Documents"), new("Fotos", @"D:\Fotos")];

    /// <summary>Contenido determinista de los archivos de la copia número <paramref name="run"/>.</summary>
    public static IReadOnlyList<(string Path, byte[] Content)> Files(int run)
    {
        var random = new Random(1234 + run);
        var big = new byte[(1024 * 1024) + 123]; // dos bloques de copia
        random.NextBytes(big);
        var small = new byte[100];
        random.NextBytes(small);
        return
        [
            ("Documentos/Facturas/2026/enero.pdf", big),
            ("Documentos/notas.txt", "hola, mundo"u8.ToArray()),
            ("Documentos/copia de notas.txt", "hola, mundo"u8.ToArray()), // deduplicado
            ("Documentos/vacío.txt", []),
            ("Fotos/año 2026/playa ñ.jpg", small),
            ("Fotos/común.txt", "igual en todas las copias"u8.ToArray()),
        ];
    }

    public static async Task<SnapshotHeader> RunAsync(
        IRepositoryStorage storage,
        TimeProvider timeProvider,
        LockOwner owner,
        int run,
        CancellationToken cancellationToken)
    {
        var probe = await BackupRepository.ProbeAsync(storage, cancellationToken);
        var repository = probe.State == RepositoryState.Compatible
            ? await BackupRepository.OpenAsync(storage, timeProvider, cancellationToken)
            : await BackupRepository.InitializeAsync(storage, new RepositoryInitializationOptions { CreatedBy = "Backbone pruebas en PC-PRUEBA" }, timeProvider, cancellationToken);

        var existing = await repository.ListSnapshotIdsAsync(cancellationToken);

        await using var repositoryLock = await repository.AcquireLockAsync(RepositoryLockType.Shared, owner, cancellationToken);
        var entries = new List<SnapshotEntry>();
        long newObjects = 0;
        foreach (var (path, content) in Files(run))
        {
            using var source = new MemoryStream(content);
            var result = await repository.WriteObjectAsync(source, expectedHash: null, repositoryLock, cancellationToken);
            newObjects += result.Stored ? 1 : 0;
            entries.Add(SnapshotEntry.File(path, result.Hash, result.Length, new DateTime(2026, 1, 31, 10, 22, 1, DateTimeKind.Utc).AddTicks(1234567), FileAttributes.Archive));
        }

        entries.Add(SnapshotEntry.Directory("Documentos/Vacía/"));

        await using var writer = await repository.CreateSnapshotAsync(
            new SnapshotStart
            {
                JobId = "1b2c3d4e-0000-0000-0000-000000000001",
                JobName = "Documentos",
                Host = owner.Host,
                StartedUtc = timeProvider.GetUtcNow().UtcDateTime,
                ParentId = existing.Count > 0 ? existing[^1] : null,
                UsedVss = true,
                Sources = Sources,
            },
            repositoryLock,
            cancellationToken);

        foreach (var entry in entries)
        {
            await writer.AddAsync(entry, cancellationToken);
        }

        var header = await writer.CompleteAsync(
            new SnapshotCompletion { Status = SnapshotStatus.Completed, Stats = new SnapshotStats { NewObjects = newObjects } },
            cancellationToken);
        await repositoryLock.ReleaseAsync(cancellationToken);
        return header;
    }
}
