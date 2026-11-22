using Backbone.Contracts;
using Backbone.Core.Engine;

namespace Backbone.Core.Tests.Engine;

/// <summary>
/// HU-08, QA de la semántica de §4.1 "Patrones de exclusión" que las tablas anteriores no fijaban: unidad UTF-16 de `?`,
/// `**` dentro de un segmento, cero carpetas, caracteres que en otros formatos son especiales y aquí son literales,
/// índice del primer patrón inválido y los límites del presupuesto de coste.
/// </summary>
[Trait("Story", "HU-08")]
[Trait("Requirement", "RF-04")]
public sealed class ExclusionSemanticsQaTests
{
    public static TheoryData<string, string, bool> Cases => new()
    {
        // ? es una unidad UTF-16 (tras NFC): un emoji (par suplente) son dos.
        { "a?", "Documentos/a\U0001F600", false },
        { "a??", "Documentos/a\U0001F600", true },
        { "a?", "Documentos/aé", true },
        { "a?", "Documentos/aé", true }, // NFD: se normaliza antes de contar
        { "a??", "Documentos/aé", false },

        // ** dentro de un segmento equivale a *: no cruza '/'.
        { "**.tmp", "Documentos/x.tmp", true },
        { "**.tmp", "Documentos/sub/x.tmp", true }, // sin '/': nombre a cualquier profundidad
        { "Documentos/**.tmp", "Documentos/x.tmp", true },
        { "Documentos/**.tmp", "Documentos/sub/x.tmp", false },
        { "a****b", "Documentos/axb", true },
        { "a****b", "Documentos/ab", true },
        { "a****b", "Documentos/a/b", false },

        // ** completo: cero o más carpetas.
        { "Documentos/**/x.txt", "Documentos/x.txt", true },
        { "Documentos/**/x.txt", "Documentos/a/b/x.txt", true },
        { "Documentos/**/x.txt", "Documentos/a/b/x.txt/", true }, // con '/': archivo o carpeta
        { "**/**/x", "Documentos/x", true },
        { "**/**/x", "Documentos/a/b/x", true },
        { "a/**/b", "a/b", true },
        { "a/**/b", "a/c/b", true },
        { "a/**/b", "a/c/d/b", true },
        { "a/**/b", "a/c/d", false },

        // Los caracteres especiales de otros formatos son literales: sin negación, sin escape, sin llaves.
        { "!*.tmp", "Documentos/!a.tmp", true },
        { "!*.tmp", "Documentos/a.tmp", false },
        { "#nota", "Documentos/#nota", true },
        { "a{b,c}", "Documentos/a{b,c}", true },
        { "a{b,c}", "Documentos/ab", false },
        { "a+b(1)^", "Documentos/a+b(1)^", true },
        { "a[b]c", "Documentos/a[b]c", true },
        { "a[b]c", "Documentos/abc", false },
        { "a[!b]c", "Documentos/a[!b]c", true },
        { "a\\*", "Documentos/a/x", false }, // \ es separador, no escape: patrón "a/*", anclado al alias
        { "a\\*", "a/x", true },
        { "Documentos\\*.log", "Documentos/a.log", true },

        // Un nombre vacío de archivo y la raíz.
        { "*", "Documentos/a", true },
        { "?", "Documentos/a", true },
        { "?", "Documentos/ab", false },
        { "Documentos/*", "Documentos/", false }, // la raíz del origen nunca se excluye
        { "**", "Documentos/", false },

        // Carpetas: X/ y X/** solo carpetas; el patrón sin barra, archivos y carpetas.
        { "Documentos/Privado/", "Documentos/Privado/", true },
        { "Documentos/Privado/", "Documentos/Privado", false },
        { "Documentos/Privado/", "Documentos/Privado/a.txt", true },
        { "Privado", "Documentos/Privado/a.txt", true }, // una carpeta con ese nombre a cualquier profundidad
        { "Privado", "Documentos/Privado", true }, // y un archivo
        { "Documentos/Privado", "Documentos/Privado", true }, // con '/': anclado, archivo o carpeta
        { "Documentos/Privado", "Documentos/Privado/a.txt", true },
        { "Documentos/Privado", "Documentos/x/Privado", false },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void HU08_2_Semantics_of_the_specification_hold(string pattern, string path, bool excluded) =>
        Assert.Equal(excluded, ExclusionMatcher.Create([pattern]).IsExcluded(path));

    [Fact]
    public void HU08_2_Duplicate_patterns_are_accepted_and_the_first_one_is_reported()
    {
        var matcher = ExclusionMatcher.Create(["*.tmp", "*.tmp", "*.TMP"]);

        Assert.Equal(0, matcher.FirstMatch("Documentos/a.tmp"));
        Assert.Equal(3, matcher.Patterns.Count);
    }

    [Fact]
    public void HU08_2_The_first_invalid_pattern_is_reported_by_index_and_the_empty_list_is_valid()
    {
        Assert.Equal(ExclusionPatternError.None, ExclusionMatcher.ValidateList([], out _));
        Assert.Equal(ExclusionPatternError.Empty, ExclusionMatcher.ValidateList(["*.tmp", " ", "a|b"], out var index));
        Assert.Equal(1, index);
        Assert.Equal(ExclusionPatternError.InvalidCharacter, ExclusionMatcher.ValidateList(["*.tmp", "ok", "a|b", "c//d"], out index));
        Assert.Equal(2, index);
        Assert.Equal(ExclusionPatternError.Empty, ExclusionMatcher.ValidateList([null], out index));
        Assert.Equal(0, index);
        Assert.Equal(ExclusionPatternError.TooMany, ExclusionMatcher.ValidateList([.. Enumerable.Repeat("*.tmp", ContractLimits.MaxExclusions + 1)], out _));
    }

    [Fact]
    public void HU08_2_A_trailing_dot_is_literal_but_trailing_whitespace_is_rejected_and_the_extremes_are_accepted()
    {
        Assert.Equal(ExclusionPatternError.None, ExclusionPattern.Validate("a."));
        Assert.Equal(ExclusionPatternError.SurroundingWhitespace, ExclusionPattern.Validate("a. "));
        Assert.Equal(ExclusionPatternError.SurroundingWhitespace, ExclusionPattern.Validate("\ta"));
        Assert.Equal(ExclusionPatternError.None, ExclusionPattern.Validate("a b"));
        Assert.Equal(ExclusionPatternError.None, ExclusionPattern.Validate("a b"));
        Assert.True(ExclusionMatcher.Create(["a."]).IsExcluded("Documentos/a."));
        Assert.False(ExclusionMatcher.Create(["a."]).IsExcluded("Documentos/a"));
    }

    [Fact]
    public void HU08_B1_256_patterns_of_the_maximum_length_without_text_between_stars_fit_the_budget()
    {
        // Spec: "una lista sin texto entre dos * en ningún patrón se admite siempre".
        var pattern = new string('a', 255) + "*" + new string('b', 256); // 512 caracteres
        Assert.Equal(ContractLimits.MaxExclusionPatternLength, pattern.Length);

        var error = ExclusionMatcher.ValidateList([.. Enumerable.Repeat(pattern, ContractLimits.MaxExclusions)], out var index);

        Assert.Equal(ExclusionPatternError.None, error);
        Assert.Equal(-1, index);
        Assert.Equal(ContractLimits.MaxExclusions * (ContractLimits.MaxExclusionPatternLength + 2), ExclusionMatcher.MaxListCost);
    }

    [Fact]
    public void HU08_B1_The_list_error_for_too_complex_has_no_index_and_a_single_pattern_never_exceeds_the_budget_alone()
    {
        var expensive = "*" + new string('a', 254) + "*" + new string('b', 254) + "*"; // dos trozos intermedios largos
        Assert.True(ExclusionPattern.Parse(expensive).Cost < ExclusionMatcher.MaxListCost);

        var many = Enumerable.Repeat(expensive, ContractLimits.MaxExclusions).ToList();
        var error = ExclusionMatcher.ValidateList(many, out var index);

        Assert.Equal(ExclusionPatternError.TooComplex, error);
        Assert.Equal(-1, index);
    }
}
