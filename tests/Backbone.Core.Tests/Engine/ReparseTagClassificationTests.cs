using System.Runtime.CompilerServices;
using Backbone.Contracts;
using Backbone.Core.Engine;
using Backbone.Core.Repository.Snapshots;
using Backbone.Core.Storage;
using Backbone.Core.Tests.Support;
using static Backbone.Core.Tests.Support.EngineHarness;

namespace Backbone.Core.Tests.Engine;

/// <summary>
/// HU-08 criterios 3 y 4, ADR-19 (§8.2 "Clasificación por etiqueta", "Archivos y carpetas solo en la nube", "Plazo de las
/// lecturas de los orígenes"; §8.4 raíz ilegible): el motor clasifica cada elemento por sus atributos y su etiqueta de
/// reanálisis antes de abrir nada.
/// </summary>
[Trait("Story", "HU-08")]
[Trait("Requirement", "RF-06")]
[Trait("Requirement", "ADR-19")]
public sealed class ReparseTagClassificationTests
{
    private const FileAttributes Rp = FileAttributes.ReparsePoint;
    private const FileAttributes File = FileAttributes.Archive;
    private const FileAttributes Dir = FileAttributes.Directory;
    private const uint OneDriveTag = 0x9000601A;
    private const uint UnknownTag = 0x8000ABCD;
    private const uint OtherSurrogateTag = 0xE0001234; // cualquier otra con el bit N

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static BackupRequest DocumentsOnly() => Request([DefaultSources[0]]);

    private static string Doc(string relative) => DocumentsRoot + @"\" + relative;

    private static async Task<List<SnapshotEntry>> EntriesAsync(InMemoryRepositoryStorage storage, BackupResult result) =>
        [.. (await ReadEntriesAsync(storage, result.Snapshot.Id)).OrderBy(e => e.Path, StringComparer.Ordinal)];

    // ================================================================== La clasificación

