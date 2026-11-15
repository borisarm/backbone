using System.Text;
using Backbone.Core.Storage;
using Backbone.Core.Tests.Support;

namespace Backbone.Core.Tests.Storage;

/// <summary>
/// HT-04 criterio 5: el contrato de <see cref="IRepositoryStorage"/>, verificado con la misma batería
/// sobre la implementación de sistema de archivos y la de memoria.
/// </summary>
[Trait("Story", "HT-04")]
public abstract class RepositoryStorageContractTests : IDisposable
{
    private bool disposed;

    protected abstract IRepositoryStorage Storage { get; }

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task HT04_5_Created_file_can_be_read_back()
    {
        await Storage.CreateDirectoryAsync("tmp", Ct);
        await WriteAsync("tmp/a.tmp", "contenido ñ");

        Assert.Equal("contenido ñ", await ReadAsync("tmp/a.tmp"));
        var info = await Storage.GetFileInfoAsync("tmp/a.tmp", Ct);
        Assert.NotNull(info);
        Assert.Equal("a.tmp", info.Name);
        Assert.Equal(Encoding.UTF8.GetByteCount("contenido ñ"), info.Length);
        Assert.False(info.IsDirectory);
        Assert.Equal(DateTimeKind.Utc, info.LastWriteUtc.Kind);
    }

