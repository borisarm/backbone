using System.Globalization;
using System.Text;
using Backbone.Core.Repository;
using Backbone.Core.Repository.Locking;
using Backbone.Core.Repository.Snapshots;
using Backbone.Core.Storage;
using Backbone.Core.Tests.Support;
using SnapshotStatus = Backbone.Core.Repository.Snapshots.SnapshotStatus;

namespace Backbone.Core.Tests.Repository;

/// <summary>
/// Reglas de §6.2-§6.4 que las demás pruebas no cubrían (revisión de QA de HT-04): límites exactos de <c>repo.json</c>
/// y de los bloqueos, fechas con desfase, carrera en la inicialización, unicidad de alias y rutas, cabecera tras
/// <c>entries</c> y saneado de los textos leídos del repositorio en los mensajes de error.
/// </summary>
[Trait("Story", "HT-04")]
public sealed class SpecRulesTests
{
    private const string ValidId = "20261115T020000Z-a1b2";
    private static readonly DateTime Started = new(2026, 11, 15, 2, 0, 0, DateTimeKind.Utc);
    private static readonly RepositoryInitializationOptions Options = new() { CreatedBy = "x" };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ================================================================== repo.json (§6.2)

    private static string Config(string createdBy = "x", string createdUtc = "2026-11-15T02:00:00Z", string formatVersion = "1", string hash = "sha256") =>
        $$"""{ "formatVersion": {{formatVersion}}, "repositoryId": "8f6c2e1a-1111-2222-3333-444455556666", "createdUtc": "{{createdUtc}}", "createdBy": "{{createdBy}}", "hash": "{{hash}}", "compression": "none", "encryption": "none" }""";

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

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task Repo_json_of_exactly_64_KiB_is_accepted_and_one_byte_more_is_corrupt(int extra, bool accepted)
    {
        var overhead = Encoding.UTF8.GetByteCount(Config(createdBy: string.Empty));
        var storage = await RepositoryWithConfigAsync(Config(createdBy: new string('a', 65536 - overhead + extra)));
        Assert.Equal(65536 + extra, storage.GetFileContent("repo.json").Length);

        if (accepted)
        {
            var repository = await BackupRepository.OpenAsync(storage, TimeProvider.System, Ct);
            Assert.Equal(65536 - overhead, repository.Configuration.CreatedBy.Length);
        }
        else
        {
            var ex = await Assert.ThrowsAsync<RepositoryException>(() => BackupRepository.OpenAsync(storage, TimeProvider.System, Ct));
            Assert.Equal(RepositoryErrorKind.Corrupt, ex.Kind);
        }
    }

    [Fact]
    public async Task Repo_json_createdUtc_with_an_offset_is_converted_to_utc()
    {
        var storage = await RepositoryWithConfigAsync(Config(createdUtc: "2026-11-15T03:30:00+01:00"));

        var repository = await BackupRepository.OpenAsync(storage, TimeProvider.System, Ct);

        Assert.Equal(new DateTime(2026, 11, 15, 2, 30, 0, DateTimeKind.Utc), repository.Configuration.CreatedUtc);
        Assert.Equal(DateTimeKind.Utc, repository.Configuration.CreatedUtc.Kind);
    }

    [Theory]
    [InlineData("4294967296")]
    [InlineData("9223372036854775807")]
    [InlineData("99999999999999999999999")]
    public async Task A_formatVersion_beyond_int32_is_a_higher_version_not_a_crash(string version)
    {
        var storage = await RepositoryWithConfigAsync(Config(formatVersion: version));

        var ex = await Assert.ThrowsAsync<RepositoryException>(() => BackupRepository.OpenAsync(storage, TimeProvider.System, Ct));

        Assert.Equal(RepositoryErrorKind.Incompatible, ex.Kind);
        Assert.Equal(RepositoryState.Incompatible, (await BackupRepository.ProbeAsync(storage, Ct)).State);
    }

    [Fact]
    public async Task Hostile_values_in_repo_json_are_sanitized_in_the_incompatibility_message()
    {
        var hostile = "x\\r\\nFALSA LINEA \\u2028 \\u202e" + new string('z', 5000);
        var storage = await RepositoryWithConfigAsync(Config(hash: hostile));

        var ex = await Assert.ThrowsAsync<RepositoryException>(() => BackupRepository.OpenAsync(storage, TimeProvider.System, Ct));
        var probe = await BackupRepository.ProbeAsync(storage, Ct);

        Assert.Equal(RepositoryErrorKind.Incompatible, ex.Kind);
        AssertSanitized(ex.Message);
        Assert.NotNull(probe.Detail);
        AssertSanitized(probe.Detail);
    }

