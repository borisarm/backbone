using System.Security.Cryptography;
using Backbone.Core.Repository;
using Backbone.Core.Repository.Snapshots;
using Backbone.Core.Storage;

namespace Backbone.Core.Tests.Support;

/// <summary>
/// Comprobación de los invariantes de RNF-01 sobre el contenido real del almacenamiento (equivale a un
/// <c>check</c> completo, que se implementará en HU-26):
/// <list type="bullet">
/// <item>En <c>objects/</c> solo hay objetos con nombre de hash, en su carpeta, y su contenido coincide con su nombre.</item>
/// <item>En <c>snapshots/</c> solo hay manifiestos completos y válidos.</item>
/// <item>Todo objeto referenciado por un snapshot existe, con el tamaño indicado.</item>
/// </list>
/// </summary>
public static class RepositoryInvariants
{
    public static async Task AssertConsistentAsync(InMemoryRepositoryStorage storage)
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
                    Assert.True(
                        Convert.ToHexStringLower(SHA256.HashData(content)) == segments[2],
                        $"El contenido de {path} no coincide con su nombre.");
                    objectSizes[segments[2]] = content.Length;
                    break;

                case RepositoryLayout.SnapshotsDirectory:
                    Assert.True(segments.Length == 2, $"Archivo fuera de lugar en snapshots/: {path}");
                    Assert.EndsWith(RepositoryLayout.SnapshotExtension, segments[1], StringComparison.Ordinal);
                    Assert.True(SnapshotId.IsValid(segments[1][..^RepositoryLayout.SnapshotExtension.Length]), $"Nombre de snapshot no válido: {path}");
                    break;

                case RepositoryLayout.ConfigFile:
                case RepositoryLayout.LocksDirectory:
                case RepositoryLayout.TempDirectory:
                    break;

                default:
                    Assert.Fail($"Archivo inesperado: {path}");
                    break;
            }
        }

        var probe = await BackupRepository.ProbeAsync(storage, ct);
        if (probe.State != RepositoryState.Compatible)
        {
            Assert.True(probe.State is RepositoryState.Empty or RepositoryState.Missing, $"Estado inesperado: {probe.State}");
            Assert.DoesNotContain(storage.FilePaths, p => p.StartsWith("objects/", StringComparison.Ordinal) || p.StartsWith("snapshots/", StringComparison.Ordinal));
            return;
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

    /// <summary>Ningún archivo de objects/ ni snapshots/ ha cambiado ni desaparecido.</summary>
    public static void AssertPreserved(IReadOnlyDictionary<string, byte[]> before, InMemoryRepositoryStorage storage)
    {
        foreach (var (path, content) in before)
        {
            Assert.Equal(content, storage.GetFileContent(path));
        }
    }

    public static Dictionary<string, byte[]> CaptureImmutableFiles(InMemoryRepositoryStorage storage) =>
        storage.FilePaths
            .Where(p => p.StartsWith("objects/", StringComparison.Ordinal) || p.StartsWith("snapshots/", StringComparison.Ordinal))
            .ToDictionary(p => p, storage.GetFileContent, StringComparer.Ordinal);
}
