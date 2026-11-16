using System.Security.Cryptography;
using Backbone.Core.Engine;
using Backbone.Core.Repository;
using Backbone.Core.Storage;
using Backbone.Core.Tests.Support;
using static Backbone.Core.Tests.Support.EngineHarness;

namespace Backbone.Core.Tests.Engine;

/// <summary>
/// HU-13 criterio 7 (RNF-09): rutas de más de 260 caracteres en el origen y en el destino, sobre el disco real. El motor
/// no compone rutas del sistema operativo (usa las que devuelve el origen), y el almacenamiento las construye con
/// <c>Path.Join</c>; .NET admite las rutas largas sin prefijos especiales.
/// </summary>
[Trait("Story", "HU-13")]
[Trait("Requirement", "RNF-09")]
public sealed class BackupEngineLongPathTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task HU13_7_Paths_longer_than_260_characters_work_in_source_and_destination()
    {
        using var temp = new TempDirectory();
        var segment = new string('c', 100);

        // Origen: la raíz ya pasa de 260 caracteres y el archivo está varios niveles más abajo.
        var sourceRoot = Path.Join(temp.Path, "origen", segment + "1", segment + "2", segment + "3");
        var folder = Path.Join("Ñandú " + segment, "carpeta " + segment);
        var relative = Path.Join(folder, "archivo con nombre largo " + segment + ".txt");
        var sourceFile = Path.Join(sourceRoot, relative);
        Directory.CreateDirectory(Path.Join(sourceRoot, folder));
        var content = RandomNumberGenerator.GetBytes((1024 * 1024) + 321);
        await File.WriteAllBytesAsync(sourceFile, content, Ct);
        Directory.CreateDirectory(Path.Join(sourceRoot, "vacía " + segment));
        Assert.True(sourceRoot.Length > 260);
        Assert.True(sourceFile.Length > 500);

        // Destino: la raíz del repositorio también pasa de 260 caracteres.
        var repositoryRoot = Path.Join(temp.Path, "destino", segment + "a", segment + "b", segment + "c");
        Assert.True(repositoryRoot.Length > 260);
        var storage = FileSystemRepositoryStorage.ForLocalTesting(repositoryRoot);

        var time = new ManualTimeProvider();
        var source = new DiskSourceFileSystem();
        var request = Request([new BackupSource("Largo", sourceRoot, sourceRoot)]);
        var first = await RunAsync(storage, time, source, request);

        Assert.Equal(Backbone.Contracts.RunState.Completed, first.State);
        var entries = await ReadEntriesAsync(storage, first.Snapshot.Id);
        var file = Assert.Single(entries, e => !e.IsDirectory);
        Assert.Equal("Largo/" + relative.Replace('\\', '/'), file.Path);
        Assert.True(file.Path.Length > 260);
        Assert.Contains(entries, e => e.IsDirectory && e.Path == "Largo/vacía " + segment + "/");

        var hash = Convert.ToHexStringLower(SHA256.HashData(content));
        Assert.Equal(hash, file.Hash.ToString());
        Assert.Equal(new FileInfo(sourceFile).LastWriteTimeUtc, file.ModifiedUtc);
        var objectFile = Path.Join(repositoryRoot, "objects", hash[..2], hash);
        Assert.True(objectFile.Length > 260);
        Assert.Equal(content, await File.ReadAllBytesAsync(objectFile, Ct));
        Assert.True(File.Exists(Path.Join(repositoryRoot, "snapshots", first.Snapshot.Id + ".json")));

        // El objeto se lee de vuelta por el repositorio.
        var repository = await BackupRepository.OpenAsync(storage, time, Ct);
        await using (var stream = await repository.OpenObjectAsync(file.Hash.GetValueOrDefault(), Ct))
        {
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy, Ct);
            Assert.Equal(content, copy.ToArray());
        }

        // Segunda copia sin cambios: no se abre ningún archivo y se reutiliza el hash.
        time.Advance(TimeSpan.FromDays(1));
        var openedBefore = source.OpenCount;
        var second = await RunAsync(storage, time, source, request);
        Assert.Equal(openedBefore, source.OpenCount);
        Assert.Equal(first.Snapshot.Id, second.Snapshot.ParentId);
        Assert.Equal(entries.OrderBy(e => e.Path, StringComparer.Ordinal), (await ReadEntriesAsync(storage, second.Snapshot.Id)).OrderBy(e => e.Path, StringComparer.Ordinal));
        Assert.Empty(Directory.EnumerateFiles(Path.Join(repositoryRoot, "tmp")));
    }
}
