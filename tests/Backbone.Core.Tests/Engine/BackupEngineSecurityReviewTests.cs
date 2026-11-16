using System.Runtime.CompilerServices;
using System.Text;
using Backbone.Contracts;
using Backbone.Core.Engine;
using Backbone.Core.Repository;
using Backbone.Core.Repository.Locking;
using Backbone.Core.Repository.Snapshots;
using Backbone.Core.Storage;
using Backbone.Core.Tests.Support;
using static Backbone.Core.Tests.Support.EngineHarness;

namespace Backbone.Core.Tests.Engine;

/// <summary>
/// HU-13, correcciones de la revisión de seguridad: exclusiones obligatorias (criterio 8, ADR-16), snapshot padre con
/// fecha futura o dañado (ADR-17), memoria acotada por bytes (MEDIA-1, §8.4), puntos de reanálisis que aparecen después
/// de enumerar (MEDIA-2, §8.2) y objeto existente con otro tamaño (informativa 4).
/// </summary>
[Trait("Story", "HU-13")]
public sealed class BackupEngineSecurityReviewTests
{
    private const string SystemRoot = @"E:\Montado";
    private const string Shadow = @"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy3";
    private const string UntrustedName = "Backbone.untrusted-5f1c2a7e-0000-0000-0000-000000000001";
    private static readonly string[] RootChildNames = ["a.txt", "Grande", "Pequeña"];
    private static readonly DateTime T0 =new(2026, 11, 15, 2, 0, 0, DateTimeKind.Utc);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ================================================================== Criterio 8 (ADR-16)

