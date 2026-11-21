using System.Diagnostics;
using Backbone.Contracts;
using Backbone.Core.Engine;

namespace Backbone.Core.Tests.Engine;

/// <summary>
/// HU-08: sintaxis y coincidencia de los patrones de exclusión (RF-04), con tablas de casos sin distinguir mayúsculas.
/// Las rutas son las del snapshot (alias primero); una <c>/</c> final indica carpeta.
/// </summary>
[Trait("Story", "HU-08")]
[Trait("Requirement", "RF-04")]
public sealed class ExclusionPatternTests
{
    /// <summary>Criterio 2: los tres patrones de RF-04 excluyen exactamente lo esperado.</summary>
    public static TheoryData<string, string, bool> Criterion2Cases => new()
    {
        // *.tmp: cualquier nombre que termine en .tmp, a cualquier profundidad (archivo o carpeta).
        { "*.tmp", "Documentos/a.tmp", true },
        { "*.tmp", "Documentos/sub/dir/B.TMP", true },
        { "*.tmp", "Documentos/informe.Tmp", true },
        { "*.tmp", "Documentos/.tmp", true },
        { "*.tmp", "Documentos/cache.tmp/", true },
        { "*.tmp", "Documentos/cache.tmp/dentro.txt", true },
        { "*.tmp", "Documentos/a.tmp.txt", false },
        { "*.tmp", "Documentos/atmp", false },
        { "*.tmp", "Documentos/a.tm", false },
        { "*.tmp", "Documentos/tmp/", false },
        { "*.tmp", "Documentos/sub/a.tmpx", false },

        // **/node_modules/**: la carpeta node_modules a cualquier profundidad, con todo su contenido.
        { "**/node_modules/**", "Documentos/node_modules/", true },
        { "**/node_modules/**", "Documentos/node_modules/a.js", true },
        { "**/node_modules/**", "Documentos/web/app/node_modules/lib/x/y.js", true },
        { "**/node_modules/**", "Documentos/Node_Modules/a.js", true },
        { "**/node_modules/**", "Fotos/NODE_MODULES/", true },
        { "**/node_modules/**", "Documentos/node_modules", false }, // un archivo que se llama así
        { "**/node_modules/**", "Documentos/node_modules2/a.js", false },
        { "**/node_modules/**", "Documentos/my_node_modules/a.js", false },
        { "**/node_modules/**", "Documentos/node_modules.txt", false },
        { "**/node_modules/**", "Documentos/web/a.js", false },

        // Documentos/Privado/**: anclado a la raíz del snapshot (alias Documentos).
        { "Documentos/Privado/**", "Documentos/Privado/", true },
        { "Documentos/Privado/**", "Documentos/Privado/a.txt", true },
        { "Documentos/Privado/**", "documentos/privado/Sub/b.txt", true },
        { "Documentos/Privado/**", "DOCUMENTOS/PRIVADO/", true },
        { "Documentos/Privado/**", "Documentos/Privado", false }, // archivo
        { "Documentos/Privado/**", "Documentos/PrivadoX/a.txt", false },
        { "Documentos/Privado/**", "Documentos/Otro/Privado/a.txt", false },
        { "Documentos/Privado/**", "Fotos/Privado/a.txt", false },
        { "Documentos/Privado/**", "Documentos/Público/a.txt", false },
    };

