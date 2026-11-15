using System.Text;
using System.Text.Json;
using Backbone.Core.Repository;
using Backbone.Core.Storage;
using Backbone.Core.Tests.Support;

namespace Backbone.Core.Tests.Repository;

/// <summary>HT-04 criterio 1: estructura de §6.1, repo.json de §6.2 y validación del formato (RNF-14).</summary>
[Trait("Story", "HT-04")]
public sealed class RepositoryInitializationTests
{
    private static readonly RepositoryInitializationOptions Options = new() { CreatedBy = "Backbone 1.0.0 en PC-SALON" };
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task HT04_1_Initialize_creates_the_v1_structure_and_repo_json()
    {
        var storage = new InMemoryRepositoryStorage();
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 11, 15, 2, 0, 0, TimeSpan.Zero));
        var id = Guid.Parse("8f6c2e1a-1111-2222-3333-444455556666");

        var repository = await BackupRepository.InitializeAsync(storage, Options with { RepositoryId = id }, time, Ct);

        Assert.Equal(["locks", "objects", "snapshots", "tmp"], storage.DirectoryPaths.Order(StringComparer.Ordinal));
        Assert.Equal(["repo.json"], storage.FilePaths);

        using var json = JsonDocument.Parse(storage.GetFileContent("repo.json"));
        var root = json.RootElement;
        Assert.Equal(
            ["formatVersion", "repositoryId", "createdUtc", "createdBy", "hash", "compression", "encryption"],
            root.EnumerateObject().Select(p => p.Name));
        Assert.Equal(1, root.GetProperty("formatVersion").GetInt32());
        Assert.Equal("8f6c2e1a-1111-2222-3333-444455556666", root.GetProperty("repositoryId").GetString());
        Assert.Equal("2026-11-15T02:00:00Z", root.GetProperty("createdUtc").GetString());
        Assert.Equal("Backbone 1.0.0 en PC-SALON", root.GetProperty("createdBy").GetString());
        Assert.Equal("sha256", root.GetProperty("hash").GetString());
        Assert.Equal("none", root.GetProperty("compression").GetString());
        Assert.Equal("none", root.GetProperty("encryption").GetString());

        Assert.Equal(new RepositoryConfiguration(1, id, time.UtcNow, Options.CreatedBy, "sha256", "none", "none"), repository.Configuration);
    }

    [Fact]
    public async Task HT04_1_Open_reads_an_initialized_repository()
    {
        var storage = new InMemoryRepositoryStorage();
        var created = await BackupRepository.InitializeAsync(storage, Options, new ManualTimeProvider(), Ct);

        var opened = await BackupRepository.OpenAsync(storage, TimeProvider.System, Ct);

        Assert.Equal(created.Configuration, opened.Configuration);
        Assert.Empty(await opened.ListSnapshotIdsAsync(Ct));
    }

    [Fact]
    public async Task HT04_1_Initialize_and_open_on_the_file_system()
    {
        using var directory = new TempDirectory();
        var storage = new FileSystemRepositoryStorage(Path.Join(directory.Path, "Backups", "PC-SALON"));

        await BackupRepository.InitializeAsync(storage, Options, TimeProvider.System, Ct);
        var opened = await BackupRepository.OpenAsync(storage, TimeProvider.System, Ct);

        Assert.Equal(1, opened.Configuration.FormatVersion);
        foreach (var name in RepositoryLayout.Directories)
        {
            Assert.True(Directory.Exists(Path.Join(directory.Path, "Backups", "PC-SALON", name)));
        }

        Assert.True(File.Exists(Path.Join(directory.Path, "Backups", "PC-SALON", "repo.json")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Join(directory.Path, "Backups", "PC-SALON", "tmp")));
    }

    public static TheoryData<string> IncompatibleConfigurations => new()
    {
        // Formato superior: se reconoce por la versión aunque el resto haya cambiado por completo.
        """{ "formatVersion": 2, "repositoryId": "8f6c2e1a-1111-2222-3333-444455556666", "createdUtc": "2026-11-15T02:00:00Z", "createdBy": "x", "hash": "sha256", "compression": "zstd", "encryption": "aes-256-gcm" }""",
        """{ "formatVersion": 7, "algo": "nuevo" }""",
        """{ "something": [1, 2], "formatVersion": 2 }""",
        // Características desconocidas en v1.
        """{ "formatVersion": 1, "repositoryId": "8f6c2e1a-1111-2222-3333-444455556666", "createdUtc": "2026-11-15T02:00:00Z", "createdBy": "x", "hash": "blake3", "compression": "none", "encryption": "none" }""",
        """{ "formatVersion": 1, "repositoryId": "8f6c2e1a-1111-2222-3333-444455556666", "createdUtc": "2026-11-15T02:00:00Z", "createdBy": "x", "hash": "sha256", "compression": "zstd", "encryption": "none" }""",
        """{ "formatVersion": 1, "repositoryId": "8f6c2e1a-1111-2222-3333-444455556666", "createdUtc": "2026-11-15T02:00:00Z", "createdBy": "x", "hash": "sha256", "compression": "none", "encryption": "aes" }""",
    };

    [Theory]
    [MemberData(nameof(IncompatibleConfigurations))]
    [Trait("Requirement", "RNF-14")]
    public async Task HT04_1_Open_rejects_higher_or_unknown_formats_with_RepositoryIncompatible(string repoJson)
    {
        var storage = await RepositoryWithConfigAsync(repoJson);
        var before = RepositoryInvariants.CaptureImmutableFiles(storage);
        var filesBefore = storage.FilePaths.Order(StringComparer.Ordinal).ToList();

        var ex = await Assert.ThrowsAsync<RepositoryException>(() => BackupRepository.OpenAsync(storage, TimeProvider.System, Ct));

        Assert.Equal(RepositoryErrorKind.Incompatible, ex.Kind);
        var probe = await BackupRepository.ProbeAsync(storage, Ct);
        Assert.Equal(RepositoryState.Incompatible, probe.State);

        // Nunca se escribe en un repositorio de formato superior (RNF-14).
        await Assert.ThrowsAsync<RepositoryException>(() => BackupRepository.InitializeAsync(storage, Options, TimeProvider.System, Ct));
        Assert.Equal(filesBefore, storage.FilePaths.Order(StringComparer.Ordinal));
        Assert.Equal(repoJson, Encoding.UTF8.GetString(storage.GetFileContent("repo.json")));
        RepositoryInvariants.AssertPreserved(before, storage);
    }

    [Fact]
    [Trait("Requirement", "RNF-14")]
    public async Task HT04_1_Probe_reports_the_declared_higher_version()
    {
        var storage = await RepositoryWithConfigAsync("""{ "formatVersion": 2 }""");

        var probe = await BackupRepository.ProbeAsync(storage, Ct);

        Assert.Equal(RepositoryState.Incompatible, probe.State);
        Assert.Equal(2, probe.FormatVersion);
        Assert.Null(probe.Configuration);
    }

    public static TheoryData<string> CorruptConfigurations => new()
    {
        "",
        "no es json",
        "[1, 2, 3]",
        """{ "formatVersion": "1" }""",
        """{ "formatVersion": 0, "repositoryId": "8f6c2e1a-1111-2222-3333-444455556666", "createdUtc": "2026-11-15T02:00:00Z", "createdBy": "x", "hash": "sha256", "compression": "none", "encryption": "none" }""",
        """{ "formatVersion": 1.5 }""",
        """{ "repositoryId": "8f6c2e1a-1111-2222-3333-444455556666" }""",
        """{ "formatVersion": 1, "repositoryId": "no-es-un-guid", "createdUtc": "2026-11-15T02:00:00Z", "createdBy": "x", "hash": "sha256", "compression": "none", "encryption": "none" }""",
        """{ "formatVersion": 1, "repositoryId": "8f6c2e1a-1111-2222-3333-444455556666", "createdUtc": "ayer", "createdBy": "x", "hash": "sha256", "compression": "none", "encryption": "none" }""",
        """{ "formatVersion": 1, "repositoryId": "8f6c2e1a-1111-2222-3333-444455556666", "createdUtc": "2026-11-15T02:00:00Z", "hash": "sha256", "compression": "none", "encryption": "none" }""",
        """{ "formatVersion": 1, "repositoryId": "8f6c2e1a-1111-2222-3333-444455556666", "createdUtc": "2026-11-15T02:00:00Z", "createdBy": "x", "compression": "none", "encryption": "none" }""",
    };

    [Theory]
    [MemberData(nameof(CorruptConfigurations))]
    public async Task HT04_1_Open_rejects_invalid_repo_json_as_corrupt(string repoJson)
    {
        var storage = await RepositoryWithConfigAsync(repoJson);

        var ex = await Assert.ThrowsAsync<RepositoryException>(() => BackupRepository.OpenAsync(storage, TimeProvider.System, Ct));

        Assert.Equal(RepositoryErrorKind.Corrupt, ex.Kind);
        Assert.Equal(RepositoryState.Corrupt, (await BackupRepository.ProbeAsync(storage, Ct)).State);
    }

    [Fact]
    public async Task HT04_1_Open_rejects_an_oversized_repo_json()
    {
        var storage = await RepositoryWithConfigAsync("{\"formatVersion\": 1, \"pad\": \"" + new string('x', 70_000) + "\"}");

        var ex = await Assert.ThrowsAsync<RepositoryException>(() => BackupRepository.OpenAsync(storage, TimeProvider.System, Ct));

        Assert.Equal(RepositoryErrorKind.Corrupt, ex.Kind);
    }

    [Theory]
    [InlineData("objects")]
    [InlineData("snapshots")]
    [InlineData("locks")]
    [InlineData("tmp")]
    public async Task HT04_1_Open_rejects_a_repository_missing_a_directory(string missing)
    {
        var storage = new InMemoryRepositoryStorage();
        await BackupRepository.InitializeAsync(storage, Options, TimeProvider.System, Ct);
        var rebuilt = new InMemoryRepositoryStorage();
        foreach (var directory in RepositoryLayout.Directories.Where(d => d != missing))
        {
            await rebuilt.CreateDirectoryAsync(directory, Ct);
        }

        rebuilt.SetFileContent("repo.json", storage.GetFileContent("repo.json"));

        var ex = await Assert.ThrowsAsync<RepositoryException>(() => BackupRepository.OpenAsync(rebuilt, TimeProvider.System, Ct));
        Assert.Equal(RepositoryErrorKind.Corrupt, ex.Kind);
        Assert.Equal(RepositoryState.Corrupt, (await BackupRepository.ProbeAsync(rebuilt, Ct)).State);
    }

    [Fact]
    public async Task HT04_1_Open_without_repo_json_is_NotARepository()
    {
        var storage = new InMemoryRepositoryStorage();

        var ex = await Assert.ThrowsAsync<RepositoryException>(() => BackupRepository.OpenAsync(storage, TimeProvider.System, Ct));

        Assert.Equal(RepositoryErrorKind.NotARepository, ex.Kind);
    }

    [Fact]
    public async Task HT04_1_Initialize_refuses_a_folder_with_other_files_and_writes_nothing()
    {
        var storage = new InMemoryRepositoryStorage();
        storage.SetFileContent("Mis documentos.docx", "datos"u8);

        var ex = await Assert.ThrowsAsync<RepositoryException>(() => BackupRepository.InitializeAsync(storage, Options, TimeProvider.System, Ct));

        Assert.Equal(RepositoryErrorKind.NotEmpty, ex.Kind);
        Assert.Equal(["Mis documentos.docx"], storage.FilePaths);
        Assert.Empty(storage.DirectoryPaths);
        Assert.Equal(RepositoryState.NotARepository, (await BackupRepository.ProbeAsync(storage, Ct)).State);
    }

    [Fact]
    public async Task HT04_1_Initialize_refuses_an_existing_repository()
    {
        var storage = new InMemoryRepositoryStorage();
        await BackupRepository.InitializeAsync(storage, Options, TimeProvider.System, Ct);
        var before = storage.GetFileContent("repo.json");

        var ex = await Assert.ThrowsAsync<RepositoryException>(() => BackupRepository.InitializeAsync(storage, Options, TimeProvider.System, Ct));

        Assert.Equal(RepositoryErrorKind.AlreadyInitialized, ex.Kind);
        Assert.Equal(before, storage.GetFileContent("repo.json"));
    }

    [Fact]
    public async Task HT04_1_Initialize_creates_a_missing_destination_folder()
    {
        var storage = new InMemoryRepositoryStorage(createRoot: false);
        Assert.Equal(RepositoryState.Missing, (await BackupRepository.ProbeAsync(storage, Ct)).State);

        await BackupRepository.InitializeAsync(storage, Options, TimeProvider.System, Ct);

        Assert.Equal(RepositoryState.Compatible, (await BackupRepository.ProbeAsync(storage, Ct)).State);
    }

    [Fact]
    public async Task HT04_1_Interrupted_initialization_can_be_repeated()
    {
        // Lo que deja un corte antes de publicar repo.json: carpetas del formato y un temporal.
        var storage = new InMemoryRepositoryStorage();
        foreach (var directory in RepositoryLayout.Directories)
        {
            await storage.CreateDirectoryAsync(directory, Ct);
        }

        storage.SetFileContent("tmp/0123456789abcdef.tmp", "{ \"formatVer"u8);
        Assert.Equal(RepositoryState.Empty, (await BackupRepository.ProbeAsync(storage, Ct)).State);

        await BackupRepository.InitializeAsync(storage, Options, TimeProvider.System, Ct);

        Assert.Equal(RepositoryState.Compatible, (await BackupRepository.ProbeAsync(storage, Ct)).State);
    }

    [Fact]
    public async Task HT04_1_Folder_with_only_layout_directories_but_content_is_not_a_repository()
    {
        var storage = new InMemoryRepositoryStorage();
        await storage.CreateDirectoryAsync("snapshots", Ct);
        storage.SetFileContent("snapshots/algo.json", "{}"u8);

        Assert.Equal(RepositoryState.NotARepository, (await BackupRepository.ProbeAsync(storage, Ct)).State);
    }

    [Fact]
    public async Task HT04_1_Initialize_validates_its_options()
    {
        var storage = new InMemoryRepositoryStorage();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            BackupRepository.InitializeAsync(storage, new RepositoryInitializationOptions { CreatedBy = " " }, TimeProvider.System, Ct));
        Assert.Empty(storage.DirectoryPaths);
    }

    private static async Task<InMemoryRepositoryStorage> RepositoryWithConfigAsync(string repoJson)
    {
        var storage = new InMemoryRepositoryStorage();
        foreach (var directory in RepositoryLayout.Directories)
        {
            await storage.CreateDirectoryAsync(directory, Ct);
        }

        storage.SetFileContent("repo.json", Encoding.UTF8.GetBytes(repoJson));
        return storage;
    }
}
