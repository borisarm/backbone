using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Backbone.Contracts;
using Backbone.Core.Engine;
using Backbone.Core.Storage;
using Backbone.Core.Tests.Support;
using static Backbone.Core.Tests.Support.EngineHarness;

namespace Backbone.Core.Tests.Engine;

/// <summary>
/// Hallazgos de la revisión de seguridad del motor de HU-08: memoria de la vista previa (M-1), coste acotado de la lista
/// de patrones (B-1) y normalización NFC de patrones y nombres (B-2).
/// </summary>
[Trait("Story", "HU-08")]
[Trait("Requirement", "RF-04")]
public sealed class ExclusionSecurityReviewTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ================================================================== M-1: la vista previa no retiene lo recorrido

    [Fact]
    [Trait("Requirement", "RNF-05")]
    public async Task HU08_M1_The_preview_releases_the_folders_it_has_already_walked()
    {
        var source = new InMemorySourceFileSystem();
        for (var i = 0; i < 6; i++)
        {
            for (var j = 0; j < 4; j++)
            {
                source.AddFile(DocumentsRoot + $@"\d{i}\e{j}\f{j}\a.txt", new byte[1]);
            }
        }

        var popped = new List<WeakReference>();
        var maxAlive = 0;
        var preview = new ExclusionPreview
        {
            FramePopped = frame =>
            {
                // Las carpetas sacadas antes que esta ya no están en la pila: nada del recorrido debería retenerlas.
                Collect();
                maxAlive = Math.Max(maxAlive, popped.Count(w => w.IsAlive));
                popped.Add(new WeakReference(frame));
            },
        };

        var result = await preview.ComputeAsync(source, DefaultSources, ["*.tmp", "**/e1/**"], Ct);

        Assert.Equal((6L, 18L), (result.Patterns[1].Files, result.IncludedFiles));
        Assert.Equal(1 + 6 + 24 + 24, popped.Count); // Fotos no existe: no se recorre
        // Antes de la corrección, una carpeta retenía todas las ya recorridas por debajo (aquí, hasta 28 a la vez). Se
        // tolera una: en Debug el JIT puede mantener viva en una variable temporal la última carpeta apilada.
        Assert.True(maxAlive <= 1, $"Hay {maxAlive} carpetas ya recorridas retenidas a la vez");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    // ================================================================== B-1: coste acotado

    /// <summary>Patrón adversario de la revisión: segmentos <c>*aaa…ab*</c> separados por <c>**</c>, hasta 512 caracteres.</summary>
    private static string Adversarial(int chunkLength)
    {
        var segment = "*" + new string('a', chunkLength - 1) + "b*";
        var parts = new List<string>();
        var length = 0;
        while (length + segment.Length + 4 <= ContractLimits.MaxExclusionPatternLength)
        {
            parts.Add("**");
            parts.Add(segment);
            length += segment.Length + 4;
        }

        return string.Join('/', parts);
    }

    [Fact]
    [Trait("Requirement", "§7.5")]
    public void HU08_B1_The_cost_of_a_segment_and_of_a_pattern_follow_the_documented_rule()
    {
        // Sin * intermedio: 1 + prefijo + sufijo. Con texto entre dos *: + 255 por cada 64 caracteres del trozo más largo.
        Assert.Equal(1 + 1 + 4, ExclusionPattern.Parse("*.tmp").Cost); // ** inicial (1) + segmento ".TMP" (1 + 4)
        Assert.Equal(1 + 1 + 255, ExclusionPattern.Parse("*factura*").Cost);
        Assert.Equal(1 + 1 + 255, ExclusionPattern.Parse("*a*b*c*").Cost); // el trozo más largo manda
        Assert.Equal(1 + 1 + (2 * 255), ExclusionPattern.Parse("*" + new string('x', 65) + "*").Cost);
        Assert.Equal(1 + 1 + 12, ExclusionPattern.Parse("**/node_modules/**").Cost); // ** + segmento; el /** final no cuenta
        Assert.True(JobDefaults.Exclusions.Sum(p => ExclusionPattern.Parse(p).Cost) < 100);
        Assert.Equal(256 * 514, ExclusionMatcher.MaxListCost);
        Assert.Equal(514, ExclusionPattern.Parse(new string('?', ContractLimits.MaxExclusionPatternLength)).Cost); // el más caro sin *…*
    }

    [Fact]
    [Trait("Requirement", "§7.5")]
    public void HU08_B1_A_list_above_the_cost_budget_is_rejected_as_too_complex_and_realistic_lists_are_accepted()
    {
        // El ejemplo de la revisión, 256 veces: unas 460 000 unidades de coste.
        var adversarial = Enumerable.Repeat(Adversarial(64), ContractLimits.MaxExclusions).ToList();
        Assert.Equal(ExclusionPatternError.TooComplex, ExclusionMatcher.ValidateList(adversarial, out var index));
        Assert.Equal(-1, index);
        var ex = Assert.Throws<ArgumentException>(() => ExclusionMatcher.Create(adversarial));
        Assert.Contains(nameof(ExclusionPatternError.TooComplex), ex.Message, StringComparison.Ordinal);

        // 256 patrones del tipo *texto* caben, y también el máximo de patrones sencillos.
        var contains = Enumerable.Range(0, ContractLimits.MaxExclusions).Select(i => $"*factura{i}*").ToList();
        Assert.Equal(ExclusionPatternError.None, ExclusionMatcher.ValidateList(contains, out index));
        Assert.Equal(-1, index);
        Assert.Equal(ContractLimits.MaxExclusions, ExclusionMatcher.Create(contains).Patterns.Count);
        // Una lista sin texto entre dos * se admite siempre, aunque sean 256 patrones de la longitud máxima.
        string[] longest =
        [
            new string('?', ContractLimits.MaxExclusionPatternLength),
            "*" + new string('x', ContractLimits.MaxExclusionPatternLength - 1),
            new string('x', ContractLimits.MaxExclusionPatternLength - 1) + "*",
            new string('x', ContractLimits.MaxExclusionPatternLength - 3) + "/**",
            string.Join('/', Enumerable.Repeat("x", (ContractLimits.MaxExclusionPatternLength / 2) - 1)) + "/**",
            string.Join('/', Enumerable.Repeat("**/x", ContractLimits.MaxExclusionPatternLength / 5)),
        ];
        foreach (var pattern in longest)
        {
            var list = Enumerable.Repeat(pattern, ContractLimits.MaxExclusions).ToList();
            Assert.Equal(ExclusionPatternError.None, ExclusionMatcher.ValidateList(list, out _));
        }

        // Los demás motivos siguen llegando por el mismo mecanismo, con el índice del patrón.
        Assert.Equal(ExclusionPatternError.EmptySegment, ExclusionMatcher.ValidateList(["*.tmp", "a//b"], out index));
        Assert.Equal(1, index);
        Assert.Equal(ExclusionPatternError.TooMany, ExclusionMatcher.ValidateList([.. Enumerable.Repeat("*.tmp", ContractLimits.MaxExclusions + 1)], out index));
        Assert.Equal(-1, index);
        Assert.Equal(ExclusionPatternError.None, ExclusionMatcher.ValidateList([], out index));
    }

    [Fact]
    [Trait("Requirement", "§7.5")]
    public async Task HU08_B1_The_preview_and_the_backup_reject_a_list_that_is_too_complex()
    {
        var adversarial = Enumerable.Repeat(Adversarial(64), ContractLimits.MaxExclusions).ToList();
        var source = new InMemorySourceFileSystem();
        source.AddFile(DocumentsRoot + @"\a.txt", new byte[1]);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => new ExclusionPreview().ComputeAsync(source, DefaultSources, adversarial, Ct));
        Assert.Contains(nameof(ExclusionPatternError.TooComplex), ex.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentException>(() => RunAsync(
            new InMemoryRepositoryStorage(), new ManualTimeProvider(), source, Request() with { Exclusions = adversarial }));
    }

    [Fact]
    [Trait("Requirement", "§7.5")]
    public void HU08_B1_The_most_expensive_accepted_list_stays_within_a_time_limit_on_adversarial_folders()
    {
        // La lista más cara que se admite con el patrón de la revisión, sobre carpetas "aaa…a" anidadas: todos los
        // segmentos de todos los patrones están activos en cada nivel y ninguno termina de casar (casi coinciden).
        var pattern = Adversarial(64);
        var cost = ExclusionPattern.Parse(pattern).Cost;
        var count = (int)(ExclusionMatcher.MaxListCost / cost);
        var patterns = Enumerable.Repeat(pattern, count).ToList();
        Assert.Equal(ExclusionPatternError.None, ExclusionMatcher.ValidateList(patterns, out _));
        Assert.Equal(ExclusionPatternError.TooComplex, ExclusionMatcher.ValidateList([.. patterns, pattern], out _));
        var matcher = ExclusionMatcher.Create(patterns);
        var name = new string('a', 255);
        var current = matcher.RootState("Documentos");
        var next = new ulong[matcher.StateWords];

        var watch = Stopwatch.StartNew();
        for (var depth = 0; depth < 2_000; depth++)
        {
            Assert.Equal(-1, matcher.Step(current, name, isDirectory: true, next));
            (current, next) = (next, current);
        }

        watch.Stop();
        // Cota: ~131 000 pasos por elemento, unos 0,1-0,5 ms; 2 000 elementos con mucho margen para CI.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"Demasiado lento: {watch.Elapsed}");
        Assert.Equal(-1, matcher.Step(current, new string('a', 63) + "b", isDirectory: true, next)); // casa un segmento de siete
    }

    // ================================================================== B-2: NFC

    private const string CafeNfc = "Caf\u00E9";
    private const string CafeNfd = "Cafe\u0301";

    public static TheoryData<string, string, bool> NfcCases => new()
    {
        { "Documentos/" + CafeNfc + "/**", "Documentos/" + CafeNfd + "/x.txt", true },
        { "Documentos/" + CafeNfd + "/**", "Documentos/" + CafeNfc + "/x.txt", true },
        { "**/" + CafeNfc + "/**", "Documentos/a/" + CafeNfd + "/", true },
        { "*" + "\u00E9" + "*", "Documentos/" + CafeNfd + ".txt", true },
        { "caf?", "Documentos/" + CafeNfd, true }, // ? es un carácter: la e acentuada compuesta cuenta como uno
        { "CAF\u00C9.TXT", "Documentos/cafe\u0301.txt", true }, // y sin distinguir mayúsculas
        { "Documentos/Cafe/**", "Documentos/" + CafeNfd + "/x.txt", false },
        { "Documentos/" + CafeNfc + "/**", "Documentos/Cafe/x.txt", false },
        { "\uAC00*", "Documentos/\u1100\u1161.txt", true }, // hangul: NFD (jamo) frente a la sílaba precompuesta
    };

    [Theory]
    [MemberData(nameof(NfcCases))]
    public void HU08_B2_Patterns_and_names_are_compared_in_NFC(string pattern, string path, bool excluded)
    {
        Assert.Equal(excluded, ExclusionMatcher.Create([pattern]).IsExcluded(path));
    }

    [Fact]
    public void HU08_B2_Names_that_are_not_valid_UTF16_are_compared_without_normalizing()
    {
        var matcher = ExclusionMatcher.Create(["*.tmp", "caf\u00E9*"]);

        Assert.True(matcher.IsExcluded("Documentos/a\uD800\u0301.tmp"));
        Assert.False(matcher.IsExcluded("Documentos/a\uD800\u0301.txt"));
        Assert.False(matcher.IsExcluded("Documentos/cafe\u0301\uDC00")); // sin normalizar, NFD no casa con NFC
        Assert.Equal("abc", ExclusionMatcher.ToNfc("abc").ToString());
        Assert.Equal(CafeNfc, ExclusionMatcher.ToNfc(CafeNfd).ToString());
        Assert.Equal("a\uD800\u0301", ExclusionMatcher.ToNfc("a\uD800\u0301").ToString());
        Assert.True(CafeNfd.Normalize(NormalizationForm.FormC) == CafeNfc);
    }

    [Fact]
    public async Task HU08_B2_A_folder_written_in_NFD_is_excluded_by_the_backup_and_counted_by_the_preview()
    {
        var source = new InMemorySourceFileSystem();
        source.AddFile(DocumentsRoot + @"\" + CafeNfd + @"\menu.txt", new byte[5]);
        source.AddFile(DocumentsRoot + @"\otro.txt", new byte[7]);
        string[] patterns = ["Documentos/" + CafeNfc + "/**"];

        var preview = await new ExclusionPreview().ComputeAsync(source, DefaultSources, patterns, Ct);
        Assert.Equal(new ExclusionPatternCount(patterns[0], 1, 5), preview.Patterns[0]);

        var storage = new InMemoryRepositoryStorage();
        var result = await RunAsync(storage, new ManualTimeProvider(), source, Request() with { Exclusions = patterns });
        var paths = (await ReadEntriesAsync(storage, result.Snapshot.Id)).Select(e => e.Path).ToList();
        Assert.Contains("Documentos/otro.txt", paths);
        Assert.DoesNotContain(paths, p => p.Contains("menu.txt", StringComparison.Ordinal));
        Assert.DoesNotContain(paths, p => p.Contains(CafeNfd, StringComparison.Ordinal));
    }
}