    /// <summary>Exclusiones por defecto (criterio 1) y otros detalles de la sintaxis.</summary>
    public static TheoryData<string, string, bool> SyntaxCases => new()
    {
        { "desktop.ini", "Documentos/desktop.ini", true },
        { "desktop.ini", "Documentos/a/b/Desktop.INI", true },
        { "desktop.ini", "Documentos/desktop.ini.bak", false },
        { "thumbs.db", "Fotos/2026/Thumbs.db", true },
        { "~$*", "Documentos/~$informe.docx", true },
        { "~$*", "Documentos/~$", true },
        { "~$*", "Documentos/~informe.docx", false },
        { "~$*", "Documentos/a~$b.docx", false },
        { "$RECYCLE.BIN", "C/$Recycle.Bin/", true },
        { "$RECYCLE.BIN", "C/$Recycle.Bin/S-1-5-21/a.txt", true },
        { "System Volume Information", "C/System Volume Information/", true },
        { "System Volume Information", "C/System Volume Information/tracking.log", true },
        { "System Volume Information", "C/System Volume", false },

        // ? es un carácter; * no cruza '/'; ** en medio son cero o más carpetas.
        { "a?.txt", "Documentos/ab.txt", true },
        { "a?.txt", "Documentos/a.txt", false },
        { "a?.txt", "Documentos/abc.txt", false },
        { "Documentos/*.txt", "Documentos/a.txt", true },
        { "Documentos/*.txt", "Documentos/sub/a.txt", false },
        { "Documentos/**/*.log", "Documentos/a.log", true },
        { "Documentos/**/*.log", "Documentos/x/y/z/a.LOG", true },
        { "Documentos/**/*.log", "Fotos/a.log", false },
        { "**/cache", "Documentos/cache", true },
        { "**/cache", "Documentos/x/cache/", true },
        { "*a*b*c*", "Documentos/xxaxxbxxcxx", true },
        { "*a*b*c*", "Documentos/cba", false },
        { "ab*ba", "Documentos/aba", false }, // el prefijo y el sufijo no se solapan
        { "ab*ba", "Documentos/abba", true },
        { "a**b", "Documentos/axyb", true }, // ** dentro de un segmento = *
        { "[1].jpg", "Fotos/[1].jpg", true }, // [ ] son literales
        { "[1].jpg", "Fotos/1.jpg", false },
        { @"Documentos\Privado\**", "Documentos/Privado/a.txt", true }, // \ como separador
        { "build/", "Documentos/src/build/", true }, // / final: solo carpetas
        { "build/", "Documentos/src/build", false },
        { "**", "Documentos/a.txt", true },
        { "año*", "Fotos/AÑO 2026/", true }, // mayúsculas fuera de ASCII

        // La raíz de un origen (el alias) nunca se excluye.
        { "Documentos", "Documentos/a.txt", false },
        { "Documentos/**", "Documentos/a.txt", false },
        { "*", "Documentos/", false },
    };

    [Theory]
    [MemberData(nameof(Criterion2Cases))]
    public void HU08_2_Patterns_of_RF04_exclude_exactly_what_is_expected_ignoring_case(string pattern, string path, bool excluded)
    {
        var matcher = ExclusionMatcher.Create([pattern]);

        Assert.Equal(excluded, matcher.IsExcluded(path));
    }

    [Theory]
    [MemberData(nameof(SyntaxCases))]
    public void HU08_2_Pattern_syntax_cases(string pattern, string path, bool excluded)
    {
        var matcher = ExclusionMatcher.Create([pattern]);

        Assert.Equal(excluded, matcher.IsExcluded(path));
    }

    [Fact]
    public void HU08_2_The_first_matching_pattern_is_reported_and_an_empty_list_excludes_nothing()
    {
        var matcher = ExclusionMatcher.Create(["*.log", "**/node_modules/**", "*.tmp"]);

        Assert.Equal(1, matcher.FirstMatch("Documentos/node_modules/a.tmp"));
        Assert.Equal(2, matcher.FirstMatch("Documentos/a.tmp"));
        Assert.Equal(-1, matcher.FirstMatch("Documentos/a.txt"));
        Assert.Equal(-1, matcher.FirstMatch("Documentos"));
        Assert.True(ExclusionMatcher.Create([]).IsEmpty);
        Assert.False(ExclusionMatcher.Create([]).IsExcluded("Documentos/a.tmp"));
    }

    public static TheoryData<string?, ExclusionPatternError> InvalidPatterns => new()
    {
        { null, ExclusionPatternError.Empty },
        { "", ExclusionPatternError.Empty },
        { "   ", ExclusionPatternError.Empty },
        { " *.tmp", ExclusionPatternError.SurroundingWhitespace },
        { "*.tmp ", ExclusionPatternError.SurroundingWhitespace },
        { "/Documentos/**", ExclusionPatternError.Rooted },
        { @"\\servidor\recurso", ExclusionPatternError.Rooted },
        { @"C:\Users\**", ExclusionPatternError.InvalidCharacter },
        { "a|b", ExclusionPatternError.InvalidCharacter },
        { "a<b", ExclusionPatternError.InvalidCharacter },
        { "a\"b", ExclusionPatternError.InvalidCharacter },
        { "a\u0001b", ExclusionPatternError.InvalidCharacter },
        { "a//b", ExclusionPatternError.EmptySegment },
        { "a//", ExclusionPatternError.EmptySegment },
        { "Documentos/../Fotos", ExclusionPatternError.RelativeSegment },
        { "./a", ExclusionPatternError.RelativeSegment },
        { new string('a', ContractLimits.MaxExclusionPatternLength + 1), ExclusionPatternError.TooLong },
    };