    public static TheoryData<int, uint, SourceEntryKind> Classification => new()
    {
        // Normal: sin punto de reanálisis la etiqueta se ignora; OFFLINE solo no significa nada.
        { (int)File, 0u, SourceEntryKind.Normal },
        { (int)Dir, 0u, SourceEntryKind.Normal },
        { (int)File, ReparseTags.Symlink, SourceEntryKind.Normal },
        { (int)File, UnknownTag, SourceEntryKind.Normal },
        { (int)(File | FileAttributes.Offline), 0u, SourceEntryKind.Normal },
        { (int)(File | FileAttributes.SparseFile | FileAttributes.Compressed | (FileAttributes)0x00080000), 0u, SourceEntryKind.Normal },

        // Enlace: bit N, tenga los atributos que tenga (va antes que la nube).
        { (int)(Dir | Rp), ReparseTags.MountPoint, SourceEntryKind.Link },
        { (int)(File | Rp), ReparseTags.Symlink, SourceEntryKind.Link },
        { (int)(Dir | Rp), ReparseTags.Symlink, SourceEntryKind.Link },
        { (int)(File | Rp), OtherSurrogateTag, SourceEntryKind.Link },
        { (int)(File | Rp | SourceAttributes.RecallOnDataAccess), ReparseTags.Symlink, SourceEntryKind.Link },
        { (int)(Dir | Rp | SourceAttributes.RecallOnOpen | FileAttributes.Offline), ReparseTags.MountPoint, SourceEntryKind.Link },

        // Solo en la nube: RECALL_* (con o sin etiqueta), u OFFLINE con etiqueta de nube o HSM.
        { (int)(File | SourceAttributes.RecallOnDataAccess), 0u, SourceEntryKind.CloudOnly },
        { (int)(File | SourceAttributes.RecallOnOpen), 0u, SourceEntryKind.CloudOnly },
        { (int)(File | Rp | SourceAttributes.RecallOnDataAccess), OneDriveTag, SourceEntryKind.CloudOnly },
        { (int)(File | Rp | SourceAttributes.RecallOnOpen), ReparseTags.ProjFs, SourceEntryKind.CloudOnly },
        { (int)(File | Rp | FileAttributes.Offline), ReparseTags.Cloud, SourceEntryKind.CloudOnly },
        { (int)(File | Rp | FileAttributes.Offline), 0x9000F01Au, SourceEntryKind.CloudOnly },
        { (int)(File | Rp | FileAttributes.Offline), ReparseTags.Hsm, SourceEntryKind.CloudOnly },
        { (int)(File | Rp | FileAttributes.Offline), ReparseTags.Hsm2, SourceEntryKind.CloudOnly },
        { (int)(Dir | SourceAttributes.RecallOnOpen), 0u, SourceEntryKind.CloudOnly },
        { (int)(Dir | Rp | SourceAttributes.RecallOnDataAccess), OneDriveTag, SourceEntryKind.CloudOnly },
        { (int)(Dir | Rp | FileAttributes.Offline), OneDriveTag, SourceEntryKind.CloudOnly },

        // Con OFFLINE y otra etiqueta, manda la etiqueta.
        { (int)(File | Rp | FileAttributes.Offline), ReparseTags.ProjFs, SourceEntryKind.Unreadable },
        { (int)(File | Rp | FileAttributes.Offline), ReparseTags.Wof, SourceEntryKind.Content },
        { (int)(File | Rp | FileAttributes.Offline), ReparseTags.Symlink, SourceEntryKind.Link },
        { (int)(File | Rp | FileAttributes.Offline), 0u, SourceEntryKind.Unreadable },

        // No legible: fuera de la lista blanca, o punto de reanálisis con etiqueta 0.
        { (int)(File | Rp), 0u, SourceEntryKind.Unreadable },
        { (int)(Dir | Rp), 0u, SourceEntryKind.Unreadable },
        { (int)(File | Rp), ReparseTags.ProjFs, SourceEntryKind.Unreadable },
        { (int)(Dir | Rp), ReparseTags.ProjFs, SourceEntryKind.Unreadable },
        { (int)(File | Rp), ReparseTags.AppExecLink, SourceEntryKind.Unreadable },
        { (int)(File | Rp), UnknownTag, SourceEntryKind.Unreadable },
        { (int)(File | Rp), ReparseTags.Hsm, SourceEntryKind.Unreadable }, // HSM sin OFFLINE: no está en la lista blanca
        { (int)(File | Rp), 0x9000101Bu, SourceEntryKind.Unreadable }, // parecida a la de nube, pero no lo es

        // Contenido (lista blanca).
        { (int)(File | Rp), ReparseTags.Cloud, SourceEntryKind.Content },
        { (int)(File | Rp | FileAttributes.SparseFile), OneDriveTag, SourceEntryKind.Content },
        { (int)(File | Rp), ReparseTags.Wof, SourceEntryKind.Content },
        { (int)(File | Rp), ReparseTags.Dedup, SourceEntryKind.Content },
        { (int)(Dir | Rp), OneDriveTag, SourceEntryKind.Content },
    };

    [Theory]
    [MemberData(nameof(Classification))]
    public void HU08_3_4_Elements_are_classified_by_attributes_and_tag_in_the_order_of_the_specification(int attributes, uint tag, SourceEntryKind expected)
    {
        Assert.Equal(expected, SourceAttributes.Classify((FileAttributes)attributes, tag));
        Assert.Equal(expected, SourceAttributes.Classify(new SourceEntry("x", "x", false, 0, default, (FileAttributes)attributes) { ReparseTag = tag }));
    }

    [Fact]
    public void HU08_The_tags_have_the_Windows_values_and_SourceEntry_defaults_to_no_tag()
    {
        Assert.Equal(0x20000000u, ReparseTags.NameSurrogateBit);
        Assert.Equal(0xA0000003u, ReparseTags.MountPoint);
        Assert.Equal(0xA000000Cu, ReparseTags.Symlink);
        Assert.Equal(0x9000001Au, ReparseTags.Cloud);
        Assert.Equal(0x80000017u, ReparseTags.Wof);
        Assert.Equal(0x80000013u, ReparseTags.Dedup);
        Assert.Equal(0xC0000004u, ReparseTags.Hsm);
        Assert.Equal(0x80000006u, ReparseTags.Hsm2);
        Assert.Equal(0x9000001Cu, ReparseTags.ProjFs);
        Assert.Equal(0x8000001Bu, ReparseTags.AppExecLink);
        Assert.True(Enumerable.Range(0, 16).All(i => ReparseTags.IsCloud(ReparseTags.Cloud | ((uint)i << 12))));
        Assert.Equal("0x9000001C", ReparseTags.Format(ReparseTags.ProjFs));
        Assert.Equal(0u, new SourceEntry("a", "a", false, 0, default, File).ReparseTag);
    }

