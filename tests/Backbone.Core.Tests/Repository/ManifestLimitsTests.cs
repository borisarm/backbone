using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Backbone.Contracts;
using Backbone.Core.Repository;
using Backbone.Core.Repository.Locking;
using Backbone.Core.Repository.Snapshots;
using Backbone.Core.Storage;
using Backbone.Core.Tests.Support;
using SnapshotStatus = Backbone.Core.Repository.Snapshots.SnapshotStatus;

namespace Backbone.Core.Tests.Repository;

/// <summary>
/// Límites de los manifiestos (§6.3 "Límites", ADR-11; hallazgos ALTA-1, MEDIA-1, MEDIA-2, MEDIA-3 y M-2 de la revisión de
/// seguridad): el lector rechaza lo que los supera y el escritor nunca publica un manifiesto que el lector rechace.
/// </summary>
[Trait("Story", "HT-04")]
[Trait("Requirement", "ADR-11")]
public sealed class ManifestLimitsTests
{
    private const string ValidId = "20261115T020000Z-a1b2";
    private static readonly DateTime Started = new(2026, 11, 15, 2, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Modified = new DateTime(2026, 1, 31, 10, 22, 1, DateTimeKind.Utc).AddTicks(1234567);
    private static readonly byte[] EntriesMarker = "\"entries\":["u8.ToArray();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Carácter de control: el escritor lo escapa como <c>\uXXXX</c> (6 bytes), el peor caso.</summary>
    private static string Ctl(int count) => new((char)1, count);

    private static SnapshotStart Start() => new()
    {
        JobId = "1b2c3d4e-0000-0000-0000-000000000001",
        JobName = "Documentos",
        Host = "PC-SALON",
        StartedUtc = Started,
        UsedVss = false,
        Sources = [new("Documentos", @"C:\Users\ana\Documents")],
    };

    // ================================================================== Escritor

    [Fact]
    public async Task ALTA1_Writer_round_trips_a_header_with_every_field_at_its_limit()
    {
        var sources = Enumerable.Range(0, SnapshotLimits.MaxSources)
            .Select(i => new SnapshotSource(i.ToString("D2", CultureInfo.InvariantCulture) + new string('a', ContractLimits.MaxAliasLength - 2), Ctl(SnapshotLimits.MaxSourcePathLength)))
            .ToArray();
        var start = Start() with
        {
            JobId = new string('j', SnapshotLimits.MaxJobIdLength),
            JobName = new string('n', SnapshotLimits.MaxJobNameLength),
            Host = new string('h', SnapshotLimits.MaxHostLength),
            Sources = sources,
        };
        var maxWarning = new SnapshotWarning(
            Ctl(SnapshotLimits.MaxWarningPathLength),
            Ctl(SnapshotLimits.MaxWarningCodeLength),
            Ctl(SnapshotLimits.MaxWarningMessageLength));
        var entry = SnapshotEntry.File(sources[0].Alias + "/a.txt", HashOf("a"), 5, Modified, FileAttributes.Archive);

        var (storage, _, repository, repositoryLock) = await CreateAsync();
        var written = await WriteAsync(repository, repositoryLock, start, [entry], [maxWarning]);

        var manifest = storage.GetFileContent($"snapshots/{written.Id}.json");
        Assert.InRange(HeaderLength(manifest), 6_000_000, SnapshotLimits.MaxHeaderBytes);

        await using var reader = await repository.OpenSnapshotAsync(written.Id, Ct);
        Assert.Equal(start.JobId, reader.Header.JobId);
        Assert.Equal(start.JobName, reader.Header.JobName);
        Assert.Equal(start.Host, reader.Header.Host);
        Assert.Equal(sources, reader.Header.Sources);
        Assert.Equal([maxWarning], reader.Header.Warnings);
        Assert.Equal([entry], await ReadAllAsync(reader));
    }

    [Fact]
    public async Task ALTA1_Writer_keeps_exactly_10000_warnings_when_they_fit()
    {
        var warnings = Warnings(SnapshotLimits.MaxWarnings);
        var (_, _, repository, repositoryLock) = await CreateAsync();

        var written = await WriteAsync(repository, repositoryLock, Start(), [], warnings);

        Assert.Equal(warnings, written.Warnings);
        var read = await repository.ReadSnapshotHeaderAsync(written.Id, Ct);
        Assert.Equal(warnings, read.Warnings);
    }

    [Fact]
    public async Task ALTA1_Writer_with_10001_warnings_keeps_9999_and_a_summary()
    {
        var warnings = Warnings(SnapshotLimits.MaxWarnings + 1);
        var (_, _, repository, repositoryLock) = await CreateAsync();

        var written = await WriteAsync(repository, repositoryLock, Start(), [], warnings);

        Assert.Equal(SnapshotLimits.MaxWarnings, written.Warnings.Count);
        Assert.Equal(warnings.Take(SnapshotLimits.MaxWarnings - 1), written.Warnings.Take(SnapshotLimits.MaxWarnings - 1));
        Assert.Equal(
            new SnapshotWarning("", nameof(RunIssueCode.WarningsTruncated), "2 avisos más no se han guardado en el snapshot."),
            written.Warnings[^1]);

        var read = await repository.ReadSnapshotHeaderAsync(written.Id, Ct);
        Assert.Equal(written.Warnings, read.Warnings);
    }

    [Fact]
    public async Task ALTA1_Writer_with_many_more_warnings_examines_at_most_the_limit_and_counts_the_rest()
    {
        var warnings = Warnings(250_000);
        var (_, _, repository, repositoryLock) = await CreateAsync();

        var written = await WriteAsync(repository, repositoryLock, Start(), [], warnings);

        Assert.Equal(SnapshotLimits.MaxWarnings, written.Warnings.Count);
        Assert.Equal("240001 avisos más no se han guardado en el snapshot.", written.Warnings[^1].Message);
        Assert.Equal(written.Warnings, (await repository.ReadSnapshotHeaderAsync(written.Id, Ct)).Warnings);
    }

    [Fact]
    public async Task ALTA1_Writer_truncates_warning_texts_to_their_limits_without_splitting_surrogate_pairs()
    {
        var emoji = char.ConvertFromUtf32(0x1F600);
        var lone = ((char)0xD800).ToString();
        var warnings = new[]
        {
            new SnapshotWarning(new string('p', 40_000), new string('c', 100), new string('m', SnapshotLimits.MaxWarningMessageLength - 1) + emoji + "fin"),
            new SnapshotWarning("p", "c", new string('m', SnapshotLimits.MaxWarningMessageLength - 2) + emoji + "fin"),
            new SnapshotWarning("p" + lone, "c", lone + new string('m', 5000)),
        };
        var (_, _, repository, repositoryLock) = await CreateAsync();

        var written = await WriteAsync(repository, repositoryLock, Start(), [], warnings);

        var read = (await repository.ReadSnapshotHeaderAsync(written.Id, Ct)).Warnings;
        Assert.Equal(written.Warnings, read);
        Assert.Equal(new string('p', SnapshotLimits.MaxWarningPathLength), read[0].Path);
        Assert.Equal(new string('c', SnapshotLimits.MaxWarningCodeLength), read[0].Code);
        Assert.Equal(new string('m', SnapshotLimits.MaxWarningMessageLength - 1), read[0].Message);
        Assert.Equal(new string('m', SnapshotLimits.MaxWarningMessageLength - 2) + emoji, read[1].Message);
        Assert.Equal("p\uFFFD", read[2].Path);
        Assert.Equal(SnapshotLimits.MaxWarningMessageLength, read[2].Message.Length);
        Assert.StartsWith("\uFFFDmmm", read[2].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ALTA1_Truncate_never_leaves_a_lone_surrogate()
    {
        var emoji = char.ConvertFromUtf32(0x1F600);
        Assert.Equal("ab", SnapshotWriter.Truncate("ab" + emoji, 3));
        Assert.Equal("ab" + emoji, SnapshotWriter.Truncate("ab" + emoji, 4));
        Assert.Equal("ab" + emoji, SnapshotWriter.Truncate("ab" + emoji + "c", 4));
        Assert.Equal("abc", SnapshotWriter.Truncate("abc", 3));
    }

    [Fact]
    public async Task ALTA1_MEDIA2_Writer_stores_every_warning_when_the_header_is_exactly_16_MiB()
    {
        var (warnings, _) = await BuildWarningsForHeaderAsync(extraBytes: 0);
        var (storage, _, repository, repositoryLock) = await CreateAsync();

        var written = await WriteAsync(repository, repositoryLock, Start(), [], warnings);

        Assert.Equal(SnapshotLimits.MaxHeaderBytes, HeaderLength(storage.GetFileContent($"snapshots/{written.Id}.json")));
        Assert.Equal(warnings, written.Warnings);
        Assert.Equal(warnings, (await repository.ReadSnapshotHeaderAsync(written.Id, Ct)).Warnings);
    }

    [Fact]
    public async Task ALTA1_MEDIA2_Writer_drops_warnings_beyond_the_header_budget_and_adds_a_summary()
    {
        // Un byte más que el caso anterior: ya no caben todos.
        var (warnings, sizes) = await BuildWarningsForHeaderAsync(extraBytes: 1);
        var (storage, _, repository, repositoryLock) = await CreateAsync();

        var written = await WriteAsync(repository, repositoryLock, Start(), [], warnings);

        // Los primeros que caben en 16 MiB − 256 KiB (contando el que se añade), y después el resumen.
        long header = sizes.Fixed;
        var kept = 0;
        while (kept < warnings.Count && header + sizes.Each[kept] + (kept > 0 ? 1 : 0) <= SnapshotLimits.TruncatedWarningsHeaderBytes)
        {
            header += sizes.Each[kept] + (kept > 0 ? 1 : 0);
            kept++;
        }

        Assert.True(kept < warnings.Count - 1, $"La prueba debe descartar varios avisos ({kept} de {warnings.Count}).");
        Assert.Equal(kept + 1, written.Warnings.Count);
        Assert.Equal(warnings.Take(kept), written.Warnings.Take(kept));
        Assert.Equal(
            new SnapshotWarning("", "WarningsTruncated", $"{warnings.Count - kept} avisos más no se han guardado en el snapshot."),
            written.Warnings[^1]);

        var length = HeaderLength(storage.GetFileContent($"snapshots/{written.Id}.json"));
        Assert.InRange(length, SnapshotLimits.TruncatedWarningsHeaderBytes - sizes.Each.Max(), SnapshotLimits.MaxHeaderBytes);
        Assert.Equal(written.Warnings, (await repository.ReadSnapshotHeaderAsync(written.Id, Ct)).Warnings);
    }

    public static TheoryData<string> OversizedStarts => ["jobId", "jobName", "host largo", "host no válido", "host dispositivo", "orígenes", "ruta de origen"];

    [Theory]
    [MemberData(nameof(OversizedStarts))]
    public async Task ALTA1_Writer_rejects_header_fields_beyond_their_limits_before_starting(string field)
    {
        var start = field switch
        {
            "jobId" => Start() with { JobId = new string('j', SnapshotLimits.MaxJobIdLength + 1) },
            "jobName" => Start() with { JobName = new string('n', SnapshotLimits.MaxJobNameLength + 1) },
            "host largo" => Start() with { Host = new string('h', SnapshotLimits.MaxHostLength + 1) },
            "host no válido" => Start() with { Host = "PC/SALON" },
            "host dispositivo" => Start() with { Host = "CON" },
            "orígenes" => Start() with { Sources = [.. Enumerable.Range(0, SnapshotLimits.MaxSources + 1).Select(i => new SnapshotSource($"O{i}", @"C:\o"))] },
            "ruta de origen" => Start() with { Sources = [new("Documentos", new string('x', SnapshotLimits.MaxSourcePathLength + 1))] },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };
        var (storage, _, repository, repositoryLock) = await CreateAsync();

        await Assert.ThrowsAnyAsync<ArgumentException>(() => repository.CreateSnapshotAsync(start, repositoryLock, Ct));
        Assert.DoesNotContain(storage.FilePaths, p => p.StartsWith("tmp/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MEDIA3_Entry_limit_is_5_million_in_reader_and_writer()
    {
        Assert.Equal(5_000_000, RepositoryLayout.MaxSnapshotEntries);
        var (storage, _, repository, repositoryLock) = await CreateAsync();
        await using var writer = await repository.CreateSnapshotAsync(Start(), repositoryLock, Ct);
        Assert.Equal(RepositoryLayout.MaxSnapshotEntries, writer.MaxEntries);
        Assert.Equal(RepositoryLayout.MaxSnapshotManifestBytes, writer.MaxManifestBytes);

        storage.SetFileContent($"snapshots/{ValidId}.json", Encoding.UTF8.GetBytes(HandManifest()));
        await using var reader = await repository.OpenSnapshotAsync(ValidId, Ct);
        Assert.Equal(RepositoryLayout.MaxSnapshotEntries, reader.MaxEntries);
    }

    [Fact]
    public async Task ALTA1_Writer_fails_without_publishing_when_the_entry_limit_is_exceeded()
    {
        var (storage, _, repository, repositoryLock) = await CreateAsync();

        // En el límite exacto se publica.
        var atLimit = await repository.CreateSnapshotAsync(Start(), repositoryLock, Ct);
        await using (atLimit)
        {
            atLimit.MaxEntries = 3;
            foreach (var entry in Files(3))
            {
                await atLimit.AddAsync(entry, Ct);
            }

            await atLimit.CompleteAsync(new SnapshotCompletion { Status = SnapshotStatus.Completed }, Ct);
        }

        // Uno más: la copia falla y no se publica nada.
        var overLimit = await repository.CreateSnapshotAsync(Start() with { StartedUtc = Started.AddSeconds(1) }, repositoryLock, Ct);
        await using (overLimit)
        {
            overLimit.MaxEntries = 3;
            var entries = Files(4).ToList();
            foreach (var entry in entries.Take(3))
            {
                await overLimit.AddAsync(entry, Ct);
            }

            var ex = await Assert.ThrowsAsync<RepositoryException>(() => overLimit.AddAsync(entries[3], Ct));
            Assert.Equal(RepositoryErrorKind.SnapshotTooLarge, ex.Kind);
            Assert.Contains("3 entradas", ex.Message, StringComparison.Ordinal);
            await Assert.ThrowsAsync<InvalidOperationException>(() => overLimit.CompleteAsync(new SnapshotCompletion { Status = SnapshotStatus.Completed }, Ct));
        }

        Assert.Equal([atLimit.Id], await repository.ListSnapshotIdsAsync(Ct));
        Assert.DoesNotContain(storage.FilePaths, p => p.StartsWith("tmp/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ALTA1_Writer_fails_without_publishing_when_the_manifest_would_exceed_its_maximum_size()
    {
        // Tamaño real de un manifiesto con estas entradas (la fecha de fin la fija el reloj manual; WriteAsync usa
        // completedWithWarnings).
        var (storage, _, repository, repositoryLock) = await CreateAsync();
        var reference = await WriteAsync(repository, repositoryLock, Start(), Files(20), []);
        long size = storage.GetFileContent($"snapshots/{reference.Id}.json").Length;

        // Justo en el tamaño máximo: se publica.
        var (exactStorage, _, exactRepository, exactLock) = await CreateAsync();
        await using (var exact = await exactRepository.CreateSnapshotAsync(Start(), exactLock, Ct))
        {
            exact.MaxManifestBytes = size;
            foreach (var entry in Files(20))
            {
                await exact.AddAsync(entry, Ct);
            }

            var header = await exact.CompleteAsync(new SnapshotCompletion { Status = SnapshotStatus.CompletedWithWarnings }, Ct);
            Assert.Equal(size, exactStorage.GetFileContent($"snapshots/{header.Id}.json").Length);
        }

        // Un byte menos: falla al completar, sin publicar.
        var (smallStorage, _, smallRepository, smallLock) = await CreateAsync();
        await using (var small = await smallRepository.CreateSnapshotAsync(Start(), smallLock, Ct))
        {
            small.MaxManifestBytes = size - 1;
            foreach (var entry in Files(20))
            {
                await small.AddAsync(entry, Ct);
            }

            var ex = await Assert.ThrowsAsync<RepositoryException>(() => small.CompleteAsync(new SnapshotCompletion { Status = SnapshotStatus.CompletedWithWarnings }, Ct));
            Assert.Equal(RepositoryErrorKind.SnapshotTooLarge, ex.Kind);
            Assert.Contains("tamaño máximo", ex.Message, StringComparison.Ordinal);
        }

        Assert.Empty(await smallRepository.ListSnapshotIdsAsync(Ct));
        Assert.DoesNotContain(smallStorage.FilePaths, p => p.StartsWith("tmp/", StringComparison.Ordinal));

        // Si ni las entradas caben, falla al añadirlas, sin esperar al final.
        var (_, _, earlyRepository, earlyLock) = await CreateAsync();
        await using var early = await earlyRepository.CreateSnapshotAsync(Start(), earlyLock, Ct);
        early.MaxManifestBytes = 500;
        var earlyError = await Assert.ThrowsAsync<RepositoryException>(async () =>
        {
            foreach (var entry in Files(20))
            {
                await early.AddAsync(entry, Ct);
            }
        });
        Assert.Equal(RepositoryErrorKind.SnapshotTooLarge, earlyError.Kind);
        Assert.InRange(early.EntryCount, 1, 19);
    }

    // ================================================================== Lector

    public static TheoryData<string, bool> FieldLimitCases
    {
        get
        {
            var data = new TheoryData<string, bool>();
            foreach (var name in new[] { "jobId", "jobName", "host", "alias", "sources[].path", "sources", "warnings", "warnings[].path", "warnings[].code", "warnings[].message" })
            {
                data.Add(name, false);
                data.Add(name, true);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(FieldLimitCases))]
    public async Task M2_Reader_accepts_every_header_field_at_its_limit_and_rejects_one_more(string field, bool overLimit)
    {
        var extra = overLimit ? 1 : 0;
        string Text(int length) => new('x', length + extra);
        var manifest = field switch
        {
            "jobId" => HandManifest(jobId: Text(SnapshotLimits.MaxJobIdLength)),
            "jobName" => HandManifest(jobName: Text(SnapshotLimits.MaxJobNameLength)),
            "host" => HandManifest(host: Text(SnapshotLimits.MaxHostLength)),
            "alias" => HandManifest(sources: [(Text(ContractLimits.MaxAliasLength), "C:\\D")], entryAlias: Text(ContractLimits.MaxAliasLength)),
            "sources[].path" => HandManifest(sources: [("Documentos", Text(SnapshotLimits.MaxSourcePathLength))]),
            "sources" => HandManifest(sources: [("Documentos", "C:\\D"), .. Enumerable.Range(1, SnapshotLimits.MaxSources - 1 + extra).Select(i => ($"O{i}", "C:\\o"))]),
            "warnings" => HandManifest(warnings: Enumerable.Repeat(("p", "FileLocked", "m"), SnapshotLimits.MaxWarnings + extra)),
            "warnings[].path" => HandManifest(warnings: [(Text(SnapshotLimits.MaxWarningPathLength), "c", "m")]),
            "warnings[].code" => HandManifest(warnings: [("p", Text(SnapshotLimits.MaxWarningCodeLength), "m")]),
            "warnings[].message" => HandManifest(warnings: [("p", "c", Text(SnapshotLimits.MaxWarningMessageLength))]),
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

        var outcome = await TryReadAsync(manifest);

        if (overLimit)
        {
            Assert.True(outcome is RepositoryException { Kind: RepositoryErrorKind.Corrupt }, $"{field}: {outcome}");
        }
        else
        {
            Assert.Null(outcome);
        }
    }

    public static TheoryData<string, string> InvalidIdentityCases => new()
    {
        { "jobId vacío", HandManifest(jobId: "") },
        { "host vacío", HandManifest(host: "") },
        { "host con barra", HandManifest(host: "PC/SALON") },
        { "host con control", HandManifest(host: "PC" + (char)10 + "SALON") },
        { "host dispositivo", HandManifest(host: "NUL") },
        { "host con punto final", HandManifest(host: "PC.") },
    };

    [Theory]
    [MemberData(nameof(InvalidIdentityCases))]
    public async Task BAJA2_Reader_requires_a_non_empty_jobId_and_a_host_that_is_a_valid_segment(string reason, string manifest)
    {
        var outcome = await TryReadAsync(manifest);
        Assert.True(outcome is RepositoryException { Kind: RepositoryErrorKind.Corrupt }, $"{reason}: {outcome}");
    }

    [Fact]
    public async Task BAJA2_Reader_rejects_a_host_with_an_escaped_lone_surrogate_as_corrupt()
    {
        var manifest = HandManifest(host: "PCHOST").Replace("PCHOST", "PC" + "\\" + "ud800", StringComparison.Ordinal);
        var outcome = await TryReadAsync(manifest);
        Assert.True(outcome is RepositoryException { Kind: RepositoryErrorKind.Corrupt }, $"{outcome}");
    }

    [Fact]
    public async Task MEDIA1_A_3_MiB_warning_fails_as_soon_as_it_is_read()
    {
        var manifest = HandManifest(warnings: [("p", "c", new string('m', 3 * 1024 * 1024))]);

        var outcome = await TryReadAsync(manifest);

        var ex = Assert.IsType<RepositoryException>(outcome);
        Assert.Equal(RepositoryErrorKind.Corrupt, ex.Kind);
        Assert.Contains("demasiado grande", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MEDIA1_An_invalid_warning_fails_before_the_rest_of_the_list_is_read()
    {
        // Si el aviso se validara al final de la lista, el error sería "truncado"; se valida al leerlo.
        var manifest = HandManifest(warnings: [("p", "c", new string('m', SnapshotLimits.MaxWarningMessageLength + 1))]);
        var truncated = manifest[..(manifest.IndexOf("\"entries\"", StringComparison.Ordinal) - 2)] + ",{\"path\":\"sin terminar";

        var ex = Assert.IsType<RepositoryException>(await TryReadAsync(truncated));

        Assert.Equal(RepositoryErrorKind.Corrupt, ex.Kind);
        Assert.Contains("aviso con textos demasiado largos", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MEDIA1_An_invalid_source_fails_before_the_rest_of_the_list_is_read()
    {
        var manifest = HandManifest(sources: [("..", "C:\\D")], entryAlias: "Documentos");
        var cut = manifest.IndexOf("\"stats\"", StringComparison.Ordinal) - 2;
        var truncated = manifest[..cut] + ",{\"alias\":\"sin terminar";

        var ex = Assert.IsType<RepositoryException>(await TryReadAsync(truncated));

        Assert.Equal(RepositoryErrorKind.Corrupt, ex.Kind);
        Assert.Contains("alias no válido", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MEDIA2_Header_unit_limit_is_256_KiB()
    {
        Assert.Equal(256 * 1024, SnapshotLimits.MaxHeaderUnitBytes);
        Assert.Equal(16 * 1024 * 1024, SnapshotLimits.MaxHeaderBytes);

        var fits = HandManifest(extraHeader: $"\"futuro\":\"{new string('f', 200_000)}\",");
        var tooLarge = HandManifest(extraHeader: $"\"futuro\":\"{new string('f', SnapshotLimits.MaxHeaderUnitBytes)}\",");
        var afterEntries = HandManifest(trailer: $",\"despues\":\"{new string('f', SnapshotLimits.MaxHeaderUnitBytes)}\"");

        Assert.Null(await TryReadAsync(fits));
        Assert.True(await TryReadAsync(tooLarge) is RepositoryException { Kind: RepositoryErrorKind.Corrupt });
        Assert.True(await TryReadAsync(afterEntries) is RepositoryException { Kind: RepositoryErrorKind.Corrupt });
    }

    [Theory]
    [InlineData(200_000, false)]
    [InlineData(600_000, true)]
    public async Task N3_Header_unit_limit_applies_after_entries_even_when_the_buffer_is_already_large(int trailerChars, bool corrupt)
    {
        // Una entrada de ~900 KB (propiedad desconocida, dentro del límite de 1 MiB de entrada) hace crecer el búfer; detrás,
        // muchas entradas y una propiedad final que cabe entera en ese búfer sin volver a rellenarlo.
        var entries = new List<string> { BigEntry("Documentos/grande.txt", 900_000) };
        entries.AddRange(Enumerable.Range(0, 2_000).Select(i => Entry($"Documentos/{i}.txt", 0)));
        entries[^1] = Entry("Documentos/1999.txt", 5);
        var manifest = HandManifest(
            entries: string.Join(",", entries),
            files: entries.Count,
            bytes: 5,
            trailer: $",\"despues\":\"{new string('f', trailerChars)}\"");

        var outcome = await TryReadAsync(manifest);

        if (corrupt)
        {
            var ex = Assert.IsType<RepositoryException>(outcome);
            Assert.Equal(RepositoryErrorKind.Corrupt, ex.Kind);
            Assert.Contains("demasiado grande", ex.Message, StringComparison.Ordinal);
        }
        else
        {
            Assert.Null(outcome);
        }

        static string BigEntry(string path, int padding) =>
            $$"""{"p":{{JsonSerializer.Serialize(path)}},"x":"{{new string('x', padding)}}","h":"{{new string('a', 64)}}","s":0,"m":"2026-01-31T10:22:01Z","a":0}""";
    }

    [Fact]
    public async Task N3_An_entry_beyond_1_MiB_after_a_large_entry_is_corrupt()
    {
        // Dos entradas: la primera (~900 KB) agranda el búfer; la segunda (~1,5 MiB) supera el límite de 1 MiB.
        var first = Entry("Documentos/a.txt", 0).Replace("\"a\":0", $"\"a\":0,\"x\":\"{new string('x', 900_000)}\"", StringComparison.Ordinal);
        var second = Entry("Documentos/b.txt", 5).Replace("\"a\":0", $"\"a\":0,\"x\":\"{new string('x', 1_500_000)}\"", StringComparison.Ordinal);

        var outcome = await TryReadAsync(HandManifest(entries: first + "," + second, files: 2, bytes: 5));

        var ex = Assert.IsType<RepositoryException>(outcome);
        Assert.Equal(RepositoryErrorKind.Corrupt, ex.Kind);
        Assert.Contains("demasiado grande", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task MEDIA2_Reader_counts_the_whole_header_including_unknown_properties(int extraBytes)
    {
        // Muchas propiedades desconocidas, cada una por debajo de la unidad, que en total llenan 16 MiB (+ extraBytes).
        var prefix = HandManifest(entries: "").Split("\"entries\":[", StringSplitOptions.None)[0];
        var target = SnapshotLimits.MaxHeaderBytes + extraBytes - Encoding.UTF8.GetByteCount(prefix) - EntriesMarker.Length;
        var padding = new StringBuilder();
        var index = 0;
        const int Overhead = 12; // "p00000":"…",
        while (target - padding.Length > 250_000)
        {
            padding.Append(CultureInfo.InvariantCulture, $"\"p{index++:D5}\":\"").Append('a', 200_000).Append("\",");
        }

        var last = target - padding.Length - Overhead;
        padding.Append(CultureInfo.InvariantCulture, $"\"p{index:D5}\":\"").Append('a', last).Append("\",");
        var manifest = prefix + padding + "\"entries\":[]}";
        Assert.Equal(SnapshotLimits.MaxHeaderBytes + extraBytes, HeaderLength(Encoding.UTF8.GetBytes(manifest)));

        var outcome = await TryReadAsync(manifest);

        if (extraBytes == 0)
        {
            Assert.Null(outcome);
        }
        else
        {
            var ex = Assert.IsType<RepositoryException>(outcome);
            Assert.Equal(RepositoryErrorKind.Corrupt, ex.Kind);
            Assert.Contains("cabecera", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task M2_Reader_cuts_at_the_entry_limit()
    {
        var entries = string.Join(",", Enumerable.Range(0, 4).Select(i => Entry($"Documentos/{i}.txt", 1)));
        var (storage, _, repository, _) = await CreateAsync();

        storage.SetFileContent($"snapshots/{ValidId}.json", Encoding.UTF8.GetBytes(HandManifest(entries: entries, files: 4, bytes: 4)));
        await using (var reader = await repository.OpenSnapshotAsync(ValidId, Ct))
        {
            reader.MaxEntries = 4;
            Assert.Equal(4, (await ReadAllAsync(reader)).Count);
        }

        await using (var reader = await repository.OpenSnapshotAsync(ValidId, Ct))
        {
            reader.MaxEntries = 3;
            var read = 0;
            var ex = await Assert.ThrowsAsync<RepositoryException>(async () =>
            {
                await foreach (var _ in reader.ReadEntriesAsync(Ct))
                {
                    read++;
                }
            });
            Assert.Equal(RepositoryErrorKind.Corrupt, ex.Kind);
            Assert.Equal(3, read);
        }
    }

    [Fact]
    public async Task B4_Reader_stops_at_the_first_entry_beyond_the_declared_file_count()
    {
        var entries = string.Join(",", Enumerable.Range(0, 1000).Select(i => Entry($"Documentos/{i}.txt", 1)));

        var (read, ex) = await ReadUntilFailureAsync(HandManifest(entries: entries, files: 2, bytes: 1000));

        Assert.Equal(RepositoryErrorKind.Corrupt, ex.Kind);
        Assert.Equal(2, read);
    }

    [Fact]
    public async Task B4_Reader_stops_at_the_first_entry_beyond_the_declared_bytes_without_overflowing()
    {
        // Dos tamaños de long.MaxValue sumarían más de lo representable: se corta en el segundo sin desbordar.
        var entries = Entry("Documentos/a.txt", long.MaxValue) + "," + Entry("Documentos/b.txt", long.MaxValue) + "," + Entry("Documentos/c.txt", 1);

        var (read, ex) = await ReadUntilFailureAsync(HandManifest(entries: entries, files: 3, bytes: long.MaxValue));

        Assert.Equal(RepositoryErrorKind.Corrupt, ex.Kind);
        Assert.Equal(1, read);
    }

    public static TheoryData<string> DevicePaths =>
    [
        "Documentos/CON",
        "Documentos/con.txt",
        "Documentos/sub/NUL",
        "Documentos/COM1.log",
        "Documentos/LPT9",
        "Documentos/AUX/a.txt",
        "Documentos/CONIN$",
        "Documentos/prn .txt",
    ];

    [Theory]
    [MemberData(nameof(DevicePaths))]
    public async Task M2_Paths_with_reserved_device_names_are_rejected_by_writer_and_reader(string path)
    {
        Assert.Throws<ArgumentException>(() => SnapshotEntry.File(path, HashOf("x"), 1, Modified, FileAttributes.None));

        var outcome = await TryReadAsync(HandManifest(entries: Entry(path, 1), files: 1, bytes: 1));
        Assert.True(outcome is RepositoryException { Kind: RepositoryErrorKind.Corrupt }, $"{path}: {outcome}");
    }

    [Fact]
    public async Task M2_Directories_and_aliases_with_reserved_device_names_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => SnapshotEntry.Directory("Documentos/PRN/"));
        var directory = await TryReadAsync(HandManifest(entries: "{\"p\":\"Documentos/PRN/\",\"d\":true}"));
        var alias = await TryReadAsync(HandManifest(sources: [("CON", "C:\\D")], entryAlias: "CON"));

        Assert.True(directory is RepositoryException { Kind: RepositoryErrorKind.Corrupt }, $"{directory}");
        Assert.True(alias is RepositoryException { Kind: RepositoryErrorKind.Corrupt }, $"{alias}");
    }

    // ================================================================== Auxiliares

    private static ContentHash HashOf(string text) => ContentHash.FromBytes(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static List<SnapshotWarning> Warnings(int count) =>
        [.. Enumerable.Range(0, count).Select(i => new SnapshotWarning($"Documentos/bloqueado {i}.pst", "FileLocked", "El archivo está en uso."))];

    private static IEnumerable<SnapshotEntry> Files(int count) =>
        Enumerable.Range(0, count).Select(i => SnapshotEntry.File($"Documentos/archivo {i}.txt", HashOf(i.ToString(CultureInfo.InvariantCulture)), i, Modified, FileAttributes.Archive));

    /// <summary>Bytes de la cabecera: desde la <c>{</c> inicial hasta <c>"entries":[</c> incluido.</summary>
    private static int HeaderLength(byte[] manifest) => manifest.AsSpan().IndexOf(EntriesMarker) + EntriesMarker.Length;

    /// <summary>
    /// Avisos que, con la cabecera de <see cref="Start"/>, dejan la cabecera en exactamente 16 MiB + <paramref name="extraBytes"/>:
    /// avisos medianos (unos 96 KB) y uno de relleno al final.
    /// </summary>
    private static async Task<(List<SnapshotWarning> Warnings, (long Fixed, List<int> Each) Sizes)> BuildWarningsForHeaderAsync(int extraBytes)
    {
        var medium = new SnapshotWarning(Ctl(16_000), "FileLocked", "m");
        var fixedBytes = await MeasureHeaderAsync([]);
        var mediumBytes = await MeasureHeaderAsync([medium]) - fixedBytes;
        var emptyBytes = await MeasureHeaderAsync([new SnapshotWarning("", "", "")]) - fixedBytes;

        long target = SnapshotLimits.MaxHeaderBytes + extraBytes;
        var count = (int)((target - fixedBytes + 1) / (mediumBytes + 1));
        var warnings = Enumerable.Repeat(medium, count).ToList();
        var sizes = Enumerable.Repeat(mediumBytes, count).ToList();
        var slack = target - (fixedBytes + (count * (long)mediumBytes) + count - 1);
        if (slack < emptyBytes + 1)
        {
            warnings.RemoveAt(warnings.Count - 1);
            sizes.RemoveAt(sizes.Count - 1);
            slack += mediumBytes + 1;
        }

        // El relleno ocupa slack − 1 bytes (lleva una coma delante): controles de 6 bytes y el resto en ASCII.
        var content = (int)(slack - 1 - emptyBytes);
        var filler = new SnapshotWarning(Ctl(content / 6) + new string('a', content % 6), "", "");
        warnings.Add(filler);
        sizes.Add(content + emptyBytes);
        Assert.Equal(target, fixedBytes + sizes.Sum(s => (long)s) + sizes.Count - 1);
        return (warnings, (fixedBytes, sizes));
    }

    private static async Task<int> MeasureHeaderAsync(IReadOnlyList<SnapshotWarning> warnings)
    {
        var (storage, _, repository, repositoryLock) = await CreateAsync();
        var written = await WriteAsync(repository, repositoryLock, Start(), [], warnings);
        return HeaderLength(storage.GetFileContent($"snapshots/{written.Id}.json"));
    }

    private static string Entry(string path, long size) =>
        $$"""{"p":{{JsonSerializer.Serialize(path)}},"h":"{{new string('a', 64)}}","s":{{size.ToString(CultureInfo.InvariantCulture)}},"m":"2026-01-31T10:22:01Z","a":0}""";

    private static string HandManifest(
        string jobId = "j",
        string jobName = "Documentos",
        string host = "PC",
        IEnumerable<(string Alias, string Path)>? sources = null,
        IEnumerable<(string Path, string Code, string Message)>? warnings = null,
        string extraHeader = "",
        string? entries = null,
        string? entryAlias = null,
        long files = 1,
        long bytes = 5,
        string trailer = "")
    {
        sources ??= [("Documentos", "C:\\D")];
        entries ??= Entry((entryAlias ?? "Documentos") + "/a.txt", 5);
        if (entries.Length == 0 || entries.Contains("\"d\":true", StringComparison.Ordinal))
        {
            files = 0;
            bytes = 0;
        }

        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"{{\"formatVersion\":1,\"id\":\"{ValidId}\",\"jobId\":{Json(jobId)},\"jobName\":{Json(jobName)},\"host\":{Json(host)},");
        builder.Append("\"startedUtc\":\"2026-11-15T02:00:00Z\",\"completedUtc\":\"2026-11-15T02:03:12Z\",\"parentId\":null,\"status\":\"completed\",\"usedVss\":false,");
        builder.Append("\"sources\":[").AppendJoin(',', sources.Select(s => $"{{\"alias\":{Json(s.Alias)},\"path\":{Json(s.Path)}}}")).Append("],");
        builder.Append(CultureInfo.InvariantCulture, $"\"stats\":{{\"files\":{files},\"bytes\":{bytes}}},");
        builder.Append("\"warnings\":[").AppendJoin(',', (warnings ?? []).Select(w => $"{{\"path\":{Json(w.Path)},\"code\":{Json(w.Code)},\"message\":{Json(w.Message)}}}")).Append("],");
        builder.Append(extraHeader);
        builder.Append("\"entries\":[").Append(entries).Append(']').Append(trailer).Append('}');
        return builder.ToString();
    }

    private static string Json(string value) => JsonSerializer.Serialize(value);

    /// <summary>Lee un manifiesto entero; devuelve la excepción o <see langword="null"/> si es válido.</summary>
    private static async Task<Exception?> TryReadAsync(string manifest)
    {
        var (storage, _, repository, _) = await CreateAsync();
        storage.SetFileContent($"snapshots/{ValidId}.json", Encoding.UTF8.GetBytes(manifest));
        try
        {
            await using var reader = await repository.OpenSnapshotAsync(ValidId, Ct);
            await ReadAllAsync(reader);
            return null;
        }
        catch (Exception ex) when (ex is RepositoryException or ArgumentException or JsonException or InvalidOperationException or OverflowException)
        {
            return ex;
        }
    }

    private static async Task<(int Read, RepositoryException Exception)> ReadUntilFailureAsync(string manifest)
    {
        var (storage, _, repository, _) = await CreateAsync();
        storage.SetFileContent($"snapshots/{ValidId}.json", Encoding.UTF8.GetBytes(manifest));
        await using var reader = await repository.OpenSnapshotAsync(ValidId, Ct);
        var read = 0;
        var ex = await Assert.ThrowsAsync<RepositoryException>(async () =>
        {
            await foreach (var _ in reader.ReadEntriesAsync(Ct))
            {
                read++;
            }
        });
        return (read, ex);
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
        SnapshotStart start,
        IEnumerable<SnapshotEntry> entries,
        IReadOnlyList<SnapshotWarning> warnings)
    {
        await using var writer = await repository.CreateSnapshotAsync(start, repositoryLock, Ct);
        foreach (var entry in entries)
        {
            await writer.AddAsync(entry, Ct);
        }

        return await writer.CompleteAsync(new SnapshotCompletion { Status = SnapshotStatus.CompletedWithWarnings, Warnings = warnings }, Ct);
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
