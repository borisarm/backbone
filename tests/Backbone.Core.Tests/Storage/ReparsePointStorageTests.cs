using System.Diagnostics;
using System.Text;
using Backbone.Core.Repository;
using Backbone.Core.Repository.Locking;
using Backbone.Core.Storage;
using Backbone.Core.Tests.Support;

namespace Backbone.Core.Tests.Storage;

/// <summary>
/// §6.1 "Puntos de reanálisis": ninguna operación del almacenamiento sigue un junction o enlace simbólico en la raíz, en una
/// carpeta intermedia, en el propio elemento ni al enumerar; la operación falla con <c>Corrupt</c> sin tocar el destino del
/// enlace. Cada prueba comprueba el estado final de la carpeta externa.
/// </summary>
[Trait("Story", "HT-04")]
[Trait("Requirement", "ADR-11")]
public sealed class ReparsePointStorageTests : IDisposable
{
    private readonly TempDirectory directory = new();
    private readonly List<string> links = [];

    private string Root => Path.Join(directory.Path, "repo");

    private string Outside => Path.Join(directory.Path, "fuera");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ReparsePointStorageTests()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Los puntos de reanálisis de este ensayo son de Windows.");
    }

    public void Dispose()
    {
        foreach (var link in links)
        {
            try
            {
                if (Directory.Exists(link))
                {
                    Directory.Delete(link);
                }
                else if (File.Exists(link))
                {
                    File.Delete(link);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Lo limpiará el sistema.
            }
        }

        directory.Dispose();
    }

    private FileSystemRepositoryStorage CreateStorage()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Outside);
        return FileSystemRepositoryStorage.ForLocalTesting(Root);
    }

    private static async Task AssertCorruptAsync(Func<Task> operation)
    {
        var ex = await Assert.ThrowsAsync<RepositoryException>(operation);
        Assert.Equal(RepositoryErrorKind.Corrupt, ex.Kind);
        Assert.Contains("reanálisis", ex.Message, StringComparison.Ordinal);
    }

    private static async Task DrainAsync(FileSystemRepositoryStorage storage, string path)
    {
        await foreach (var _ in storage.EnumerateAsync(path, Ct))
        {
        }
    }

    private void AssertOutsideUntouched(params string[] expectedFiles)
    {
        var actual = Directory.EnumerateFileSystemEntries(Outside, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(Outside, p))
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.Equal(expectedFiles.Order(StringComparer.Ordinal), actual);
    }

    // ================================================================== Carpeta intermedia

    [Fact]
    public async Task Every_operation_through_an_intermediate_junction_fails_without_touching_its_target()
    {
        var storage = CreateStorage();
        Directory.CreateDirectory(Path.Join(Root, "objects"));
        Directory.CreateDirectory(Path.Join(Root, "tmp"));
        await File.WriteAllTextAsync(Path.Join(Outside, "x"), "dentro", Ct);
        await File.WriteAllTextAsync(Path.Join(Root, "tmp", "t.tmp"), "t", Ct);
        CreateJunction(Path.Join(Root, "objects", "ab"), Outside);

        await AssertCorruptAsync(() => storage.OpenReadAsync("objects/ab/x", Ct));
        await AssertCorruptAsync(() => storage.CreateNewAsync("objects/ab/nuevo", Ct));
        await AssertCorruptAsync(() => storage.TryMoveAsync("tmp/t.tmp", "objects/ab/destino", overwrite: false, Ct));
        await AssertCorruptAsync(() => storage.TryMoveAsync("objects/ab/x", "tmp/robado", overwrite: false, Ct));
        await AssertCorruptAsync(() => storage.DeleteFileAsync("objects/ab/x", Ct));
        await AssertCorruptAsync(() => storage.CreateDirectoryAsync("objects/ab", Ct));
        await AssertCorruptAsync(() => storage.CreateDirectoryAsync("objects/ab/sub", Ct));
        await AssertCorruptAsync(() => storage.DirectoryExistsAsync("objects/ab", Ct));
        await AssertCorruptAsync(() => storage.GetFileInfoAsync("objects/ab/x", Ct));
        await AssertCorruptAsync(() => DrainAsync(storage, "objects/ab"));

        AssertOutsideUntouched("x");
        Assert.Equal("dentro", await File.ReadAllTextAsync(Path.Join(Outside, "x"), Ct));
        Assert.True(File.Exists(Path.Join(Root, "tmp", "t.tmp")));
        Assert.False(File.Exists(Path.Join(Root, "tmp", "robado")));
    }

    [Theory]
    [InlineData("objects")]
    [InlineData("snapshots")]
    [InlineData("locks")]
    [InlineData("tmp")]
    public async Task A_junction_in_place_of_a_top_level_folder_is_detected(string folder)
    {
        var storage = CreateStorage();
        CreateJunction(Path.Join(Root, folder), Outside);

        await AssertCorruptAsync(() => storage.CreateNewAsync($"{folder}/nuevo.tmp", Ct));
        await AssertCorruptAsync(() => storage.DirectoryExistsAsync(folder, Ct));
        AssertOutsideUntouched();
    }

    // ================================================================== El propio elemento

    [Fact]
    public async Task A_junction_as_the_element_itself_is_detected_by_file_operations()
    {
        var storage = CreateStorage();
        Directory.CreateDirectory(Path.Join(Root, "snapshots"));
        await File.WriteAllTextAsync(Path.Join(Outside, "contenido.txt"), "dentro", Ct);
        CreateJunction(Path.Join(Root, "snapshots", "20261115T020000Z-a1b2.json"), Outside);

        await AssertCorruptAsync(() => storage.OpenReadAsync("snapshots/20261115T020000Z-a1b2.json", Ct));
        await AssertCorruptAsync(() => storage.GetFileInfoAsync("snapshots/20261115T020000Z-a1b2.json", Ct));
        await AssertCorruptAsync(() => storage.DeleteFileAsync("snapshots/20261115T020000Z-a1b2.json", Ct));
        await AssertCorruptAsync(() => storage.TryMoveAsync("snapshots/20261115T020000Z-a1b2.json", "snapshots/otro.json", false, Ct));

        AssertOutsideUntouched("contenido.txt");
    }

    [Fact]
    public async Task A_file_symbolic_link_is_not_followed_read_deleted_or_overwritten()
    {
        var storage = CreateStorage();
        Directory.CreateDirectory(Path.Join(Root, "tmp"));
        var target = Path.Join(Outside, "secreto.txt");
        await File.WriteAllTextAsync(target, "original", Ct);
        CreateFileLink(Path.Join(Root, "tmp", "enlace.tmp"), target);

        await AssertCorruptAsync(() => storage.OpenReadAsync("tmp/enlace.tmp", Ct));
        await AssertCorruptAsync(() => storage.GetFileInfoAsync("tmp/enlace.tmp", Ct));
        await AssertCorruptAsync(() => storage.DeleteFileAsync("tmp/enlace.tmp", Ct));
        await AssertCorruptAsync(() => storage.CreateNewAsync("tmp/enlace.tmp", Ct));

        Assert.Equal("original", await File.ReadAllTextAsync(target, Ct));
    }

    [Fact]
    public async Task A_move_whose_destination_is_a_symbolic_link_does_not_replace_the_link_target()
    {
        var storage = CreateStorage();
        Directory.CreateDirectory(Path.Join(Root, "tmp"));
        Directory.CreateDirectory(Path.Join(Root, "snapshots"));
        await File.WriteAllTextAsync(Path.Join(Root, "tmp", "a.tmp"), "nuevo", Ct);
        var target = Path.Join(Outside, "destino.txt");
        await File.WriteAllTextAsync(target, "original", Ct);
        CreateFileLink(Path.Join(Root, "snapshots", "x.json"), target);

        await AssertCorruptAsync(() => storage.TryMoveAsync("tmp/a.tmp", "snapshots/x.json", overwrite: true, Ct));

        Assert.Equal("original", await File.ReadAllTextAsync(target, Ct));
        Assert.True(File.Exists(Path.Join(Root, "tmp", "a.tmp")));
    }

    // ================================================================== Enumeración

    [Fact]
    public async Task Enumerating_a_folder_that_contains_a_junction_fails_even_if_other_items_come_first()
    {
        var storage = CreateStorage();
        Directory.CreateDirectory(Path.Join(Root, "snapshots"));
        for (var i = 0; i < 5; i++)
        {
            await File.WriteAllTextAsync(Path.Join(Root, "snapshots", $"a{i}.json"), "{}", Ct);
        }

        CreateJunction(Path.Join(Root, "snapshots", "zz"), Outside);

        await AssertCorruptAsync(() => DrainAsync(storage, "snapshots"));
    }

    [Fact]
    public async Task Enumerating_a_folder_that_contains_a_file_symbolic_link_fails()
    {
        var storage = CreateStorage();
        Directory.CreateDirectory(Path.Join(Root, "locks"));
        var target = Path.Join(Outside, "t.txt");
        await File.WriteAllTextAsync(target, "x", Ct);
        CreateFileLink(Path.Join(Root, "locks", "PC-1.lock"), target);

        await AssertCorruptAsync(() => DrainAsync(storage, "locks"));
    }

    // ================================================================== Raíz

    [Fact]
    public async Task Every_operation_on_a_junction_root_fails_and_the_target_stays_untouched()
    {
        CreateStorage();
        var link = Path.Join(directory.Path, "enlace");
        CreateJunction(link, Outside);
        var storage = FileSystemRepositoryStorage.ForLocalTesting(link);

        await AssertCorruptAsync(() => storage.DirectoryExistsAsync(string.Empty, Ct));
        await AssertCorruptAsync(() => storage.CreateDirectoryAsync("objects", Ct));
        await AssertCorruptAsync(() => storage.CreateNewAsync("repo.json", Ct));
        await AssertCorruptAsync(() => storage.OpenReadAsync("repo.json", Ct));
        await AssertCorruptAsync(() => DrainAsync(storage, string.Empty));

        AssertOutsideUntouched();
    }

    [Fact]
    public async Task Initializing_on_a_junction_root_writes_nothing_to_its_target()
    {
        CreateStorage();
        var link = Path.Join(directory.Path, "enlace");
        CreateJunction(link, Outside);
        var storage = FileSystemRepositoryStorage.ForLocalTesting(link);

        var ex = await Assert.ThrowsAsync<RepositoryException>(() =>
            BackupRepository.InitializeAsync(storage, new RepositoryInitializationOptions { CreatedBy = "x" }, TimeProvider.System, Ct));

        // Falla sin escribir; el motivo exacto (AlreadyInitialized o Corrupt) lo cubre la prueba siguiente.
        Assert.NotEqual(default, ex.Kind);
        AssertOutsideUntouched();
        Assert.False(File.Exists(Path.Join(Outside, "repo.json")));
    }

    [Fact]
    public async Task Initializing_on_a_junction_root_reports_Corrupt()
    {
        CreateStorage();
        var link = Path.Join(directory.Path, "enlace");
        CreateJunction(link, Outside);
        var storage = FileSystemRepositoryStorage.ForLocalTesting(link);

        var ex = await Assert.ThrowsAsync<RepositoryException>(() =>
            BackupRepository.InitializeAsync(storage, new RepositoryInitializationOptions { CreatedBy = "x" }, TimeProvider.System, Ct));

        Assert.Equal(RepositoryErrorKind.Corrupt, ex.Kind);
    }

    // ================================================================== A nivel de repositorio

    [Theory]
    [InlineData("objects")]
    [InlineData("snapshots")]
    public async Task A_repository_whose_top_level_folder_became_a_junction_is_corrupt_and_nothing_is_written_outside(string folder)
    {
        var storage = CreateStorage();
        var repository = await BackupRepository.InitializeAsync(storage, new RepositoryInitializationOptions { CreatedBy = "x" }, TimeProvider.System, Ct);
        Directory.Delete(Path.Join(Root, folder));
        CreateJunction(Path.Join(Root, folder), Outside);

        Assert.Equal(RepositoryState.Corrupt, (await BackupRepository.ProbeAsync(storage, Ct)).State);
        var ex = await Assert.ThrowsAsync<RepositoryException>(() => BackupRepository.OpenAsync(storage, TimeProvider.System, Ct));
        Assert.Equal(RepositoryErrorKind.Corrupt, ex.Kind);
        if (folder == "snapshots")
        {
            var ex2 = await Assert.ThrowsAsync<RepositoryException>(() => repository.ListSnapshotIdsAsync(Ct));
            Assert.Equal(RepositoryErrorKind.Corrupt, ex2.Kind);
        }
        else
        {
            await using var shared = await repository.AcquireLockAsync(RepositoryLockType.Shared, new LockOwner("PC-SALON", 1), Ct);
            using var content = new MemoryStream("contenido"u8.ToArray());
            var ex2 = await Assert.ThrowsAsync<RepositoryException>(() => repository.WriteObjectAsync(content, expectedHash: null, shared, Ct));
            Assert.Equal(RepositoryErrorKind.Corrupt, ex2.Kind);
        }

        AssertOutsideUntouched();
    }

    [Fact]
    public async Task A_repo_json_that_is_a_symbolic_link_is_corrupt_and_its_target_is_not_used()
    {
        var storage = CreateStorage();
        await BackupRepository.InitializeAsync(storage, new RepositoryInitializationOptions { CreatedBy = "x" }, TimeProvider.System, Ct);
        var config = await File.ReadAllBytesAsync(Path.Join(Root, "repo.json"), Ct);
        var target = Path.Join(Outside, "repo.json");
        await File.WriteAllBytesAsync(target, config, Ct);
        File.Delete(Path.Join(Root, "repo.json"));
        CreateFileLink(Path.Join(Root, "repo.json"), target);

        var probe = await BackupRepository.ProbeAsync(storage, Ct);
        var ex = await Assert.ThrowsAsync<RepositoryException>(() => BackupRepository.OpenAsync(storage, TimeProvider.System, Ct));

        Assert.Equal(RepositoryState.Corrupt, probe.State);
        Assert.Equal(RepositoryErrorKind.Corrupt, ex.Kind);
        Assert.Equal(config, await File.ReadAllBytesAsync(target, Ct));
    }

    [Fact]
    public async Task A_junction_planted_as_a_lock_file_blocks_acquisition_without_touching_its_target()
    {
        var storage = CreateStorage();
        var repository = await BackupRepository.InitializeAsync(storage, new RepositoryInitializationOptions { CreatedBy = "x" }, TimeProvider.System, Ct);
        await File.WriteAllTextAsync(Path.Join(Outside, "valioso.txt"), "no borrar", Ct);
        CreateJunction(Path.Join(Root, "locks", "PC-7.lock"), Outside);

        var ex = await Assert.ThrowsAsync<RepositoryException>(() =>
            repository.AcquireLockAsync(RepositoryLockType.Exclusive, new LockOwner("PC-SALON", 1), Ct));

        Assert.Equal(RepositoryErrorKind.Corrupt, ex.Kind);
        AssertOutsideUntouched("valioso.txt");
        Assert.False(File.Exists(Path.Join(Root, "locks", "PC-SALON-1.lock")));
    }

    // ================================================================== Auxiliares

    private void CreateJunction(string link, string target)
    {
        RunCmd("mklink", "/J", link, target);
        links.Insert(0, link);
        Assert.True(new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint));
    }

    private void CreateFileLink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Assert.Skip($"No se pueden crear enlaces simbólicos sin privilegio o modo desarrollador: {ex.Message}");
        }

        links.Insert(0, link);
        Assert.True(new FileInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint));
    }

    private static void RunCmd(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("/c");
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("No se pudo ejecutar cmd.");
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"{string.Join(' ', arguments)} falló: {output}");
    }
}