    // ================================================================== Enlaces

    public static TheoryData<uint> LinkTags => [ReparseTags.MountPoint, ReparseTags.Symlink, OtherSurrogateTag];

    [Theory]
    [MemberData(nameof(LinkTags))]
    [Trait("Requirement", "RF-06")]
    public async Task HU08_3_A_link_is_skipped_with_ReparsePointSkipped_and_nothing_of_it_is_kept_from_the_parent(uint tag)
    {
        var storage = new InMemoryRepositoryStorage();
        var time = new ManualTimeProvider();
        var source = new InMemorySourceFileSystem();
        source.AddFile(Doc(@"Carpeta\dentro.txt"), "dentro"u8.ToArray());
        source.AddFile(Doc("archivo.txt"), "archivo"u8.ToArray());
        source.AddFile(Doc("normal.txt"), "normal"u8.ToArray());
        await RunAsync(storage, time, source, DocumentsOnly());

        time.Advance(TimeSpan.FromDays(1));
        source.SetAttributes(Doc("Carpeta"), Dir | Rp | SourceAttributes.RecallOnOpen, tag);
        source.SetAttributes(Doc("archivo.txt"), File | Rp | FileAttributes.Offline, tag);
        source.ResetCounters();
        var enumerated = new List<string>();
        source.OnEnumerate = enumerated.Add;
        var result = await RunAsync(storage, time, source, DocumentsOnly());

        Assert.Equal(
            [("Documentos/Carpeta", nameof(RunIssueCode.ReparsePointSkipped)), ("Documentos/archivo.txt", nameof(RunIssueCode.ReparsePointSkipped))],
            result.Warnings.Select(w => (w.Path, w.Code)).OrderBy(w => w.Path, StringComparer.Ordinal));
        Assert.All(result.Warnings, w => Assert.Contains(ReparseTags.Format(tag), w.Message, StringComparison.Ordinal));
        Assert.Equal(["Documentos/normal.txt"], (await EntriesAsync(storage, result)).Select(e => e.Path));
        Assert.Equal([DocumentsRoot], enumerated);
        Assert.Empty(source.Opened);
    }

    // ================================================================== Solo en la nube

    public static TheoryData<int, uint> CloudOnlyFolders => new()
    {
        { (int)(Dir | SourceAttributes.RecallOnOpen), 0u },
        { (int)(Dir | Rp | SourceAttributes.RecallOnOpen), OneDriveTag },
        { (int)(Dir | Rp | SourceAttributes.RecallOnDataAccess), OneDriveTag },
        { (int)(Dir | Rp | FileAttributes.Offline), OneDriveTag },
        { (int)(Dir | Rp | FileAttributes.Offline), ReparseTags.Hsm },
    };

    [Theory]
    [MemberData(nameof(CloudOnlyFolders))]
    [Trait("Requirement", "R4")]
    public async Task HU08_4_An_unpopulated_cloud_folder_is_not_traversed_and_keeps_everything_it_had_in_the_parent(int attributes, uint tag)
    {
        var storage = new InMemoryRepositoryStorage();
        var time = new ManualTimeProvider();
        var source = new InMemorySourceFileSystem();
        source.AddFile(Doc(@"OneDrive\a.txt"), "a"u8.ToArray());
        source.AddFile(Doc(@"OneDrive\sub\b.txt"), "b"u8.ToArray());
        source.AddFile(Doc("fuera.txt"), "fuera"u8.ToArray());
        var first = await RunAsync(storage, time, source, DocumentsOnly());
        var before = await EntriesAsync(storage, first);

        time.Advance(TimeSpan.FromDays(1));
        source.SetAttributes(Doc("OneDrive"), (FileAttributes)attributes, tag);
        source.AddFile(Doc(@"OneDrive\nuevo.txt"), "nuevo"u8.ToArray()); // no se ve: la carpeta no se recorre
        source.ResetCounters();
        var enumerated = new List<string>();
        source.OnEnumerate = enumerated.Add;
        var result = await RunAsync(storage, time, source, DocumentsOnly());

        var warning = Assert.Single(result.Warnings);
        Assert.Equal(("Documentos/OneDrive", nameof(RunIssueCode.CloudOnlySkipped)), (warning.Path, warning.Code));
        Assert.Equal(RunState.CompletedWithWarnings, result.State);
        Assert.Equal(before, await EntriesAsync(storage, result));
        Assert.Equal([DocumentsRoot], enumerated);
        Assert.Empty(source.Opened);
    }

