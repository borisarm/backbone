using Backbone.Contracts;
using Backbone.Core.Engine;
using Backbone.Core.Storage;
using Backbone.Core.Tests.Support;
using static Backbone.Core.Tests.Support.EngineHarness;

namespace Backbone.Core.Tests.Engine;

/// <summary>
/// HU-08, pruebas de QA del motor: mayúsculas fuera de ASCII, exclusión obligatoria con cualquier lista, sustitución por
/// un junction en carpetas intermedias, decisión de OneDrive solo por atributos y vista previa en los bordes.
/// No fijan qué puntos de reanálisis cuentan como enlace ni cómo el servicio detecta OneDrive: el motor decide solo por
/// los atributos que le da <see cref="ISourceFileSystem"/> y por las excepciones de <see cref="SourceAccessException"/>.
/// </summary>
[Trait("Story", "HU-08")]
public sealed class ExclusionQaTests
{
    private const string SystemRoot = @"E:\Montado";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static BackupRequest WithExclusions(IReadOnlyList<string> exclusions, IReadOnlyList<BackupSource>? sources = null) =>
        Request(sources) with { Exclusions = exclusions };

    private static async Task<List<string>> PathsAsync(InMemoryRepositoryStorage storage, BackupResult result) =>
        [.. (await ReadEntriesAsync(storage, result.Snapshot.Id)).Select(e => e.Path).Order(StringComparer.Ordinal)];

