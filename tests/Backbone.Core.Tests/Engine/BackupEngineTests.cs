using System.Security.Cryptography;
using Backbone.Contracts;
using Backbone.Core.Engine;
using Backbone.Core.Repository;
using Backbone.Core.Repository.Locking;
using Backbone.Core.Repository.Snapshots;
using Backbone.Core.Storage;
using Backbone.Core.Tests.Support;
using SnapshotStatus = Backbone.Core.Repository.Snapshots.SnapshotStatus;
using static Backbone.Core.Tests.Support.EngineHarness;

namespace Backbone.Core.Tests.Engine;

/// <summary>HU-13: copia incremental con versiones (RF-23, RF-25, §6.4).</summary>
[Trait("Story", "HU-13")]
public sealed class BackupEngineTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ================================================================== Criterio 1

    [Fact]
    public async Task HU13_1_First_backup_uploads_every_file_as_an_object_and_writes_a_full_snapshot()
    {
        var storage = new InMemoryRepositoryStorage();
        var time = new ManualTimeProvider();
        var source = SampleSource();

        var result = await RunAsync(storage, time, source);

        Assert.Equal(RunState.Completed, result.State);
        Assert.Equal(SnapshotStatus.Completed, result.Snapshot.Status);
        Assert.Null(result.Snapshot.ParentId);
        Assert.Empty(result.Warnings);
        Assert.Equal([result.Snapshot.Id], await ListSnapshotsAsync(storage));

        var entries = await ReadEntriesAsync(storage, result.Snapshot.Id);
        AssertMatchesSource(entries, source);

        // La carpeta vacía también está; los atributos guardados y la fecha, tal cual.
        Assert.Contains(entries, e => e.IsDirectory && e.Path == "Documentos/Vacía/");
        var hidden = entries.Single(e => e.Path == "Documentos/oculto.ini");
        Assert.Equal(FileAttributes.Hidden | FileAttributes.ReadOnly | FileAttributes.Archive, hidden.Attributes);
        Assert.Equal(InMemorySourceFileSystem.DefaultModified, hidden.ModifiedUtc);

        // Cada archivo se ha subido como objeto (uno por contenido distinto) con su contenido exacto.
        var files = source.FilesUnder(DocumentsRoot).Concat(source.FilesUnder(PhotosRoot)).ToList();
        Assert.Equal(files.Count, ObjectPaths(storage).Count);
        foreach (var (_, content) in files)
        {
            var hash = Sha256(content);
            Assert.Equal(content, storage.GetFileContent($"objects/{hash[..2]}/{hash}"));
        }

        var stats = result.Snapshot.Stats;
        Assert.Equal(files.Count, stats.Files);
        Assert.Equal(files.Sum(f => (long)f.Content.Length), stats.Bytes);
        Assert.Equal(files.Count, stats.NewFiles);
        Assert.Equal(0, stats.ChangedFiles);
        Assert.Equal(files.Count, stats.NewObjects);
        Assert.Equal(stats.Bytes, stats.NewBytes);
        Assert.Equal(0, stats.Skipped);

        // Cada archivo se abre una sola vez: el hash se calcula mientras se copia, sin una pasada previa (§6.4.2).
        Assert.Equal(files.Count, source.OpenCount);
        Assert.Equal(source.Opened.Count, source.Opened.Distinct(StringComparer.Ordinal).Count());
        await RepositoryInvariants.AssertConsistentAsync(storage);
    }

    [Fact]
    public async Task HU13_1_Empty_source_is_stored_as_an_empty_folder_entry()
    {
        var storage = new InMemoryRepositoryStorage();
        var source = SampleSource();
        source.AddDirectory(@"E:\Vacío");
        var sources = DefaultSources.Append(new BackupSource("Vacío", @"E:\Vacío", @"E:\Vacío")).ToList();

        var result = await RunAsync(storage, new ManualTimeProvider(), source, Request(sources));

        var entries = await ReadEntriesAsync(storage, result.Snapshot.Id);
        Assert.Contains(entries, e => e.IsDirectory && e.Path == "Vacío/");
        Assert.Equal(["Documentos", "Fotos", "Vacío"], result.Snapshot.Sources.Select(s => s.Alias));
        Assert.Equal(@"E:\Vacío", result.Snapshot.Sources[2].Path);
    }

    [Fact]
    public async Task HU13_1_Snapshot_records_the_original_path_of_each_source_not_the_read_path()
    {
        var storage = new InMemoryRepositoryStorage();
        var source = new InMemorySourceFileSystem();
        const string shadow = @"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy7\Users\ana\Documents";
        source.AddFile(shadow + @"\a.txt", "a"u8.ToArray());

        var result = await RunAsync(storage, new ManualTimeProvider(), source, Request([new BackupSource("Documentos", DocumentsRoot, shadow)]));

        Assert.Equal(DocumentsRoot, Assert.Single(result.Snapshot.Sources).Path);
        Assert.True(result.Snapshot.UsedVss);
        Assert.Equal("Documentos/a.txt", Assert.Single(await ReadEntriesAsync(storage, result.Snapshot.Id)).Path);
    }

    // ================================================================== Criterio 2

    [Fact]
    public async Task HU13_2_Second_backup_without_changes_reads_no_source_file_and_reuses_the_same_hashes()
    {
        var storage = new InMemoryRepositoryStorage();
        var time = new ManualTimeProvider();
        var source = SampleSource();
        var first = await RunAsync(storage, time, source);
        var objectsBefore = ObjectPaths(storage);
        source.ResetCounters();
        time.Advance(TimeSpan.FromDays(1));

        var second = await RunAsync(storage, time, source);

        Assert.Equal(0, source.OpenCount);
        Assert.Equal(0, source.InfoQueryCount);
        Assert.NotEqual(first.Snapshot.Id, second.Snapshot.Id);
        Assert.Equal(first.Snapshot.Id, second.Snapshot.ParentId);
        Assert.Equal(RunState.Completed, second.State);

        var before = await ReadEntriesAsync(storage, first.Snapshot.Id);
        var after = await ReadEntriesAsync(storage, second.Snapshot.Id);
        Assert.Equal(before.OrderBy(e => e.Path, StringComparer.Ordinal), after.OrderBy(e => e.Path, StringComparer.Ordinal));

        Assert.Equal(objectsBefore, ObjectPaths(storage));
        Assert.Equal(0, second.Snapshot.Stats.NewObjects);
        Assert.Equal(0, second.Snapshot.Stats.NewFiles);
        Assert.Equal(0, second.Snapshot.Stats.ChangedFiles);
        Assert.Equal(first.Snapshot.Stats.Files, second.Snapshot.Stats.Files);
        await RepositoryInvariants.AssertConsistentAsync(storage);
    }

    public static TheoryData<string> MetadataChanges => ["size", "mtime", "attributes"];

    [Theory]
    [MemberData(nameof(MetadataChanges))]
    [Trait("Requirement", "RF-23")]
    public async Task HU13_2_A_file_is_read_again_when_its_size_date_or_attributes_change(string change)
    {
        var storage = new InMemoryRepositoryStorage();
        var time = new ManualTimeProvider();
        var source = SampleSource();
        await RunAsync(storage, time, source);
        source.ResetCounters();
        const string path = DocumentsRoot + @"\notas.txt";
        switch (change)
        {
            case "size":
                source.Modify(path, "hola, mundo!"u8.ToArray(), InMemorySourceFileSystem.DefaultModified);
                break;
            case "mtime":
                source.SetModified(path, InMemorySourceFileSystem.DefaultModified.AddTicks(1));
                break;
            default:
                source.SetAttributes(path, FileAttributes.Archive | FileAttributes.ReadOnly);
                break;
        }

        time.Advance(TimeSpan.FromDays(1));
        var second = await RunAsync(storage, time, source);

        Assert.Equal([path], source.Opened);
        AssertMatchesSource(await ReadEntriesAsync(storage, second.Snapshot.Id), source);
    }

    [Fact]
    [Trait("Requirement", "RF-23")]
    public async Task HU13_2_Attributes_that_are_not_stored_do_not_force_a_new_read()
    {
        var storage = new InMemoryRepositoryStorage();
        var time = new ManualTimeProvider();
        var source = SampleSource();
        await RunAsync(storage, time, source);
        source.ResetCounters();
        source.SetAttributes(DocumentsRoot + @"\notas.txt", FileAttributes.Archive | FileAttributes.NotContentIndexed);

        await RunAsync(storage, time, source);

        Assert.Equal(0, source.OpenCount);
    }

    [Fact]
    public async Task HU13_2_Parent_is_the_last_snapshot_of_the_same_job()
    {
        var storage = new InMemoryRepositoryStorage();
        var time = new ManualTimeProvider();
        var source = SampleSource();
        var mine = await RunAsync(storage, time, source);
        time.Advance(TimeSpan.FromHours(1));
        var other = await RunAsync(storage, time, source, Request() with { JobId = "99999999-0000-0000-0000-000000000000" });
        Assert.Null(other.Snapshot.ParentId);
        time.Advance(TimeSpan.FromHours(1));
        source.ResetCounters();

        var next = await RunAsync(storage, time, source);

        Assert.Equal(mine.Snapshot.Id, next.Snapshot.ParentId);
        Assert.Equal(0, source.OpenCount);
    }

    [Fact]
    public async Task HU13_2_Deleted_and_new_files_are_reflected_in_the_new_snapshot()
    {
        var storage = new InMemoryRepositoryStorage();
        var time = new ManualTimeProvider();
        var source = SampleSource();
        await RunAsync(storage, time, source);
        source.Delete(DocumentsRoot + @"\notas.txt");
        source.AddFile(DocumentsRoot + @"\nuevo.txt", "nuevo"u8.ToArray());
        source.AddFile(DocumentsRoot + @"\Vacía\ya no.txt", "ya no está vacía"u8.ToArray());
        source.ResetCounters();

        var second = await RunAsync(storage, time, source);

        var entries = await ReadEntriesAsync(storage, second.Snapshot.Id);
        AssertMatchesSource(entries, source);
        Assert.DoesNotContain(entries, e => e.IsDirectory);
        Assert.Equal(2, source.OpenCount);
        Assert.Equal(2, second.Snapshot.Stats.NewFiles);
    }

    // ================================================================== Criterio 3

    [Fact]
    public async Task HU13_3_Two_files_with_the_same_content_produce_a_single_object()
    {
        var storage = new InMemoryRepositoryStorage();
        var source = new InMemorySourceFileSystem();
        var content = RandomNumberGenerator.GetBytes((2 * 1024 * 1024) + 7);
        source.AddFile(DocumentsRoot + @"\informe.docx", content);
        source.AddFile(DocumentsRoot + @"\copia\informe (copia).docx", content);
        source.AddFile(PhotosRoot + @"\otra copia.docx", content);

        var result = await RunAsync(storage, new ManualTimeProvider(), source);

        var hash = Sha256(content);
        Assert.Equal([$"objects/{hash[..2]}/{hash}"], ObjectPaths(storage));
        var entries = await ReadEntriesAsync(storage, result.Snapshot.Id);
        Assert.Equal(3, entries.Count);
        Assert.All(entries, e => Assert.Equal(hash, e.Hash.ToString()));
        Assert.Equal(1, result.Snapshot.Stats.NewObjects);
        Assert.Equal(content.Length, result.Snapshot.Stats.NewBytes);
        Assert.Equal(3, result.Snapshot.Stats.NewFiles);

        // Los temporales de las copias descartadas no quedan en tmp/.
        Assert.DoesNotContain(storage.FilePaths, p => p.StartsWith("tmp/", StringComparison.Ordinal));
        await RepositoryInvariants.AssertConsistentAsync(storage);
    }

    [Fact]
    public async Task HU13_3_A_new_file_with_content_already_in_the_repository_adds_no_object()
    {
        var storage = new InMemoryRepositoryStorage();
        var time = new ManualTimeProvider();
        var source = SampleSource();
        await RunAsync(storage, time, source);
        var objects = ObjectPaths(storage);
        source.AddFile(DocumentsRoot + @"\notas (2).txt", "hola, mundo"u8.ToArray());

        var second = await RunAsync(storage, time, source);

        Assert.Equal(objects, ObjectPaths(storage));
        Assert.Equal(0, second.Snapshot.Stats.NewObjects);
        Assert.Equal(1, second.Snapshot.Stats.NewFiles);
    }

    // ================================================================== Criterio 4

    [Fact]
    public async Task HU13_4_A_modified_file_creates_a_new_object_and_the_previous_snapshot_still_references_the_old_one()
    {
        var storage = new InMemoryRepositoryStorage();
        var time = new ManualTimeProvider();
        var source = SampleSource();
        const string path = DocumentsRoot + @"\notas.txt";
        var oldContent = source.Content(path);
        var first = await RunAsync(storage, time, source);
        var published = RepositoryInvariants.CaptureImmutableFiles(storage);

        var newContent = "hola, mundo (versión 2)"u8.ToArray();
        source.Modify(path, newContent);
        source.ResetCounters();
        time.Advance(TimeSpan.FromDays(1));
        var second = await RunAsync(storage, time, source);

        // Lo publicado no ha cambiado (inmutabilidad) y el objeto anterior sigue ahí.
        RepositoryInvariants.AssertPreserved(published, storage);
        var oldHash = Sha256(oldContent);
        var newHash = Sha256(newContent);
        Assert.Contains($"objects/{oldHash[..2]}/{oldHash}", storage.FilePaths);
        Assert.Contains($"objects/{newHash[..2]}/{newHash}", storage.FilePaths);

        var before = (await ReadEntriesAsync(storage, first.Snapshot.Id)).Single(e => e.Path == "Documentos/notas.txt");
        var after = (await ReadEntriesAsync(storage, second.Snapshot.Id)).Single(e => e.Path == "Documentos/notas.txt");
        Assert.Equal(oldHash, before.Hash.ToString());
        Assert.Equal(newHash, after.Hash.ToString());
        Assert.Equal(InMemorySourceFileSystem.DefaultModified.AddSeconds(1), after.ModifiedUtc);

        Assert.Equal(1, second.Snapshot.Stats.ChangedFiles);
        Assert.Equal(1, second.Snapshot.Stats.NewObjects);
        Assert.Equal(newContent.Length, second.Snapshot.Stats.NewBytes);
        Assert.Equal([path], source.Opened);
        await RepositoryInvariants.AssertConsistentAsync(storage);
    }

    // ================================================================== Criterio 5

    public static TheoryData<string, RunIssueCode> UnreadableErrors => new()
    {
        { "locked", RunIssueCode.FileLocked },
        { "denied", RunIssueCode.AccessDenied },
        { "io", RunIssueCode.ReadError },
        { "missing", RunIssueCode.ReadError },
        { "tooLong", RunIssueCode.PathInvalid },
    };

    private static Exception Error(string kind) => kind switch
    {
        "locked" => new SourceAccessException(RunIssueCode.FileLocked, "El proceso no tiene acceso al archivo porque está siendo utilizado por otro proceso."),
        "denied" => new UnauthorizedAccessException("Acceso denegado."),
        "io" => new IOException("Error de E/S."),
        "missing" => new FileNotFoundException("Ya no existe."),
        _ => new PathTooLongException("Ruta demasiado larga."),
    };

    [Theory]
    [MemberData(nameof(UnreadableErrors))]
    [Trait("Requirement", "RF-25")]
    public async Task HU13_5_A_new_unreadable_file_is_skipped_with_a_warning_and_the_run_completes_with_warnings(string kind, RunIssueCode code)
    {
        var storage = new InMemoryRepositoryStorage();
        var source = SampleSource();
        source.AddFile(DocumentsRoot + @"\bloqueado.pst", "correo"u8.ToArray());
        source.FailOpen(DocumentsRoot + @"\bloqueado.pst", Error(kind));

        var result = await RunAsync(storage, new ManualTimeProvider(), source);

        Assert.Equal(RunState.CompletedWithWarnings, result.State);
        Assert.Equal(SnapshotStatus.CompletedWithWarnings, result.Snapshot.Status);
        var warning = Assert.Single(result.Snapshot.Warnings);
        Assert.Equal("Documentos/bloqueado.pst", warning.Path);
        Assert.Equal(code.ToString(), warning.Code);
        Assert.Equal(1, result.WarningCount);
        Assert.Equal(1, result.Snapshot.Stats.Skipped);

        var entries = await ReadEntriesAsync(storage, result.Snapshot.Id);
        Assert.DoesNotContain(entries, e => e.Path == "Documentos/bloqueado.pst");
        Assert.Equal(SampleSource().FilesUnder(DocumentsRoot).Count, entries.Count(e => e.Path.StartsWith("Documentos/", StringComparison.Ordinal) && !e.IsDirectory));
        await RepositoryInvariants.AssertConsistentAsync(storage);
    }

    [Fact]
    [Trait("Requirement", "RF-25")]
    public async Task HU13_5_An_unreadable_file_that_was_in_the_previous_snapshot_keeps_its_previous_version()
    {
        var storage = new InMemoryRepositoryStorage();
        var time = new ManualTimeProvider();
        var source = SampleSource();
        const string path = DocumentsRoot + @"\notas.txt";
        var first = await RunAsync(storage, time, source);
        var previous = (await ReadEntriesAsync(storage, first.Snapshot.Id)).Single(e => e.Path == "Documentos/notas.txt");

        // Ha cambiado (hay que leerlo) pero ahora está bloqueado.
        source.Modify(path, "versión nueva que no se puede leer"u8.ToArray());
        source.FailOpen(path, new SourceAccessException(RunIssueCode.FileLocked, "bloqueado"));
        time.Advance(TimeSpan.FromDays(1));
        var second = await RunAsync(storage, time, source);

        Assert.Equal(RunState.CompletedWithWarnings, second.State);
        var warning = Assert.Single(second.Warnings);
        Assert.Equal(nameof(RunIssueCode.FileLocked), warning.Code);
        Assert.Equal("Documentos/notas.txt", warning.Path);
        var kept = (await ReadEntriesAsync(storage, second.Snapshot.Id)).Single(e => e.Path == "Documentos/notas.txt");
        Assert.Equal(previous, kept);
        Assert.Equal(1, second.Snapshot.Stats.Skipped);
        Assert.Equal(0, second.Snapshot.Stats.ChangedFiles);

        // En cuanto se puede leer, la copia siguiente guarda la versión nueva.
        source.FailOpen(path, null);
        var third = await RunAsync(storage, time, source);
        Assert.Equal(RunState.Completed, third.State);
        AssertMatchesSource(await ReadEntriesAsync(storage, third.Snapshot.Id), source);
        await RepositoryInvariants.AssertConsistentAsync(storage);
    }

    [Fact]
    [Trait("Requirement", "RF-25")]
    public async Task HU13_5_A_read_error_in_the_middle_of_a_file_leaves_no_partial_object()
    {
        var storage = new InMemoryRepositoryStorage();
        var time = new ManualTimeProvider();
        var source = SampleSource();
        const string path = DocumentsRoot + @"\Facturas\2026\enero.pdf";
        source.FailRead(path, afterBytes: 1024 * 1024, new IOException("El dispositivo no está listo."));

        var result = await RunAsync(storage, time, source);

        Assert.Equal(RunState.CompletedWithWarnings, result.State);
        var warning = Assert.Single(result.Warnings);
        Assert.Equal(nameof(RunIssueCode.ReadError), warning.Code);
        Assert.Contains("El dispositivo no está listo.", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(await ReadEntriesAsync(storage, result.Snapshot.Id), e => e.Path == "Documentos/Facturas/2026/enero.pdf");
        Assert.DoesNotContain(storage.FilePaths, p => p.StartsWith("tmp/", StringComparison.Ordinal));
        Assert.DoesNotContain(ObjectPaths(storage), p => p.EndsWith(Sha256(source.Content(path)), StringComparison.Ordinal));
        await RepositoryInvariants.AssertConsistentAsync(storage);
    }

    [Fact]
    [Trait("Requirement", "RF-25")]
    public async Task HU13_5_An_unreadable_folder_keeps_everything_it_had_in_the_previous_snapshot()
    {
        var storage = new InMemoryRepositoryStorage();
        var time = new ManualTimeProvider();
        var source = SampleSource();
        source.AddDirectory(DocumentsRoot + @"\Facturas\2025");
        var first = await RunAsync(storage, time, source);
        var before = await ReadEntriesAsync(storage, first.Snapshot.Id);

        source.FailEnumerate(DocumentsRoot + @"\Facturas", new UnauthorizedAccessException("Acceso denegado."));
        source.Modify(DocumentsRoot + @"\notas.txt", "cambiado"u8.ToArray());
        time.Advance(TimeSpan.FromDays(1));
        var second = await RunAsync(storage, time, source);

        Assert.Equal(RunState.CompletedWithWarnings, second.State);
        var warning = Assert.Single(second.Warnings);
        Assert.Equal("Documentos/Facturas", warning.Path);
        Assert.Equal(nameof(RunIssueCode.AccessDenied), warning.Code);

        var after = await ReadEntriesAsync(storage, second.Snapshot.Id);
        var underFacturas = before.Where(e => e.Path.StartsWith("Documentos/Facturas/", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, underFacturas.Count); // enero.pdf y la carpeta vacía 2025/
        Assert.All(underFacturas, e => Assert.Contains(e, after));
        Assert.Equal(Sha256("cambiado"u8.ToArray()), after.Single(e => e.Path == "Documentos/notas.txt").Hash.ToString());
        await RepositoryInvariants.AssertConsistentAsync(storage);
    }

    [Fact]
    [Trait("Requirement", "RF-25")]
    public async Task HU13_5_An_unreadable_source_root_keeps_its_previous_content_and_the_other_sources_are_copied()
    {
        var storage = new InMemoryRepositoryStorage();
        var time = new ManualTimeProvider();
        var source = SampleSource();
        var first = await RunAsync(storage, time, source);
        source.FailEnumerate(PhotosRoot, new IOException("La unidad no está disponible."));
        source.AddFile(DocumentsRoot + @"\otro.txt", "otro"u8.ToArray());

        var second = await RunAsync(storage, time, source);

        Assert.Equal(RunState.CompletedWithWarnings, second.State);
        var warning = Assert.Single(second.Warnings);
        Assert.Equal("Fotos", warning.Path);
        Assert.Equal(nameof(RunIssueCode.SourceRootUnreadable), warning.Code); // §8.4, R11: no el de la causa
        Assert.Contains("(ReadError)", warning.Message, StringComparison.Ordinal);
        Assert.Contains("La unidad no está disponible.", warning.Message, StringComparison.Ordinal);
        var before = await ReadEntriesAsync(storage, first.Snapshot.Id);
        var after = await ReadEntriesAsync(storage, second.Snapshot.Id);
        Assert.Equal(
            before.Where(e => e.Alias == "Fotos").OrderBy(e => e.Path, StringComparer.Ordinal),
            after.Where(e => e.Alias == "Fotos").OrderBy(e => e.Path, StringComparer.Ordinal));
        Assert.Contains(after, e => e.Path == "Documentos/otro.txt");
    }

    [Fact]
    [Trait("Requirement", "RF-25")]
    public async Task HU13_5_An_unreadable_folder_without_previous_snapshot_is_only_a_warning()
    {
        var storage = new InMemoryRepositoryStorage();
        var source = SampleSource();
        source.FailEnumerate(DocumentsRoot + @"\Facturas", new UnauthorizedAccessException("Acceso denegado."));

        var result = await RunAsync(storage, new ManualTimeProvider(), source);

        Assert.Equal(RunState.CompletedWithWarnings, result.State);
        var entries = await ReadEntriesAsync(storage, result.Snapshot.Id);
        Assert.DoesNotContain(entries, e => e.Path.StartsWith("Documentos/Facturas", StringComparison.Ordinal));
        Assert.Contains(entries, e => e.Path == "Documentos/notas.txt");
    }

    [Fact]
    public async Task HU13_5_Warnings_from_the_service_are_stored_first_and_make_the_run_complete_with_warnings()
    {
        var storage = new InMemoryRepositoryStorage();
        var source = SampleSource();
        source.FailOpen(DocumentsRoot + @"\notas.txt", new UnauthorizedAccessException("Acceso denegado."));
        var request = Request() with
        {
            UsedVss = false,
            AdditionalWarnings = [new SnapshotWarning(string.Empty, nameof(RunIssueCode.VssUnavailable), "VSS no disponible; copia en vivo.")],
        };

        var result = await RunAsync(storage, new ManualTimeProvider(), source, request);

        Assert.Equal(RunState.CompletedWithWarnings, result.State);
        Assert.False(result.Snapshot.UsedVss);
        Assert.Equal([nameof(RunIssueCode.VssUnavailable), nameof(RunIssueCode.AccessDenied)], result.Snapshot.Warnings.Select(w => w.Code));
    }

    [Fact]
    [Trait("Requirement", "RF-25")]
    public async Task HU13_5_More_warnings_than_the_snapshot_admits_are_counted_and_summarized()
    {
        const int count = SnapshotLimits.MaxWarnings + 5;
        var storage = new InMemoryRepositoryStorage();
        var source = new InMemorySourceFileSystem();
        source.AddFile(DocumentsRoot + @"\bien.txt", "bien"u8.ToArray());
        for (var i = 0; i < count; i++)
        {
            var path = DocumentsRoot + $@"\bloqueados\{i:D5}.txt";
            source.AddFile(path, []);
            source.FailOpen(path, new SourceAccessException(RunIssueCode.FileLocked, "bloqueado"));
        }

        var result = await RunAsync(storage, new ManualTimeProvider(), source, Request([DefaultSources[0]]));

        Assert.Equal(count, result.WarningCount);
        Assert.Equal(SnapshotLimits.MaxWarnings, result.Warnings.Count);
        Assert.Equal(count, result.Snapshot.Stats.Skipped);
        Assert.Equal(SnapshotLimits.MaxWarnings, result.Snapshot.Warnings.Count);
        var summary = result.Snapshot.Warnings[^1];
        Assert.Equal(nameof(RunIssueCode.WarningsTruncated), summary.Code);
        Assert.StartsWith($"{count - (SnapshotLimits.MaxWarnings - 1)} avisos más", summary.Message, StringComparison.Ordinal);

        // El lector acepta el manifiesto.
        Assert.Single(await ReadEntriesAsync(storage, result.Snapshot.Id), e => !e.IsDirectory);
    }

    // ================================================================== Criterio 6

    [Fact]
    [Trait("Requirement", "§6.4.2")]
    public async Task HU13_6_A_file_that_changes_while_it_is_copied_is_stored_with_the_hash_of_the_bytes_actually_read()
    {
        var storage = new InMemoryRepositoryStorage();
        var time = new ManualTimeProvider();
        var source = SampleSource();
        const string path = DocumentsRoot + @"\Facturas\2026\enero.pdf";
        var original = source.Content(path);
        var rewritten = RandomNumberGenerator.GetBytes(original.Length + 5000);
        var seenModified = InMemorySourceFileSystem.DefaultModified;

        // Después del primer bloque de 1 MiB, otro proceso reescribe el archivo (sin VSS).
        source.OnFirstRead(path, () => source.Modify(path, rewritten, seenModified.AddMinutes(1)));
        var result = await RunAsync(storage, time, source);

        var mixed = original.AsSpan(0, 1024 * 1024).ToArray().Concat(rewritten.AsSpan(1024 * 1024).ToArray()).ToArray();
        var entry = (await ReadEntriesAsync(storage, result.Snapshot.Id)).Single(e => e.Path == "Documentos/Facturas/2026/enero.pdf");
        Assert.Equal(Sha256(mixed), entry.Hash.ToString());
        Assert.Equal(mixed.Length, entry.Size);
        Assert.Equal(mixed, storage.GetFileContent($"objects/{entry.Hash.GetValueOrDefault().Prefix}/{entry.Hash}"));

        // Se registra el cambio y la entrada lleva la fecha vista antes de copiar.
        Assert.Equal(RunState.CompletedWithWarnings, result.State);
        var warning = Assert.Single(result.Warnings);
        Assert.Equal(nameof(RunIssueCode.FileChangedDuringCopy), warning.Code);
        Assert.Equal("Documentos/Facturas/2026/enero.pdf", warning.Path);
        Assert.Equal(seenModified, entry.ModifiedUtc);
        await RepositoryInvariants.AssertConsistentAsync(storage);

        // La copia siguiente vuelve a leerlo y guarda la versión completa.
        source.ResetCounters();
        var next = await RunAsync(storage, time, source);
        Assert.Equal([path], source.Opened);
        Assert.Equal(RunState.Completed, next.State);
        AssertMatchesSource(await ReadEntriesAsync(storage, next.Snapshot.Id), source);
    }

    [Fact]
    [Trait("Requirement", "§6.4.2")]
    public async Task HU13_6_A_change_of_the_same_size_is_detected_by_the_date_after_the_copy()
    {
        var storage = new InMemoryRepositoryStorage();
        var source = SampleSource();
        const string path = DocumentsRoot + @"\notas.txt";
        source.OnFirstRead(path, () => source.Modify(path, "HOLA, MUNDO"u8.ToArray()));

        var result = await RunAsync(storage, new ManualTimeProvider(), source);

        var warning = Assert.Single(result.Warnings);
        Assert.Equal(nameof(RunIssueCode.FileChangedDuringCopy), warning.Code);
        var entry = (await ReadEntriesAsync(storage, result.Snapshot.Id)).Single(e => e.Path == "Documentos/notas.txt");
        Assert.Equal(Sha256("hola, mundo"u8.ToArray()), entry.Hash.ToString());
        Assert.Equal(InMemorySourceFileSystem.DefaultModified, entry.ModifiedUtc);
    }

    [Fact]
    [Trait("Requirement", "§6.4.2")]
    public async Task HU13_6_A_file_deleted_right_after_being_copied_is_kept_with_a_change_warning()
    {
        var storage = new InMemoryRepositoryStorage();
        var source = SampleSource();
        const string path = DocumentsRoot + @"\notas.txt";
        source.OnFirstRead(path, () => source.Delete(path));

        var result = await RunAsync(storage, new ManualTimeProvider(), source);

        Assert.Equal(nameof(RunIssueCode.FileChangedDuringCopy), Assert.Single(result.Warnings).Code);
        Assert.Contains(await ReadEntriesAsync(storage, result.Snapshot.Id), e => e.Path == "Documentos/notas.txt");
    }

    // ================================================================== Otros casos del recorrido

    public static TheoryData<string> InvalidNames => ["CON.txt", "nul", "termina en punto.", "termina en espacio ", "a\u0001b.txt", new string('x', 256)];

    [Theory]
    [MemberData(nameof(InvalidNames))]
    [Trait("Requirement", "§7.5")]
    public async Task HU13_Files_with_names_that_are_not_valid_in_a_snapshot_are_skipped_with_PathInvalid(string name)
    {
        var storage = new InMemoryRepositoryStorage();
        var source = SampleSource();
        source.AddFile(DocumentsRoot + @"\" + name, "x"u8.ToArray());

        var result = await RunAsync(storage, new ManualTimeProvider(), source);

        var warning = Assert.Single(result.Warnings);
        Assert.Equal(nameof(RunIssueCode.PathInvalid), warning.Code);
        Assert.Equal(1, result.Snapshot.Stats.Skipped);
        AssertMatchesSource(
            await ReadEntriesAsync(storage, result.Snapshot.Id),
            WithoutFile(source, DocumentsRoot + @"\" + name));
        Assert.DoesNotContain(source.Opened, p => p.EndsWith(name, StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Requirement", "§7.5")]
    public async Task HU13_A_file_name_with_a_lone_surrogate_is_skipped_with_PathInvalid()
    {
        // Fuera de la teoría: la serialización de los datos de xUnit sustituye las suplentes sueltas por U+FFFD.
        await HU13_Files_with_names_that_are_not_valid_in_a_snapshot_are_skipped_with_PathInvalid("suplente \ud800 suelta.txt");
    }

    [Fact]
    [Trait("Requirement", "§7.5")]
    public async Task HU13_A_folder_with_an_invalid_name_is_skipped_with_its_content()
    {
        var storage = new InMemoryRepositoryStorage();
        var source = SampleSource();
        source.AddFile(DocumentsRoot + @"\AUX\dentro.txt", "x"u8.ToArray());

        var result = await RunAsync(storage, new ManualTimeProvider(), source);

        var warning = Assert.Single(result.Warnings);
        Assert.Equal(nameof(RunIssueCode.PathInvalid), warning.Code);
        Assert.Equal("Documentos/AUX", warning.Path);
        Assert.DoesNotContain(await ReadEntriesAsync(storage, result.Snapshot.Id), e => e.Path.Contains("AUX", StringComparison.Ordinal));
        Assert.Equal(6, source.EnumerationCount); // raíces (2) y carpetas válidas (Facturas, 2026, Vacía, año 2026)
    }

    [Fact]
    [Trait("Requirement", "RF-06")]
    public async Task HU13_Reparse_points_are_not_followed()
    {
        var storage = new InMemoryRepositoryStorage();
        var source = SampleSource();
        source.AddFile(DocumentsRoot + @"\Mi música\canción.mp3", "x"u8.ToArray());
        source.MakeReparsePoint(DocumentsRoot + @"\Mi música");
        source.AddFile(DocumentsRoot + @"\enlace.txt", "x"u8.ToArray());
        source.MakeReparsePoint(DocumentsRoot + @"\enlace.txt");

        var result = await RunAsync(storage, new ManualTimeProvider(), source);

        Assert.Equal([nameof(RunIssueCode.ReparsePointSkipped), nameof(RunIssueCode.ReparsePointSkipped)], result.Warnings.Select(w => w.Code));
        var entries = await ReadEntriesAsync(storage, result.Snapshot.Id);
        Assert.DoesNotContain(entries, e => e.Path.Contains("Mi música", StringComparison.Ordinal) || e.Path.Contains("enlace", StringComparison.Ordinal));
        Assert.DoesNotContain(source.Opened, p => p.Contains("enlace", StringComparison.Ordinal));
        Assert.Equal(2, result.Snapshot.Stats.Skipped);
    }

    [Fact]
    public async Task HU13_Names_that_differ_only_in_case_are_two_entries()
    {
        var storage = new InMemoryRepositoryStorage();
        var source = new InMemorySourceFileSystem();
        source.AddFile(DocumentsRoot + @"\A.txt", "mayúscula"u8.ToArray());
        source.AddFile(DocumentsRoot + @"\a.txt", "minúscula"u8.ToArray());

        var result = await RunAsync(storage, new ManualTimeProvider(), source, Request([DefaultSources[0]]));

        AssertMatchesSource(await ReadEntriesAsync(storage, result.Snapshot.Id), source, [DefaultSources[0]]);
    }

    [Fact]
    public async Task HU13_Progress_is_reported_after_each_file()
    {
        var storage = new InMemoryRepositoryStorage();
        var source = SampleSource();
        var reports = new List<BackupProgress>();

        await RunAsync(storage, new ManualTimeProvider(), source, progress: new SynchronousProgress<BackupProgress>(reports.Add));

        var files = source.FilesUnder(DocumentsRoot).Concat(source.FilesUnder(PhotosRoot)).ToList();
        Assert.Equal(files.Count, reports.Count);
        Assert.Equal(Enumerable.Range(1, files.Count).Select(i => (long)i), reports.Select(r => r.FilesProcessed));
        Assert.Equal(files.Sum(f => (long)f.Content.Length), reports[^1].BytesProcessed);
        Assert.All(reports, r => Assert.True(EntryPath.IsValidFilePath(r.CurrentPath)));
    }

    // ================================================================== Fallos de la ejecución

    [Fact]
    [Trait("Requirement", "RF-21")]
    public async Task HU13_Cancelling_publishes_nothing_and_the_next_run_completes()
    {
        var storage = new InMemoryRepositoryStorage();
        var time = new ManualTimeProvider();
        var source = SampleSource();
        var first = await RunAsync(storage, time, source);
        var published = RepositoryInvariants.CaptureImmutableFiles(storage);
        source.Modify(DocumentsRoot + @"\notas.txt", "cambio"u8.ToArray());

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        source.OnEnumerate = path =>
        {
            if (path == PhotosRoot)
            {
                cancellation.Cancel();
            }
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync(storage, time, source, cancellationToken: cancellation.Token));

        Assert.Equal([first.Snapshot.Id], await ListSnapshotsAsync(storage));
        RepositoryInvariants.AssertPreserved(published, storage);
        Assert.DoesNotContain(storage.FilePaths, p => p.StartsWith("tmp/", StringComparison.Ordinal));
        await RepositoryInvariants.AssertConsistentAsync(storage);

        source.OnEnumerate = null;
        var next = await RunAsync(storage, time, source, owner: new LockOwner("PC-PRUEBA", 5000));
        Assert.Equal(first.Snapshot.Id, next.Snapshot.ParentId);
        AssertMatchesSource(await ReadEntriesAsync(storage, next.Snapshot.Id), source);
    }

    [Fact]
    public async Task HU13_A_lost_lock_publishes_nothing()
    {
        var storage = new InMemoryRepositoryStorage();
        var time = new ManualTimeProvider();
        var source = SampleSource();

        // A mitad de la copia el titular pasa 14 minutos sin latido (p. ej. el equipo se colgó): el bloqueo se da por perdido.
        source.OnEnumerate = path =>
        {
            if (path == PhotosRoot)
            {
                time.Jump(TimeSpan.FromMinutes(14));
            }
        };
        var ex = await Assert.ThrowsAsync<RepositoryException>(() => RunAsync(storage, time, source));

        Assert.Equal(RepositoryErrorKind.LockLost, ex.Kind);
        Assert.Empty(await ListSnapshotsAsync(storage));
        await RepositoryInvariants.AssertConsistentAsync(storage);
    }

    [Fact]
    [Trait("Requirement", "§6.3")]
    public async Task HU13_Exceeding_the_entry_limit_fails_with_SnapshotTooLarge_and_publishes_nothing()
    {
        var storage = new InMemoryRepositoryStorage();
        var time = new ManualTimeProvider();
        var source = SampleSource();
        var first = await RunAsync(storage, time, source);

        var ex = await Assert.ThrowsAsync<RepositoryException>(() => RunAsync(storage, time, source, configure: e => e.MaxSnapshotEntries = 3));

        Assert.Equal(RepositoryErrorKind.SnapshotTooLarge, ex.Kind);
        Assert.Contains("3 entradas", ex.Message, StringComparison.Ordinal);
        Assert.Equal([first.Snapshot.Id], await ListSnapshotsAsync(storage));
        await RepositoryInvariants.AssertConsistentAsync(storage);
    }

    [Fact]
    public async Task HU13_A_corrupt_previous_snapshot_fails_the_run_without_publishing()
    {
        var storage = new InMemoryRepositoryStorage();
        var time = new ManualTimeProvider();
        var source = SampleSource();
        var first = await RunAsync(storage, time, source);
        var manifest = $"snapshots/{first.Snapshot.Id}.json";
        var content = storage.GetFileContent(manifest);
        storage.SetFileContent(manifest, content.AsSpan(0, content.Length - 10));

        var ex = await Assert.ThrowsAsync<RepositoryException>(() => RunAsync(storage, time, source));

        Assert.Equal(RepositoryErrorKind.Corrupt, ex.Kind);
        Assert.Equal([first.Snapshot.Id], await ListSnapshotsAsync(storage));
    }

    [Fact]
    public async Task HU13_Invalid_requests_are_rejected_before_writing_anything()
    {
        var storage = new InMemoryRepositoryStorage();
        var time = new ManualTimeProvider();
        var source = SampleSource();
        var repository = await OpenOrInitializeAsync(storage, time, Ct);
        await using var repositoryLock = await repository.AcquireLockAsync(RepositoryLockType.Shared, Owner, Ct);
        var engine = new BackupEngine(time);

        await Assert.ThrowsAsync<ArgumentException>(() => engine.RunAsync(repository, repositoryLock, source, Request([new BackupSource("Documentos", DocumentsRoot, string.Empty)]), null, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => engine.RunAsync(repository, repositoryLock, source, Request([DefaultSources[0], DefaultSources[0] with { ReadPath = PhotosRoot }]), null, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => engine.RunAsync(repository, repositoryLock, source, Request([new BackupSource("CON", DocumentsRoot, DocumentsRoot)]), null, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => engine.RunAsync(repository, repositoryLock, source, Request([]), null, Ct));
        Assert.Equal(0, source.EnumerationCount);
        Assert.Empty(await repository.ListSnapshotIdsAsync(Ct));
        Assert.DoesNotContain(storage.FilePaths, p => p.StartsWith("tmp/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HU13_Started_time_from_the_request_names_the_snapshot()
    {
        var storage = new InMemoryRepositoryStorage();
        var started = new DateTime(2026, 11, 14, 23, 59, 58, DateTimeKind.Utc);

        var result = await RunAsync(storage, new ManualTimeProvider(), SampleSource(), Request() with { StartedUtc = started });

        Assert.StartsWith("20261114T235958Z-", result.Snapshot.Id, StringComparison.Ordinal);
        Assert.Equal(started, result.Snapshot.StartedUtc);
    }

    [Fact]
    [Trait("Requirement", "RF-21")]
    public async Task HU13_Cancelling_inside_a_large_file_stops_the_copy_and_leaves_no_temporary_or_object()
    {
        var storage = new InMemoryRepositoryStorage();
        var time = new ManualTimeProvider();
        var source = new InMemorySourceFileSystem();
        var big = new byte[5 * 1024 * 1024];
        new Random(42).NextBytes(big);
        source.AddFile(DocumentsRoot + @"\grande.bin", big);

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        source.OnFirstRead(DocumentsRoot + @"\grande.bin", cancellation.Cancel);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => RunAsync(storage, time, source, Request([DefaultSources[0]]), cancellationToken: cancellation.Token));

        Assert.Empty(await ListSnapshotsAsync(storage));
        Assert.Empty(ObjectPaths(storage));
        Assert.DoesNotContain(storage.FilePaths, p => p.StartsWith("tmp/", StringComparison.Ordinal));
        await RepositoryInvariants.AssertConsistentAsync(storage);

        var next = await RunAsync(storage, time, source, Request([DefaultSources[0]]), owner: new LockOwner("PC-PRUEBA", 5000));
        AssertMatchesSource(await ReadEntriesAsync(storage, next.Snapshot.Id), source, [DefaultSources[0]]);
    }

    public static TheoryData<string, bool> NamesThatAreNotASegment => new()
    {
        { "a/b.txt", false }, { "a/b.txt", true }, { "..", false }, { "..", true }, { ".", true },
        { @"a\b.txt", false }, { "", false }, { "", true },
    };

    [Theory]
    [MemberData(nameof(NamesThatAreNotASegment))]
    [Trait("Requirement", "§7.5")]
    public async Task HU13_Names_that_are_not_a_single_segment_are_skipped_even_if_the_composed_path_is_valid(string name, bool isDirectory)
    {
        // Una implementación defectuosa de ISourceFileSystem no puede colar segmentos de más ni duplicar una ruta
        // (Documentos/a/b.txt existe de verdad).
        var storage = new InMemoryRepositoryStorage();
        var inner = new InMemorySourceFileSystem();
        inner.AddFile(DocumentsRoot + @"\a\b.txt", "real"u8.ToArray());
        inner.AddFile(DocumentsRoot + @"\falso\dentro.txt", "falso"u8.ToArray());
        var fake = new SourceEntry(
            name,
            isDirectory ? DocumentsRoot + @"\falso" : DocumentsRoot + @"\falso\dentro.txt",
            isDirectory,
            isDirectory ? 0 : 5,
            InMemorySourceFileSystem.DefaultModified,
            isDirectory ? FileAttributes.Directory : FileAttributes.Archive);
        var hostile = new ExtraEntrySource(inner, DocumentsRoot, [fake]);

        var result = await RunAsync(storage, new ManualTimeProvider(), hostile, Request([DefaultSources[0]]));

        Assert.Equal(RunState.CompletedWithWarnings, result.State);
        var warning = Assert.Single(result.Warnings);
        Assert.Equal(nameof(RunIssueCode.PathInvalid), warning.Code);
        var entries = await ReadEntriesAsync(storage, result.Snapshot.Id);
        Assert.Equal(["Documentos/a/b.txt", "Documentos/falso/dentro.txt"], entries.Select(e => e.Path).Order(StringComparer.Ordinal));
        Assert.Equal(1, inner.Opened.Count(p => p.EndsWith("dentro.txt", StringComparison.Ordinal)));
    }

    /// <summary>Origen que añade entradas arbitrarias a la enumeración de una carpeta.</summary>
    private sealed class ExtraEntrySource(InMemorySourceFileSystem inner, string directory, IReadOnlyList<SourceEntry> extra) : ISourceFileSystem
    {
        public async IAsyncEnumerable<SourceEntry> EnumerateAsync(string directoryPath, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var entry in inner.EnumerateAsync(directoryPath, cancellationToken))
            {
                yield return entry;
            }

            if (directoryPath == directory)
            {
                foreach (var entry in extra)
                {
                    yield return entry;
                }
            }
        }

        public Task<Stream> OpenReadAsync(string filePath, CancellationToken cancellationToken) => inner.OpenReadAsync(filePath, cancellationToken);

        public Task<SourceEntry?> GetFileInfoAsync(string filePath, CancellationToken cancellationToken) => inner.GetFileInfoAsync(filePath, cancellationToken);
    }

    private static InMemorySourceFileSystem WithoutFile(InMemorySourceFileSystem source, string path)
    {
        var copy = new InMemorySourceFileSystem();
        foreach (var root in new[] { DocumentsRoot, PhotosRoot })
        {
            foreach (var (relative, content) in source.FilesUnder(root))
            {
                var full = root + @"\" + relative.Replace('/', '\\');
                if (full != path)
                {
                    copy.AddFile(full, content);
                }
            }
        }

        return copy;
    }

    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