    [Fact]
    [Trait("Requirement", "R4")]
    public async Task HU08_4_An_unpopulated_cloud_folder_without_parent_snapshot_is_skipped_with_a_warning()
    {
        var storage = new InMemoryRepositoryStorage();
        var source = new InMemorySourceFileSystem();
        source.AddFile(Doc(@"OneDrive\a.txt"), "a"u8.ToArray());
        source.AddFile(Doc("fuera.txt"), "fuera"u8.ToArray());
        source.SetAttributes(Doc("OneDrive"), Dir | SourceAttributes.RecallOnOpen);

        var result = await RunAsync(storage, new ManualTimeProvider(), source, DocumentsOnly());

        Assert.Equal(nameof(RunIssueCode.CloudOnlySkipped), Assert.Single(result.Warnings).Code);
        Assert.Equal(["Documentos/fuera.txt"], (await EntriesAsync(storage, result)).Select(e => e.Path));
        Assert.Equal(1, result.Snapshot.Stats.Skipped);
    }

    // ================================================================== No legibles

    public static TheoryData<uint> UnreadableTags => [ReparseTags.ProjFs, ReparseTags.AppExecLink, UnknownTag, 0u, ReparseTags.Hsm];

    [Theory]
    [MemberData(nameof(UnreadableTags))]
    [Trait("Requirement", "RF-25")]
    public async Task HU08_A_file_with_a_tag_outside_the_white_list_is_not_opened_warns_ReadError_with_the_tag_and_keeps_its_previous_version(uint tag)
    {
        var storage = new InMemoryRepositoryStorage();
        var time = new ManualTimeProvider();
        var source = new InMemorySourceFileSystem();
        source.AddFile(Doc("antes.txt"), "antes"u8.ToArray());
        var first = await RunAsync(storage, time, source, DocumentsOnly());
        var previous = Assert.Single(await EntriesAsync(storage, first));

        time.Advance(TimeSpan.FromDays(1));
        source.AddFile(Doc("antes.txt"), "otro contenido"u8.ToArray(), modifiedUtc: InMemorySourceFileSystem.DefaultModified.AddDays(1), attributes: File | Rp, reparseTag: tag);
        source.AddFile(Doc("nuevo.txt"), "nuevo"u8.ToArray(), attributes: File | Rp, reparseTag: tag);
        source.ResetCounters();
        var result = await RunAsync(storage, time, source, DocumentsOnly());

        Assert.Equal(2, result.Warnings.Count);
        Assert.All(result.Warnings, w =>
        {
            Assert.Equal(nameof(RunIssueCode.ReadError), w.Code);
            Assert.Contains(ReparseTags.Format(tag), w.Message, StringComparison.Ordinal);
        });
        Assert.Equal([previous], await EntriesAsync(storage, result)); // el nuevo, sin versión anterior, no se guarda
        Assert.Empty(source.Opened);
        Assert.Equal(0, source.InfoQueryCount);
        Assert.Equal(2, result.Snapshot.Stats.Skipped);
    }

    [Theory]
    [MemberData(nameof(UnreadableTags))]
    [Trait("Requirement", "RF-25")]
    public async Task HU08_A_folder_with_a_tag_outside_the_white_list_is_not_traversed_and_keeps_everything_it_had_in_the_parent(uint tag)
    {
        var storage = new InMemoryRepositoryStorage();
        var time = new ManualTimeProvider();
        var source = new InMemorySourceFileSystem();
        source.AddFile(Doc(@"Proyectado\a.txt"), "a"u8.ToArray());
        source.AddFile(Doc(@"Proyectado\sub\b.txt"), "b"u8.ToArray());
        source.AddFile(Doc("fuera.txt"), "fuera"u8.ToArray());
        var first = await RunAsync(storage, time, source, DocumentsOnly());
        var before = await EntriesAsync(storage, first);

        time.Advance(TimeSpan.FromDays(1));
        source.SetAttributes(Doc("Proyectado"), Dir | Rp, tag);
        source.ResetCounters();
        var enumerated = new List<string>();
        source.OnEnumerate = enumerated.Add;
        var result = await RunAsync(storage, time, source, DocumentsOnly());

        var warning = Assert.Single(result.Warnings);
        Assert.Equal(("Documentos/Proyectado", nameof(RunIssueCode.ReadError)), (warning.Path, warning.Code));
        Assert.Contains(ReparseTags.Format(tag), warning.Message, StringComparison.Ordinal);
        Assert.Equal(before, await EntriesAsync(storage, result));
        Assert.Equal([DocumentsRoot], enumerated);
    }