    private static BackupSource SystemSource() =>
        new("C", @"C:\", SystemRoot) { MandatoryExclusions = [@"ProgramData\Backbone"] };

    // ================================================================== Criterio 2: mayúsculas y límites

    public static TheoryData<string, string, bool> NonAsciiCases => new()
    {
        { "Documentos/Público/**", "DOCUMENTOS/PÚBLICO/x.txt", true },
        { "Documentos/PÚBLICO/**", "documentos/público/x.txt", true },
        { "Ünïcode*", "Documentos/ÜNÏCODE largo.txt", true },
        { "Ünïcode*", "Documentos/ünïcode.txt", true },
        { "Ünïcode*", "Documentos/Unicode.txt", false }, // sin diacríticos no es lo mismo
        { "*.ñandú", "Fotos/a.ÑANDÚ", true },
        { "Привет/**", "Documentos/ПРИВЕТ/", false }, // anclado: el primer segmento es el alias
        { "**/Привет/**", "Documentos/ПРИВЕТ/a.txt", true },
        { "**/привет/**", "Documentos/Привет/a.txt", true },
        { "*.ΩΜΕΓΑ", "Documentos/a.ωμεγα", true },
        { "a?c", "Documentos/AÑC", true },
        { "a?c", "Documentos/añc", true },
        { "a?c", "Documentos/ac", false },
        { "straße*", "Documentos/STRASSE.txt", false }, // ß no se pliega a SS (comparación ordinal por carácter)
        { "**/node_modules/**", "node_modules/a.js", false }, // la raíz (alias) nunca se excluye aunque se llame así
        { "node_modules/**", "node_modules/a.js", false },
    };

    [Theory]
    [MemberData(nameof(NonAsciiCases))]
    public void HU08_2_Case_is_ignored_also_for_non_ASCII_letters(string pattern, string path, bool excluded) =>
        Assert.Equal(excluded, ExclusionMatcher.Create([pattern]).IsExcluded(path));

    [Fact]
    public void HU08_2_Every_criterion_2_case_gives_the_same_answer_in_upper_and_lower_case()
    {
        foreach (var row in ExclusionPatternTests_Criterion2())
        {
            var (pattern, path, expected) = row;
            var matcher = ExclusionMatcher.Create([pattern]);
            Assert.Equal(expected, matcher.IsExcluded(path.ToUpperInvariant()));
            Assert.Equal(expected, matcher.IsExcluded(path.ToLowerInvariant()));
            Assert.Equal(expected, ExclusionMatcher.Create([pattern.ToUpperInvariant()]).IsExcluded(path));
            Assert.Equal(expected, ExclusionMatcher.Create([pattern.ToLowerInvariant()]).IsExcluded(path));
        }
    }

    private static IEnumerable<(string Pattern, string Path, bool Excluded)> ExclusionPatternTests_Criterion2() =>
        ExclusionPatternTests.Criterion2Cases.Select(r => (Pattern: (string)r.Data.Item1!, Path: (string)r.Data.Item2!, Excluded: (bool)r.Data.Item3!));

    [Fact]
    public void HU08_2_Paths_beyond_260_characters_and_zero_length_names_are_evaluated_without_error()
    {
        var folder = string.Join('/', Enumerable.Repeat(new string('d', 50), 8)); // 8 x 51 = 408 caracteres
        var matcher = ExclusionMatcher.Create(["*.tmp", "**/node_modules/**", "Documentos/Privado/**"]);

        Assert.True(matcher.IsExcluded($"Documentos/{folder}/a.tmp"));
        Assert.True(matcher.IsExcluded($"Documentos/{folder}/node_modules/x.js"));
        Assert.False(matcher.IsExcluded($"Documentos/{folder}/a.txt"));
        Assert.True(matcher.IsExcluded($"Documentos/Privado/{folder}/a.txt"));
        Assert.False(matcher.IsExcluded("Documentos"));
    }

    [Fact]
    public async Task HU08_2_A_non_ASCII_folder_is_excluded_in_a_real_run_whatever_its_case_and_empty_files_follow_the_same_rules()
    {
        var storage = new InMemoryRepositoryStorage();
        var source = new InMemorySourceFileSystem();
        source.AddFile(DocumentsRoot + @"\Año\a.txt", "a"u8.ToArray());
        source.AddFile(DocumentsRoot + @"\AÑO\b.txt", "b"u8.ToArray());
        source.AddFile(DocumentsRoot + @"\vacío.TMP", []);
        source.AddFile(DocumentsRoot + @"\vacío.txt", []);
        source.AddFile(DocumentsRoot + @"\ñandú.txt", "ñ"u8.ToArray());

        var result = await RunAsync(storage, new ManualTimeProvider(), source, WithExclusions(["**/año/**", "*.tmp"], [DefaultSources[0]]));

        Assert.Empty(result.Warnings);
        Assert.Equal(["Documentos/vacío.txt", "Documentos/ñandú.txt"], await PathsAsync(storage, result));
    }

    // ================================================================== ADR-16 con cualquier lista de patrones

    public static TheoryData<string> PatternListsThatTryToInterfere => new()
    {
        "none", "defaults", "**", "*", "**/Backbone/**", "ProgramData/Backbone/**", "C/ProgramData/**", "*.bin", "credentials", "Backbone*",
    };

    [Theory]
    [MemberData(nameof(PatternListsThatTryToInterfere))]
    [Trait("Requirement", "ADR-16")]
    public async Task HU08_2_The_mandatory_exclusion_holds_with_any_pattern_list_whatever_the_case_on_disk(string kind)
    {
        IReadOnlyList<string> patterns = kind switch
        {
            "none" => [],
            "defaults" => JobDefaults.Exclusions,
            _ => [kind],
        };
        var storage = new InMemoryRepositoryStorage();
        var source = new InMemorySourceFileSystem();
        source.AddFile(SystemRoot + @"\Users\ana\doc.md", "documento"u8.ToArray());
        source.AddFile(SystemRoot + @"\ProgramData\BACKBONE\credentials\0123abcd.bin", "CLAVE-CENTINELA"u8.ToArray());
        source.AddFile(SystemRoot + @"\ProgramData\BACKBONE\nube.bin", "nube"u8.ToArray(), attributes: SourceAttributes.RecallOnDataAccess);
        source.AddFile(SystemRoot + @"\ProgramData\BACKBONE\enlace\x.txt", "x"u8.ToArray());
        source.MakeReparsePoint(SystemRoot + @"\ProgramData\BACKBONE\enlace");
        var enumerated = new List<string>();
        source.OnEnumerate = enumerated.Add;

        var result = await RunAsync(storage, new ManualTimeProvider(), source, WithExclusions(patterns, [SystemSource()]));

        // Ni se recorre, ni avisa (ni de nube ni de enlaces), ni se abre, ni llega al repositorio.
        Assert.Equal(RunState.Completed, result.State);
        Assert.Empty(result.Warnings);
        Assert.DoesNotContain(enumerated, p => p.Contains("Backbone", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(source.Opened, p => p.Contains("Backbone", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("CLAVE-CENTINELA", string.Join('\n', await PathsAsync(storage, result)), StringComparison.Ordinal);
        Assert.DoesNotContain(await PathsAsync(storage, result), p => p.Contains("Backbone", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(Sha256("CLAVE-CENTINELA"u8.ToArray()), string.Join('\n', ObjectPaths(storage)), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Requirement", "ADR-16")]
    public async Task HU08_2_A_previous_snapshot_that_contained_the_data_folder_is_not_reintroduced_when_a_folder_is_unreadable_and_patterns_apply()
    {
        var storage = new InMemoryRepositoryStorage();
        var time = new ManualTimeProvider();
        var source = new InMemorySourceFileSystem();
        source.AddFile(SystemRoot + @"\ProgramData\Backbone\credentials\k.bin", "antiguo"u8.ToArray());
        source.AddFile(SystemRoot + @"\ProgramData\Otro\a.txt", "otro"u8.ToArray());
        source.AddFile(SystemRoot + @"\ProgramData\Otro\b.tmp", "tmp"u8.ToArray());
        var unprotected = new BackupSource("C", @"C:\", SystemRoot);
        await RunAsync(storage, time, source, WithExclusions([], [unprotected])); // copia anterior a la exclusión (HU-07)
        time.Advance(TimeSpan.FromDays(1));
        source.FailEnumerate(SystemRoot + @"\ProgramData", new UnauthorizedAccessException("denegado"));

        var result = await RunAsync(storage, time, source, WithExclusions(["*.tmp"], [SystemSource()]));

        Assert.Equal(nameof(RunIssueCode.AccessDenied), Assert.Single(result.Warnings).Code);
        var paths = await PathsAsync(storage, result);
        Assert.Contains("C/ProgramData/Otro/a.txt", paths);
        Assert.DoesNotContain(paths, p => p.Contains("Backbone", StringComparison.Ordinal) || p.EndsWith(".tmp", StringComparison.Ordinal));
    }

    // ================================================================== Criterio 3: sustituciones por junctions

    [Fact]
    [Trait("Requirement", "RF-06")]
    [Trait("Requirement", "ADR-18")]
    public async Task HU08_3_A_nested_folder_replaced_by_a_junction_is_skipped_and_its_siblings_and_ancestors_are_kept()
    {
        var storage = new InMemoryRepositoryStorage();
        var inner = new InMemorySourceFileSystem();
        inner.AddFile(DocumentsRoot + @"\A\arriba.txt", "arriba"u8.ToArray());
        inner.AddFile(DocumentsRoot + @"\A\B\secreto.txt", "SECRETO"u8.ToArray());
        inner.AddFile(DocumentsRoot + @"\A\C\ok.txt", "ok"u8.ToArray());
        var source = new ReparseSwitchingSource(inner);
        source.EnumerateBecomesLink.Add(DocumentsRoot + @"\A\B");

        var result = await RunAsync(storage, new ManualTimeProvider(), source, WithExclusions(JobDefaults.Exclusions, [DefaultSources[0]]));

        var warning = Assert.Single(result.Warnings);
        Assert.Equal(("Documentos/A/B", nameof(RunIssueCode.ReparsePointSkipped)), (warning.Path, warning.Code));
        Assert.Equal(["Documentos/A/C/ok.txt", "Documentos/A/arriba.txt"], await PathsAsync(storage, result));
        Assert.DoesNotContain(inner.Opened, p => p.Contains(@"\B\", StringComparison.Ordinal));
        Assert.DoesNotContain(Sha256("SECRETO"u8.ToArray()), string.Join('\n', ObjectPaths(storage)), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Requirement", "RF-06")]
    [Trait("Requirement", "ADR-18")]
    public async Task HU08_3_An_intermediate_folder_swapped_for_a_junction_to_the_data_folder_leaks_nothing_with_patterns_in_force()
    {
        var storage = new InMemoryRepositoryStorage();
        var inner = new InMemorySourceFileSystem();
        inner.AddFile(DocumentsRoot + @"\A\B\x.txt", "normal"u8.ToArray());
        inner.AddFile(@"C:\ProgramData\Backbone\B\x.txt", "CLAVE-CENTINELA"u8.ToArray());
        inner.AddFile(DocumentsRoot + @"\z.txt", "z"u8.ToArray());
        var source = new FinalPathCheckingSource(inner, [DefaultSources[0]]);
        source.SwapAfterListing(DocumentsRoot + @"\A", @"C:\ProgramData\Backbone");

        var result = await RunAsync(storage, new ManualTimeProvider(), source, WithExclusions(["*.tmp"], [DefaultSources[0]]));

        Assert.Equal(nameof(RunIssueCode.ReparsePointSkipped), Assert.Single(result.Warnings).Code);
        Assert.DoesNotContain(inner.Opened, p => p.StartsWith(@"C:\ProgramData", StringComparison.Ordinal));
        Assert.DoesNotContain(Sha256("CLAVE-CENTINELA"u8.ToArray()), string.Join('\n', ObjectPaths(storage)), StringComparison.Ordinal);
        Assert.Contains("Documentos/z.txt", await PathsAsync(storage, result));
        await RepositoryInvariants.AssertConsistentAsync(storage);
    }

    [Fact]
    [Trait("Requirement", "RF-06")]
    public async Task HU08_3_A_junction_replacing_an_ancestor_keeps_the_previous_version_out_of_the_new_snapshot()
    {
        // Un enlace no conserva nada del padre (a diferencia de una carpeta ilegible): el contenido anterior no se arrastra.
        var storage = new InMemoryRepositoryStorage();
        var time = new ManualTimeProvider();
        var inner = new InMemorySourceFileSystem();
        inner.AddFile(DocumentsRoot + @"\Carpeta\dentro.txt", "dentro"u8.ToArray());
        inner.AddFile(DocumentsRoot + @"\fuera.txt", "fuera"u8.ToArray());
        await RunAsync(storage, time, inner, Request([DefaultSources[0]]));
        time.Advance(TimeSpan.FromDays(1));
        var source = new ReparseSwitchingSource(inner);
        source.EnumerateBecomesLink.Add(DocumentsRoot + @"\Carpeta");

        var result = await RunAsync(storage, time, source, Request([DefaultSources[0]]));

        Assert.Equal(["Documentos/fuera.txt"], await PathsAsync(storage, result));
        Assert.Equal(nameof(RunIssueCode.ReparsePointSkipped), Assert.Single(result.Warnings).Code);
    }

    // ================================================================== Criterio 4: el motor decide solo por atributos

    public static TheoryData<string, int, bool> AttributeDecisions => new()
    {
        // (ruta relativa, atributos, ¿se omite como nube?)  El nombre de la carpeta no influye.
        { @"Datos\x.bin", (int)SourceAttributes.RecallOnDataAccess, true },
        { @"OneDrive\x.bin", (int)SourceAttributes.RecallOnOpen, true },
        { @"OneDrive - Empresa\x.bin", (int)FileAttributes.Offline, true },
        { @"Datos\x.bin", (int)(SourceAttributes.RecallOnDataAccess | FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReadOnly), true },
        { @"OneDrive\x.bin", (int)FileAttributes.Archive, false },
        { @"OneDrive\x.bin", (int)FileAttributes.Normal, false },
        { @"OneDrive\x.bin", (int)(FileAttributes.Archive | (FileAttributes)0x00080000), false }, // PINNED sin recall: contenido local
        { @"OneDrive\x.bin", (int)(FileAttributes.Archive | (FileAttributes)0x00100000), false }, // UNPINNED sin recall
        { @"OneDrive\x.bin", (int)(FileAttributes.Archive | FileAttributes.SparseFile | FileAttributes.Compressed), false },
    };

    [Theory]
    [MemberData(nameof(AttributeDecisions))]
    [Trait("Requirement", "R4")]
    public async Task HU08_4_The_engine_decides_only_by_the_attributes_the_source_gives_it(string relative, int attributes, bool skipped)
    {
        var storage = new InMemoryRepositoryStorage();
        var source = new InMemorySourceFileSystem();
        source.AddFile(DocumentsRoot + @"\" + relative, [1, 2, 3], attributes: (FileAttributes)attributes);
        source.AddFile(DocumentsRoot + @"\control.txt", "c"u8.ToArray());

        var result = await RunAsync(storage, new ManualTimeProvider(), source, Request([DefaultSources[0]]));

        var expected = "Documentos/" + relative.Replace('\\', '/');
        if (skipped)
        {
            Assert.Equal((expected, nameof(RunIssueCode.CloudOnlySkipped)), (Assert.Single(result.Warnings).Path, Assert.Single(result.Warnings).Code));
            Assert.DoesNotContain(DocumentsRoot + @"\" + relative, source.Opened);
            Assert.DoesNotContain(await PathsAsync(storage, result), p => p == expected);
        }
        else
        {
            Assert.Empty(result.Warnings);
            Assert.Contains(expected, await PathsAsync(storage, result));
        }
    }

    [Fact]
    [Trait("Requirement", "R4")]
    public async Task HU08_4_A_reparse_point_without_cloud_bits_is_a_link_and_one_with_cloud_bits_is_cloud_only()
    {
        // La precedencia (nube antes que enlace) es la del motor sobre los atributos recibidos; el servicio decide qué ve cada uno.
        var storage = new InMemoryRepositoryStorage();
        var source = new InMemorySourceFileSystem();
        source.AddFile(DocumentsRoot + @"\enlace.txt", "e"u8.ToArray(), attributes: FileAttributes.Archive | FileAttributes.ReparsePoint);
        source.AddFile(DocumentsRoot + @"\placeholder.txt", "p"u8.ToArray(), attributes: FileAttributes.Archive | FileAttributes.ReparsePoint | SourceAttributes.RecallOnDataAccess);

        var result = await RunAsync(storage, new ManualTimeProvider(), source, Request([DefaultSources[0]]));

        Assert.Equal(
            [("Documentos/enlace.txt", nameof(RunIssueCode.ReparsePointSkipped)), ("Documentos/placeholder.txt", nameof(RunIssueCode.CloudOnlySkipped))],
            result.Warnings.Select(w => (w.Path, w.Code)).Order());
        Assert.Empty(source.Opened);
        Assert.Equal(["Documentos/"], await PathsAsync(storage, result)); // solo la carpeta vacía del origen
    }

    [Fact]
    [Trait("Requirement", "R4")]
    public async Task HU08_4_An_empty_cloud_only_file_is_skipped_and_an_empty_local_file_is_copied()
    {
        var storage = new InMemoryRepositoryStorage();
        var source = new InMemorySourceFileSystem();
        source.AddFile(DocumentsRoot + @"\vacio-nube.txt", [], attributes: SourceAttributes.RecallOnOpen);
        source.AddFile(DocumentsRoot + @"\vacio.txt", []);

        var result = await RunAsync(storage, new ManualTimeProvider(), source, Request([DefaultSources[0]]));

        Assert.Equal(("Documentos/vacio-nube.txt", nameof(RunIssueCode.CloudOnlySkipped)), (Assert.Single(result.Warnings).Path, Assert.Single(result.Warnings).Code));
        Assert.Equal(["Documentos/vacio.txt"], await PathsAsync(storage, result));
    }

    [Fact]
    [Trait("Requirement", "R4")]
    public async Task HU08_4_A_cloud_only_file_that_a_new_pattern_excludes_is_neither_reported_nor_kept_from_the_previous_snapshot()
    {
        var storage = new InMemoryRepositoryStorage();
        var time = new ManualTimeProvider();
        var source = new InMemorySourceFileSystem();
        source.AddFile(DocumentsRoot + @"\informe.docx", "versión local"u8.ToArray());
        await RunAsync(storage, time, source, Request([DefaultSources[0]]));
        time.Advance(TimeSpan.FromDays(1));
        source.SetAttributes(DocumentsRoot + @"\informe.docx", FileAttributes.Archive | SourceAttributes.RecallOnDataAccess);
        source.ResetCounters();

        var result = await RunAsync(storage, time, source, WithExclusions(["*.docx"], [DefaultSources[0]]));

        Assert.Empty(result.Warnings);
        Assert.Equal(["Documentos/"], await PathsAsync(storage, result)); // solo la carpeta vacía; el archivo anterior no se arrastra
        Assert.Equal(0, source.OpenCount);
    }

    [Fact]
    [Trait("Requirement", "R4")]
    public async Task HU08_4_The_cloud_check_runs_before_any_open_when_the_source_would_fail_on_open()
    {
        // Si el motor abriera el archivo, esta implementación fallaría con otro error; el aviso debe ser el de la nube.
        var storage = new InMemoryRepositoryStorage();
        var source = new InMemorySourceFileSystem();
        source.AddFile(DocumentsRoot + @"\nube.bin", [1], attributes: SourceAttributes.RecallOnDataAccess);
        source.FailOpen(DocumentsRoot + @"\nube.bin", new IOException("descarga lanzada"));

        var result = await RunAsync(storage, new ManualTimeProvider(), source, Request([DefaultSources[0]]));

        Assert.Equal(nameof(RunIssueCode.CloudOnlySkipped), Assert.Single(result.Warnings).Code);
    }

    // ================================================================== Criterio 5: vista previa en los bordes

    [Fact]
    public async Task HU08_5_The_preview_counts_empty_files_ignores_case_and_counts_cloud_only_files_without_opening_them()
    {
        var source = new InMemorySourceFileSystem();
        source.AddFile(DocumentsRoot + @"\vacio.TMP", []);
        source.AddFile(DocumentsRoot + @"\Año\a.txt", new byte[9]);
        source.AddFile(DocumentsRoot + @"\nube.docx", new byte[1234], attributes: SourceAttributes.RecallOnDataAccess);

        var result = await new ExclusionPreview().ComputeAsync(source, [DefaultSources[0]], ["*.tmp", "**/AÑO/**", "*.DOCX", "*.docx"], Ct);

        Assert.Equal(
            [
                new ExclusionPatternCount("*.tmp", 1, 0),
                new ExclusionPatternCount("**/AÑO/**", 1, 9),
                new ExclusionPatternCount("*.DOCX", 1, 1234),
                new ExclusionPatternCount("*.docx", 1, 1234),
            ],
            result.Patterns);
        Assert.Equal((3L, 1243L), (result.ExcludedFiles, result.ExcludedBytes));
        Assert.Equal(0, result.IncludedFiles);
        Assert.Equal(0, source.OpenCount);
    }

    [Fact]
    [Trait("Requirement", "RF-06")]
    public async Task HU08_5_The_preview_does_not_follow_a_link_inside_an_excluded_folder()
    {
        var source = new InMemorySourceFileSystem();
        source.AddFile(DocumentsRoot + @"\node_modules\a.js", new byte[5]);
        source.AddFile(DocumentsRoot + @"\node_modules\enlace\fuera.js", new byte[500]);
        source.MakeReparsePoint(DocumentsRoot + @"\node_modules\enlace");

        var result = await new ExclusionPreview().ComputeAsync(source, [DefaultSources[0]], ["**/node_modules/**"], Ct);

        Assert.Equal(new ExclusionPatternCount("**/node_modules/**", 1, 5), Assert.Single(result.Patterns));
    }

    [Fact]
    public async Task HU08_5_Cancelling_in_the_middle_of_the_walk_stops_the_preview()
    {
        var source = new InMemorySourceFileSystem();
        for (var i = 0; i < 20; i++)
        {
            source.AddFile(DocumentsRoot + $@"\d{i}\a.tmp", new byte[1]);
        }

        using var cts = new CancellationTokenSource();
        var enumerations = 0;
        source.OnEnumerate = _ =>
        {
            if (++enumerations == 3)
            {
                cts.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ExclusionPreview().ComputeAsync(source, [DefaultSources[0]], ["*.tmp"], cts.Token));
        Assert.True(enumerations < 20);
    }

    [Fact]
    [Trait("Requirement", "ADR-16")]
    public async Task HU08_5_The_preview_never_counts_the_data_folder_whatever_the_patterns()
    {
        var source = new InMemorySourceFileSystem();
        source.AddFile(SystemRoot + @"\ProgramData\BACKBONE\credentials\k.bin", new byte[77]);
        source.AddFile(SystemRoot + @"\a.bin", new byte[3]);

        var result = await new ExclusionPreview().ComputeAsync(source, [SystemSource()], ["*.bin", "**"], Ct);

        Assert.Equal(1, result.Patterns[0].Files);
        Assert.Equal(3, result.Patterns[0].Bytes);
        Assert.Equal((1L, 3L), (result.ExcludedFiles, result.ExcludedBytes));
    }
}