    /// <summary>Otro proceso publica repo.json entre el sondeo y la publicación: la inicialización falla y no lo pisa.</summary>
    [Fact]
    public async Task Initialization_that_loses_the_race_fails_as_already_initialized_and_keeps_the_winner_repo_json()
    {
        var inner = new InMemoryRepositoryStorage();
        var winner = Encoding.UTF8.GetBytes(Config(createdBy: "ganador"));
        var storage = new RacingStorage(inner, () => inner.SetFileContent("repo.json", winner));

        var ex = await Assert.ThrowsAsync<RepositoryException>(() =>
            BackupRepository.InitializeAsync(storage, Options, TimeProvider.System, Ct));

        Assert.Equal(RepositoryErrorKind.AlreadyInitialized, ex.Kind);
        Assert.Equal(winner, inner.GetFileContent("repo.json"));
        Assert.DoesNotContain(inner.FilePaths, p => p.StartsWith("tmp/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_repository_in_a_missing_destination_has_state_Missing_and_probing_writes_nothing()
    {
        var storage = new InMemoryRepositoryStorage(createRoot: false);

        var probe = await BackupRepository.ProbeAsync(storage, Ct);

        Assert.Equal(RepositoryState.Missing, probe.State);
        Assert.Empty(storage.FilePaths);
        Assert.Empty(storage.DirectoryPaths);
    }

    [Fact]
    public async Task Probing_a_folder_with_foreign_files_and_no_repo_json_reports_NotARepository_and_writes_nothing()
    {
        var storage = new InMemoryRepositoryStorage();
        await storage.CreateDirectoryAsync("fotos", Ct);
        storage.SetFileContent("fotos/a.jpg", "x"u8);
        var before = storage.FilePaths.Order(StringComparer.Ordinal).ToList();
        var directoriesBefore = storage.DirectoryPaths.Order(StringComparer.Ordinal).ToList();

        var probe = await BackupRepository.ProbeAsync(storage, Ct);

        Assert.Equal(RepositoryState.NotARepository, probe.State);
        Assert.Equal(before, storage.FilePaths.Order(StringComparer.Ordinal));
        Assert.Equal(directoriesBefore, storage.DirectoryPaths.Order(StringComparer.Ordinal));
    }

    // ================================================================== Bloqueos (§6.4.4)

    [Fact]
    public async Task A_lock_file_over_64_KiB_is_unreadable_and_therefore_a_live_exclusive_lock()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(Started));
        var storage = new InMemoryRepositoryStorage(time);
        var repository = await BackupRepository.InitializeAsync(storage, Options, time, Ct);
        var padded = $$"""{ "host": "PC-X", "pid": 7, "type": "shared", "heartbeatUtc": "2026-11-15T02:00:00Z", "pad": "{{new string('a', 70_000)}}" }""";
        storage.SetFileContent("locks/PC-X-7.lock", Encoding.UTF8.GetBytes(padded));

        var info = Assert.Single(await repository.ListLocksAsync(Ct));
        var ex = await Assert.ThrowsAsync<RepositoryException>(() => repository.AcquireLockAsync(RepositoryLockType.Shared, new LockOwner("PC-SALON", 1), Ct));

        Assert.False(info.IsReadable);
        Assert.Equal(RepositoryErrorKind.Locked, ex.Kind);
        AssertSanitized(ex.Message);
    }

    [Fact]
    public async Task A_lock_file_of_exactly_64_KiB_is_still_read()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(Started));
        var storage = new InMemoryRepositoryStorage(time);
        var repository = await BackupRepository.InitializeAsync(storage, Options, time, Ct);
        var shell = """{ "host": "PC-X", "pid": 7, "type": "shared", "heartbeatUtc": "2026-11-15T02:00:00Z", "pad": "" }""";
        var padded = shell.Replace("\"pad\": \"\"", $"\"pad\": \"{new string('a', 65536 - Encoding.UTF8.GetByteCount(shell))}\"", StringComparison.Ordinal);
        Assert.Equal(65536, Encoding.UTF8.GetByteCount(padded));
        storage.SetFileContent("locks/PC-X-7.lock", Encoding.UTF8.GetBytes(padded));

