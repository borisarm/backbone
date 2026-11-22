using System.Runtime.CompilerServices;
using Backbone.Contracts;
using Backbone.Core.Engine;
using Backbone.Core.Repository;
using Backbone.Core.Repository.Locking;
using Backbone.Core.Storage;
using Backbone.Core.Tests.Support;
using static Backbone.Core.Tests.Support.EngineHarness;

namespace Backbone.Core.Tests.Engine;

/// <summary>
/// HU-08 en el motor: exclusiones por defecto (criterio 1), patrones (criterio 2), puntos de reanálisis (criterio 3) y
/// archivos solo en la nube (criterio 4), con la exclusión obligatoria de ADR-16 intacta e independiente de los patrones.
/// </summary>
[Trait("Story", "HU-08")]
public sealed class BackupEngineExclusionTests
{
    private const string SystemRoot = @"E:\Montado";

    private static readonly string[] Rf04Defaults =
        ["desktop.ini", "thumbs.db", "~$*", "*.tmp", "$RECYCLE.BIN", "System Volume Information"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static BackupRequest WithExclusions(IReadOnlyList<string> exclusions, IReadOnlyList<BackupSource>? sources = null) =>
        Request(sources) with { Exclusions = exclusions };

    private static async Task<List<string>> PathsAsync(InMemoryRepositoryStorage storage, BackupResult result) =>
        [.. (await ReadEntriesAsync(storage, result.Snapshot.Id)).Select(e => e.Path).Order(StringComparer.Ordinal)];

    // ================================================================== Criterio 1: exclusiones por defecto

    /// <summary>Árbol con un ejemplar de cada exclusión por defecto y archivos normales.</summary>
    private static InMemorySourceFileSystem TreeWithDefaults()
    {
        var source = new InMemorySourceFileSystem();
        source.AddFile(DocumentsRoot + @"\informe.docx", "informe"u8.ToArray());
        source.AddFile(DocumentsRoot + @"\~$informe.docx", "bloqueo de Word"u8.ToArray());
        source.AddFile(DocumentsRoot + @"\Desktop.ini", "[.ShellClassInfo]"u8.ToArray());
        source.AddFile(DocumentsRoot + @"\copia.TMP", "temporal"u8.ToArray());
        source.AddFile(DocumentsRoot + @"\$RECYCLE.BIN\S-1-5-21\$R1.txt", "papelera"u8.ToArray());
        source.AddFile(DocumentsRoot + @"\System Volume Information\tracking.log", "sistema"u8.ToArray());
        source.AddFile(PhotosRoot + @"\2026\Thumbs.db", "miniaturas"u8.ToArray());
        source.AddFile(PhotosRoot + @"\2026\playa.jpg", "foto"u8.ToArray());
        return source;
    }

    [Fact]
    [Trait("Requirement", "RF-04")]
    public void HU08_1_The_default_exclusions_are_those_of_RF04_valid_and_preloaded_in_a_new_job()
    {
        Assert.Equal(Rf04Defaults, JobDefaults.Exclusions);
        Assert.Equal(Rf04Defaults, JobDefaults.NewJob().Exclusions);
        Assert.All(JobDefaults.Exclusions, p => Assert.Equal(ExclusionPatternError.None, ExclusionPattern.Validate(p)));
    }

    [Fact]
    [Trait("Requirement", "RF-04")]
    public async Task HU08_1_With_the_default_exclusions_none_of_their_items_is_traversed_opened_or_stored()
    {
        var storage = new InMemoryRepositoryStorage();
        var source = TreeWithDefaults();
        var enumerated = new List<string>();
        source.OnEnumerate = enumerated.Add;

        var result = await RunAsync(storage, new ManualTimeProvider(), source, WithExclusions(JobDefaults.Exclusions));

        Assert.Equal(RunState.Completed, result.State);
        Assert.Empty(result.Warnings);
        Assert.Equal(0, result.Snapshot.Stats.Skipped);
        Assert.Equal(["Documentos/informe.docx", "Fotos/2026/playa.jpg"], await PathsAsync(storage, result));
        Assert.Equal([DocumentsRoot + @"\informe.docx", PhotosRoot + @"\2026\playa.jpg"], source.Opened.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(enumerated, p => p.Contains("RECYCLE", StringComparison.Ordinal) || p.Contains("System Volume", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Requirement", "RF-04")]
    public async Task HU08_1_Removed_defaults_no_longer_exclude_anything_the_engine_has_no_built_in_list()
    {
        var storage = new InMemoryRepositoryStorage();
        var source = TreeWithDefaults();

        // Sin la lista (todas quitadas): se copia todo.
        var all = await RunAsync(storage, new ManualTimeProvider(), source, WithExclusions([]));
        Assert.Equal(8, (await PathsAsync(storage, all)).Count);

        // Quitando solo *.tmp y desktop.ini: esos dos vuelven a copiarse; el resto sigue excluido.
        var storage2 = new InMemoryRepositoryStorage();
        var some = await RunAsync(storage2, new ManualTimeProvider(), source, WithExclusions([.. JobDefaults.Exclusions.Except(["*.tmp", "desktop.ini"])]));
        Assert.Equal(
            ["Documentos/Desktop.ini", "Documentos/copia.TMP", "Documentos/informe.docx", "Fotos/2026/playa.jpg"],
            await PathsAsync(storage2, some));
    }

    // ================================================================== Criterio 2: patrones

    [Fact]
    [Trait("Requirement", "RF-04")]
    public async Task HU08_2_The_three_RF04_patterns_exclude_exactly_what_is_expected_in_a_real_run()
    {
        var storage = new InMemoryRepositoryStorage();
        var source = new InMemorySourceFileSystem();
        source.AddFile(DocumentsRoot + @"\a.txt", "a"u8.ToArray());
        source.AddFile(DocumentsRoot + @"\a.TMP", "t"u8.ToArray());
        source.AddFile(DocumentsRoot + @"\a.tmp.txt", "no es tmp"u8.ToArray());
        source.AddFile(DocumentsRoot + @"\web\app.js", "app"u8.ToArray());
        source.AddFile(DocumentsRoot + @"\web\Node_Modules\lib\index.js", "dependencia"u8.ToArray());
        source.AddFile(DocumentsRoot + @"\web\node_modules2\x.js", "otra carpeta"u8.ToArray());
        source.AddFile(DocumentsRoot + @"\web\node_modules", "archivo con ese nombre"u8.ToArray());
        source.AddFile(DocumentsRoot + @"\privado\clave.txt", "secreto"u8.ToArray());
        source.AddFile(DocumentsRoot + @"\privado\sub\otra.txt", "secreto"u8.ToArray());
        source.AddFile(DocumentsRoot + @"\Otro\Privado\visible.txt", "no anclado"u8.ToArray());
        source.AddFile(PhotosRoot + @"\Privado\foto.jpg", "otro alias"u8.ToArray());
        var enumerated = new List<string>();
        source.OnEnumerate = enumerated.Add;

        var result = await RunAsync(storage, new ManualTimeProvider(), source, WithExclusions(["*.tmp", "**/node_modules/**", "Documentos/Privado/**"]));

        Assert.Equal(RunState.Completed, result.State);
        Assert.Empty(result.Warnings);
        Assert.Equal(
            [
                "Documentos/Otro/Privado/visible.txt",
                "Documentos/a.tmp.txt",
                "Documentos/a.txt",
                "Documentos/web/app.js",
                "Documentos/web/node_modules",
                "Documentos/web/node_modules2/x.js",
                "Fotos/Privado/foto.jpg",
            ],
            await PathsAsync(storage, result));

        // Las carpetas excluidas no se recorren (ni aparecen como carpetas vacías) y no se abre nada excluido.
        Assert.DoesNotContain(enumerated, p => p.Contains(@"\Node_Modules", StringComparison.Ordinal) || p.EndsWith(@"\privado", StringComparison.Ordinal));
        Assert.DoesNotContain(source.Opened, p => p.EndsWith(".TMP", StringComparison.Ordinal) || p.Contains("privado", StringComparison.Ordinal));
        await RepositoryInvariants.AssertConsistentAsync(storage);
    }

    [Fact]
    [Trait("Requirement", "RF-04")]
    public async Task HU08_2_A_folder_left_empty_by_exclusions_is_stored_as_an_empty_folder_and_progress_skips_excluded_files()
    {
        var storage = new InMemoryRepositoryStorage();
        var source = new InMemorySourceFileSystem();
        source.AddFile(DocumentsRoot + @"\Temporal\a.tmp", "t"u8.ToArray());
        source.AddFile(DocumentsRoot + @"\b.txt", "b"u8.ToArray());
        var progress = new List<BackupProgress>();

        var result = await RunAsync(storage, new ManualTimeProvider(), source, WithExclusions(["*.tmp"], [DefaultSources[0]]), progress: new SyncProgress<BackupProgress>(progress.Add));

        Assert.Equal(["Documentos/Temporal/", "Documentos/b.txt"], await PathsAsync(storage, result));
        Assert.Equal(["Documentos/b.txt"], progress.Select(p => p.CurrentPath));
    }

    [Fact]
    [Trait("Requirement", "RF-04")]
    public async Task HU08_2_Adding_a_pattern_removes_the_files_from_the_next_snapshot_without_touching_the_previous_one()
    {
        var storage = new InMemoryRepositoryStorage();
        var time = new ManualTimeProvider();
        var source = SampleSource();
        var first = await RunAsync(storage, time, source);
        time.Advance(TimeSpan.FromDays(1));
        source.ResetCounters();

        var second = await RunAsync(storage, time, source, WithExclusions(["Documentos/Facturas/**", "*.ini"]));

        var paths = await PathsAsync(storage, second);
        Assert.DoesNotContain(paths, p => p.StartsWith("Documentos/Facturas", StringComparison.Ordinal) || p.EndsWith(".ini", StringComparison.Ordinal));
        Assert.Contains("Documentos/notas.txt", paths);
        Assert.Contains(await PathsAsync(storage, first), p => p.StartsWith("Documentos/Facturas/", StringComparison.Ordinal));
        Assert.Equal(0, source.OpenCount); // lo demás no ha cambiado (RF-23)
        Assert.Empty(second.Warnings);
    }

    [Fact]
    [Trait("Requirement", "RF-04")]
    [Trait("Requirement", "RF-25")]
    public async Task HU08_2_Contents_kept_from_the_parent_for_an_unreadable_folder_honour_the_current_patterns()
    {
        var storage = new InMemoryRepositoryStorage();
        var time = new ManualTimeProvider();
        var source = new InMemorySourceFileSystem();
        source.AddFile(DocumentsRoot + @"\Proyecto\a.txt", "a"u8.ToArray());
        source.AddFile(DocumentsRoot + @"\Proyecto\b.tmp", "b"u8.ToArray());
        source.AddFile(DocumentsRoot + @"\Proyecto\node_modules\x.js", "x"u8.ToArray());
        await RunAsync(storage, time, source, Request([DefaultSources[0]]));
        time.Advance(TimeSpan.FromDays(1));
        source.FailEnumerate(DocumentsRoot + @"\Proyecto", new UnauthorizedAccessException("denegado"));

        var result = await RunAsync(storage, time, source, WithExclusions(["*.tmp", "**/node_modules/**"], [DefaultSources[0]]));

        Assert.Equal(nameof(RunIssueCode.AccessDenied), Assert.Single(result.Warnings).Code);
        Assert.Equal(["Documentos/Proyecto/a.txt"], await PathsAsync(storage, result));
    }

    [Fact]
    [Trait("Requirement", "ADR-16")]
    public async Task HU08_2_The_mandatory_exclusion_stays_in_force_with_no_patterns_with_patterns_and_with_a_catch_all_negation_attempt()
    {
        foreach (var patterns in new IReadOnlyList<string>[] { [], JobDefaults.Exclusions, ["*.txt"], ["Backbone"] })
        {
            var storage = new InMemoryRepositoryStorage();
            var source = new InMemorySourceFileSystem();
            source.AddFile(SystemRoot + @"\Users\ana\doc.md", "documento"u8.ToArray());
            source.AddFile(SystemRoot + @"\ProgramData\Backbone\credentials\0123abcd.bin", "CREDENCIAL"u8.ToArray());
            source.AddFile(SystemRoot + @"\ProgramData\Otro\a.md", "otro"u8.ToArray());
            var enumerated = new List<string>();
            source.OnEnumerate = enumerated.Add;
            var system = new BackupSource("C", @"C:\", SystemRoot) { MandatoryExclusions = [@"ProgramData\Backbone"] };

            var result = await RunAsync(storage, new ManualTimeProvider(), source, WithExclusions(patterns, [system]));

            Assert.Equal(RunState.Completed, result.State);
            Assert.Empty(result.Warnings);
            Assert.Equal(["C/ProgramData/Otro/a.md", "C/Users/ana/doc.md"], await PathsAsync(storage, result));
            Assert.DoesNotContain(enumerated, p => p.Contains(@"\Backbone", StringComparison.Ordinal));
            Assert.DoesNotContain(source.Opened, p => p.Contains(@"\Backbone", StringComparison.Ordinal));
        }
    }

    [Fact]
    [Trait("Requirement", "§7.5")]
    public async Task HU08_2_Invalid_or_null_pattern_lists_are_rejected_before_reading_anything()
    {
        var storage = new InMemoryRepositoryStorage();
        var source = SampleSource();

        await Assert.ThrowsAsync<ArgumentException>(() => RunAsync(storage, new ManualTimeProvider(), source, WithExclusions(["*.tmp", "a//b"])));
        await Assert.ThrowsAsync<ArgumentNullException>(() => RunAsync(storage, new ManualTimeProvider(), source, Request() with { Exclusions = null! }));

        Assert.Equal(0, source.EnumerationCount);
        Assert.Empty(await ListSnapshotsAsync(storage));
    }

    [Fact]
    [Trait("Requirement", "§8.4")]
    public async Task HU08_2_Excluded_children_do_not_count_against_the_listing_budget()
    {
        // 200 temporales con nombres largos no caben en el presupuesto, pero al excluirse no se guardan en el listado.
        var source = new InMemorySourceFileSystem();
        for (var i = 0; i < 200; i++)
        {
            source.AddFile(DocumentsRoot + $@"\{new string('t', 100)}{i}.tmp", [1]);
        }

        source.AddFile(DocumentsRoot + @"\a.txt", "a"u8.ToArray());
        var budget = 4096;

        var withoutPatterns = await RunAsync(new InMemoryRepositoryStorage(), new ManualTimeProvider(), source, Request([DefaultSources[0]]), configure: e => e.ListingBudgetBytes = budget);
        Assert.Equal(nameof(RunIssueCode.SourceRootUnreadable), Assert.Single(withoutPatterns.Warnings).Code);

        var storage = new InMemoryRepositoryStorage();
        var withPatterns = await RunAsync(storage, new ManualTimeProvider(), source, WithExclusions(["*.tmp"], [DefaultSources[0]]), configure: e => e.ListingBudgetBytes = budget);
        Assert.Empty(withPatterns.Warnings);
        Assert.Equal(["Documentos/a.txt"], await PathsAsync(storage, withPatterns));
    }

    // ================================================================== Criterio 3: puntos de reanálisis

    [Fact]
    [Trait("Requirement", "RF-06")]
    public async Task HU08_3_Enumerated_junctions_and_symbolic_links_are_not_followed_and_are_reported_as_skipped()
    {
        var storage = new InMemoryRepositoryStorage();
        var source = new InMemorySourceFileSystem();
        source.AddFile(DocumentsRoot + @"\a.txt", "a"u8.ToArray());
        source.AddFile(DocumentsRoot + @"\Junction\secreto.txt", "fuera del origen"u8.ToArray());
        source.AddFile(DocumentsRoot + @"\enlace.txt", "destino del enlace"u8.ToArray());
        source.MakeReparsePoint(DocumentsRoot + @"\Junction");
        source.MakeReparsePoint(DocumentsRoot + @"\enlace.txt");
        var enumerated = new List<string>();
        source.OnEnumerate = enumerated.Add;

        var result = await RunAsync(storage, new ManualTimeProvider(), source, WithExclusions(JobDefaults.Exclusions, [DefaultSources[0]]));

        Assert.Equal(RunState.CompletedWithWarnings, result.State);
        Assert.Equal(
            [("Documentos/Junction", nameof(RunIssueCode.ReparsePointSkipped)), ("Documentos/enlace.txt", nameof(RunIssueCode.ReparsePointSkipped))],
            result.Warnings.Select(w => (w.Path, w.Code)));
        Assert.Equal(2, result.Snapshot.Stats.Skipped);
        Assert.Equal(["Documentos/a.txt"], await PathsAsync(storage, result));
        Assert.Equal([DocumentsRoot], enumerated);
        Assert.Equal([DocumentsRoot + @"\a.txt"], source.Opened);
        Assert.Equal(1, source.InfoQueryCount);
    }

    [Fact]
    [Trait("Requirement", "RF-06")]
    public async Task HU08_3_A_link_that_a_pattern_excludes_is_neither_followed_nor_reported()
    {
        var storage = new InMemoryRepositoryStorage();
        var source = new InMemorySourceFileSystem();
        source.AddFile(DocumentsRoot + @"\a.txt", "a"u8.ToArray());
        source.AddFile(DocumentsRoot + @"\node_modules\x.js", "x"u8.ToArray());
        source.MakeReparsePoint(DocumentsRoot + @"\node_modules");

        var result = await RunAsync(storage, new ManualTimeProvider(), source, WithExclusions(["**/node_modules/**"], [DefaultSources[0]]));

        Assert.Equal(RunState.Completed, result.State);
        Assert.Empty(result.Warnings);
    }

    public static TheoryData<string> SwitchKinds => ["Folder", "File", "FileAfterCopy"];

    [Theory]
    [MemberData(nameof(SwitchKinds))]
    [Trait("Requirement", "RF-06")]
    [Trait("Requirement", "§8.2")]
    public async Task HU08_3_An_item_replaced_by_a_junction_between_enumeration_and_opening_is_skipped_and_never_read(string kind)
    {
        var storage = new InMemoryRepositoryStorage();
        var inner = new InMemorySourceFileSystem();
        inner.AddFile(DocumentsRoot + @"\Carpeta\dentro.txt", "dentro"u8.ToArray());
        inner.AddFile(DocumentsRoot + @"\archivo.txt", "archivo"u8.ToArray());
        inner.AddFile(DocumentsRoot + @"\b.txt", "b"u8.ToArray());
        var source = new ReparseSwitchingSource(inner);
        var (target, expectedPath) = kind switch
        {
            "Folder" => (DocumentsRoot + @"\Carpeta", "Documentos/Carpeta"),
            _ => (DocumentsRoot + @"\archivo.txt", "Documentos/archivo.txt"),
        };
        switch (kind)
        {
            case "Folder":
                source.EnumerateBecomesLink.Add(target);
                break;
            case "File":
                source.OpenBecomesLink.Add(target);
                break;
            default:
                source.InfoBecomesLink.Add(target);
                break;
        }

        var result = await RunAsync(storage, new ManualTimeProvider(), source, WithExclusions(JobDefaults.Exclusions, [DefaultSources[0]]));

        var warning = Assert.Single(result.Warnings);
        Assert.Equal((expectedPath, nameof(RunIssueCode.ReparsePointSkipped)), (warning.Path, warning.Code));
        var paths = await PathsAsync(storage, result);
        Assert.DoesNotContain(paths, p => p.StartsWith(expectedPath, StringComparison.Ordinal));
        Assert.Contains("Documentos/b.txt", paths);
        if (kind != "FileAfterCopy")
        {
            Assert.DoesNotContain(inner.Opened, p => p.StartsWith(target, StringComparison.Ordinal));
        }

        await RepositoryInvariants.AssertConsistentAsync(storage);
    }

    [Fact]
    [Trait("Requirement", "RF-06")]
    [Trait("Requirement", "ADR-18")]
    public async Task HU08_3_An_intermediate_folder_replaced_by_a_junction_after_listing_does_not_leak_the_secret_file()
    {
        var storage = new InMemoryRepositoryStorage();
        var inner = new InMemorySourceFileSystem();
        inner.AddFile(DocumentsRoot + @"\A\B\x.txt", "normal"u8.ToArray());
        inner.AddFile(@"C:\Secreto\B\x.txt", "SECRETO"u8.ToArray());
        var source = new FinalPathCheckingSource(inner, [DefaultSources[0]]);
        source.SwapAfterListing(DocumentsRoot + @"\A", @"C:\Secreto");

        var result = await RunAsync(storage, new ManualTimeProvider(), source, WithExclusions(JobDefaults.Exclusions, [DefaultSources[0]]));

        Assert.Equal(nameof(RunIssueCode.ReparsePointSkipped), Assert.Single(result.Warnings).Code);
        Assert.DoesNotContain(inner.Opened, p => p.StartsWith(@"C:\Secreto", StringComparison.Ordinal));
        Assert.DoesNotContain(Sha256("SECRETO"u8.ToArray()), string.Join('\n', ObjectPaths(storage)), StringComparison.Ordinal);
    }

    // ================================================================== Criterio 4: OneDrive solo en la nube (R4)

    /// <summary>Atributos y etiqueta de un archivo solo en la nube (§8.2 "Archivos y carpetas solo en la nube").</summary>
    public static TheoryData<int, uint> CloudAttributes => new()
    {
        { (int)SourceAttributes.RecallOnDataAccess, 0u },
        { (int)SourceAttributes.RecallOnOpen, 0u },
        { (int)(FileAttributes.Offline | FileAttributes.ReparsePoint), ReparseTags.Cloud },
        { (int)(FileAttributes.Offline | FileAttributes.ReparsePoint), 0x9000601Au },
        { (int)(FileAttributes.Offline | FileAttributes.ReparsePoint), ReparseTags.Hsm },
        { (int)(FileAttributes.Offline | FileAttributes.ReparsePoint), ReparseTags.Hsm2 },
        { (int)(SourceAttributes.RecallOnDataAccess | FileAttributes.ReparsePoint | FileAttributes.SparseFile), 0x9000601Au },
        { (int)(SourceAttributes.RecallOnDataAccess | FileAttributes.ReparsePoint | FileAttributes.SparseFile), 0u },
        { (int)(SourceAttributes.RecallOnOpen | FileAttributes.ReparsePoint), ReparseTags.ProjFs }, // la nube va antes que "no legible"
    };

    [Theory]
    [MemberData(nameof(CloudAttributes))]
    [Trait("Requirement", "RF-06")]
    [Trait("Requirement", "R4")]
    public async Task HU08_4_Cloud_only_files_are_skipped_with_CloudOnlySkipped_without_opening_them(int attribute, uint tag)
    {
        var storage = new InMemoryRepositoryStorage();
        var source = new InMemorySourceFileSystem();
        source.AddFile(DocumentsRoot + @"\OneDrive\local.txt", "descargado"u8.ToArray());
        source.AddFile(DocumentsRoot + @"\OneDrive\nube.pdf", new byte[5000], attributes: FileAttributes.Archive | (FileAttributes)attribute, reparseTag: tag);

        var result = await RunAsync(storage, new ManualTimeProvider(), source, WithExclusions(JobDefaults.Exclusions, [DefaultSources[0]]));

        var warning = Assert.Single(result.Warnings);
        Assert.Equal(("Documentos/OneDrive/nube.pdf", nameof(RunIssueCode.CloudOnlySkipped)), (warning.Path, warning.Code));
        Assert.Contains("no se descarga", warning.Message, StringComparison.Ordinal);
        Assert.Equal(RunState.CompletedWithWarnings, result.State);
        Assert.Equal(1, result.Snapshot.Stats.Skipped);
        Assert.Equal(["Documentos/OneDrive/local.txt"], await PathsAsync(storage, result));

        // Ni se abre ni se consultan sus metadatos: nada que pueda desencadenar la descarga.
        Assert.Equal([DocumentsRoot + @"\OneDrive\local.txt"], source.Opened);
        Assert.Equal(1, source.InfoQueryCount);
    }

    [Fact]
    [Trait("Requirement", "RF-06")]
    [Trait("Requirement", "R4")]
    public async Task HU08_4_A_file_freed_up_to_the_cloud_keeps_its_previous_version_and_comes_back_when_downloaded()
    {
        var storage = new InMemoryRepositoryStorage();
        var time = new ManualTimeProvider();
        var source = new InMemorySourceFileSystem();
        var path = DocumentsRoot + @"\OneDrive\informe.docx";
        source.AddFile(path, "versión local"u8.ToArray());
        var first = await RunAsync(storage, time, source, Request([DefaultSources[0]]));
        var original = Assert.Single(await ReadEntriesAsync(storage, first.Snapshot.Id));

        // OneDrive libera espacio: el archivo queda solo en la nube.
        time.Advance(TimeSpan.FromDays(1));
        source.SetAttributes(path, FileAttributes.Archive | SourceAttributes.RecallOnDataAccess | FileAttributes.SparseFile);
        source.ResetCounters();
        var second = await RunAsync(storage, time, source, Request([DefaultSources[0]]));

        var warning = Assert.Single(second.Warnings);
        Assert.Equal(nameof(RunIssueCode.CloudOnlySkipped), warning.Code);
        Assert.Contains("se conserva su versión anterior", warning.Message, StringComparison.Ordinal);
        Assert.Equal([original], await ReadEntriesAsync(storage, second.Snapshot.Id));
        Assert.Equal(0, source.OpenCount);
        Assert.Equal(0, second.Snapshot.Stats.NewObjects);

        // Vuelve a estar descargado: se copia con normalidad y sin avisos.
        time.Advance(TimeSpan.FromDays(1));
        source.SetAttributes(path, FileAttributes.Archive);
        var third = await RunAsync(storage, time, source, Request([DefaultSources[0]]));
        Assert.Empty(third.Warnings);
        Assert.Equal(original.Hash, Assert.Single(await ReadEntriesAsync(storage, third.Snapshot.Id)).Hash);
        await RepositoryInvariants.AssertConsistentAsync(storage);
    }

    [Fact]
    [Trait("Requirement", "R4")]
    public async Task HU08_4_A_populated_cloud_folder_is_traversed_and_only_its_dehydrated_files_are_skipped()
    {
        var storage = new InMemoryRepositoryStorage();
        var source = new InMemorySourceFileSystem();
        source.AddFile(DocumentsRoot + @"\OneDrive\local.txt", "descargado"u8.ToArray(), attributes: FileAttributes.Archive | FileAttributes.ReparsePoint, reparseTag: 0x9000601A);
        source.AddFile(DocumentsRoot + @"\OneDrive\nube.txt", "en la nube"u8.ToArray(), attributes: SourceAttributes.RecallOnDataAccess | FileAttributes.ReparsePoint, reparseTag: 0x9000601A);
        // Lo que se ve en una máquina con OneDrive: 0x410 y 0x9000601A, sin RECALL_* (§8.2).
        source.SetAttributes(DocumentsRoot + @"\OneDrive", FileAttributes.Directory | FileAttributes.ReparsePoint, 0x9000601A);

        var result = await RunAsync(storage, new ManualTimeProvider(), source, Request([DefaultSources[0]]));

        Assert.Equal(["Documentos/OneDrive/local.txt"], await PathsAsync(storage, result));
        Assert.Equal(("Documentos/OneDrive/nube.txt", nameof(RunIssueCode.CloudOnlySkipped)), (Assert.Single(result.Warnings).Path, Assert.Single(result.Warnings).Code));
    }

    [Fact]
    [Trait("Requirement", "R4")]
    public async Task HU08_4_A_cloud_only_file_that_a_pattern_excludes_gives_no_warning()
    {
        var storage = new InMemoryRepositoryStorage();
        var source = new InMemorySourceFileSystem();
        source.AddFile(DocumentsRoot + @"\a.txt", "a"u8.ToArray());
        source.AddFile(DocumentsRoot + @"\b.tmp", "b"u8.ToArray(), attributes: SourceAttributes.RecallOnDataAccess);

        var result = await RunAsync(storage, new ManualTimeProvider(), source, WithExclusions(["*.tmp"], [DefaultSources[0]]));

        Assert.Equal(RunState.Completed, result.State);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    [Trait("Requirement", "R4")]
    [Trait("Requirement", "§8.2")]
    public async Task HU08_4_A_file_that_becomes_cloud_only_before_opening_is_skipped_with_CloudOnlySkipped_and_keeps_its_previous_version()
    {
        var storage = new InMemoryRepositoryStorage();
        var time = new ManualTimeProvider();
        var inner = new InMemorySourceFileSystem();
        inner.AddFile(DocumentsRoot + @"\a.txt", "a"u8.ToArray());
        inner.AddFile(DocumentsRoot + @"\b.txt", "b"u8.ToArray());
        var first = await RunAsync(storage, time, inner, Request([DefaultSources[0]]));
        time.Advance(TimeSpan.FromDays(1));
        inner.Modify(DocumentsRoot + @"\a.txt", "cambiado"u8.ToArray());
        var source = new CloudOnOpenSource(inner, DocumentsRoot + @"\a.txt");

        var result = await RunAsync(storage, time, source, Request([DefaultSources[0]]));

        var warning = Assert.Single(result.Warnings);
        Assert.Equal(("Documentos/a.txt", nameof(RunIssueCode.CloudOnlySkipped)), (warning.Path, warning.Code));
        Assert.Contains("se conserva su versión anterior", warning.Message, StringComparison.Ordinal);
        Assert.Equal(await ReadEntriesAsync(storage, first.Snapshot.Id), await ReadEntriesAsync(storage, result.Snapshot.Id));
    }

    [Fact]
    [Trait("Requirement", "R4")]
    public async Task HU08_4_A_cloud_only_file_with_an_invalid_name_is_reported_as_PathInvalid()
    {
        var storage = new InMemoryRepositoryStorage();
        var source = new InMemorySourceFileSystem();
        source.AddFile(DocumentsRoot + @"\CON", "x"u8.ToArray(), attributes: SourceAttributes.RecallOnDataAccess);

        var result = await RunAsync(storage, new ManualTimeProvider(), source, Request([DefaultSources[0]]));

        Assert.Equal(nameof(RunIssueCode.PathInvalid), Assert.Single(result.Warnings).Code);
        Assert.Equal(0, source.OpenCount);
    }

    [Fact]
    [Trait("Requirement", "R4")]
    public void HU08_4_Cloud_attributes_have_the_Windows_values()
    {
        Assert.Equal(0x00040000, (int)SourceAttributes.RecallOnOpen);
        Assert.Equal(0x00400000, (int)SourceAttributes.RecallOnDataAccess);
        Assert.Equal(0x00001000, (int)FileAttributes.Offline);
        Assert.Equal(SourceEntryKind.Content, SourceAttributes.Classify(FileAttributes.Archive | FileAttributes.SparseFile | FileAttributes.ReparsePoint, ReparseTags.Cloud));
        Assert.Equal(SourceEntryKind.Normal, SourceAttributes.Classify(FileAttributes.Archive | FileAttributes.Offline, 0));
    }

    // ================================================================== RNF-01 con exclusiones

    [Fact]
    [Trait("Requirement", "RNF-01")]
    public async Task HU08_A_failure_at_every_repository_operation_with_exclusions_and_cloud_files_leaves_a_consistent_repository()
    {
        static (InMemorySourceFileSystem Source, BackupRequest Request) Scenario()
        {
            var source = TreeWithDefaults();
            source.AddFile(DocumentsRoot + @"\nube.txt", "nube"u8.ToArray(), attributes: SourceAttributes.RecallOnDataAccess);
            source.AddFile(DocumentsRoot + @"\node_modules\x.js", "x"u8.ToArray());
            return (source, WithExclusions([.. JobDefaults.Exclusions, "**/node_modules/**"]));
        }

        var (probeSource, probeRequest) = Scenario();
        var probeStorage = new InMemoryRepositoryStorage();
        var probe = new FaultInjectingStorage(probeStorage);
        await RunAsync(probe, new ManualTimeProvider(), probeSource, probeRequest);
        var total = probe.OperationCount;

        for (var n = 1; n <= total; n++)
        {
            foreach (var mode in new[] { FaultMode.Crash, FaultMode.Transient })
            {
                var (source, request) = Scenario();
                var storage = new InMemoryRepositoryStorage();
                var time = new ManualTimeProvider();
                var faults = new FaultInjectingStorage(storage) { FailAt = n, Mode = mode };
                var ex = await Record.ExceptionAsync(() => RunAsync(faults, time, source, request));
                Assert.True(ex is null or IOException or RepositoryException, $"{n}, {mode}: {ex}");
                await RepositoryInvariants.AssertConsistentAsync(storage);

                time.Advance(TimeSpan.FromHours(1));
                var next = await RunAsync(storage, time, source, request, owner: new LockOwner("PC-PRUEBA", 999));
                Assert.Equal(nameof(RunIssueCode.CloudOnlySkipped), Assert.Single(next.Warnings).Code);
                Assert.Equal(["Documentos/informe.docx", "Fotos/2026/playa.jpg"], await PathsAsync(storage, next));
            }
        }
    }

    /// <summary>Progreso síncrono (<see cref="Progress{T}"/> publica en el contexto de sincronización, de forma asíncrona).</summary>
    private sealed class SyncProgress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }

    /// <summary>
    /// Simula la implementación de §8.2 cuando un archivo pasa a estar solo en la nube después de enumerarlo: lo ve sobre
    /// el handle y lanza <see cref="SourceAccessException"/> con <see cref="RunIssueCode.CloudOnlySkipped"/> sin leer nada.
    /// </summary>
    private sealed class CloudOnOpenSource(InMemorySourceFileSystem inner, string cloudPath) : ISourceFileSystem
    {
        public async IAsyncEnumerable<SourceEntry> EnumerateAsync(string directoryPath, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var entry in inner.EnumerateAsync(directoryPath, cancellationToken))
            {
                yield return entry;
            }
        }

        public Task<Stream> OpenReadAsync(string filePath, CancellationToken cancellationToken) =>
            string.Equals(filePath, cloudPath, StringComparison.Ordinal)
                ? throw new SourceAccessException(RunIssueCode.CloudOnlySkipped, "El archivo está solo en la nube.")
                : inner.OpenReadAsync(filePath, cancellationToken);

        public Task<SourceEntry?> GetFileInfoAsync(string filePath, CancellationToken cancellationToken) =>
            inner.GetFileInfoAsync(filePath, cancellationToken);
    }
}