    [Theory]
    [MemberData(nameof(InvalidPatterns))]
    [Trait("Requirement", "§7.5")]
    public void HU08_2_Invalid_patterns_are_rejected_with_their_reason(string? pattern, ExclusionPatternError expected)
    {
        Assert.Equal(expected, ExclusionPattern.Validate(pattern));
        Assert.False(ExclusionPattern.TryParse(pattern, out _, out var error));
        Assert.Equal(expected, error);
        Assert.Throws<ArgumentException>(() => ExclusionPattern.Parse(pattern));
        Assert.Throws<ArgumentException>(() => ExclusionMatcher.Create([pattern!]));
    }

    [Fact]
    [Trait("Requirement", "§7.5")]
    public void HU08_2_Valid_limits_are_accepted_and_more_than_256_patterns_are_rejected()
    {
        Assert.Equal(ExclusionPatternError.None, ExclusionPattern.Validate(new string('a', ContractLimits.MaxExclusionPatternLength)));
        Assert.Equal(ExclusionPatternError.None, ExclusionPattern.Validate("😀*.tmp")); // par suplente válido

        // Suplentes sueltas (fuera de TheoryData, cuya serialización las sustituye por U+FFFD).
        Assert.Equal(ExclusionPatternError.InvalidCharacter, ExclusionPattern.Validate("a\uD800b"));
        Assert.Equal(ExclusionPatternError.InvalidCharacter, ExclusionPattern.Validate("a\uDC00"));
        Assert.Equal(ExclusionPatternError.InvalidCharacter, ExclusionPattern.Validate("a\uD800"));
        Assert.Equal(ContractLimits.MaxExclusions, ExclusionMatcher.Create([.. Enumerable.Repeat("*.tmp", ContractLimits.MaxExclusions)]).Patterns.Count);
        var ex = Assert.Throws<ArgumentException>(() => ExclusionMatcher.Create([.. Enumerable.Repeat("*.tmp", ContractLimits.MaxExclusions + 1)]));
        Assert.Contains(nameof(ExclusionPatternError.TooMany), ex.Message, StringComparison.Ordinal);
        Assert.Equal("*.tmp", ExclusionPattern.Parse("*.tmp").ToString());
        Assert.True(ExclusionPattern.Parse("a/**").DirectoryOnly);
        Assert.False(ExclusionPattern.Parse("a/**/b").DirectoryOnly);
    }