    // ================================================================== Contenido (lista blanca)

    public static TheoryData<uint> ContentTags => [ReparseTags.Wof, ReparseTags.Dedup, ReparseTags.Cloud, OneDriveTag];

    [Theory]
    [MemberData(nameof(ContentTags))]
    public async Task HU08_A_white_listed_file_is_copied_by_its_content_without_warnings(uint tag)
    {
        var storage = new InMemoryRepositoryStorage();
        var source = new InMemorySourceFileSystem { ClassifyOnOpen = true };
        source.AddFile(Doc("comprimido.bin"), "contenido real"u8.ToArray(), attributes: File | Rp | FileAttributes.SparseFile, reparseTag: tag);

        var result = await RunAsync(storage, new ManualTimeProvider(), source, DocumentsOnly());

        Assert.Empty(result.Warnings);
        var entry = Assert.Single(await EntriesAsync(storage, result));
        Assert.Equal(("Documentos/comprimido.bin", Sha256("contenido real"u8.ToArray())), (entry.Path, entry.Hash.ToString()));
        Assert.Equal([Doc("comprimido.bin")], source.Opened);
    }

    [Fact]
    public async Task HU08_A_white_listed_file_is_not_reported_as_changed_after_the_copy()
    {
        // Antes de ADR-19, cualquier FileAttributes.ReparsePoint al volver a mirar el archivo lo daba por enlace.
        var storage = new InMemoryRepositoryStorage();
        var source = new InMemorySourceFileSystem();
        source.AddFile(Doc("nube.txt"), "descargado"u8.ToArray(), attributes: File | Rp, reparseTag: OneDriveTag);

        var result = await RunAsync(storage, new ManualTimeProvider(), source, DocumentsOnly());

        Assert.Empty(result.Warnings);
        Assert.Equal(1, source.InfoQueryCount);
    }

    // ================================================================== Cambios sobre el handle (§8.2) y plazo

    [Fact]
    [Trait("Requirement", "R4")]
    public async Task HU08_4_CloudOnlySkipped_from_the_metadata_after_the_copy_discards_what_was_read_and_keeps_the_previous_version()
    {
        var (storage, time, source, previous) = await WithPreviousVersionAsync();
        source.FailInfo(Doc("a.txt"), new SourceAccessException(RunIssueCode.CloudOnlySkipped, "Ha pasado a estar solo en la nube."));

        var result = await RunAsync(storage, time, source, DocumentsOnly());

        var warning = Assert.Single(result.Warnings);
        Assert.Equal(nameof(RunIssueCode.CloudOnlySkipped), warning.Code);
        Assert.Contains("se conserva su versión anterior", warning.Message, StringComparison.Ordinal);
        Assert.Equal([previous], await EntriesAsync(storage, result));
        await RepositoryInvariants.AssertConsistentAsync(storage);
    }

    [Fact]
    [Trait("Requirement", "RF-25")]
    public async Task HU08_A_timeout_reading_the_metadata_after_the_copy_keeps_the_previous_version()
    {
        var (storage, time, source, previous) = await WithPreviousVersionAsync();
        source.FailInfo(Doc("a.txt"), new SourceAccessException(RunIssueCode.ReadError, "Se superó el plazo de 60 s."));

        var result = await RunAsync(storage, time, source, DocumentsOnly());

        var warning = Assert.Single(result.Warnings);
        Assert.Equal(nameof(RunIssueCode.ReadError), warning.Code);
        Assert.Contains("plazo", warning.Message, StringComparison.Ordinal);
        Assert.Equal([previous], await EntriesAsync(storage, result));
    }

