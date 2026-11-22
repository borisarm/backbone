using System.Security.Cryptography;
using Backbone.Core.Repository;
using Backbone.Core.Repository.Snapshots;
using Backbone.Core.Storage;

namespace Backbone.Service.Tests.TestSupport;

/// <summary>
/// Equivale a un <c>check</c> completo del repositorio (HU-26 lo implementará de verdad) sobre un almacenamiento en memoria
/// (HU-14 criterio 3): en <c>objects/</c> solo hay objetos con nombre de hash y contenido que coincide; en <c>snapshots/</c>
/// solo hay manifiestos completos; todo objeto que referencia un snapshot existe con su tamaño; no queda ningún bloqueo.
/// </summary>
internal static class RepositoryCheck
{
    public static async Task AssertConsistentAsync(InMemoryRepositoryStorage storage, bool expectNoLocks = true)
    {
        var ct = TestContext.Current.CancellationToken;
        var objectSizes = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var path in storage.FilePaths)
        {
            var segments = path.Split('/');
            switch (segments[0])
            {
                case RepositoryLayout.ObjectsDirectory:
                    Assert.True(segments.Length == 3, $"Archivo fuera de lugar en objects/: {path}");
                    Assert.True(ContentHash.TryParse(segments[2], out var hash), $"Nombre de objeto no válido: {path}");
                    Assert.Equal(hash.Prefix, segments[1]);
                    var content = storage.GetFileContent(path);
                    Assert.True(Convert.ToHexStringLower(SHA256.HashData(content)) == segments[2], $"El contenido de {path} no coincide con su nombre.");
                    objectSizes[segments[2]] = content.Length;
                    break;

                case RepositoryLayout.SnapshotsDirectory:
                    Assert.True(segments.Length == 2, $"Archivo fuera de lugar en snapshots/: {path}");
                    Assert.EndsWith(RepositoryLayout.SnapshotExtension, segments[1], StringComparison.Ordinal);
                    Assert.True(SnapshotId.IsValid(segments[1][..^RepositoryLayout.SnapshotExtension.Length]), $"Nombre de snapshot no válido: {path}");
                    break;

                case RepositoryLayout.LocksDirectory:
                    if (expectNoLocks)
                    {
                        Assert.Fail($"Ha quedado un bloqueo: {path}");
                    }

                    break;

                case RepositoryLayout.ConfigFile:
                case RepositoryLayout.TempDirectory:
                    break;

                default:
                    Assert.Fail($"Archivo inesperado: {path}");
                    break;
            }
        }

        var repository = await BackupRepository.OpenAsync(storage, TimeProvider.System, ct);
        foreach (var id in await repository.ListSnapshotIdsAsync(ct))
        {
            var reader = await repository.OpenSnapshotAsync(id, ct);
            await using (reader)
            {
                await foreach (var entry in reader.ReadEntriesAsync(ct))
                {
                    if (entry.IsDirectory)
                    {
                        continue;
                    }

                    var hex = entry.Hash.GetValueOrDefault().ToString();
                    Assert.True(objectSizes.TryGetValue(hex, out var size), $"El snapshot {id} referencia un objeto inexistente: {hex} ({entry.Path}).");
                    Assert.Equal(entry.Size, size);
                }
            }
        }
    }

    /// <summary>Manifiestos publicados.</summary>
    public static IReadOnlyList<string> Snapshots(InMemoryRepositoryStorage storage) =>
        [.. storage.FilePaths.Where(p => p.StartsWith(RepositoryLayout.SnapshotsDirectory + "/", StringComparison.Ordinal))];
}
