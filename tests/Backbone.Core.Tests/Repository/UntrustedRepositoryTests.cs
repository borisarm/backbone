using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Backbone.Core.Repository;
using Backbone.Core.Repository.Locking;
using Backbone.Core.Storage;
using Backbone.Core.Tests.Support;

namespace Backbone.Core.Tests.Repository;

/// <summary>
/// Contenido del repositorio no confiable (§6.1, ADR-11; hallazgos M-3, BAJA-1, BAJA-3 y las informativas de la revisión de
/// seguridad): textos saneados, nombres del NAS que Backbone no admite y puntos de reanálisis.
/// </summary>
[Trait("Story", "HT-04")]
[Trait("Requirement", "ADR-11")]
public sealed class UntrustedRepositoryTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 15, 2, 0, 0, DateTimeKind.Utc);
    private static readonly LockOwner Salon = new("PC-SALON", 1);
    private readonly TempDirectory directory = new();
    private readonly List<string> junctions = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Borra primero los junctions (sin seguirlos) y después la carpeta temporal.</summary>
    public void Dispose()
    {
        foreach (var junction in junctions)
        {
            try
            {
                Directory.Delete(junction);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Lo limpiará el sistema.
            }
        }

        try
        {
            directory.Dispose();
        }
        catch (UnauthorizedAccessException)
        {
            // Lo limpiará el sistema.
        }
    }

    // ================================================================== Untrusted.Describe

    [Fact]
    public void Describe_handles_null_and_keeps_ordinary_text()
    {
        Assert.Equal("(null)", Untrusted.Describe(null));
        Assert.Equal("", Untrusted.Describe(""));
        Assert.Equal("Documentos/año ñ 😀.txt", Untrusted.Describe("Documentos/año ñ 😀.txt"));
    }

    [Fact]
    public void Describe_truncates_to_200_characters_with_an_ellipsis()
    {
        Assert.Equal(new string('a', 200), Untrusted.Describe(new string('a', 200)));
        Assert.Equal(new string('a', 200) + "…", Untrusted.Describe(new string('a', 201)));
        Assert.Equal(new string('a', 200) + "…", Untrusted.Describe(new string('a', 1_000_000)));
    }

    [Fact]
    public void Describe_escapes_controls_lone_surrogates_line_separators_and_bidi_controls()
    {
        var text = string.Concat(
            "a", ((char)10).ToString(), "b", ((char)13).ToString(), ((char)0).ToString(), ((char)0x7F).ToString(),
            ((char)0xD800).ToString(), "c", ((char)0xDC00).ToString(),
            ((char)0x2028).ToString(), ((char)0x2029).ToString(),
            ((char)0x202A).ToString(), ((char)0x202E).ToString(), ((char)0x2066).ToString(), ((char)0x2069).ToString(),
            ((char)0x200F).ToString());

        var described = Untrusted.Describe(text);

        Assert.Equal(
            "a" + Esc("000a") + "b" + Esc("000d") + Esc("0000") + Esc("007f") + Esc("d800") + "c" + Esc("dc00")
                + Esc("2028") + Esc("2029") + Esc("202a") + Esc("202e") + Esc("2066") + Esc("2069") + Esc("200f"),
            described);
        Assert.DoesNotContain(described, c => char.IsControl(c) || char.IsSurrogate(c));
    }

    [Fact]
    public void Describe_counts_escaped_characters_against_the_limit()
    {
        var described = Untrusted.Describe(new string((char)1, 500));

        Assert.Equal((200 * 6) + 1, described.Length);
        Assert.EndsWith("…", described, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0x200B, "200b")]
    [InlineData(0xFEFF, "feff")]
    [InlineData(0x00AD, "00ad")]
    [InlineData(0x061C, "061c")]
    [InlineData(0x2060, "2060")]
    [InlineData(0xE0001, "db40" + "dc01")]
    [InlineData(0xE007F, "db40" + "dc7f")]
    public void N4_Describe_escapes_every_format_character_by_code_point(int codePoint, string expectedUnits)
    {
        var cf = char.ConvertFromUtf32(codePoint);

        var described = Untrusted.Describe("a" + cf + "b");

        var expected = expectedUnits.Length == 4 ? Esc(expectedUnits) : Esc(expectedUnits[..4]) + Esc(expectedUnits[4..]);
        Assert.Equal("a" + expected + "b", described);
        Assert.DoesNotContain(cf, described, StringComparison.Ordinal);
    }

    [Fact]
    public void N4_Describe_keeps_non_format_characters_outside_the_bmp()
    {
        // U+1F600 (So) y U+20000 (Lo) no son Cf: se conservan como par de suplentes.
        Assert.Equal("😀𠀀", Untrusted.Describe("😀𠀀"));
    }

    [Fact]
    public void N4_An_escaped_supplementary_format_character_counts_as_one_character()
    {
        var described = Untrusted.Describe(string.Concat(Enumerable.Repeat(char.ConvertFromUtf32(0xE0001), 300)));

        Assert.Equal((200 * 12) + 1, described.Length);
        Assert.EndsWith("…", described, StringComparison.Ordinal);
    }

    private static string Esc(string hex) => "\\" + "u" + hex;

    // ================================================================== repo.json (BAJA-3)

    [Fact]
    public async Task BAJA3_Lone_surrogates_in_createdBy_are_replaced_with_U_FFFD()
    {
        var storage = await InitializedStorageAsync();
        storage.SetFileContent("repo.json", Encoding.UTF8.GetBytes(Config(createdBy: "Backbone " + Esc("d800") + " en PC " + Esc("d83d") + Esc("de00") + " " + Esc("0041"))));

        var probe = await BackupRepository.ProbeAsync(storage, Ct);

        Assert.Equal(RepositoryState.Compatible, probe.State);
        Assert.Equal("Backbone � en PC 😀 A", probe.Configuration?.CreatedBy);
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("repositoryId")]
    [InlineData("compression")]
    public async Task BAJA3_Lone_surrogates_in_other_repo_json_fields_are_corrupt_not_a_crash(string field)
    {
        var storage = await InitializedStorageAsync();
        var config = field switch
        {
            "hash" => Config(hash: "sha256" + Esc("dc00")),
            "repositoryId" => Config(repositoryId: Esc("d800")),
            _ => Config(compression: Esc("d800")),
        };
        storage.SetFileContent("repo.json", Encoding.UTF8.GetBytes(config));

        var probe = await BackupRepository.ProbeAsync(storage, Ct);
        var ex = await Assert.ThrowsAsync<RepositoryException>(() => BackupRepository.OpenAsync(storage, new ManualTimeProvider(), Ct));

        Assert.Equal(RepositoryState.Corrupt, probe.State);
        Assert.Equal(RepositoryErrorKind.Corrupt, ex.Kind);
    }

    [Fact]
    public async Task BAJA3_createdBy_is_written_as_valid_utf16()
    {
        var storage = new InMemoryRepositoryStorage();
        var repository = await BackupRepository.InitializeAsync(
            storage,
            new RepositoryInitializationOptions { CreatedBy = "Backbone " + ((char)0xD800).ToString() },
            new ManualTimeProvider(),
            Ct);

        Assert.Equal("Backbone �", repository.Configuration.CreatedBy);
        var reopened = await BackupRepository.OpenAsync(storage, new ManualTimeProvider(), Ct);
        Assert.Equal("Backbone �", reopened.Configuration.CreatedBy);
    }

    // ================================================================== Bloqueos (BAJA-1, BAJA-3)

    [Fact]
    public async Task BAJA1_The_holder_of_a_lock_is_sanitized_in_the_locked_message()
    {
        var (storage, time, repository) = await CreateAsync();
        // 240 caracteres: segmento válido y nombre de archivo de bloqueo dentro de 255.
        var host = new string('h', 240);
        storage.SetFileContent($"locks/{host}-7.lock", Encoding.UTF8.GetBytes(LockJson(host, "exclusive", Now)));
        storage.SetLastWriteUtc($"locks/{host}-7.lock", time.UtcNow);

        var ex = await Assert.ThrowsAsync<RepositoryException>(() => repository.AcquireLockAsync(RepositoryLockType.Shared, Salon, Ct));

        Assert.Equal(RepositoryErrorKind.Locked, ex.Kind);
        Assert.Contains(new string('h', 200) + "…", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(host, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("PC/SALON")]
    [InlineData("CON")]
    [InlineData("PC.")]
    [InlineData("")]
    public async Task BAJA1_A_lock_whose_host_is_not_a_valid_segment_is_unreadable_and_blocks(string host)
    {
        var (storage, time, repository) = await CreateAsync();
        storage.SetFileContent("locks/OTRO-7.lock", Encoding.UTF8.GetBytes(LockJson(host, "shared", Now)));
        storage.SetLastWriteUtc("locks/OTRO-7.lock", time.UtcNow);

        var info = Assert.Single(await repository.ListLocksAsync(Ct));
        var ex = await Assert.ThrowsAsync<RepositoryException>(() => repository.AcquireLockAsync(RepositoryLockType.Shared, Salon, Ct));

        Assert.False(info.IsReadable);
        Assert.Equal(RepositoryLockType.Exclusive, info.Type);
        Assert.Equal(RepositoryErrorKind.Locked, ex.Kind);
    }

    [Fact]
    public async Task BAJA3_A_lock_file_whose_name_backbone_cannot_address_is_an_unreadable_exclusive_lock()
    {
        var (inner, time, _) = await CreateAsync();
        var storage = new ExtraEntriesStorage(inner);
        storage.Add("locks", new StorageEntry("CON.lock", IsDirectory: false, 10, time.UtcNow));
        var repository = await BackupRepository.OpenAsync(storage, time, Ct);

        var info = Assert.Single(await repository.ListLocksAsync(Ct));
        var ex = await Assert.ThrowsAsync<RepositoryException>(() => repository.AcquireLockAsync(RepositoryLockType.Shared, Salon, Ct));

        Assert.Equal("CON.lock", info.FileName);
        Assert.False(info.IsReadable);
        Assert.Equal(RepositoryLockType.Exclusive, info.Type);
        Assert.Equal(RepositoryErrorKind.Locked, ex.Kind);
        Assert.DoesNotContain(inner.FilePaths, p => p.StartsWith("locks/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BAJA3_An_abandoned_lock_with_an_unaddressable_name_does_not_break_exclusive_acquisition()
    {
        var (inner, time, _) = await CreateAsync();
        var storage = new ExtraEntriesStorage(inner);
        storage.Add("locks", new StorageEntry("aux.lock", IsDirectory: false, 10, time.UtcNow - TimeSpan.FromHours(1)));
        var repository = await BackupRepository.OpenAsync(storage, time, Ct);

        await using var exclusive = await repository.AcquireLockAsync(RepositoryLockType.Exclusive, Salon, Ct);

        Assert.Equal(RepositoryLockType.Exclusive, exclusive.Type);
        Assert.DoesNotContain(storage.DeleteAttempts, p => p.EndsWith("aux.lock", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(-15)]
    [InlineData(-60 * 24)]
    [InlineData(60 * 24 * 365)]
    [Trait("Requirement", "R8")]
    public async Task N1_An_abandoned_unaddressable_lock_stops_blocking_and_is_never_deleted(int modifiedMinutes)
    {
        var (inner, time, _) = await CreateAsync();
        var storage = new ExtraEntriesStorage(inner);
        storage.Add("locks", new StorageEntry("nul.lock", IsDirectory: false, 10, time.UtcNow.AddMinutes(modifiedMinutes)));
        var repository = await BackupRepository.OpenAsync(storage, time, Ct);

        var info = Assert.Single(await repository.ListLocksAsync(Ct));
        Assert.False(info.IsReadable);
        Assert.False(info.IsAddressable);
        Assert.True(RepositoryLockPolicy.IsStale(info, time.UtcNow));

        await using (var shared = await repository.AcquireLockAsync(RepositoryLockType.Shared, Salon, Ct))
        {
            Assert.Equal(RepositoryLockType.Shared, shared.Type);
        }

        await using (var exclusive = await repository.AcquireLockAsync(RepositoryLockType.Exclusive, Salon, Ct))
        {
            Assert.Equal(RepositoryLockType.Exclusive, exclusive.Type);
        }

        Assert.DoesNotContain(storage.DeleteAttempts, p => p.EndsWith("nul.lock", StringComparison.OrdinalIgnoreCase));
        Assert.Single(await repository.ListLocksAsync(Ct));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-14)]
    [InlineData(15)]
    [Trait("Requirement", "R8")]
    public async Task N1_An_unaddressable_lock_blocks_while_its_modification_time_is_alive(int modifiedMinutes)
    {
        var (inner, time, _) = await CreateAsync();
        var storage = new ExtraEntriesStorage(inner);
        storage.Add("locks", new StorageEntry("a:b.lock", IsDirectory: false, 10, time.UtcNow.AddMinutes(modifiedMinutes)));
        var repository = await BackupRepository.OpenAsync(storage, time, Ct);

        var shared = await Assert.ThrowsAsync<RepositoryException>(() => repository.AcquireLockAsync(RepositoryLockType.Shared, Salon, Ct));
        var exclusive = await Assert.ThrowsAsync<RepositoryException>(() => repository.AcquireLockAsync(RepositoryLockType.Exclusive, Salon, Ct));

        Assert.Equal(RepositoryErrorKind.Locked, shared.Kind);
        Assert.Equal(RepositoryErrorKind.Locked, exclusive.Kind);
        Assert.DoesNotContain(storage.DeleteAttempts, p => p.EndsWith("a:b.lock", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BAJA3_Cleaning_tmp_ignores_names_that_backbone_cannot_address()
    {
        var (inner, time, _) = await CreateAsync();
        inner.SetFileContent("tmp/abandonado.tmp", "x"u8);
        var storage = new ExtraEntriesStorage(inner);
        storage.Add("tmp", new StorageEntry("NUL.tmp", IsDirectory: false, 1, time.UtcNow));
        storage.Add("tmp", new StorageEntry("con", IsDirectory: false, 1, time.UtcNow));
        var repository = await BackupRepository.OpenAsync(storage, time, Ct);
        await using var exclusive = await repository.AcquireLockAsync(RepositoryLockType.Exclusive, Salon, Ct);

        var deleted = await repository.CleanTemporaryFilesAsync(exclusive, Ct);

        Assert.Equal(1, deleted);
        Assert.DoesNotContain("tmp/abandonado.tmp", inner.FilePaths);
    }

    // ================================================================== Sondeo (BAJA-3)

    [Fact]
    public async Task BAJA3_Probe_reports_corruption_found_while_enumerating_instead_of_throwing()
    {
        var storage = new CorruptOnEnumerateStorage(new InMemoryRepositoryStorage());

        var probe = await BackupRepository.ProbeAsync(storage, Ct);

        Assert.Equal(RepositoryState.Corrupt, probe.State);
        Assert.Contains("reanálisis", probe.Detail, StringComparison.Ordinal);
    }

    // ================================================================== Puntos de reanálisis (M-3)

    [Fact]
    public async Task M3_A_junction_as_the_repository_root_makes_it_corrupt()
    {
        var real = Path.Join(directory.Path, "real");
        await InitializeOnDiskAsync(real);
        var link = Path.Join(directory.Path, "enlace");
        CreateJunction(link, real);
        var storage = FileSystemRepositoryStorage.ForLocalTesting(link);

        var probe = await BackupRepository.ProbeAsync(storage, Ct);
        var ex = await Assert.ThrowsAsync<RepositoryException>(() => BackupRepository.OpenAsync(storage, new ManualTimeProvider(), Ct));

        Assert.Equal(RepositoryState.Corrupt, probe.State);
        Assert.Equal(RepositoryErrorKind.Corrupt, ex.Kind);
    }

    [Fact]
    public async Task M3_A_junction_in_objects_prefix_folder_stops_the_write_without_touching_its_target()
    {
        var root = Path.Join(directory.Path, "repo");
        var outside = Path.Join(directory.Path, "fuera");
        Directory.CreateDirectory(outside);
        var (_, repository) = await InitializeOnDiskAsync(root);
        var content = "contenido que no debe salir del repositorio"u8.ToArray();
        var hash = ContentHash.FromBytes(SHA256.HashData(content));
        CreateJunction(Path.Join(root, "objects", hash.Prefix), outside);
        await using var repositoryLock = await repository.AcquireLockAsync(RepositoryLockType.Shared, Salon, Ct);

        using var source = new MemoryStream(content);
        var ex = await Assert.ThrowsAsync<RepositoryException>(() => repository.WriteObjectAsync(source, expectedHash: null, repositoryLock, Ct));

        Assert.Equal(RepositoryErrorKind.Corrupt, ex.Kind);
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Join(root, "tmp")));
    }

    [Fact]
    public async Task M3_A_junction_as_locks_stops_lock_acquisition_without_writing_outside()
    {
        var root = Path.Join(directory.Path, "repo");
        var outside = Path.Join(directory.Path, "fuera");
        Directory.CreateDirectory(outside);
        var (storage, repository) = await InitializeOnDiskAsync(root);
        Directory.Delete(Path.Join(root, "locks"));
        CreateJunction(Path.Join(root, "locks"), outside);

        var ex = await Assert.ThrowsAsync<RepositoryException>(() => repository.AcquireLockAsync(RepositoryLockType.Exclusive, Salon, Ct));

        Assert.Equal(RepositoryErrorKind.Corrupt, ex.Kind);
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
        Assert.Equal(RepositoryState.Corrupt, (await BackupRepository.ProbeAsync(storage, Ct)).State);
    }

    [Fact]
    public async Task M3_A_junction_as_tmp_stops_the_cleanup_without_deleting_outside()
    {
        var root = Path.Join(directory.Path, "repo");
        var outside = Path.Join(directory.Path, "fuera");
        Directory.CreateDirectory(outside);
        var victim = Path.Join(outside, "importante.tmp");
        await File.WriteAllTextAsync(victim, "no borrar", Ct);
        var (_, repository) = await InitializeOnDiskAsync(root);
        await using var exclusive = await repository.AcquireLockAsync(RepositoryLockType.Exclusive, Salon, Ct);
        Directory.Delete(Path.Join(root, "tmp"));
        CreateJunction(Path.Join(root, "tmp"), outside);

        var ex = await Assert.ThrowsAsync<RepositoryException>(() => repository.CleanTemporaryFilesAsync(exclusive, Ct));

        Assert.Equal(RepositoryErrorKind.Corrupt, ex.Kind);
        Assert.True(File.Exists(victim));
    }

    [Fact]
    public async Task M3_A_junction_inside_snapshots_makes_listing_fail_as_corrupt()
    {
        var root = Path.Join(directory.Path, "repo");
        var outside = Path.Join(directory.Path, "fuera");
        Directory.CreateDirectory(outside);
        var (_, repository) = await InitializeOnDiskAsync(root);
        CreateJunction(Path.Join(root, "snapshots", "20261115T020000Z-a1b2.json"), outside);

        var ex = await Assert.ThrowsAsync<RepositoryException>(() => repository.ListSnapshotIdsAsync(Ct));

        Assert.Equal(RepositoryErrorKind.Corrupt, ex.Kind);
        Assert.Contains("punto de reanálisis", ex.Message, StringComparison.Ordinal);
    }

    // ================================================================== Auxiliares

    private static string LockJson(string host, string type, DateTime heartbeat) =>
        $$"""{ "host": {{System.Text.Json.JsonSerializer.Serialize(host)}}, "pid": 7, "type": "{{type}}", "heartbeatUtc": "{{heartbeat:yyyy-MM-dd'T'HH:mm:ss'Z'}}" }""";

    private static string Config(
        string createdBy = "Backbone",
        string hash = "sha256",
        string repositoryId = "8f6c2e1a-0000-0000-0000-000000000001",
        string compression = "none") =>
        $$"""{ "formatVersion": 1, "repositoryId": "{{repositoryId}}", "createdUtc": "2026-11-15T02:00:00Z", "createdBy": "{{createdBy}}", "hash": "{{hash}}", "compression": "{{compression}}", "encryption": "none" }""";

    private static async Task<InMemoryRepositoryStorage> InitializedStorageAsync()
    {
        var (storage, _, _) = await CreateAsync();
        return storage;
    }

    private static async Task<(InMemoryRepositoryStorage Storage, ManualTimeProvider Time, BackupRepository Repository)> CreateAsync()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(Now));
        var storage = new InMemoryRepositoryStorage(time);
        var repository = await BackupRepository.InitializeAsync(storage, new RepositoryInitializationOptions { CreatedBy = "x" }, time, Ct);
        return (storage, time, repository);
    }

    private static async Task<(FileSystemRepositoryStorage Storage, BackupRepository Repository)> InitializeOnDiskAsync(string root)
    {
        var storage = FileSystemRepositoryStorage.ForLocalTesting(root);
        var repository = await BackupRepository.InitializeAsync(storage, new RepositoryInitializationOptions { CreatedBy = "x" }, TimeProvider.System, Ct);
        return (storage, repository);
    }

    /// <summary>Crea un <i>junction</i> con <c>mklink /J</c> (no requiere privilegios); omite la prueba fuera de Windows.</summary>
    private void CreateJunction(string link, string target)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Los junctions solo existen en Windows.");
        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(link);
        startInfo.ArgumentList.Add(target);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("No se pudo ejecutar mklink.");
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"mklink /J falló: {output}");
        junctions.Add(link);
        Assert.True(new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint));
    }

    /// <summary>Almacenamiento que añade a la enumeración elementos con nombres que el NAS admite y Backbone no.</summary>
    private sealed class ExtraEntriesStorage(IRepositoryStorage inner) : IRepositoryStorage
    {
        private readonly Dictionary<string, List<StorageEntry>> extra = new(StringComparer.OrdinalIgnoreCase);

        public void Add(string directory, StorageEntry entry)
        {
            if (!extra.TryGetValue(directory, out var list))
            {
                extra[directory] = list = [];
            }

            list.Add(entry);
        }

        public Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken) => inner.OpenReadAsync(path, cancellationToken);

        public Task<Stream> CreateNewAsync(string path, CancellationToken cancellationToken) => inner.CreateNewAsync(path, cancellationToken);

        public Task<bool> TryMoveAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken) =>
            inner.TryMoveAsync(sourcePath, destinationPath, overwrite, cancellationToken);

        /// <summary>Rutas que se han intentado borrar.</summary>
        public List<string> DeleteAttempts { get; } = [];

        public Task DeleteFileAsync(string path, CancellationToken cancellationToken)
        {
            DeleteAttempts.Add(path);
            return inner.DeleteFileAsync(path, cancellationToken);
        }

        public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken) => inner.CreateDirectoryAsync(path, cancellationToken);

        public Task<bool> DirectoryExistsAsync(string path, CancellationToken cancellationToken) => inner.DirectoryExistsAsync(path, cancellationToken);

        public Task<StorageEntry?> GetFileInfoAsync(string path, CancellationToken cancellationToken) => inner.GetFileInfoAsync(path, cancellationToken);

        public async IAsyncEnumerable<StorageEntry> EnumerateAsync(string directoryPath, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var entry in inner.EnumerateAsync(directoryPath, cancellationToken))
            {
                yield return entry;
            }

            if (extra.TryGetValue(directoryPath, out var list))
            {
                foreach (var entry in list)
                {
                    yield return entry;
                }
            }
        }
    }

    /// <summary>Almacenamiento cuya enumeración de la raíz encuentra un punto de reanálisis (como <see cref="FileSystemRepositoryStorage"/>).</summary>
    private sealed class CorruptOnEnumerateStorage(InMemoryRepositoryStorage inner) : IRepositoryStorage
    {
        public Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken) => inner.OpenReadAsync(path, cancellationToken);

        public Task<Stream> CreateNewAsync(string path, CancellationToken cancellationToken) => inner.CreateNewAsync(path, cancellationToken);

        public Task<bool> TryMoveAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken) =>
            inner.TryMoveAsync(sourcePath, destinationPath, overwrite, cancellationToken);

        public Task DeleteFileAsync(string path, CancellationToken cancellationToken) => inner.DeleteFileAsync(path, cancellationToken);

        public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken) => inner.CreateDirectoryAsync(path, cancellationToken);

        public Task<bool> DirectoryExistsAsync(string path, CancellationToken cancellationToken) => inner.DirectoryExistsAsync(path, cancellationToken);

        public Task<StorageEntry?> GetFileInfoAsync(string path, CancellationToken cancellationToken) => inner.GetFileInfoAsync(path, cancellationToken);

public IAsyncEnumerable<StorageEntry> EnumerateAsync(string directoryPath, CancellationToken cancellationToken) =>
            throw new RepositoryException(RepositoryErrorKind.Corrupt, "'x' es un punto de reanálisis (junction o enlace simbólico); Backbone no los sigue.");
    }
}