    /// <summary>Árbol de un origen <c>C:\</c> con la carpeta de datos, una carpeta apartada y datos normales.</summary>
    private static InMemorySourceFileSystem SystemDrive(string root)
    {
        var source = new InMemorySourceFileSystem();
        source.AddFile(root + @"\Users\ana\doc.txt", "documento"u8.ToArray());
        source.AddFile(root + @"\ProgramData\Backbone\credentials\0123abcd.bin", "CREDENCIAL-CIFRADA"u8.ToArray());
        source.AddFile(root + @"\ProgramData\Backbone\config\jobs.json", "{}"u8.ToArray());
        source.AddFile(root + @"\ProgramData\" + UntrustedName + @"\credentials\viejo.bin", "CREDENCIAL-ANTIGUA"u8.ToArray());
        source.AddFile(root + @"\ProgramData\Otro\conservar.txt", "otro programa"u8.ToArray());
        source.AddFile(root + @"\ProgramData\Backbone.txt", "no es la carpeta de datos"u8.ToArray());
        return source;
    }

    private static BackupSource SystemSource(string readPath) =>
        new("C", @"C:\", readPath) { MandatoryExclusions = [@"ProgramData\Backbone", @"ProgramData\" + UntrustedName] };

    public static TheoryData<string> ReadRoots => [SystemRoot, Shadow];

    [Theory]
    [MemberData(nameof(ReadRoots))]
    [Trait("Requirement", "ADR-16")]
    public async Task HU13_8_The_data_folder_and_set_aside_folders_are_never_traversed_stored_or_uploaded_even_from_VSS(string readRoot)
    {
        var storage = new InMemoryRepositoryStorage();
        var source = SystemDrive(readRoot);
        var enumerated = new List<string>();
        source.OnEnumerate = enumerated.Add;

        var result = await RunAsync(storage, new ManualTimeProvider(), source, Request([SystemSource(readRoot)]));

        // Sin avisos de ejecución: la exclusión no convierte la copia en "con avisos" (RF-25).
        Assert.Equal(RunState.Completed, result.State);
        Assert.Empty(result.Warnings);
        Assert.Equal(0, result.Snapshot.Stats.Skipped);

        var paths = (await ReadEntriesAsync(storage, result.Snapshot.Id)).Select(e => e.Path).ToList();
        Assert.Equal(
            ["C/ProgramData/Backbone.txt", "C/ProgramData/Otro/conservar.txt", "C/Users/ana/doc.txt"],
            paths.Order(StringComparer.Ordinal));

        // No se ha enumerado ni abierto nada debajo, y ningún objeto tiene el contenido de las credenciales.
        Assert.DoesNotContain(enumerated, p => p.Contains(@"\Backbone\", StringComparison.Ordinal) || p.EndsWith(@"\Backbone", StringComparison.Ordinal) || p.Contains(UntrustedName, StringComparison.Ordinal));
        Assert.DoesNotContain(source.Opened, p => p.Contains(@"\Backbone\", StringComparison.Ordinal) || p.Contains(UntrustedName, StringComparison.Ordinal));
        Assert.DoesNotContain(Sha256("CREDENCIAL-CIFRADA"u8.ToArray()), string.Join('\n', ObjectPaths(storage)), StringComparison.Ordinal);
        Assert.DoesNotContain(Sha256("CREDENCIAL-ANTIGUA"u8.ToArray()), string.Join('\n', ObjectPaths(storage)), StringComparison.Ordinal);
        Assert.Equal(3, ObjectPaths(storage).Count);
        await RepositoryInvariants.AssertConsistentAsync(storage);
    }

    [Fact]
    [Trait("Requirement", "ADR-16")]
    public async Task HU13_8_Exclusions_ignore_case_and_produce_no_warning_even_if_the_folder_is_unreadable_or_a_link()
    {
        var storage = new InMemoryRepositoryStorage();
        var source = new InMemorySourceFileSystem();
        source.AddFile(SystemRoot + @"\programdata\BACKBONE\credentials\x.bin", "secreto"u8.ToArray());
        source.FailEnumerate(SystemRoot + @"\programdata\BACKBONE", new UnauthorizedAccessException("Acceso denegado."));
        source.AddFile(SystemRoot + @"\programdata\" + UntrustedName.ToUpperInvariant() + @"\x.bin", "secreto 2"u8.ToArray());
        source.MakeReparsePoint(SystemRoot + @"\programdata\" + UntrustedName.ToUpperInvariant());
        source.AddFile(SystemRoot + @"\programdata\datos.txt", "datos"u8.ToArray());

        var result = await RunAsync(storage, new ManualTimeProvider(), source, Request([SystemSource(SystemRoot)]));

        Assert.Equal(RunState.Completed, result.State);
        Assert.Empty(result.Warnings);
        Assert.Equal(["C/programdata/datos.txt"], (await ReadEntriesAsync(storage, result.Snapshot.Id)).Select(e => e.Path));
        Assert.DoesNotContain(source.Opened, p => p.Contains("x.bin", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Requirement", "ADR-16")]
    public async Task HU13_8_Exclusions_apply_only_to_their_own_source()
    {
        var storage = new InMemoryRepositoryStorage();
        var source = SystemDrive(SystemRoot);
        source.AddFile(@"F:\Otro\ProgramData\Backbone\copia.txt", "de otro disco"u8.ToArray());
        var other = new BackupSource("D", @"D:\", @"F:\Otro");

        var result = await RunAsync(storage, new ManualTimeProvider(), source, Request([SystemSource(SystemRoot), other]));

        var paths = (await ReadEntriesAsync(storage, result.Snapshot.Id)).Select(e => e.Path).ToList();
        Assert.Contains("D/ProgramData/Backbone/copia.txt", paths);
        Assert.DoesNotContain(paths, p => p.StartsWith("C/ProgramData/Backbone/", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Requirement", "ADR-16")]
    [Trait("Requirement", "RF-25")]
    public async Task HU13_8_Content_kept_from_the_parent_for_an_unreadable_folder_never_brings_back_an_excluded_folder()
    {
        // El snapshot padre es de antes de la exclusión (p. ej. una versión anterior) y contiene credenciales.
        var storage = new InMemoryRepositoryStorage();
        var time = new ManualTimeProvider();
        var source = SystemDrive(SystemRoot);
        var before = await RunAsync(storage, time, source, Request([new BackupSource("C", @"C:\", SystemRoot)]));
        Assert.Contains(await ReadEntriesAsync(storage, before.Snapshot.Id), e => e.Path == "C/ProgramData/Backbone/credentials/0123abcd.bin");
        time.Advance(TimeSpan.FromDays(1));

        // Ahora ProgramData no se puede leer: se conserva lo que tenía, salvo lo excluido.
        source.FailEnumerate(SystemRoot + @"\ProgramData", new UnauthorizedAccessException("Acceso denegado."));
        var result = await RunAsync(storage, time, source, Request([SystemSource(SystemRoot)]));

        var paths = (await ReadEntriesAsync(storage, result.Snapshot.Id)).Select(e => e.Path).ToList();
        Assert.Contains("C/ProgramData/Otro/conservar.txt", paths);
        Assert.Contains("C/ProgramData/Backbone.txt", paths);
        Assert.DoesNotContain(paths, p => p.StartsWith("C/ProgramData/Backbone/", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(paths, p => p.Contains(UntrustedName, StringComparison.OrdinalIgnoreCase));
        Assert.Equal("C/ProgramData", Assert.Single(result.Warnings).Path);
    }

    public static TheoryData<string> InvalidExclusions =>
        ["", @"..\Backbone", @"ProgramData\..\Backbone", @"C:\ProgramData\Backbone", @"\ProgramData", @"ProgramData\\Backbone", @"ProgramData\.\Backbone", @"ProgramData\Backbone\", "a:b"];

    [Theory]
    [MemberData(nameof(InvalidExclusions))]
    [Trait("Requirement", "ADR-16")]
    public async Task HU13_8_Invalid_exclusions_reject_the_run_before_reading_anything(string exclusion)
    {
        var storage = new InMemoryRepositoryStorage();
        var source = SystemDrive(SystemRoot);

        await Assert.ThrowsAsync<ArgumentException>(
            () => RunAsync(storage, new ManualTimeProvider(), source, Request([new BackupSource("C", @"C:\", SystemRoot) { MandatoryExclusions = [exclusion] }])));

        Assert.Equal(0, source.EnumerationCount);
        Assert.Empty(await ListSnapshotsAsync(storage));
    }

    [Fact]
    [Trait("Requirement", "ADR-16")]
    public async Task HU13_8_Forward_slashes_and_single_file_exclusions_are_accepted()
    {
        var storage = new InMemoryRepositoryStorage();
        var source = SystemDrive(SystemRoot);
        var request = Request([new BackupSource("C", @"C:\", SystemRoot) { MandatoryExclusions = ["ProgramData/Backbone", "ProgramData/" + UntrustedName, "Users/ana/doc.txt"] }]);

        var result = await RunAsync(storage, new ManualTimeProvider(), source, request);

        Assert.Equal(
            ["C/ProgramData/Backbone.txt", "C/ProgramData/Otro/conservar.txt", "C/Users/ana/"],
            (await ReadEntriesAsync(storage, result.Snapshot.Id)).Select(e => e.Path).Order(StringComparer.Ordinal));
        Assert.Empty(result.Warnings);
    }

    // ================================================================== Snapshot padre (ADR-17)

    /// <summary>Copia con la fecha de inicio indicada (el reloj del motor no interviene en la elección del padre).</summary>
    private static Task<BackupResult> RunAtAsync(InMemoryRepositoryStorage storage, ManualTimeProvider time, ISourceFileSystem source, DateTime startedUtc, LockOwner? owner = null) =>
        RunAsync(storage, time, source, Request() with { StartedUtc = startedUtc }, owner);

    public static TheoryData<int, bool> FutureOffsets => new()
    {
        { 0, true },
        { 15 * 60, true },        // justo en la tolerancia: se acepta
        { (15 * 60) + 1, false }, // un segundo más: se descarta
        { 30 * 24 * 3600, false },
    };

    [Theory]
    [MemberData(nameof(FutureOffsets))]
    [Trait("Requirement", "ADR-17")]
    public async Task HU13_Parent_A_snapshot_dated_after_the_start_plus_the_clock_tolerance_is_not_used_as_parent(int offsetSeconds, bool accepted)
    {
        var storage = new InMemoryRepositoryStorage();
        var time = new ManualTimeProvider();
        var source = SampleSource();
        var start = T0.AddDays(1);
        var legit = await RunAtAsync(storage, time, source, T0);
        var planted = await RunAtAsync(storage, time, source, start.AddSeconds(offsetSeconds), new LockOwner("PC-PRUEBA", 5000));

        var result = await RunAtAsync(storage, time, source, start, new LockOwner("PC-PRUEBA", 5001));

        if (accepted)
        {
            Assert.Equal(planted.Snapshot.Id, result.Snapshot.ParentId);
            Assert.Empty(result.IgnoredParents);
        }
        else
        {
            Assert.Equal(legit.Snapshot.Id, result.Snapshot.ParentId);
            var ignored = Assert.Single(result.IgnoredParents);
            Assert.Equal(1, result.IgnoredParentCount);
            Assert.Equal(planted.Snapshot.Id, ignored.SnapshotId);
            Assert.False(ignored.HeaderRead); // descartado por la fecha del identificador, sin leer su cabecera
            Assert.Equal(start + RepositoryLockPolicy.StaleAfter, ignored.LimitUtc);
            Assert.True(ignored.DateUtc > ignored.LimitUtc);
        }

        Assert.Equal(RunState.Completed, result.State);
        Assert.Equal(0, result.Snapshot.Stats.NewObjects);
    }

    [Fact]
    [Trait("Requirement", "ADR-17")]
    public async Task HU13_Parent_A_snapshot_whose_header_start_is_in_the_future_is_ignored_even_if_its_name_is_not()
    {
        var storage = new InMemoryRepositoryStorage();
        var time = new ManualTimeProvider();
        var source = SampleSource();
        var legit = await RunAtAsync(storage, time, source, T0);
        var planted = await RunAtAsync(storage, time, source, T0.AddDays(30), new LockOwner("PC-PRUEBA", 5000));

        // Se renombra con una fecha pasada, manteniendo startedUtc futuro en la cabecera.
        var renamed = "20261115T030000Z" + planted.Snapshot.Id[16..];
        var text = Encoding.UTF8.GetString(storage.GetFileContent($"snapshots/{planted.Snapshot.Id}.json"));
        storage.SetFileContent($"snapshots/{renamed}.json", Encoding.UTF8.GetBytes(text.Replace(planted.Snapshot.Id, renamed, StringComparison.Ordinal)));
        await storage.DeleteFileAsync($"snapshots/{planted.Snapshot.Id}.json", Ct);

        var result = await RunAtAsync(storage, time, source, T0.AddDays(1), new LockOwner("PC-PRUEBA", 5001));

        Assert.Equal(legit.Snapshot.Id, result.Snapshot.ParentId);
        var ignored = Assert.Single(result.IgnoredParents);
        Assert.Equal(renamed, ignored.SnapshotId);
        Assert.True(ignored.HeaderRead);
        Assert.Equal(T0.AddDays(30), ignored.DateUtc);
    }

    [Fact]
    [Trait("Requirement", "ADR-17")]
    public async Task HU13_Parent_A_future_snapshot_is_discarded_by_its_name_without_reading_its_header_even_if_it_is_damaged()
    {
        var storage = new InMemoryRepositoryStorage();
        var time = new ManualTimeProvider();
        var source = SampleSource();
        var legit = await RunAtAsync(storage, time, source, T0);
        storage.SetFileContent("snapshots/20270101T000000Z-dead.json", "{ basura"u8);

        var result = await RunAtAsync(storage, time, source, T0.AddDays(1), new LockOwner("PC-PRUEBA", 5001));

        Assert.Equal(legit.Snapshot.Id, result.Snapshot.ParentId);
        Assert.Equal("20270101T000000Z-dead", Assert.Single(result.IgnoredParents).SnapshotId);
    }

    [Fact]
    [Trait("Requirement", "ADR-17")]
    public async Task HU13_Parent_An_unreadable_header_that_has_to_be_read_fails_the_run_with_Corrupt_and_publishes_nothing()
    {
        var storage = new InMemoryRepositoryStorage();
        var time = new ManualTimeProvider();
        var source = SampleSource();
        var legit = await RunAtAsync(storage, time, source, T0);
        storage.SetFileContent("snapshots/20261115T120000Z-dead.json", "{ basura"u8);
        var published = RepositoryInvariants.CaptureImmutableFiles(storage);
        source.ResetCounters();

        var ex = await Assert.ThrowsAsync<RepositoryException>(() => RunAtAsync(storage, time, source, T0.AddDays(1), new LockOwner("PC-PRUEBA", 5001)));

        Assert.Equal(RepositoryErrorKind.Corrupt, ex.Kind);
        Assert.Equal([legit.Snapshot.Id, "20261115T120000Z-dead"], await ListSnapshotsAsync(storage));
        RepositoryInvariants.AssertPreserved(published, storage);
        Assert.DoesNotContain(storage.FilePaths, p => p.StartsWith("tmp/", StringComparison.Ordinal));
        Assert.Equal(0, source.EnumerationCount); // falla antes de leer el origen
    }

    [Fact]
    [Trait("Requirement", "ADR-17")]
    public async Task HU13_Parent_A_parent_whose_entries_are_corrupt_fails_the_run_with_Corrupt_and_publishes_nothing()
    {
        var storage = new InMemoryRepositoryStorage();
        var time = new ManualTimeProvider();
        var source = SampleSource();
        var legit = await RunAtAsync(storage, time, source, T0);
        var manifest = $"snapshots/{legit.Snapshot.Id}.json";
        var text = Encoding.UTF8.GetString(storage.GetFileContent(manifest));
        storage.SetFileContent(manifest, Encoding.UTF8.GetBytes(text.Replace("Documentos/notas.txt", "Documentos/../x.txt", StringComparison.Ordinal)));

        var ex = await Assert.ThrowsAsync<RepositoryException>(() => RunAtAsync(storage, time, source, T0.AddDays(1), new LockOwner("PC-PRUEBA", 5001)));

        Assert.Equal(RepositoryErrorKind.Corrupt, ex.Kind);
        Assert.Equal([legit.Snapshot.Id], await ListSnapshotsAsync(storage));
    }

    public static TheoryData<int> ParentReadFailures => [1, 2];

    [Theory]
    [MemberData(nameof(ParentReadFailures))]
    [Trait("Requirement", "ADR-17")]
    public async Task HU13_Parent_An_IO_error_reading_the_header_or_the_entries_of_the_parent_is_an_IO_error_not_Corrupt(int failingOpen)
    {
        // Apertura 1: cabecera al elegir el padre; apertura 2: entradas del padre (índice incremental).
        var inner = new InMemoryRepositoryStorage();
        var time = new ManualTimeProvider();
        var source = SampleSource();
        var legit = await RunAtAsync(inner, time, source, T0);
        var storage = new ReadFailingStorage(inner, $"snapshots/{legit.Snapshot.Id}.json", failingOpen);

        var ex = await Assert.ThrowsAnyAsync<IOException>(
            () => RunAsync(storage, time, source, Request() with { StartedUtc = T0.AddDays(1) }, new LockOwner("PC-PRUEBA", 5001)));

        Assert.Contains("E/S simulado", ex.Message, StringComparison.Ordinal);
        Assert.Equal([legit.Snapshot.Id], await ListSnapshotsAsync(inner));
        Assert.DoesNotContain(inner.FilePaths, p => p.StartsWith("tmp/", StringComparison.Ordinal));
    }

    // ================================================================== Memoria (MEDIA-1, §8.4)

    private static long WarningBytes(SnapshotWarning w) =>
        Encoding.UTF8.GetByteCount(w.Path) + Encoding.UTF8.GetByteCount(w.Code) + Encoding.UTF8.GetByteCount(w.Message);

    private static InMemorySourceFileSystem LockedFiles(int count)
    {
        var source = new InMemorySourceFileSystem();
        source.AddFile(DocumentsRoot + @"\bien.txt", "bien"u8.ToArray());
        for (var i = 0; i < count; i++)
        {
            var path = DocumentsRoot + $@"\bloqueados\{i:D2}.txt";
            source.AddFile(path, []);
            source.FailOpen(path, new SourceAccessException(RunIssueCode.FileLocked, "bloqueado"));
        }

        return source;
    }

    [Fact]
    [Trait("Requirement", "RNF-05")]
    public async Task HU13_Memory_Retained_warnings_are_bounded_by_their_UTF8_size_and_the_rest_are_counted_and_summarized()
    {
        var probe = await RunAsync(new InMemoryRepositoryStorage(), new ManualTimeProvider(), LockedFiles(20), Request([DefaultSources[0]]));
        var perWarning = WarningBytes(probe.Warnings[0]);
        Assert.All(probe.Warnings, w => Assert.Equal(perWarning, WarningBytes(w)));

        var storage = new InMemoryRepositoryStorage();
        var result = await RunAsync(storage, new ManualTimeProvider(), LockedFiles(20), Request([DefaultSources[0]]), configure: e => e.MaxRetainedWarningBytes = (5 * perWarning) + perWarning - 1);

        Assert.Equal(20, result.WarningCount);
        Assert.Equal(probe.Warnings.Take(5), result.Warnings);
        Assert.True(result.Warnings.Sum(WarningBytes) <= (6 * perWarning) - 1);
        Assert.Equal(RunState.CompletedWithWarnings, result.State);

        // El snapshot guarda los cinco primeros y el resumen de los otros quince.
        Assert.Equal(6, result.Snapshot.Warnings.Count);
        Assert.Equal(probe.Warnings.Take(5).Select(w => w.Path), result.Snapshot.Warnings.Take(5).Select(w => w.Path));
        var summary = result.Snapshot.Warnings[^1];
        Assert.Equal(nameof(RunIssueCode.WarningsTruncated), summary.Code);
        Assert.StartsWith("15 avisos más", summary.Message, StringComparison.Ordinal);
        Assert.Single(await ReadEntriesAsync(storage, result.Snapshot.Id), e => !e.IsDirectory); // el lector lo acepta
    }

    [Fact]
    [Trait("Requirement", "RNF-05")]
    public async Task HU13_Memory_Once_a_warning_does_not_fit_later_smaller_ones_are_only_counted_to_keep_the_order()
    {
        var storage = new InMemoryRepositoryStorage();
        var request = Request([DefaultSources[0]]) with
        {
            AdditionalWarnings =
            [
                new SnapshotWarning(string.Empty, nameof(RunIssueCode.Other), new string('x', 2000)),
                new SnapshotWarning(string.Empty, nameof(RunIssueCode.Other), "corto"),
            ],
        };

        var result = await RunAsync(storage, new ManualTimeProvider(), LockedFiles(0), request, configure: e => e.MaxRetainedWarningBytes = 1000);

        Assert.Empty(result.Warnings);
        Assert.Equal(2, result.WarningCount);
        Assert.Equal(RunState.CompletedWithWarnings, result.State);
        var summary = Assert.Single(result.Snapshot.Warnings);
        Assert.Equal(nameof(RunIssueCode.WarningsTruncated), summary.Code);
        Assert.StartsWith("2 avisos más", summary.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Requirement", "RNF-05")]
    public async Task HU13_Memory_The_count_limit_still_applies_and_warning_texts_are_trimmed_to_their_limits()
    {
        var storage = new InMemoryRepositoryStorage();
        var request = Request([DefaultSources[0]]) with
        {
            AdditionalWarnings = [new SnapshotWarning(new string('p', 40_000), new string('c', 100), new string('m', 5000))],
        };

        var result = await RunAsync(storage, new ManualTimeProvider(), LockedFiles(10), request, configure: e => e.MaxRetainedWarnings = 3);

        Assert.Equal(11, result.WarningCount);
        Assert.Equal(3, result.Warnings.Count);
        var first = result.Warnings[0];
        Assert.Equal(SnapshotLimits.MaxWarningPathLength, first.Path.Length);
        Assert.Equal(SnapshotLimits.MaxWarningCodeLength, first.Code.Length);
        Assert.Equal(SnapshotLimits.MaxWarningMessageLength, first.Message.Length);
        Assert.StartsWith("8 avisos más", result.Snapshot.Warnings[^1].Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Requirement", "§6.3")]
    public void HU13_Memory_The_writer_always_adds_the_summary_when_the_caller_already_omitted_warnings_even_if_few()
    {
        var kept = new[] { new SnapshotWarning("Documentos/a.txt", nameof(RunIssueCode.FileLocked), "bloqueado") };

        var selected = SnapshotWriter.SelectWarnings(kept, fixedHeaderBytes: 1000, omitted: 3);

        Assert.Equal(2, selected.Count);
        Assert.Equal(kept[0], selected[0]);
        Assert.Equal(nameof(RunIssueCode.WarningsTruncated), selected[1].Code);
        Assert.StartsWith("3 avisos más", selected[1].Message, StringComparison.Ordinal);
        Assert.Single(SnapshotWriter.SelectWarnings(kept, fixedHeaderBytes: 1000));
    }

    [Fact]
    [Trait("Requirement", "RNF-05")]
    public void HU13_Memory_Default_budgets_are_the_ones_of_the_specification()
    {
        var engine = new BackupEngine(TimeProvider.System);
        Assert.Equal(128L * 1024 * 1024, BackupEngine.DefaultListingBudgetBytes);
        Assert.Equal(BackupEngine.DefaultListingBudgetBytes, engine.ListingBudgetBytes);
        Assert.Equal(10_000, engine.MaxRetainedWarnings);
        Assert.Equal((16L * 1024 * 1024) - (256 * 1024), engine.MaxRetainedWarningBytes);
        Assert.Equal((2L * (3 + 10)) + 64, BackupEngine.EstimateListingBytes(new SourceEntry("abc", "0123456789", false, 0, default, default)));
    }

    private static long FrameBytes(string fullPath, string snapshotPath) => (2L * (fullPath.Length + snapshotPath.Length)) + 64;

    private static InMemorySourceFileSystem ListingSource(int bigCount)
    {
        var source = new InMemorySourceFileSystem();
        source.AddFile(DocumentsRoot + @"\a.txt", "a"u8.ToArray());
        for (var i = 0; i < bigCount; i++)
        {
            source.AddFile(DocumentsRoot + $@"\Grande\f{i:D3}.txt", Encoding.UTF8.GetBytes($"grande {i}"));
        }

        source.AddFile(DocumentsRoot + @"\Pequeña\x.txt", "x"u8.ToArray());
        return source;
    }

    [Fact]
    [Trait("Requirement", "RNF-05")]
    [Trait("Requirement", "RF-25")]
    public async Task HU13_Memory_A_folder_whose_listing_exceeds_the_budget_is_unreadable_with_ReadError_and_keeps_its_previous_content()
    {
        const int big = 50;
        var storage = new InMemoryRepositoryStorage();
        var time = new ManualTimeProvider();
        var source = ListingSource(big);
        var first = await RunAsync(storage, time, source, Request([DefaultSources[0]]));
        time.Advance(TimeSpan.FromDays(1));
        source.Modify(DocumentsRoot + @"\Grande\f000.txt", "cambiado"u8.ToArray());

        // Caben la raíz con sus tres hijos y una subcarpeta con 10 elementos, no los 50 de Grande.
        var rootChildren = RootChildNames.Sum(n => (2L * (n.Length + (DocumentsRoot + "\\" + n).Length)) + 64);
        var bigChild = (2L * ("f000.txt".Length + (DocumentsRoot + @"\Grande\f000.txt").Length)) + 64;
        var budget = FrameBytes(DocumentsRoot, "Documentos") + rootChildren + FrameBytes(DocumentsRoot + @"\Grande", "Documentos/Grande") + (10 * bigChild);
        var counting = new CountingSource(source);
        BackupEngine? engine = null;

        var result = await RunAsync(storage, time, counting, Request([DefaultSources[0]]), configure: e => { e.ListingBudgetBytes = budget; engine = e; });

        Assert.NotNull(engine);
        Assert.True(engine.PeakListingBytes <= budget, $"{engine.PeakListingBytes} > {budget}");
        Assert.Equal(RunState.CompletedWithWarnings, result.State);
        var warning = Assert.Single(result.Warnings);
        Assert.Equal("Documentos/Grande", warning.Path);
        Assert.Equal(nameof(RunIssueCode.ReadError), warning.Code);
        Assert.Contains("demasiados elementos", warning.Message, StringComparison.Ordinal);

        // Se dejó de enumerar en cuanto no cupo (no se consumió la carpeta entera).
        Assert.True(counting.Yielded(DocumentsRoot + @"\Grande") <= 11, $"{counting.Yielded(DocumentsRoot + @"\Grande")} elementos leídos");

        // Lo de Grande se conserva del padre (la versión anterior de f000.txt); lo demás se copia.
        var before = await ReadEntriesAsync(storage, first.Snapshot.Id);
        var after = await ReadEntriesAsync(storage, result.Snapshot.Id);
        Assert.Equal(
            before.Where(e => e.Path.StartsWith("Documentos/Grande/", StringComparison.Ordinal)).OrderBy(e => e.Path, StringComparer.Ordinal),
            after.Where(e => e.Path.StartsWith("Documentos/Grande/", StringComparison.Ordinal)).OrderBy(e => e.Path, StringComparer.Ordinal));
        Assert.Equal(big, after.Count(e => e.Path.StartsWith("Documentos/Grande/", StringComparison.Ordinal)));
        Assert.Contains(after, e => e.Path == "Documentos/Pequeña/x.txt");
        Assert.Contains(after, e => e.Path == "Documentos/a.txt");
        Assert.Equal(1, result.Snapshot.Stats.Skipped);
    }

    [Fact]
    [Trait("Requirement", "RNF-05")]
    public async Task HU13_Memory_A_source_root_that_does_not_fit_is_kept_from_the_parent_and_without_parent_is_only_a_warning()
    {
        var storage = new InMemoryRepositoryStorage();
        var time = new ManualTimeProvider();
        var source = ListingSource(5);

        var noParent = await RunAsync(storage, time, source, Request([DefaultSources[0]]), configure: e => e.ListingBudgetBytes = 100);
        Assert.Equal("Documentos", Assert.Single(noParent.Warnings).Path);
        Assert.Empty(await ReadEntriesAsync(storage, noParent.Snapshot.Id));

        time.Advance(TimeSpan.FromDays(1));
        var full = await RunAsync(storage, time, source, Request([DefaultSources[0]]));
        time.Advance(TimeSpan.FromDays(1));
        var kept = await RunAsync(storage, time, source, Request([DefaultSources[0]]), configure: e => e.ListingBudgetBytes = 100);

        Assert.Equal(
            (await ReadEntriesAsync(storage, full.Snapshot.Id)).OrderBy(e => e.Path, StringComparer.Ordinal),
            (await ReadEntriesAsync(storage, kept.Snapshot.Id)).OrderBy(e => e.Path, StringComparer.Ordinal));
        Assert.Equal(nameof(RunIssueCode.ReadError), Assert.Single(kept.Warnings).Code);
    }

    [Fact]
    [Trait("Requirement", "RNF-05")]
    public async Task HU13_Memory_Random_trees_never_exceed_the_listing_budget_and_never_lose_a_file_silently()
    {
        var random = new Random(20261116);
        for (var round = 0; round < 25; round++)
        {
            var source = new InMemorySourceFileSystem();
            var files = new List<string>();
            AddRandomTree(random, source, DocumentsRoot, "Documentos", depth: 0, files);
            var budget = random.Next(600, 6000);
            var storage = new InMemoryRepositoryStorage();
            BackupEngine? engine = null;

            var result = await RunAsync(storage, new ManualTimeProvider(), source, Request([DefaultSources[0]]), configure: e => { e.ListingBudgetBytes = budget; engine = e; });

            Assert.NotNull(engine);
            Assert.True(engine.PeakListingBytes <= budget, $"Ronda {round}: {engine.PeakListingBytes} > {budget}");
            var stored = (await ReadEntriesAsync(storage, result.Snapshot.Id)).Select(e => e.Path).ToHashSet(StringComparer.Ordinal);
            var unreadable = result.Warnings.Where(w => w.Code == nameof(RunIssueCode.ReadError)).Select(w => w.Path + "/").ToList();
            Assert.Equal(result.WarningCount, result.Warnings.Count);
            foreach (var file in files)
            {
                Assert.True(
                    stored.Contains(file) || unreadable.Any(prefix => file.StartsWith(prefix, StringComparison.Ordinal)),
                    $"Ronda {round}: {file} ni se ha guardado ni tiene aviso.");
            }
        }
    }

    private static void AddRandomTree(Random random, InMemorySourceFileSystem source, string fullPath, string snapshotPath, int depth, List<string> files)
    {
        source.AddDirectory(fullPath);
        var fileCount = random.Next(0, 12);
        for (var i = 0; i < fileCount; i++)
        {
            var name = $"f{i}" + new string('n', random.Next(0, 40)) + ".txt";
            source.AddFile(fullPath + "\\" + name, [(byte)i]);
            files.Add(snapshotPath + "/" + name);
        }

        if (depth < 4)
        {
            var directoryCount = random.Next(0, 4);
            for (var i = 0; i < directoryCount; i++)
            {
                var name = $"d{i}" + new string('c', random.Next(0, 30));
                AddRandomTree(random, source, fullPath + "\\" + name, snapshotPath + "/" + name, depth + 1, files);
            }
        }
    }

    // ================================================================== Puntos de reanálisis después de enumerar (MEDIA-2, §8.2)

    [Fact]
    [Trait("Requirement", "RF-06")]
    public async Task HU13_Reparse_A_folder_that_becomes_a_junction_after_listing_is_skipped_with_ReparsePointSkipped_and_not_followed()
    {
        var storage = new InMemoryRepositoryStorage();
        var time = new ManualTimeProvider();
        var inner = SampleSource();
        var first = await RunAsync(storage, time, inner);
        time.Advance(TimeSpan.FromDays(1));
        inner.ResetCounters();
        var source = new ReparseSwitchingSource(inner);
        source.EnumerateBecomesLink.Add(DocumentsRoot + @"\Facturas");

        var result = await RunAsync(storage, time, source);

        var warning = Assert.Single(result.Warnings);
        Assert.Equal(nameof(RunIssueCode.ReparsePointSkipped), warning.Code);
        Assert.Equal("Documentos/Facturas", warning.Path);
        Assert.Equal(RunState.CompletedWithWarnings, result.State);

        // No se sigue ni se conserva: se trata igual que un enlace que ya devolvía la enumeración (RF-06).
        var entries = await ReadEntriesAsync(storage, result.Snapshot.Id);
        Assert.DoesNotContain(entries, e => e.Path.StartsWith("Documentos/Facturas", StringComparison.Ordinal));
        Assert.DoesNotContain(inner.Opened, p => p.Contains("Facturas", StringComparison.Ordinal));
        Assert.Contains(await ReadEntriesAsync(storage, first.Snapshot.Id), e => e.Path.StartsWith("Documentos/Facturas/", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Requirement", "RF-06")]
    public async Task HU13_Reparse_A_source_root_that_becomes_a_junction_is_skipped_and_the_other_sources_are_copied()
    {
        var storage = new InMemoryRepositoryStorage();
        var inner = SampleSource();
        var source = new ReparseSwitchingSource(inner);
        source.EnumerateBecomesLink.Add(DocumentsRoot);

        var result = await RunAsync(storage, new ManualTimeProvider(), source);

        Assert.Equal(("Documentos", nameof(RunIssueCode.ReparsePointSkipped)), (Assert.Single(result.Warnings).Path, result.Warnings[0].Code));
        var entries = await ReadEntriesAsync(storage, result.Snapshot.Id);
        Assert.DoesNotContain(entries, e => e.Path.StartsWith("Documentos", StringComparison.Ordinal));
        Assert.Contains(entries, e => e.Path == "Fotos/año 2026/playa ñ.jpg");
    }

    [Fact]
    [Trait("Requirement", "RF-06")]
    public async Task HU13_Reparse_A_file_that_becomes_a_link_before_opening_is_skipped_without_object_or_previous_version()
    {
        var storage = new InMemoryRepositoryStorage();
        var time = new ManualTimeProvider();
        var inner = SampleSource();
        await RunAsync(storage, time, inner);
        time.Advance(TimeSpan.FromDays(1));
        inner.Modify(DocumentsRoot + @"\notas.txt", "nuevo contenido que no se debe subir"u8.ToArray());
        var source = new ReparseSwitchingSource(inner);
        source.OpenBecomesLink.Add(DocumentsRoot + @"\notas.txt");
        var objectsBefore = ObjectPaths(storage);

        var result = await RunAsync(storage, time, source);

        var warning = Assert.Single(result.Warnings);
        Assert.Equal(("Documentos/notas.txt", nameof(RunIssueCode.ReparsePointSkipped)), (warning.Path, warning.Code));
        Assert.DoesNotContain(await ReadEntriesAsync(storage, result.Snapshot.Id), e => e.Path == "Documentos/notas.txt");
        Assert.Equal(objectsBefore, ObjectPaths(storage));
        Assert.Equal(1, result.Snapshot.Stats.Skipped);
    }

    [Fact]
    [Trait("Requirement", "RF-06")]
    public async Task HU13_Reparse_A_file_that_becomes_a_link_after_being_copied_is_not_stored()
    {
        // "Cambia a enlace tras enumerar": se abrió y copió el archivo real, pero al volver a mirarlo por handle ya es un
        // enlace. La implementación lo detecta (§8.2) y el motor no lo guarda.
        var storage = new InMemoryRepositoryStorage();
        var inner = SampleSource();
        var source = new ReparseSwitchingSource(inner);
        source.InfoBecomesLink.Add(DocumentsRoot + @"\notas.txt");

        var result = await RunAsync(storage, new ManualTimeProvider(), source);

        var warning = Assert.Single(result.Warnings);
        Assert.Equal(("Documentos/notas.txt", nameof(RunIssueCode.ReparsePointSkipped)), (warning.Path, warning.Code));
        Assert.DoesNotContain(await ReadEntriesAsync(storage, result.Snapshot.Id), e => e.Path == "Documentos/notas.txt");
        Assert.Contains(await ReadEntriesAsync(storage, result.Snapshot.Id), e => e.Path == "Documentos/oculto.ini");
        await RepositoryInvariants.AssertConsistentAsync(storage);
    }

    [Fact]
    [Trait("Requirement", "RF-06")]
    public async Task HU13_Reparse_Metadata_that_report_a_reparse_point_after_the_copy_also_skip_the_file()
    {
        var storage = new InMemoryRepositoryStorage();
        var inner = SampleSource();
        var source = new ReparseSwitchingSource(inner);
        source.InfoReportsLink.Add(DocumentsRoot + @"\notas.txt");

        var result = await RunAsync(storage, new ManualTimeProvider(), source);

        Assert.Equal(nameof(RunIssueCode.ReparsePointSkipped), Assert.Single(result.Warnings).Code);
        Assert.DoesNotContain(await ReadEntriesAsync(storage, result.Snapshot.Id), e => e.Path == "Documentos/notas.txt");
    }

    // ================================================================== Objeto existente (informativa 4)

    [Fact]
    [Trait("Requirement", "§6.4.2")]
    public async Task HU13_Dedup_An_existing_object_with_a_different_size_fails_the_run_with_Corrupt_and_is_not_touched()
    {
        var storage = new InMemoryRepositoryStorage();
        var time = new ManualTimeProvider();
        var source = SampleSource();
        await OpenOrInitializeAsync(storage, time, Ct);
        var hash = Sha256("hola, mundo"u8.ToArray());
        await storage.CreateDirectoryAsync($"objects/{hash[..2]}", Ct);
        storage.SetFileContent($"objects/{hash[..2]}/{hash}", "dañado"u8);

        var ex = await Assert.ThrowsAsync<RepositoryException>(() => RunAsync(storage, time, source));

        Assert.Equal(RepositoryErrorKind.Corrupt, ex.Kind);
        Assert.Contains(hash, ex.Message, StringComparison.Ordinal);
        Assert.Empty(await ListSnapshotsAsync(storage));
        Assert.Equal("dañado"u8.ToArray(), storage.GetFileContent($"objects/{hash[..2]}/{hash}"));
        Assert.DoesNotContain(storage.FilePaths, p => p.StartsWith("tmp/", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Requirement", "§6.4.2")]
    public async Task HU13_Dedup_An_existing_object_with_the_right_size_is_reused_after_a_metadata_check_only()
    {
        var inner = new InMemoryRepositoryStorage();
        var time = new ManualTimeProvider();
        var source = new InMemorySourceFileSystem();
        source.AddFile(DocumentsRoot + @"\a.txt", "igual"u8.ToArray());
        source.AddFile(DocumentsRoot + @"\b.txt", "igual"u8.ToArray());
        var hash = Sha256("igual"u8.ToArray());
        var storage = new ReadFailingStorage(inner, $"objects/{hash[..2]}/{hash}", failingOpen: 1);

        var result = await RunAsync(storage, time, source, Request([DefaultSources[0]]));

        // El segundo archivo reutiliza el objeto sin abrirlo (abrirlo fallaría).
        Assert.Equal(1, result.Snapshot.Stats.NewObjects);
        Assert.Equal(0, storage.Opens);
        Assert.Equal(2, (await ReadEntriesAsync(inner, result.Snapshot.Id)).Count);
    }

    // ================================================================== Auxiliares

    /// <summary>Hace fallar con <see cref="IOException"/> la apertura número <c>failingOpen</c> de un archivo.</summary>
    private sealed class ReadFailingStorage(IRepositoryStorage inner, string path, int failingOpen) : IRepositoryStorage
    {
        private int opens;

        public int Opens => Volatile.Read(ref opens);

        public Task<Stream> OpenReadAsync(string path1, CancellationToken cancellationToken)
        {
            if (string.Equals(path1, path, StringComparison.Ordinal) && Interlocked.Increment(ref opens) == failingOpen)
            {
                throw new IOException("Error de E/S simulado.");
            }

            return inner.OpenReadAsync(path1, cancellationToken);
        }

        public Task<Stream> CreateNewAsync(string path1, CancellationToken cancellationToken) => inner.CreateNewAsync(path1, cancellationToken);

        public Task<bool> TryMoveAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken) =>
            inner.TryMoveAsync(sourcePath, destinationPath, overwrite, cancellationToken);

        public Task DeleteFileAsync(string path1, CancellationToken cancellationToken) => inner.DeleteFileAsync(path1, cancellationToken);

        public Task CreateDirectoryAsync(string path1, CancellationToken cancellationToken) => inner.CreateDirectoryAsync(path1, cancellationToken);

        public Task<bool> DirectoryExistsAsync(string path1, CancellationToken cancellationToken) => inner.DirectoryExistsAsync(path1, cancellationToken);

        public Task<StorageEntry?> GetFileInfoAsync(string path1, CancellationToken cancellationToken) => inner.GetFileInfoAsync(path1, cancellationToken);

        public IAsyncEnumerable<StorageEntry> EnumerateAsync(string directoryPath, CancellationToken cancellationToken) => inner.EnumerateAsync(directoryPath, cancellationToken);
    }

    /// <summary>
    /// Simula la implementación de §8.2 cuando un elemento se sustituye por un <i>junction</i> o un enlace después de
    /// enumerarlo: al abrirlo por handle detecta el punto de reanálisis y lanza
    /// <see cref="SourceAccessException"/> con <see cref="RunIssueCode.ReparsePointSkipped"/>.
    /// </summary>
    private sealed class ReparseSwitchingSource(InMemorySourceFileSystem inner) : ISourceFileSystem
    {
        public HashSet<string> EnumerateBecomesLink { get; } = new(StringComparer.Ordinal);

        public HashSet<string> OpenBecomesLink { get; } = new(StringComparer.Ordinal);

        public HashSet<string> InfoBecomesLink { get; } = new(StringComparer.Ordinal);

        public HashSet<string> InfoReportsLink { get; } = new(StringComparer.Ordinal);

        private static SourceAccessException Link(string path) =>
            new(RunIssueCode.ReparsePointSkipped, $"{path} es un punto de reanálisis; no se sigue.");

        public async IAsyncEnumerable<SourceEntry> EnumerateAsync(string directoryPath, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (EnumerateBecomesLink.Contains(directoryPath))
            {
                throw Link(directoryPath);
            }

            await foreach (var entry in inner.EnumerateAsync(directoryPath, cancellationToken))
            {
                yield return entry;
            }
        }

        public Task<Stream> OpenReadAsync(string filePath, CancellationToken cancellationToken) =>
            OpenBecomesLink.Contains(filePath) ? throw Link(filePath) : inner.OpenReadAsync(filePath, cancellationToken);

        public async Task<SourceEntry?> GetFileInfoAsync(string filePath, CancellationToken cancellationToken)
        {
            if (InfoBecomesLink.Contains(filePath))
            {
                throw Link(filePath);
            }

            var info = await inner.GetFileInfoAsync(filePath, cancellationToken);
            return info is not null && InfoReportsLink.Contains(filePath) ? info with { Attributes = info.Attributes | FileAttributes.ReparsePoint } : info;
        }
    }

    /// <summary>Cuenta cuántos elementos se han pedido de la enumeración de cada carpeta.</summary>
    private sealed class CountingSource(InMemorySourceFileSystem inner) : ISourceFileSystem
    {
        private readonly Dictionary<string, int> yielded = new(StringComparer.Ordinal);

        public int Yielded(string directory)
        {
            lock (yielded)
            {
                return yielded.GetValueOrDefault(directory);
            }
        }

        public async IAsyncEnumerable<SourceEntry> EnumerateAsync(string directoryPath, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var entry in inner.EnumerateAsync(directoryPath, cancellationToken))
            {
                lock (yielded)
                {
                    yielded[directoryPath] = yielded.GetValueOrDefault(directoryPath) + 1;
                }

                yield return entry;
            }
        }

        public Task<Stream> OpenReadAsync(string filePath, CancellationToken cancellationToken) => inner.OpenReadAsync(filePath, cancellationToken);

        public Task<SourceEntry?> GetFileInfoAsync(string filePath, CancellationToken cancellationToken) => inner.GetFileInfoAsync(filePath, cancellationToken);
    }
}