    [Fact]
    [Trait("Requirement", "RF-25")]
    public async Task HU08_A_timeout_opening_or_reading_a_block_keeps_the_previous_version()
    {
        var (storage, time, source, previous) = await WithPreviousVersionAsync();
        source.AddFile(Doc("b.txt"), new byte[3 * 1024 * 1024]);
        source.FailOpen(Doc("a.txt"), new SourceAccessException(RunIssueCode.ReadError, "Se superó el plazo de 60 s al abrir."));
        source.FailRead(Doc("b.txt"), 1024 * 1024, new SourceAccessException(RunIssueCode.ReadError, "Se superó el plazo de 60 s al leer."));

        var result = await RunAsync(storage, time, source, DocumentsOnly());

        Assert.Equal(
            [("Documentos/a.txt", nameof(RunIssueCode.ReadError)), ("Documentos/b.txt", nameof(RunIssueCode.ReadError))],
            result.Warnings.Select(w => (w.Path, w.Code)).OrderBy(w => w.Path, StringComparer.Ordinal));
        Assert.Equal([previous], await EntriesAsync(storage, result)); // b.txt no tenía versión anterior
    }

    [Fact]
    public async Task HU08_Other_metadata_errors_after_the_copy_still_mean_the_file_changed_during_the_copy()
    {
        var (storage, time, source, _) = await WithPreviousVersionAsync();
        source.FailInfo(Doc("a.txt"), new IOException("Error de E/S."));

        var result = await RunAsync(storage, time, source, DocumentsOnly());

        Assert.Equal(nameof(RunIssueCode.FileChangedDuringCopy), Assert.Single(result.Warnings).Code);
        Assert.Equal(Sha256("cambiado"u8.ToArray()), Assert.Single(await EntriesAsync(storage, result)).Hash.ToString());
    }

    public static TheoryData<int, uint, string> ChangedAfterCopy => new()
    {
        { (int)(File | SourceAttributes.RecallOnDataAccess), 0u, nameof(RunIssueCode.CloudOnlySkipped) },
        { (int)(File | Rp), ReparseTags.ProjFs, nameof(RunIssueCode.ReadError) },
        { (int)(File | Rp), ReparseTags.Symlink, nameof(RunIssueCode.ReparsePointSkipped) },
    };

    [Theory]
    [MemberData(nameof(ChangedAfterCopy))]
    public async Task HU08_Metadata_that_classify_the_file_differently_after_the_copy_are_honoured(int attributes, uint tag, string code)
    {
        var (storage, time, source, previous) = await WithPreviousVersionAsync();
        source.OnFirstRead(Doc("a.txt"), () => source.SetAttributes(Doc("a.txt"), (FileAttributes)attributes, tag));

        var result = await RunAsync(storage, time, source, DocumentsOnly());

        Assert.Equal(code, Assert.Single(result.Warnings).Code);
        var entries = await EntriesAsync(storage, result);
        Assert.Equal(code == nameof(RunIssueCode.ReparsePointSkipped) ? ["Documentos/"] : [previous.Path], entries.Select(e => e.Path)); // sin nada debajo, la carpeta vacía
        Assert.Equal(code != nameof(RunIssueCode.ReparsePointSkipped), entries.Contains(previous));
    }

    [Fact]
    public async Task HU08_An_element_that_changes_class_between_listing_and_opening_is_classified_again_on_the_handle()
    {
        var storage = new InMemoryRepositoryStorage();
        var time = new ManualTimeProvider();
        var inner = new InMemorySourceFileSystem { ClassifyOnOpen = true };
        inner.AddFile(Doc(@"Nube\x.txt"), "x"u8.ToArray());
        inner.AddFile(Doc(@"Proy\y.txt"), "y"u8.ToArray());
        inner.AddFile(Doc("z.txt"), "z"u8.ToArray());
        inner.AddFile(Doc("w.txt"), "w"u8.ToArray());
        var first = await RunAsync(storage, time, inner, DocumentsOnly());
        var before = await EntriesAsync(storage, first);

        time.Advance(TimeSpan.FromDays(1));
        inner.Modify(Doc("z.txt"), "z2"u8.ToArray());
        inner.Modify(Doc("w.txt"), "w2"u8.ToArray());
        var source = new ChangeAfterListingSource(inner, DocumentsRoot, () =>
        {
            inner.SetAttributes(Doc("Nube"), Dir | Rp | SourceAttributes.RecallOnOpen, OneDriveTag);
            inner.SetAttributes(Doc("Proy"), Dir | Rp, ReparseTags.ProjFs);
            inner.SetAttributes(Doc("z.txt"), File | Rp, UnknownTag);
            inner.SetAttributes(Doc("w.txt"), File | SourceAttributes.RecallOnDataAccess);
        });
        var result = await RunAsync(storage, time, source, DocumentsOnly());

        Assert.Equal(
            [
                ("Documentos/Nube", nameof(RunIssueCode.CloudOnlySkipped)),
                ("Documentos/Proy", nameof(RunIssueCode.ReadError)),
                ("Documentos/w.txt", nameof(RunIssueCode.CloudOnlySkipped)),
                ("Documentos/z.txt", nameof(RunIssueCode.ReadError)),
            ],
            result.Warnings.Select(w => (w.Path, w.Code)).OrderBy(w => w.Path, StringComparer.Ordinal));
        Assert.Equal(before, await EntriesAsync(storage, result));
    }