        var info = Assert.Single(await repository.ListLocksAsync(Ct));

        Assert.True(info.IsReadable);
    }

    // ================================================================== Manifiestos (§6.3)

    private static SnapshotStart Start(params (string Alias, string Path)[] sources) => new()
    {
        JobId = "1b2c3d4e-0000-0000-0000-000000000001",
        JobName = "Documentos",
        Host = "PC-SALON",
        StartedUtc = Started,
        UsedVss = false,
        Sources = sources.Length == 0 ? [new("Documentos", @"C:\D")] : sources.Select(s => new SnapshotSource(s.Alias, s.Path)).ToList(),
    };

    private static SnapshotEntry File(string path) =>
        SnapshotEntry.File(path, ContentHash.Parse(new string('a', 64)), 5, new DateTime(2026, 1, 31, 10, 22, 1, DateTimeKind.Utc), FileAttributes.None);

    private static async Task<(InMemoryRepositoryStorage Storage, BackupRepository Repository, RepositoryLock Lock)> CreateAsync()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(Started));
        var storage = new InMemoryRepositoryStorage(time);
        var repository = await BackupRepository.InitializeAsync(storage, Options, time, Ct);
        var repositoryLock = await repository.AcquireLockAsync(RepositoryLockType.Shared, new LockOwner("PC-SALON", 1), Ct);
        return (storage, repository, repositoryLock);
    }

    [Fact]
    public async Task Source_aliases_are_unique_ignoring_case_in_the_writer()
    {
        var (_, repository, repositoryLock) = await CreateAsync();

        var ex = await Record.ExceptionAsync(() => repository.CreateSnapshotAsync(Start(("Docs", @"C:\A"), ("docs", @"C:\B")), repositoryLock, Ct));

        Assert.IsAssignableFrom<ArgumentException>(ex);
    }

    [Fact]
    public async Task Source_aliases_that_only_differ_in_case_make_a_manifest_corrupt()
    {
        var manifest = Manifest(sources: """[ { "alias": "Docs", "path": "C:\\A" }, { "alias": "docs", "path": "C:\\B" } ]""", entries: Entry("Docs/a.txt"));

        var ex = await ReadFailureAsync(manifest);

        Assert.Equal(RepositoryErrorKind.Corrupt, ex.Kind);
    }

    [Fact]
    public async Task Paths_that_only_differ_in_case_are_two_entries_in_writer_and_reader()
    {
        var (storage, repository, repositoryLock) = await CreateAsync();
        await using (var writer = await repository.CreateSnapshotAsync(Start(), repositoryLock, Ct))
        {
            await writer.AddAsync(File("Documentos/A.txt"), Ct);
            await writer.AddAsync(File("Documentos/a.txt"), Ct);
            await writer.CompleteAsync(new SnapshotCompletion { Status = SnapshotStatus.Completed }, Ct);
        }

        var id = Assert.Single(await repository.ListSnapshotIdsAsync(Ct));
        await using var reader = await repository.OpenSnapshotAsync(id, Ct);
        var paths = new List<string>();
        await foreach (var entry in reader.ReadEntriesAsync(Ct))
        {
            paths.Add(entry.Path);
        }

        Assert.Equal(["Documentos/A.txt", "Documentos/a.txt"], paths.Order(StringComparer.Ordinal));
        Assert.Contains(storage.FilePaths, p => p == $"snapshots/{id}.json");
    }

    [Fact]
    public async Task A_required_header_field_placed_after_entries_is_not_found_and_the_manifest_is_corrupt()
    {
        // El lector no busca cabecera detrás de entries (§6.3 "Orden"): el host detrás de entries no cuenta.
        var manifest = Manifest(entries: Entry("Documentos/a.txt"), host: null, trailer: ",\"host\":\"PC\"");

        var ex = await ReadFailureAsync(manifest);

        Assert.Equal(RepositoryErrorKind.Corrupt, ex.Kind);
    }

    [Fact]
    public async Task Stats_declared_in_the_header_must_match_what_was_read_even_with_directories_mixed_in()
    {
        // Las carpetas vacías no cuentan como archivos ni suman bytes.
        var manifest = Manifest(entries: Entry("Documentos/a.txt") + ",{\"p\":\"Documentos/vacia/\",\"d\":true}", files: 1, bytes: 5);

        var (storage, repository, _) = await CreateAsync();
        storage.SetFileContent($"snapshots/{ValidId}.json", Encoding.UTF8.GetBytes(manifest));
        await using var reader = await repository.OpenSnapshotAsync(ValidId, Ct);
        var count = 0;
        await foreach (var _ in reader.ReadEntriesAsync(Ct))
        {
            count++;
        }

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task The_manifest_is_compact_header_on_the_first_line_one_entry_per_line_and_unicode_unescaped()
    {
        var (storage, repository, repositoryLock) = await CreateAsync();
        await using (var writer = await repository.CreateSnapshotAsync(Start(), repositoryLock, Ct))
        {
            await writer.AddAsync(File("Documentos/año ñ 日本.txt"), Ct);
            await writer.AddAsync(SnapshotEntry.Directory("Documentos/vacía/"), Ct);
            await writer.CompleteAsync(new SnapshotCompletion { Status = SnapshotStatus.Completed }, Ct);
        }

        var id = Assert.Single(await repository.ListSnapshotIdsAsync(Ct));
        var bytes = storage.GetFileContent($"snapshots/{id}.json");
        var text = new UTF8Encoding(false, true).GetString(bytes);
        var lines = text.Split('\n');

        Assert.NotEqual(0xEF, bytes[0]);
        Assert.EndsWith("\"entries\":[", lines[0], StringComparison.Ordinal);
        Assert.DoesNotContain("\n", lines[0], StringComparison.Ordinal);
        Assert.Contains("año ñ 日本.txt", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("{\"p\":\"Documentos/año", lines[1], StringComparison.Ordinal);
        Assert.Contains("\"m\":\"2026-01-31T10:22:01.0000000Z\"", lines[1], StringComparison.Ordinal);
        Assert.Contains("\"startedUtc\":\"2026-11-15T02:00:00Z\"", lines[0], StringComparison.Ordinal);
        Assert.Equal("{\"p\":\"Documentos/vacía/\",\"d\":true}", lines[2].TrimEnd(','));
        Assert.Equal("]}", lines[^1].TrimEnd('\r'));
    }

    [Fact]
    public async Task The_writer_always_picks_an_id_that_is_free_and_canonical()
    {
        var (storage, repository, repositoryLock) = await CreateAsync();
        var ids = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < 20; i++)
        {
            await using var writer = await repository.CreateSnapshotAsync(Start(), repositoryLock, Ct);
            Assert.True(ids.Add(writer.Id), $"El identificador {writer.Id} se repitió.");
            Assert.Matches("^20261115T020000Z-[0-9a-f]{4}$", writer.Id);
            Assert.DoesNotContain($"snapshots/{writer.Id}.json", storage.FilePaths);

            // Ocupar el nombre para la siguiente: nunca debe elegir uno ocupado.
            storage.SetFileContent($"snapshots/{writer.Id}.json", "{}"u8);
        }
    }

    [Theory]
    [InlineData("status")]
    [InlineData("host")]
    [InlineData("entry path")]
    [InlineData("entry source")]
    [InlineData("duplicate")]
    [InlineData("alias")]
    public async Task Hostile_values_read_from_a_manifest_are_sanitized_in_the_error_message(string where)
    {
        var hostile = "a\\r\\nFALSA LINEA \\u2028 \\u202e" + new string('z', 3000);
        var manifest = where switch
        {
            "status" => Manifest(entries: Entry("Documentos/a.txt"), status: hostile),
            "host" => Manifest(entries: Entry("Documentos/a.txt"), host: hostile),
            "entry path" => Manifest(entries: Entry("Documentos/" + hostile)),
            "entry source" => Manifest(entries: Entry(hostile + "/a.txt")),
            "duplicate" => Manifest(entries: Entry("Documentos/" + hostile.Replace("\\r\\n", "_", StringComparison.Ordinal)) + "," + Entry("Documentos/" + hostile.Replace("\\r\\n", "_", StringComparison.Ordinal)), files: 2, bytes: 10),
            "alias" => Manifest(sources: $$"""[ { "alias": "{{hostile}}", "path": "C:\\A" } ]""", entries: Entry("Documentos/a.txt")),
            _ => throw new ArgumentOutOfRangeException(nameof(where)),
        };

        var ex = await ReadFailureAsync(manifest);

        Assert.Equal(RepositoryErrorKind.Corrupt, ex.Kind);
        AssertSanitized(ex.Message);
    }

    // ================================================================== Auxiliares

    /// <summary>Un mensaje con contenido del repositorio: sin controles ni separadores de línea y acotado.</summary>
    private static void AssertSanitized(string? message)
    {
        Assert.NotNull(message);
        Assert.False(message.Any(c => char.IsControl(c) || c is '\u2028' or '\u2029' or '\u202e'), "Hay controles sin escapar.");
        Assert.True(message.Length < 1500, $"Mensaje demasiado largo ({message.Length}): {message[..Math.Min(200, message.Length)]}");
        Assert.DoesNotContain(new string('z', 300), message, StringComparison.Ordinal);
    }

    private static string Entry(string path) =>
        $$"""{"p":{{System.Text.Json.JsonSerializer.Serialize(path)}},"h":"{{new string('a', 64)}}","s":5,"m":"2026-01-31T10:22:01Z","a":0}""";

    private static string Manifest(
        string? sources = null,
        string entries = "",
        string? host = "PC",
        string status = "completed",
        string trailer = "",
        long files = 1,
        long bytes = 5)
    {
        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"{{\"formatVersion\":1,\"id\":\"{ValidId}\",\"jobId\":\"j\",\"jobName\":\"Documentos\",");
        if (host is not null)
        {
            builder.Append(CultureInfo.InvariantCulture, $"\"host\":\"{host}\",");
        }

        builder.Append("\"startedUtc\":\"2026-11-15T02:00:00Z\",\"completedUtc\":\"2026-11-15T02:03:12Z\",\"parentId\":null,");
        builder.Append(CultureInfo.InvariantCulture, $"\"status\":\"{status}\",\"usedVss\":false,");
        builder.Append("\"sources\":").Append(sources ?? """[{"alias":"Documentos","path":"C:\\D"}]""").Append(',');
        builder.Append(CultureInfo.InvariantCulture, $"\"stats\":{{\"files\":{files},\"bytes\":{bytes}}},\"warnings\":[],");
        builder.Append("\"entries\":[").Append(entries).Append(']').Append(trailer).Append('}');
        return builder.ToString();
    }

    private static async Task<RepositoryException> ReadFailureAsync(string manifest)
    {
        var (storage, repository, _) = await CreateAsync();
        storage.SetFileContent($"snapshots/{ValidId}.json", Encoding.UTF8.GetBytes(manifest));
        return await Assert.ThrowsAsync<RepositoryException>(async () =>
        {
            await using var reader = await repository.OpenSnapshotAsync(ValidId, Ct);
            await foreach (var _ in reader.ReadEntriesAsync(Ct))
            {
            }
        });
    }

    /// <summary>Ejecuta una acción justo antes de publicar <c>repo.json</c>, como si otro proceso se hubiera adelantado.</summary>
    private sealed class RacingStorage(InMemoryRepositoryStorage inner, Action beforePublishingConfig) : IRepositoryStorage
    {
        public Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken) => inner.OpenReadAsync(path, cancellationToken);

        public Task<Stream> CreateNewAsync(string path, CancellationToken cancellationToken) => inner.CreateNewAsync(path, cancellationToken);

        public Task<bool> TryMoveAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken)
        {
            if (destinationPath == "repo.json")
            {
                beforePublishingConfig();
            }

            return inner.TryMoveAsync(sourcePath, destinationPath, overwrite, cancellationToken);
        }

        public Task DeleteFileAsync(string path, CancellationToken cancellationToken) => inner.DeleteFileAsync(path, cancellationToken);

        public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken) => inner.CreateDirectoryAsync(path, cancellationToken);

        public Task<bool> DirectoryExistsAsync(string path, CancellationToken cancellationToken) => inner.DirectoryExistsAsync(path, cancellationToken);

        public Task<StorageEntry?> GetFileInfoAsync(string path, CancellationToken cancellationToken) => inner.GetFileInfoAsync(path, cancellationToken);

        public IAsyncEnumerable<StorageEntry> EnumerateAsync(string directoryPath, CancellationToken cancellationToken) =>
            inner.EnumerateAsync(directoryPath, cancellationToken);
    }
}