    [Fact]
    [Trait("Requirement", "§7.5")]
    public void HU08_2_Pathological_patterns_are_evaluated_without_backtracking()
    {
        // Con retroceso, *a*a*…*b contra "aaaa…" es exponencial; aquí cada segmento es O(n·m) y los ** un autómata.
        var star = string.Concat(Enumerable.Repeat("*a", 120)) + "*b";
        var globstars = string.Join('/', Enumerable.Repeat("**/a*", 40)) + "/b";
        var matcher = ExclusionMatcher.Create([star, globstars]);
        var longName = new string('a', 255);
        var deepPath = "Documentos/" + string.Join('/', Enumerable.Repeat(new string('a', 50), 400));

        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 20; i++)
        {
            Assert.False(matcher.IsExcluded("Documentos/" + longName));
            Assert.False(matcher.IsExcluded(deepPath));
        }

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"Demasiado lento: {watch.Elapsed}");
        Assert.True(matcher.IsExcluded("Documentos/" + longName + "b"));
    }

    [Fact]
    [Trait("Category", "Perf")]
    [Trait("Requirement", "RNF-03")]
    public void HU08_Perf_256_patterns_over_a_million_names_stay_within_a_few_seconds()
    {
        // Lista grande y realista (256 patrones, la mitad con **) sobre 1 000 000 de nombres: coste por elemento constante.
        // No es el peor caso: el coste de la lista (ExclusionPattern.Cost) es pequeño. El peor caso admitido, una lista
        // cerca de ExclusionMatcher.MaxListCost con segmentos *…* activos en cada nivel, está en
        // ExclusionSecurityReviewTests.HU08_B1_The_most_expensive_accepted_list_stays_within_a_time_limit_on_adversarial_folders.
        var patterns = Enumerable.Range(0, 256).Select(i => i % 2 == 0 ? $"*.ext{i}" : $"**/carpeta{i}/**").ToList();
        var matcher = ExclusionMatcher.Create(patterns);
        var parent = matcher.RootState("Documentos");
        var child = new ulong[matcher.StateWords];
        var excluded = 0;

        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 1_000_000; i++)
        {
            if (matcher.Step(parent, $"archivo número {i}.ext{i % 300}", isDirectory: false, child) >= 0)
            {
                excluded++;
            }
        }

        watch.Stop();
        Assert.Equal(426_674, excluded); // 3 333 ciclos de 300 × 128 extensiones + 50 en el resto
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"Demasiado lento: {watch.Elapsed}");
    }

    [Fact]
    [Trait("Requirement", "§7.5")]
    public void HU08_2_Patterns_with_more_than_64_segments_use_several_state_words()
    {
        var segments = Enumerable.Range(0, 100).Select(i => $"d{i}").ToList();
        var pattern = "Documentos/" + string.Join('/', segments) + "/**";
        var matcher = ExclusionMatcher.Create([pattern, "*.tmp"]);

        Assert.True(matcher.IsExcluded("Documentos/" + string.Join('/', segments) + "/"));
        Assert.True(matcher.IsExcluded("Documentos/" + string.Join('/', segments) + "/x.txt"));
        Assert.False(matcher.IsExcluded("Documentos/" + string.Join('/', segments.Take(99)) + "/x.txt"));
        Assert.True(matcher.IsExcluded("Documentos/" + string.Join('/', segments.Take(99)) + "/x.tmp"));
    }

    /// <summary>
    /// Comparación con una implementación de referencia, recursiva y sencilla (exponencial, solo para rutas cortas), con
    /// patrones y rutas aleatorios de semilla fija.
    /// </summary>
    [Fact]
    public void HU08_2_Random_patterns_agree_with_a_reference_implementation()
    {
        var random = new Random(808);
        string[] alphabet = ["a", "b", "A", "ñ", "Ñ", "."];
        string RandomName(bool wildcards)
        {
            var length = random.Next(1, 5);
            var chars = new List<string>();
            for (var i = 0; i < length; i++)
            {
                chars.Add(wildcards && random.Next(4) == 0 ? (random.Next(2) == 0 ? "*" : "?") : alphabet[random.Next(alphabet.Length)]);
            }

            var name = string.Concat(chars);
            return name is "." or ".." ? "a" : name;
        }

        for (var round = 0; round < 3000; round++)
        {
            var patternSegments = Enumerable.Range(0, random.Next(1, 4)).Select(_ => random.Next(5) == 0 ? "**" : RandomName(wildcards: true)).ToList();
            var pattern = string.Join('/', patternSegments);
            var pathSegments = Enumerable.Range(0, random.Next(1, 5)).Select(_ => RandomName(wildcards: false)).ToList();
            var isDirectory = random.Next(2) == 0;
            var path = "R/" + string.Join('/', pathSegments) + (isDirectory ? "/" : string.Empty);

            var expected = ReferenceExcluded(pattern, ["R", .. pathSegments], isDirectory);

            Assert.True(expected == ExclusionMatcher.Create([pattern]).IsExcluded(path), $"{pattern} contra {path}: se esperaba {expected}");
        }
    }

    private static bool ReferenceExcluded(string pattern, IReadOnlyList<string> path, bool isDirectory)
    {
        var segments = pattern.Split('/').ToList();
        var directoryOnly = false;
        if (segments.Count > 1 && segments[^1] == "**")
        {
            directoryOnly = true;
            segments.RemoveAt(segments.Count - 1);
        }

        if (pattern.Split('/').Length == 1)
        {
            segments.Insert(0, "**");
        }

        // Excluida si alguna carpeta (o el propio elemento), sin contar el alias, casa entera.
        for (var end = 2; end <= path.Count; end++)
        {
            var elementIsDirectory = end < path.Count || isDirectory;
            if ((elementIsDirectory || !directoryOnly) && MatchSegments(segments, 0, path.Take(end).ToList(), 0))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchSegments(List<string> pattern, int p, List<string> path, int s)
    {
        if (p == pattern.Count)
        {
            return s == path.Count;
        }

        if (pattern[p] == "**")
        {
            return MatchSegments(pattern, p + 1, path, s) || (s < path.Count && MatchSegments(pattern, p, path, s + 1));
        }

        return s < path.Count && MatchName(pattern[p].Replace("**", "*", StringComparison.Ordinal).ToUpperInvariant(), 0, path[s].ToUpperInvariant(), 0)
            && MatchSegments(pattern, p + 1, path, s + 1);
    }

    private static bool MatchName(string pattern, int p, string name, int n)
    {
        if (p == pattern.Length)
        {
            return n == name.Length;
        }

        return pattern[p] switch
        {
            '*' => MatchName(pattern, p + 1, name, n) || (n < name.Length && MatchName(pattern, p, name, n + 1)),
            '?' => n < name.Length && MatchName(pattern, p + 1, name, n + 1),
            _ => n < name.Length && pattern[p] == name[n] && MatchName(pattern, p + 1, name, n + 1),
        };
    }
}