    // ================================================================== Raíz de un origen

    public static TheoryData<int, uint, string> UnreadableRoots => new()
    {
        { (int)(Dir | Rp), ReparseTags.MountPoint, nameof(RunIssueCode.ReparsePointSkipped) },
        { (int)(Dir | Rp), ReparseTags.Symlink, nameof(RunIssueCode.ReparsePointSkipped) },
        { (int)(Dir | SourceAttributes.RecallOnOpen), 0u, nameof(RunIssueCode.CloudOnlySkipped) },
        { (int)(Dir | Rp | FileAttributes.Offline), OneDriveTag, nameof(RunIssueCode.CloudOnlySkipped) },
        { (int)(Dir | Rp), ReparseTags.ProjFs, nameof(RunIssueCode.ReadError) },
        { (int)(Dir | Rp), UnknownTag, nameof(RunIssueCode.ReadError) },
        { (int)(Dir | Rp), 0u, nameof(RunIssueCode.ReadError) },
    };

    [Theory]
    [MemberData(nameof(UnreadableRoots))]
    [Trait("Requirement", "§8.4")]
    public async Task HU08_A_source_root_in_any_non_readable_class_gives_SourceRootUnreadable_and_keeps_the_whole_source(int attributes, uint tag, string cause)
    {
        var storage = new InMemoryRepositoryStorage();
        var time = new ManualTimeProvider();
        var source = new InMemorySourceFileSystem { ClassifyOnOpen = true };
        source.AddFile(Doc(@"sub\a.txt"), "a"u8.ToArray());
        source.AddFile(Doc("b.txt"), "b"u8.ToArray());
        source.AddFile(PhotosRoot + @"\foto.jpg", "foto"u8.ToArray());
        var first = await RunAsync(storage, time, source, Request());
        var documents = (await EntriesAsync(storage, first)).Where(e => e.Path.StartsWith("Documentos/", StringComparison.Ordinal)).ToList();

        time.Advance(TimeSpan.FromDays(1));
        source.SetAttributes(DocumentsRoot, (FileAttributes)attributes, tag);
        source.AddFile(PhotosRoot + @"\nueva.jpg", "nueva"u8.ToArray());
        var result = await RunAsync(storage, time, source, Request());

        var warning = Assert.Single(result.Warnings);
        Assert.Equal(("Documentos", nameof(RunIssueCode.SourceRootUnreadable)), (warning.Path, warning.Code));
        Assert.Contains($"({cause})", warning.Message, StringComparison.Ordinal);
        var entries = await EntriesAsync(storage, result);
        Assert.Equal(documents, entries.Where(e => e.Path.StartsWith("Documentos/", StringComparison.Ordinal)));
        Assert.Contains(entries, e => e.Path == "Fotos/nueva.jpg");
    }

    [Fact]
    public async Task HU08_A_populated_cloud_folder_as_source_root_is_traversed()
    {
        var storage = new InMemoryRepositoryStorage();
        var source = new InMemorySourceFileSystem { ClassifyOnOpen = true };
        source.AddFile(Doc("a.txt"), "a"u8.ToArray(), attributes: File | Rp, reparseTag: OneDriveTag);
        source.SetAttributes(DocumentsRoot, Dir | Rp, OneDriveTag);

        var result = await RunAsync(storage, new ManualTimeProvider(), source, DocumentsOnly());

        Assert.Empty(result.Warnings);
        Assert.Equal(["Documentos/a.txt"], (await EntriesAsync(storage, result)).Select(e => e.Path));
    }

