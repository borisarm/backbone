using System.Security.Cryptography;
using Backbone.Core.Engine;
using Backbone.Core.Repository;
using Backbone.Core.Repository.Locking;
using Backbone.Core.Repository.Snapshots;
using Backbone.Core.Storage;

namespace Backbone.Core.Tests.Support;

/// <summary>
/// Una copia completa con <see cref="BackupEngine"/> como la orquestará el servicio: abrir (o inicializar) el repositorio,
/// bloqueo compartido, motor y liberación del bloqueo. Cada llamada usa una instancia nueva del repositorio, como un
/// proceso distinto.
/// </summary>
public static class EngineHarness
{
    public const string JobId = "1b2c3d4e-0000-0000-0000-0000000000aa";
    public const string DocumentsRoot = @"C:\Users\ana\Documents";
    public const string PhotosRoot = @"D:\Fotos";

    public static readonly LockOwner Owner = new("PC-PRUEBA", 4242);

    public static IReadOnlyList<BackupSource> DefaultSources { get; } =
    [
        new("Documentos", DocumentsRoot, DocumentsRoot),
        new("Fotos", PhotosRoot, PhotosRoot),
    ];

    public static BackupRequest Request(IReadOnlyList<BackupSource>? sources = null) => new()
    {
        JobId = JobId,
        JobName = "Documentos",
        Host = Owner.Host,
        Sources = sources ?? DefaultSources,
        UsedVss = true,
    };

    /// <summary>Origen de ejemplo con los dos orígenes por defecto.</summary>
    public static InMemorySourceFileSystem SampleSource()
    {
        var source = new InMemorySourceFileSystem();
        var random = new Random(1234);
        var big = new byte[(1024 * 1024) + 123]; // dos bloques de copia
        random.NextBytes(big);
        var small = new byte[100];
        random.NextBytes(small);
        source.AddFile(DocumentsRoot + @"\Facturas\2026\enero.pdf", big);
        source.AddFile(DocumentsRoot + @"\notas.txt", "hola, mundo"u8.ToArray());
        source.AddFile(DocumentsRoot + @"\vacío.txt", []);
        source.AddFile(DocumentsRoot + @"\oculto.ini", "x=1"u8.ToArray(), attributes: FileAttributes.Hidden | FileAttributes.ReadOnly | FileAttributes.Archive);
        source.AddDirectory(DocumentsRoot + @"\Vacía");
        source.AddFile(PhotosRoot + @"\año 2026\playa ñ.jpg", small);
        return source;
    }

    public static async Task<BackupRepository> OpenOrInitializeAsync(IRepositoryStorage storage, TimeProvider time, CancellationToken cancellationToken)
    {
        var probe = await BackupRepository.ProbeAsync(storage, cancellationToken);
        return probe.State == RepositoryState.Compatible
            ? await BackupRepository.OpenAsync(storage, time, cancellationToken)
            : await BackupRepository.InitializeAsync(storage, new RepositoryInitializationOptions { CreatedBy = "Backbone pruebas en PC-PRUEBA" }, time, cancellationToken);
    }

    public static async Task<BackupResult> RunAsync(
        IRepositoryStorage storage,
        TimeProvider time,
        ISourceFileSystem source,
        BackupRequest? request = null,
        LockOwner? owner = null,
        Action<BackupEngine>? configure = null,
        IProgress<BackupProgress>? progress = null,
        CancellationToken? cancellationToken = null)
    {
        var ct = cancellationToken ?? TestContext.Current.CancellationToken;
        var repository = await OpenOrInitializeAsync(storage, time, ct);
        await using var repositoryLock = await repository.AcquireLockAsync(RepositoryLockType.Shared, owner ?? Owner, ct);
        var engine = new BackupEngine(time);
        configure?.Invoke(engine);
        var result = await engine.RunAsync(repository, repositoryLock, source, request ?? Request(), progress, ct);
        await repositoryLock.ReleaseAsync(ct);
        return result;
    }

    public static async Task<List<SnapshotEntry>> ReadEntriesAsync(IRepositoryStorage storage, string snapshotId)
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = await BackupRepository.OpenAsync(storage, TimeProvider.System, ct);
        var entries = new List<SnapshotEntry>();
        await using var reader = await repository.OpenSnapshotAsync(snapshotId, ct);
        await foreach (var entry in reader.ReadEntriesAsync(ct))
        {
            entries.Add(entry);
        }

        return entries;
    }

    public static async Task<IReadOnlyList<string>> ListSnapshotsAsync(IRepositoryStorage storage)
    {
        var ct = TestContext.Current.CancellationToken;
        var probe = await BackupRepository.ProbeAsync(storage, ct);
        if (probe.State != RepositoryState.Compatible)
        {
            return [];
        }

        var repository = await BackupRepository.OpenAsync(storage, TimeProvider.System, ct);
        return await repository.ListSnapshotIdsAsync(ct);
    }

    public static string Sha256(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    public static IReadOnlyList<string> ObjectPaths(InMemoryRepositoryStorage storage) =>
        [.. storage.FilePaths.Where(p => p.StartsWith("objects/", StringComparison.Ordinal))];

    /// <summary>
    /// El snapshot describe exactamente los archivos del origen: misma lista de rutas y, para cada una, el hash y el tamaño
    /// de su contenido.
    /// </summary>
    public static void AssertMatchesSource(IReadOnlyList<SnapshotEntry> entries, InMemorySourceFileSystem source, IReadOnlyList<BackupSource>? sources = null)
    {
        var expected = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var backupSource in sources ?? DefaultSources)
        {
            foreach (var (relative, content) in source.FilesUnder(backupSource.ReadPath))
            {
                expected[backupSource.Alias + "/" + relative] = content;
            }
        }

        var files = entries.Where(e => !e.IsDirectory).ToDictionary(e => e.Path, StringComparer.Ordinal);
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), files.Keys.Order(StringComparer.Ordinal));
        foreach (var (path, content) in expected)
        {
            Assert.Equal(Sha256(content), files[path].Hash.ToString());
            Assert.Equal(content.Length, files[path].Size);
        }
    }
}
