using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Backbone.Core.Repository;
using Backbone.Core.Repository.Locking;
using Backbone.Core.Repository.Snapshots;
using Backbone.Core.Storage;
using Backbone.Core.Tests.Support;

namespace Backbone.Core.Tests.Repository;

/// <summary>HT-04 criterio 3: manifiestos de §6.3 escritos y leídos en streaming.</summary>
[Trait("Story", "HT-04")]
public sealed class SnapshotTests
{
    private static readonly DateTime Started = new(2026, 11, 15, 2, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Modified = new DateTime(2026, 1, 31, 10, 22, 1, DateTimeKind.Utc).AddTicks(1234567);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SnapshotStart Start(string? parentId = null) => new()
    {
        JobId = "1b2c3d4e-0000-0000-0000-000000000001",
        JobName = "Documentos",
        Host = "PC-SALON",
        StartedUtc = Started,
        ParentId = parentId,
        UsedVss = true,
        Sources = [new("Documentos", @"C:\Users\ana\Documents"), new("Fotos", @"D:\Fotos")],
    };

    private static ContentHash HashOf(string text) => ContentHash.FromBytes(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static readonly SnapshotEntry[] SampleEntries =
    [
        SnapshotEntry.File("Documentos/Facturas/2026/enero.pdf", HashOf("enero"), 182733, Modified, FileAttributes.Archive),
        SnapshotEntry.File("Documentos/año ñ/ç 'comillas' & é #.txt", HashOf("raro"), 0, Modified, FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System),
        SnapshotEntry.File("Fotos/😀 emoji.jpg", HashOf("emoji"), long.MaxValue / 4, new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc), FileAttributes.None),
        SnapshotEntry.Directory("Documentos/Vacía/"),
        SnapshotEntry.Directory("Fotos/"),
    ];

    [Fact]
    public async Task HT04_3_Snapshot_round_trips_header_and_entries()
    {
        var (_, time, repository, repositoryLock) = await CreateAsync();
        time.Advance(TimeSpan.FromSeconds(192));

        var written = await WriteAsync(repository, repositoryLock, SampleEntries, new SnapshotCompletion
        {
            Status = SnapshotStatus.CompletedWithWarnings,
            Stats = new SnapshotStats { Files = 999, Bytes = 999, NewFiles = 12, ChangedFiles = 3, NewObjects = 14, NewBytes = 10485760, Skipped = 1 },
            Warnings = [new("Documentos/bloqueado.pst", "FileLocked", "El archivo está en uso.")],
        });

        Assert.Equal([written.Id], await repository.ListSnapshotIdsAsync(Ct));
        await using var reader = await repository.OpenSnapshotAsync(written.Id, Ct);
        var header = reader.Header;
        Assert.Equal(1, header.FormatVersion);
        Assert.Equal(written.Id, header.Id);
        Assert.StartsWith("20261115T020000Z-", header.Id, StringComparison.Ordinal);
        Assert.Equal("1b2c3d4e-0000-0000-0000-000000000001", header.JobId);
        Assert.Equal("Documentos", header.JobName);
        Assert.Equal("PC-SALON", header.Host);
        Assert.Equal(Started, header.StartedUtc);
        Assert.Equal(Started.AddSeconds(192), header.CompletedUtc);
        Assert.Null(header.ParentId);
        Assert.Equal(SnapshotStatus.CompletedWithWarnings, header.Status);
        Assert.True(header.UsedVss);
        Assert.Equal(Start().Sources, header.Sources);

        // files y bytes los calcula el escritor a partir de las entradas.
        Assert.Equal(new SnapshotStats { Files = 3, Bytes = 182733 + (long.MaxValue / 4), NewFiles = 12, ChangedFiles = 3, NewObjects = 14, NewBytes = 10485760, Skipped = 1 }, header.Stats);
        Assert.Equal([new SnapshotWarning("Documentos/bloqueado.pst", "FileLocked", "El archivo está en uso.")], header.Warnings);
        Assert.Equal(written, header with { Sources = written.Sources, Warnings = written.Warnings });

        var entries = await ReadAllAsync(reader);
        Assert.Equal(SampleEntries, entries);
    }

    [Fact]
    public async Task HT04_3_Manifest_uses_the_property_names_of_section_6_3()
    {
        var (storage, _, repository, repositoryLock) = await CreateAsync();
        var header = await WriteAsync(repository, repositoryLock, SampleEntries, new SnapshotCompletion { Status = SnapshotStatus.Completed }, Start("20261114T020000Z-77fe"));

        var bytes = storage.GetFileContent($"snapshots/{header.Id}.json");
        using var json = JsonDocument.Parse(bytes);
        var root = json.RootElement;
        Assert.Equal(
            ["formatVersion", "id", "jobId", "jobName", "host", "startedUtc", "completedUtc", "parentId", "status", "usedVss", "sources", "stats", "warnings", "entries"],
            root.EnumerateObject().Select(p => p.Name));
        Assert.Equal("2026-11-15T02:00:00Z", root.GetProperty("startedUtc").GetString());
        Assert.Equal("20261114T020000Z-77fe", root.GetProperty("parentId").GetString());
        Assert.Equal("completed", root.GetProperty("status").GetString());
        Assert.Equal(["alias", "path"], root.GetProperty("sources")[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal(
            ["files", "bytes", "newFiles", "changedFiles", "newObjects", "newBytes", "skipped"],
            root.GetProperty("stats").EnumerateObject().Select(p => p.Name));

        var file = root.GetProperty("entries")[0];
        Assert.Equal(["p", "h", "s", "m", "a"], file.EnumerateObject().Select(p => p.Name));
        Assert.Equal("Documentos/Facturas/2026/enero.pdf", file.GetProperty("p").GetString());
        Assert.Equal(HashOf("enero").ToString(), file.GetProperty("h").GetString());
        Assert.Equal(182733, file.GetProperty("s").GetInt64());
        Assert.Equal("2026-01-31T10:22:01.1234567Z", file.GetProperty("m").GetString());
        Assert.Equal(32, file.GetProperty("a").GetInt32());

        var directory = root.GetProperty("entries")[3];
        Assert.Equal(["p", "d"], directory.EnumerateObject().Select(p => p.Name));
        Assert.True(directory.GetProperty("d").GetBoolean());

        // Los nombres no ASCII se guardan legibles (sin \uXXXX).
        Assert.Contains("año ñ", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
    }

    [Fact]
    public async Task HT04_3_Reader_handles_every_buffer_boundary()
    {
        // Con un búfer mínimo, cada token queda partido entre lecturas en algún momento.
        var (_, _, repository, repositoryLock) = await CreateAsync();
        var entries = ManyEntries(300).ToList();
        var header = await WriteAsync(repository, repositoryLock, entries, new SnapshotCompletion
        {
            Status = SnapshotStatus.Completed,
            Warnings = [.. Enumerable.Range(0, 50).Select(i => new SnapshotWarning($"Documentos/w{i}", "FileLocked", new string('x', i * 10)))],
        });

        foreach (var bufferSize in new[] { 1, 7, 16, 33, 100, 1000, 4096 })
        {
            repository.SnapshotReadBufferSize = bufferSize;
            await using var reader = await repository.OpenSnapshotAsync(header.Id, Ct);
            Assert.Equal(50, reader.Header.Warnings.Count);
            Assert.Equal(entries, await ReadAllAsync(reader));
        }
    }

    [Fact]
    public async Task HT04_3_Header_is_read_without_reading_the_entries()
    {
        var (storage, _, repository, repositoryLock) = await CreateAsync();
        var header = await WriteAsync(repository, repositoryLock, ManyEntries(2000), new SnapshotCompletion { Status = SnapshotStatus.Completed });

        // Se estropea el final de las entradas: la cabecera sigue siendo legible (no se ha leído el resto).
        var path = $"snapshots/{header.Id}.json";
        var bytes = storage.GetFileContent(path);
        storage.SetFileContent(path, bytes.AsSpan(0, bytes.Length - 5000));
        repository.SnapshotReadBufferSize = 4096;

        var read = await repository.ReadSnapshotHeaderAsync(header.Id, Ct);
        Assert.Equal(1960, read.Stats.Files); // 2000 entradas, 40 de ellas carpetas

        await using var reader = await repository.OpenSnapshotAsync(header.Id, Ct);
        var ex = await Assert.ThrowsAsync<RepositoryException>(() => ReadAllAsync(reader));
        Assert.Equal(RepositoryErrorKind.Corrupt, ex.Kind);
    }

    [Fact]
    public async Task HT04_3_Entries_can_only_be_enumerated_once()
    {
        var (_, _, repository, repositoryLock) = await CreateAsync();
        var header = await WriteAsync(repository, repositoryLock, SampleEntries, new SnapshotCompletion { Status = SnapshotStatus.Completed });
        await using var reader = await repository.OpenSnapshotAsync(header.Id, Ct);
        await ReadAllAsync(reader);

        await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAllAsync(reader));
    }

    [Fact]
    public async Task HT04_3_Empty_snapshot_round_trips()
    {
        var (_, _, repository, repositoryLock) = await CreateAsync();
        var header = await WriteAsync(repository, repositoryLock, [], new SnapshotCompletion { Status = SnapshotStatus.Completed });

        await using var reader = await repository.OpenSnapshotAsync(header.Id, Ct);
        Assert.Empty(await ReadAllAsync(reader));
        Assert.Equal(0, reader.Header.Stats.Files);
    }

    [Fact]
    public async Task HT04_3_Snapshot_round_trips_on_the_file_system()
    {
        using var directory = new TempDirectory();
        var storage = new FileSystemRepositoryStorage(directory.Path);
        var repository = await BackupRepository.InitializeAsync(storage, new RepositoryInitializationOptions { CreatedBy = "x" }, TimeProvider.System, Ct);
        await using var repositoryLock = await repository.AcquireLockAsync(RepositoryLockType.Shared, new LockOwner("PC", 1), Ct);
        var entries = ManyEntries(5000).ToList();

        var header = await WriteAsync(repository, repositoryLock, entries, new SnapshotCompletion { Status = SnapshotStatus.Completed });

        await using var reader = await repository.OpenSnapshotAsync(header.Id, Ct);
        Assert.Equal(entries, await ReadAllAsync(reader));
        Assert.Empty(Directory.EnumerateFiles(Path.Join(directory.Path, "tmp")));
    }

    // ---------------------------------------------------------------- Escritura: atomicidad y validación

    [Fact]
    public async Task HT04_2_Abandoned_writer_publishes_nothing_and_cleans_tmp()
    {
        var (storage, _, repository, repositoryLock) = await CreateAsync();

        await using (var writer = await repository.CreateSnapshotAsync(Start(), repositoryLock, Ct))
        {
            foreach (var entry in ManyEntries(20000))
            {
                await writer.AddAsync(entry, Ct);
            }
        }

        Assert.Empty(await repository.ListSnapshotIdsAsync(Ct));
        Assert.DoesNotContain(storage.FilePaths, p => p.StartsWith("tmp/", StringComparison.Ordinal) || p.StartsWith("snapshots/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HT04_2_Snapshot_is_not_published_if_the_lock_was_lost()
    {
        var (storage, _, repository, repositoryLock) = await CreateAsync();
        await using var writer = await repository.CreateSnapshotAsync(Start(), repositoryLock, Ct);
        await writer.AddAsync(SampleEntries[0], Ct);
        await storage.DeleteFileAsync("locks/PC-SALON-1.lock", Ct); // otro proceso lo dio por abandonado

        var ex = await Assert.ThrowsAsync<RepositoryException>(() => writer.CompleteAsync(new SnapshotCompletion { Status = SnapshotStatus.Completed }, Ct));

        Assert.Equal(RepositoryErrorKind.LockLost, ex.Kind);
        Assert.Empty(await repository.ListSnapshotIdsAsync(Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.AddAsync(SampleEntries[1], Ct));
        await writer.DisposeAsync();
        Assert.DoesNotContain(storage.FilePaths, p => p.StartsWith("tmp/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HT04_2_Existing_snapshot_is_never_overwritten()
    {
        var (storage, _, repository, repositoryLock) = await CreateAsync();
        await using var writer = await repository.CreateSnapshotAsync(Start(), repositoryLock, Ct);
        await writer.AddAsync(SampleEntries[0], Ct);
        var path = $"snapshots/{writer.Id}.json";
        storage.SetFileContent(path, "otro snapshot"u8);

        var ex = await Assert.ThrowsAsync<RepositoryException>(() => writer.CompleteAsync(new SnapshotCompletion { Status = SnapshotStatus.Completed }, Ct));

        Assert.Equal(RepositoryErrorKind.SnapshotIdConflict, ex.Kind);
        Assert.Equal("otro snapshot"u8.ToArray(), storage.GetFileContent(path));
    }

    [Fact]
    public async Task HT04_3_Writer_rejects_duplicate_paths_and_unknown_aliases()
    {
        var (_, _, repository, repositoryLock) = await CreateAsync();
        await using var writer = await repository.CreateSnapshotAsync(Start(), repositoryLock, Ct);
        await writer.AddAsync(SampleEntries[0], Ct);

        await Assert.ThrowsAsync<ArgumentException>(() => writer.AddAsync(SampleEntries[0], Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => writer.AddAsync(SnapshotEntry.File("Música/a.mp3", HashOf("a"), 1, Modified, FileAttributes.None), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => writer.AddAsync(SnapshotEntry.File("documentos/x.txt", HashOf("a"), 1, Modified, FileAttributes.None), Ct));

        // El escritor sigue siendo utilizable tras rechazar una entrada.
        await writer.AddAsync(SampleEntries[1], Ct);
        var header = await writer.CompleteAsync(new SnapshotCompletion { Status = SnapshotStatus.Completed }, Ct);
        Assert.Equal(2, header.Stats.Files);
        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.AddAsync(SampleEntries[2], Ct));
    }

    public static TheoryData<string> InvalidFilePaths => new()
    {
        "",
        "Documentos",
        "Documentos/",
        "/Documentos/a.txt",
        "Documentos//a.txt",
        "Documentos/../a.txt",
        "Documentos/./a.txt",
        "../Documentos/a.txt",
        "Documentos\\a.txt",
        "C:/Documentos/a.txt",
        "Documentos/a.txt:secreto",
        "Documentos/a?.txt",
        "Documentos/a.",
        "Documentos/a ",
        "Documentos/\u0001",
    };

    [Theory]
    [MemberData(nameof(InvalidFilePaths))]
    public void HT04_3_File_entry_rejects_invalid_paths(string path)
    {
        Assert.Throws<ArgumentException>(() => SnapshotEntry.File(path, HashOf("x"), 1, Modified, FileAttributes.None));
    }

    [Fact]
    public void HT04_3_File_entry_rejects_paths_that_are_not_valid_utf16()
    {
        // Fuera de los datos de la teoría: el serializador de xUnit sustituye las suplentes sueltas.
        Assert.Throws<ArgumentException>(() => SnapshotEntry.File("Documentos/\uD800suelto", HashOf("x"), 1, Modified, FileAttributes.None));
        Assert.Throws<ArgumentException>(() => SnapshotEntry.File("Documentos/suelto\uDC00", HashOf("x"), 1, Modified, FileAttributes.None));
        Assert.Throws<ArgumentException>(() => SnapshotEntry.Directory("Documentos/\uDBFF/"));
        Assert.Equal("Documentos/\uD83D\uDE00.txt", SnapshotEntry.File("Documentos/\uD83D\uDE00.txt", HashOf("x"), 1, Modified, FileAttributes.None).Path);
    }

    [Theory]
    [InlineData("Documentos")]
    [InlineData("/")]
    [InlineData("Documentos//")]
    [InlineData("Documentos/../")]
    public void HT04_3_Directory_entry_rejects_invalid_paths(string path)
    {
        Assert.Throws<ArgumentException>(() => SnapshotEntry.Directory(path));
    }

    [Fact]
    public void HT04_3_File_entry_validates_size_date_and_keeps_only_stored_attributes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SnapshotEntry.File("D/a", HashOf("x"), -1, Modified, FileAttributes.None));
        Assert.Throws<ArgumentException>(() => SnapshotEntry.File("D/a", HashOf("x"), 1, DateTime.SpecifyKind(Modified, DateTimeKind.Local), FileAttributes.None));
        Assert.Throws<ArgumentException>(() => SnapshotEntry.File("D/a", default, 1, Modified, FileAttributes.None));

        var entry = SnapshotEntry.File("D/a", HashOf("x"), 1, Modified, FileAttributes.Archive | FileAttributes.Compressed | FileAttributes.ReparsePoint | FileAttributes.Hidden);
        Assert.Equal(FileAttributes.Archive | FileAttributes.Hidden, entry.Attributes);
        Assert.Equal("D", entry.Alias);
    }

    public static TheoryData<int> InvalidStartCases => [.. Enumerable.Range(0, InvalidStarts.Length)];

    private static readonly SnapshotStart[] InvalidStarts =
    [
        Start() with { JobId = "" },
        Start() with { Host = " " },
        Start() with { StartedUtc = DateTime.SpecifyKind(Started, DateTimeKind.Unspecified) },
        Start() with { ParentId = "ayer" },
        Start() with { Sources = [] },
        Start() with { Sources = [new("Documentos", "C:\\a"), new("documentos", "C:\\b")] },
        Start() with { Sources = [new("Doc/umentos", "C:\\a")] },
        Start() with { Sources = [new("..", "C:\\a")] },
        Start() with { Sources = [new("Documentos", "")] },
        Start() with { JobName = "mal \uDC00" },
    ];

    [Theory]
    [MemberData(nameof(InvalidStartCases))]
    public async Task HT04_3_Writer_validates_the_start_data(int index)
    {
        var (storage, _, repository, repositoryLock) = await CreateAsync();

        await Assert.ThrowsAnyAsync<ArgumentException>(() => repository.CreateSnapshotAsync(InvalidStarts[index], repositoryLock, Ct));
        Assert.DoesNotContain(storage.FilePaths, p => p.StartsWith("tmp/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HT04_3_Warnings_with_invalid_text_are_sanitized()
    {
        var (_, _, repository, repositoryLock) = await CreateAsync();

        var header = await WriteAsync(repository, repositoryLock, [], new SnapshotCompletion
        {
            Status = SnapshotStatus.CompletedWithWarnings,
            Warnings = [new("Documentos/mal\uD800.txt", "InvalidName", "ok \uD83D\uDE00")],
        });

        var read = await repository.ReadSnapshotHeaderAsync(header.Id, Ct);
        Assert.Equal(new SnapshotWarning("Documentos/mal\uFFFD.txt", "InvalidName", "ok \uD83D\uDE00"), Assert.Single(read.Warnings));
    }

    [Fact]
    public async Task HT04_3_List_ignores_foreign_files_and_sorts_by_date()
    {
        var (storage, _, repository, repositoryLock) = await CreateAsync();
        var first = await WriteAsync(repository, repositoryLock, [], new SnapshotCompletion { Status = SnapshotStatus.Completed }, Start() with { StartedUtc = Started.AddDays(1) });
        var second = await WriteAsync(repository, repositoryLock, [], new SnapshotCompletion { Status = SnapshotStatus.Completed }, Start());
        storage.SetFileContent("snapshots/LEEME.txt", "x"u8);
        storage.SetFileContent("snapshots/no-es-un-id.json", "x"u8);
        await storage.CreateDirectoryAsync("snapshots/20261115T020000Z-0000.json", Ct);

        Assert.Equal([second.Id, first.Id], await repository.ListSnapshotIdsAsync(Ct));
    }

    [Fact]
    public async Task HT04_3_Open_missing_snapshot_is_SnapshotNotFound()
    {
        var (_, _, repository, _) = await CreateAsync();

        var ex = await Assert.ThrowsAsync<RepositoryException>(() => repository.OpenSnapshotAsync("20261115T020000Z-abcd", Ct));
        Assert.Equal(RepositoryErrorKind.SnapshotNotFound, ex.Kind);
        await Assert.ThrowsAsync<ArgumentException>(() => repository.OpenSnapshotAsync("../repo", Ct));
    }

    // ---------------------------------------------------------------- Lectura: manifiestos no válidos

    private const string ValidId = "20261115T020000Z-a1b2";

    private static string Manifest(string entries = "", string? header = null, string trailer = "") =>
        "{" + (header ?? """
            "formatVersion": 1, "id": "20261115T020000Z-a1b2", "jobId": "j", "jobName": "Documentos", "host": "PC",
            "startedUtc": "2026-11-15T02:00:00Z", "completedUtc": "2026-11-15T02:03:12Z", "parentId": null,
            "status": "completed", "usedVss": false, "sources": [ { "alias": "Documentos", "path": "C:\\D" } ],
            "stats": { "files": 1, "bytes": 5 }, "warnings": [],
            """) + "\"entries\": [" + entries + "]" + trailer + "}";

    private static readonly string GoodEntry =
        $$"""{ "p": "Documentos/a.txt", "h": "{{new string('a', 64)}}", "s": 5, "m": "2026-01-31T10:22:01Z", "a": 0 }""";

    [Fact]
    public async Task HT04_3_Reader_accepts_a_hand_written_v1_manifest_and_ignores_unknown_fields()
    {
        var entries = $$"""{ "x": [1, {"y": 2}], "p": "Documentos/a.txt", "h": "{{new string('a', 64)}}", "s": 5, "m": "2026-01-31T12:22:01+02:00", "a": 1, "futuro": null }""";
        var manifest = Manifest(entries, header: """
            "extra": { "a": [1, 2, 3] }, "formatVersion": 1, "id": "20261115T020000Z-a1b2", "jobId": "j", "jobName": "Documentos", "host": "PC",
            "startedUtc": "2026-11-15T02:00:00Z", "completedUtc": "2026-11-15T02:03:12.5Z",
            "status": "completed", "usedVss": false, "sources": [ { "alias": "Documentos", "path": "C:\\D", "nuevo": 1 } ],
            "stats": { "files": 1, "bytes": 5, "otra": 3 }, "warnings": [ { "path": "p", "code": "c", "message": "m", "más": true } ],
            """, trailer: """, "despues": { "de": "entries" }""");

        var (storage, _, repository, _) = await CreateAsync();
        storage.SetFileContent($"snapshots/{ValidId}.json", Encoding.UTF8.GetBytes(manifest));

        await using var reader = await repository.OpenSnapshotAsync(ValidId, Ct);
        Assert.Null(reader.Header.ParentId);
        Assert.Equal(new DateTime(2026, 11, 15, 2, 3, 12, 500, DateTimeKind.Utc), reader.Header.CompletedUtc);
        var entry = Assert.Single(await ReadAllAsync(reader));
        Assert.Equal(new DateTime(2026, 1, 31, 10, 22, 1, DateTimeKind.Utc), entry.ModifiedUtc);
        Assert.Equal(DateTimeKind.Utc, entry.ModifiedUtc.Kind);
        Assert.Equal(FileAttributes.ReadOnly, entry.Attributes);
    }

    public static TheoryData<string, string> CorruptManifests
    {
        get
        {
            var hash = new string('a', 64);
            var data = new TheoryData<string, string>
            {
                { "vacío", "" },
                { "no es JSON", "hola" },
                { "lista", "[]" },
                { "truncado", Manifest(GoodEntry)[..^10] },
                { "sin entries", "{ \"formatVersion\": 1 }" },
                { "entries no es lista", Manifest().Replace("\"entries\": []", "\"entries\": {}", StringComparison.Ordinal) },
                { "basura al final", Manifest(GoodEntry) + " {}" },
                { "id distinto", Manifest(GoodEntry).Replace("20261115T020000Z-a1b2", "20261115T020000Z-ffff", StringComparison.Ordinal) },
                { "sin versión", Manifest(GoodEntry).Replace("\"formatVersion\": 1,", "", StringComparison.Ordinal) },
                { "versión 0", Manifest(GoodEntry).Replace("\"formatVersion\": 1", "\"formatVersion\": 0", StringComparison.Ordinal) },
                { "estado desconocido", Manifest(GoodEntry).Replace("\"completed\"", "\"failed\"", StringComparison.Ordinal) },
                { "sin orígenes", Manifest(GoodEntry).Replace("[ { \"alias\": \"Documentos\", \"path\": \"C:\\\\D\" } ]", "[]", StringComparison.Ordinal) },
                { "alias no válido", Manifest(GoodEntry).Replace("\"alias\": \"Documentos\"", "\"alias\": \"..\"", StringComparison.Ordinal) },
                { "stats no cuadran", Manifest(GoodEntry).Replace("\"files\": 1", "\"files\": 2", StringComparison.Ordinal) },
                { "bytes no cuadran", Manifest(GoodEntry).Replace("\"bytes\": 5", "\"bytes\": 6", StringComparison.Ordinal) },
                { "stats negativas", Manifest(GoodEntry).Replace("\"bytes\": 5", "\"bytes\": 5, \"skipped\": -1", StringComparison.Ordinal) },
                { "fecha no válida", Manifest(GoodEntry).Replace("2026-11-15T02:03:12Z", "ayer", StringComparison.Ordinal) },
                { "usedVss no booleano", Manifest(GoodEntry).Replace("\"usedVss\": false", "\"usedVss\": \"no\"", StringComparison.Ordinal) },
                { "parentId no válido", Manifest(GoodEntry).Replace("\"parentId\": null", "\"parentId\": \"x\"", StringComparison.Ordinal) },
                { "entrada no es objeto", Manifest("1") },
                { "ruta con ..", Manifest(GoodEntry.Replace("Documentos/a.txt", "Documentos/../../Windows/a.txt", StringComparison.Ordinal)) },
                { "ruta absoluta", Manifest(GoodEntry.Replace("Documentos/a.txt", "C:/Windows/a.txt", StringComparison.Ordinal)) },
                { "ruta con \\", Manifest(GoodEntry.Replace("Documentos/a.txt", "Documentos\\\\..\\\\a.txt", StringComparison.Ordinal)) },
                { "origen desconocido", Manifest(GoodEntry.Replace("Documentos/a.txt", "Otro/a.txt", StringComparison.Ordinal)) },
                { "ruta repetida", Manifest(GoodEntry + "," + GoodEntry).Replace("\"files\": 1, \"bytes\": 5", "\"files\": 2, \"bytes\": 10", StringComparison.Ordinal) },
                { "hash en mayúsculas", Manifest(GoodEntry.Replace(hash, new string('A', 64), StringComparison.Ordinal)) },
                { "hash corto", Manifest(GoodEntry.Replace(hash, "abc", StringComparison.Ordinal)) },
                { "sin hash", Manifest(GoodEntry.Replace($"\"h\": \"{hash}\",", "", StringComparison.Ordinal)) },
                { "tamaño negativo", Manifest(GoodEntry.Replace("\"s\": 5", "\"s\": -5", StringComparison.Ordinal)) },
                { "tamaño no numérico", Manifest(GoodEntry.Replace("\"s\": 5", "\"s\": \"5\"", StringComparison.Ordinal)) },
                { "sin fecha", Manifest(GoodEntry.Replace("\"m\": \"2026-01-31T10:22:01Z\",", "", StringComparison.Ordinal)) },
                { "atributo no guardado", Manifest(GoodEntry.Replace("\"a\": 0", "\"a\": 1024", StringComparison.Ordinal)) },
                { "carpeta sin barra", Manifest("{ \"p\": \"Documentos/x\", \"d\": true }").Replace("\"files\": 1, \"bytes\": 5", "\"files\": 0, \"bytes\": 0", StringComparison.Ordinal) },
                { "archivo con barra final", Manifest(GoodEntry.Replace("Documentos/a.txt", "Documentos/a/", StringComparison.Ordinal)) },
                { "demasiado anidado", Manifest(GoodEntry.Replace("\"a\": 0", "\"a\": 0, \"z\": " + new string('[', 40) + new string(']', 40), StringComparison.Ordinal)) },
            };
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(CorruptManifests))]
    public async Task HT04_3_Reader_rejects_invalid_manifests_as_corrupt(string reason, string manifest)
    {
        var (storage, _, repository, _) = await CreateAsync();
        storage.SetFileContent($"snapshots/{ValidId}.json", Encoding.UTF8.GetBytes(manifest));

        var ex = await Assert.ThrowsAsync<RepositoryException>(async () =>
        {
            await using var reader = await repository.OpenSnapshotAsync(ValidId, Ct);
            await ReadAllAsync(reader);
        });

        Assert.True(ex.Kind == RepositoryErrorKind.Corrupt, $"{reason}: {ex.Kind} {ex.Message}");
    }

    [Theory]
    [InlineData(2)]
    [InlineData(99)]
    [Trait("Requirement", "RNF-14")]
    public async Task HT04_1_Snapshot_of_a_higher_format_is_RepositoryIncompatible(int version)
    {
        var (storage, _, repository, _) = await CreateAsync();
        var manifest = $$"""{ "formatVersion": {{version}}, "chunks": { "algo": "nuevo" }, "entries": 7 }""";
        storage.SetFileContent($"snapshots/{ValidId}.json", Encoding.UTF8.GetBytes(manifest));

        var ex = await Assert.ThrowsAsync<RepositoryException>(() => repository.OpenSnapshotAsync(ValidId, Ct));

        Assert.Equal(RepositoryErrorKind.Incompatible, ex.Kind);
    }

    [Fact]
    public void HT04_3_Snapshot_ids_follow_the_format_and_sort_by_date()
    {
        var id = SnapshotId.Create(Started);

        Assert.Matches("^20261115T020000Z-[0-9a-f]{4}$", id);
        Assert.True(SnapshotId.TryGetTimestamp(id, out var timestamp));
        Assert.Equal(Started, timestamp);
        Assert.True(string.CompareOrdinal(SnapshotId.Create(Started), SnapshotId.Create(Started.AddSeconds(1))) < 0);

        foreach (var invalid in new[] { "", "20261115T020000Z", "20261115T020000Z-ABCD", "20261315T020000Z-abcd", "20261115T020000Z_abcd", "2026111ST020000Z-abcd", "20261115T020000Z-abcde" })
        {
            Assert.False(SnapshotId.IsValid(invalid), invalid);
        }

        Assert.Throws<ArgumentException>(() => SnapshotId.Create(DateTime.SpecifyKind(Started, DateTimeKind.Local)));
    }

    // ---------------------------------------------------------------- Auxiliares

    internal static IEnumerable<SnapshotEntry> ManyEntries(int count)
    {
        for (var i = 0; i < count; i++)
        {
            var alias = i % 3 == 0 ? "Fotos" : "Documentos";
            yield return i % 50 == 49
                ? SnapshotEntry.Directory($"{alias}/carpeta {i}/vacía/")
                : SnapshotEntry.File(
                    $"{alias}/carpeta {i / 100}/subcarpeta ñ {i % 7}/archivo número {i}.txt",
                    ContentHash.FromBytes(SHA256.HashData(BitConverter.GetBytes(i))),
                    i * 1000L,
                    Modified.AddSeconds(i),
                    (FileAttributes)(i % 2 == 0 ? 32 : 33));
        }
    }

    private static async Task<(InMemoryRepositoryStorage Storage, ManualTimeProvider Time, BackupRepository Repository, RepositoryLock Lock)> CreateAsync()
    {
        var storage = new InMemoryRepositoryStorage();
        var time = new ManualTimeProvider(new DateTimeOffset(Started));
        var repository = await BackupRepository.InitializeAsync(storage, new RepositoryInitializationOptions { CreatedBy = "x" }, time, Ct);
        var repositoryLock = await repository.AcquireLockAsync(RepositoryLockType.Shared, new LockOwner("PC-SALON", 1), Ct);
        return (storage, time, repository, repositoryLock);
    }

    private static async Task<SnapshotHeader> WriteAsync(
        BackupRepository repository,
        RepositoryLock repositoryLock,
        IEnumerable<SnapshotEntry> entries,
        SnapshotCompletion completion,
        SnapshotStart? start = null)
    {
        await using var writer = await repository.CreateSnapshotAsync(start ?? Start(), repositoryLock, Ct);
        foreach (var entry in entries)
        {
            await writer.AddAsync(entry, Ct);
        }

        return await writer.CompleteAsync(completion, Ct);
    }

    private static async Task<List<SnapshotEntry>> ReadAllAsync(SnapshotReader reader)
    {
        var list = new List<SnapshotEntry>();
        await foreach (var entry in reader.ReadEntriesAsync(Ct))
        {
            list.Add(entry);
        }

        return list;
    }
}