    // ================================================================== Vista previa

    [Fact]
    [Trait("Requirement", "RF-04")]
    public async Task HU08_5_The_preview_classifies_like_the_backup()
    {
        var source = new InMemorySourceFileSystem { ClassifyOnOpen = true };
        source.AddFile(Doc("normal.txt"), new byte[1]);
        source.AddFile(Doc("enlace.txt"), new byte[10], attributes: File | Rp, reparseTag: ReparseTags.Symlink);
        source.AddFile(Doc("proyectado.txt"), new byte[100], attributes: File | Rp, reparseTag: ReparseTags.ProjFs);
        source.AddFile(Doc("nube.txt"), new byte[1000], attributes: File | SourceAttributes.RecallOnDataAccess);
        source.AddFile(Doc("wof.bin"), new byte[10000], attributes: File | Rp, reparseTag: ReparseTags.Wof);
        source.AddFile(Doc(@"NoPoblada\x.txt"), new byte[7]);
        source.SetAttributes(Doc("NoPoblada"), Dir | SourceAttributes.RecallOnOpen);
        source.AddFile(Doc(@"ProjFS\y.txt"), new byte[7]);
        source.SetAttributes(Doc("ProjFS"), Dir | Rp, ReparseTags.ProjFs);
        source.AddFile(Doc(@"Junction\z.txt"), new byte[7]);
        source.SetAttributes(Doc("Junction"), Dir | Rp, ReparseTags.MountPoint);
        source.AddFile(Doc(@"Poblada\p.txt"), new byte[3], attributes: File | Rp, reparseTag: OneDriveTag);
        source.SetAttributes(Doc("Poblada"), Dir | Rp, OneDriveTag);

        var result = await new ExclusionPreview().ComputeAsync(source, [DefaultSources[0]], [], Ct);

        // Sin abrir nada: normal, no legible, nube, WOF y el de la carpeta poblada; ni el enlace ni lo de las carpetas omitidas.
        Assert.Equal((5L, 11104L), (result.IncludedFiles, result.IncludedBytes));
        Assert.Equal(2, result.UnreadableDirectories);
        Assert.Equal(0, source.OpenCount);
    }

    // ================================================================== Auxiliares

    /// <summary>Repositorio con una versión de <c>a.txt</c> y un archivo modificado después, listo para la segunda copia.</summary>
    private static async Task<(InMemoryRepositoryStorage Storage, ManualTimeProvider Time, InMemorySourceFileSystem Source, SnapshotEntry Previous)> WithPreviousVersionAsync()
    {
        var storage = new InMemoryRepositoryStorage();
        var time = new ManualTimeProvider();
        var source = new InMemorySourceFileSystem();
        source.AddFile(Doc("a.txt"), "original"u8.ToArray());
        var first = await RunAsync(storage, time, source, DocumentsOnly());
        var previous = Assert.Single(await EntriesAsync(storage, first));
        time.Advance(TimeSpan.FromDays(1));
        source.Modify(Doc("a.txt"), "cambiado"u8.ToArray());
        return (storage, time, source, previous);
    }

    /// <summary>Ejecuta <paramref name="change"/> después de listar <paramref name="folder"/>, antes de abrir sus elementos.</summary>
    private sealed class ChangeAfterListingSource(InMemorySourceFileSystem inner, string folder, Action change) : ISourceFileSystem
    {
        public async IAsyncEnumerable<SourceEntry> EnumerateAsync(string directoryPath, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var entries = new List<SourceEntry>();
            await foreach (var entry in inner.EnumerateAsync(directoryPath, cancellationToken))
            {
                entries.Add(entry);
            }

            if (string.Equals(directoryPath, folder, StringComparison.Ordinal))
            {
                change();
            }

            foreach (var entry in entries)
            {
                yield return entry;
            }
        }

        public Task<Stream> OpenReadAsync(string filePath, CancellationToken cancellationToken) => inner.OpenReadAsync(filePath, cancellationToken);

        public Task<SourceEntry?> GetFileInfoAsync(string filePath, CancellationToken cancellationToken) => inner.GetFileInfoAsync(filePath, cancellationToken);
    }
}