    [Fact]
    public async Task HT04_5_CreateNew_fails_if_the_file_exists_or_the_folder_is_missing()
    {
        await Storage.CreateDirectoryAsync("tmp", Ct);
        await WriteAsync("tmp/a.tmp", "uno");

        await Assert.ThrowsAnyAsync<IOException>(() => Storage.CreateNewAsync("tmp/a.tmp", Ct));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => Storage.CreateNewAsync("no-existe/a.tmp", Ct));
        Assert.Equal("uno", await ReadAsync("tmp/a.tmp"));
    }

    [Fact]
    public async Task HT04_5_Move_without_overwrite_keeps_the_existing_destination()
    {
        await Storage.CreateDirectoryAsync("tmp", Ct);
        await Storage.CreateDirectoryAsync("objects/ab", Ct);
        await WriteAsync("tmp/1.tmp", "nuevo");
        await WriteAsync("tmp/2.tmp", "existente");
        Assert.True(await Storage.TryMoveAsync("tmp/2.tmp", "objects/ab/x", overwrite: false, Ct));

        Assert.False(await Storage.TryMoveAsync("tmp/1.tmp", "objects/ab/x", overwrite: false, Ct));

        Assert.Equal("existente", await ReadAsync("objects/ab/x"));
        Assert.Equal("nuevo", await ReadAsync("tmp/1.tmp"));
        Assert.Null(await Storage.GetFileInfoAsync("tmp/2.tmp", Ct));
    }

    [Fact]
    public async Task HT04_5_Move_with_overwrite_replaces_the_destination()
    {
        await Storage.CreateDirectoryAsync("locks", Ct);
        await WriteAsync("locks/a.lock", "viejo");
        await WriteAsync("locks/a.tmp", "nuevo");

        Assert.True(await Storage.TryMoveAsync("locks/a.tmp", "locks/a.lock", overwrite: true, Ct));

        Assert.Equal("nuevo", await ReadAsync("locks/a.lock"));
        Assert.Null(await Storage.GetFileInfoAsync("locks/a.tmp", Ct));
    }

    [Fact]
    public async Task HT04_5_Move_fails_without_source_or_destination_folder()
    {
        await Storage.CreateDirectoryAsync("tmp", Ct);
        await WriteAsync("tmp/a.tmp", "x");

        await Assert.ThrowsAsync<FileNotFoundException>(() => Storage.TryMoveAsync("tmp/no.tmp", "tmp/b", overwrite: false, Ct));
        await Assert.ThrowsAnyAsync<IOException>(() => Storage.TryMoveAsync("tmp/a.tmp", "objects/zz/b", overwrite: false, Ct));
        Assert.Equal("x", await ReadAsync("tmp/a.tmp"));
    }

    [Fact]
    public async Task HT04_5_Delete_is_idempotent()
    {
        await Storage.CreateDirectoryAsync("tmp", Ct);
        await WriteAsync("tmp/a.tmp", "x");

        await Storage.DeleteFileAsync("tmp/a.tmp", Ct);
        await Storage.DeleteFileAsync("tmp/a.tmp", Ct);
        await Storage.DeleteFileAsync("no-existe/a.tmp", Ct);

        Assert.Null(await Storage.GetFileInfoAsync("tmp/a.tmp", Ct));
    }

    [Fact]
    public async Task HT04_5_Read_of_a_missing_file_is_FileNotFound()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() => Storage.OpenReadAsync("repo.json", Ct));
        await Assert.ThrowsAsync<FileNotFoundException>(() => Storage.OpenReadAsync("no-existe/repo.json", Ct));
        Assert.Null(await Storage.GetFileInfoAsync("no-existe/x", Ct));
    }

    [Fact]
    public async Task HT04_5_Directories_are_created_recursively_and_enumerated()
    {
        await Storage.CreateDirectoryAsync("objects/ab", Ct);
        await Storage.CreateDirectoryAsync("objects/ab", Ct);
        await Storage.CreateDirectoryAsync("objects/cd", Ct);
        await WriteAsync("objects/f.txt", "x");

        Assert.True(await Storage.DirectoryExistsAsync("", Ct));
        Assert.True(await Storage.DirectoryExistsAsync("objects/ab", Ct));
        Assert.False(await Storage.DirectoryExistsAsync("objects/ef", Ct));

        var entries = new List<StorageEntry>();
        await foreach (var entry in Storage.EnumerateAsync("objects", Ct))
        {
            entries.Add(entry);
        }

        Assert.Equal(
            ["ab:True", "cd:True", "f.txt:False"],
            entries.Select(e => $"{e.Name}:{e.IsDirectory}").Order(StringComparer.Ordinal));
        Assert.Equal(1, entries.Single(e => e.Name == "f.txt").Length);

        await foreach (var _ in Storage.EnumerateAsync("no-existe", Ct))
        {
            Assert.Fail("Una carpeta inexistente se enumera vacía.");
        }
    }

    public static TheoryData<string> InvalidPaths => new()
    {
        "/repo.json",
        "repo.json/",
        "a//b",
        "..",
        "../fuera.txt",
        "tmp/../../fuera.txt",
        "./repo.json",
        "tmp\\a.tmp",
        "C:/Windows/win.ini",
        "C:",
        "repo.json:flujo",
        "a*b",
        "a?b",
        "a<b",
        "a>b",
        "a|b",
        "a\"b",
        "a\tb",
        "fin.",
        "fin ",
    };

    [Theory]
    [MemberData(nameof(InvalidPaths))]
    public async Task HT04_5_Paths_outside_the_repository_are_rejected(string path)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Storage.OpenReadAsync(path, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => Storage.CreateNewAsync(path, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => Storage.TryMoveAsync("x", path, overwrite: false, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => Storage.TryMoveAsync(path, "x", overwrite: false, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => Storage.DeleteFileAsync(path, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => Storage.CreateDirectoryAsync(path, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => Storage.DirectoryExistsAsync(path, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => Storage.GetFileInfoAsync(path, Ct));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await foreach (var _ in Storage.EnumerateAsync(path, Ct))
            {
            }
        });
    }

    [Fact]
    public async Task HT04_5_Empty_path_is_the_root_only_for_folder_operations()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Storage.OpenReadAsync("", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => Storage.CreateNewAsync("", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => Storage.DeleteFileAsync("", Ct));
        await Storage.CreateDirectoryAsync("", Ct);
        Assert.True(await Storage.DirectoryExistsAsync("", Ct));
    }

    [Fact]
    public async Task HT04_5_Cancelled_operations_throw()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Storage.CreateDirectoryAsync("tmp", cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Storage.GetFileInfoAsync("repo.json", cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Storage.OpenReadAsync("repo.json", cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Storage.CreateNewAsync("repo.json", cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Storage.TryMoveAsync("a", "b", false, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Storage.DeleteFileAsync("a", cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Storage.DirectoryExistsAsync("a", cancelled.Token));
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        disposed = true;
    }

    protected bool IsDisposed => disposed;

    private async Task WriteAsync(string path, string content)
    {
        await using var stream = await Storage.CreateNewAsync(path, Ct);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(content), Ct);
        await stream.FlushAsync(Ct);
    }

    private async Task<string> ReadAsync(string path)
    {
        await using var stream = await Storage.OpenReadAsync(path, Ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(Ct);
    }
}

public sealed class FileSystemRepositoryStorageTests : RepositoryStorageContractTests
{
    private readonly TempDirectory directory = new();

    public FileSystemRepositoryStorageTests()
    {
        Storage = new FileSystemRepositoryStorage(directory.Path);
    }

    protected override IRepositoryStorage Storage { get; }

    [Fact]
    public void HT04_5_Root_must_be_an_absolute_path()
    {
        Assert.Throws<ArgumentException>(() => new FileSystemRepositoryStorage(@"relativa\repo"));
        Assert.ThrowsAny<ArgumentException>(() => new FileSystemRepositoryStorage(" "));
        Assert.Equal(directory.Path, new FileSystemRepositoryStorage(directory.Path + Path.DirectorySeparatorChar).RootPath);
    }

    [Fact]
    public async Task HT04_5_Files_are_placed_under_the_root_with_long_paths()
    {
        // Rutas de más de 260 caracteres (RNF-09): .NET las admite sin prefijos especiales.
        var deep = string.Join('/', Enumerable.Repeat(new string('d', 50), 6));
        await Storage.CreateDirectoryAsync(deep, Ct);
        await using (var stream = await Storage.CreateNewAsync(deep + "/archivo.txt", Ct))
        {
            await stream.WriteAsync("x"u8.ToArray(), Ct);
            await stream.FlushAsync(Ct);
        }

        var full = Path.Join([directory.Path, .. deep.Split('/'), "archivo.txt"]);
        Assert.True(full.Length > 260);
        Assert.True(File.Exists(full));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed)
        {
            directory.Dispose();
        }

        base.Dispose(disposing);
    }
}

public sealed class InMemoryRepositoryStorageTests : RepositoryStorageContractTests
{
    private readonly InMemoryRepositoryStorage storage = new();

    protected override IRepositoryStorage Storage => storage;

    [Fact]
    public async Task HT04_5_Names_are_case_insensitive_like_NTFS_and_SMB()
    {
        await storage.CreateDirectoryAsync("Objects/AB", Ct);
        storage.SetFileContent("objects/ab/X", "1"u8);

        Assert.True(await storage.DirectoryExistsAsync("OBJECTS/ab", Ct));
        Assert.NotNull(await storage.GetFileInfoAsync("OBJECTS/AB/x", Ct));
        await Assert.ThrowsAnyAsync<IOException>(() => storage.CreateNewAsync("objects/AB/x", Ct));
    }

    [Fact]
    public async Task HT04_5_Partial_content_is_visible_while_writing()
    {
        await storage.CreateDirectoryAsync("tmp", Ct);
        await using var stream = await storage.CreateNewAsync("tmp/a.tmp", Ct);
        await stream.WriteAsync("medio"u8.ToArray(), Ct);

        Assert.Equal("medio"u8.ToArray(), storage.GetFileContent("tmp/a.tmp"));
        Assert.False(stream.CanRead);
        Assert.Throws<NotSupportedException>(() => stream.Length);
    }

    [Fact]
    public async Task HT04_5_Files_and_directories_cannot_share_a_name()
    {
        storage.SetFileContent("a", "x"u8);

        await Assert.ThrowsAnyAsync<IOException>(() => storage.CreateDirectoryAsync("a/b", Ct));
        await storage.CreateDirectoryAsync("d", Ct);
        await Assert.ThrowsAnyAsync<IOException>(() => storage.CreateNewAsync("d", Ct));
        await Assert.ThrowsAnyAsync<IOException>(() => storage.TryMoveAsync("a", "d", overwrite: true, Ct));
        Assert.True(await storage.TryMoveAsync("a", "A", overwrite: false, Ct));
    }

    [Fact]
    public void HT04_5_Test_helpers_validate_their_paths()
    {
        Assert.Throws<FileNotFoundException>(() => storage.GetFileContent("no"));
        Assert.Throws<FileNotFoundException>(() => storage.SetLastWriteUtc("no", DateTime.UtcNow));
        Assert.Throws<DirectoryNotFoundException>(() => storage.SetFileContent("no/existe", "x"u8));
        Assert.Throws<ArgumentException>(() => storage.SetFileContent("../x", "x"u8));
    }
}
