using System.Globalization;
using System.Text;

namespace Backbone.Contracts.Tests;

/// <summary>
/// <see cref="UntrustedText.Describe"/> (§6.1 "Contenido no confiable", ADR-11): mismas pruebas que las de
/// <c>Untrusted.Describe</c> de Core (<c>UntrustedRepositoryTests</c>, <c>Describe_*</c> y <c>N4_*</c>) más una batería
/// aleatoria que comprueba que la salida nunca lleva caracteres peligrosos.
/// </summary>
[Trait("Story", "HU-04")]
[Trait("Requirement", "ADR-11")]
public sealed class UntrustedTextTests
{
    /// <summary>Cf del plano básico (bidi, anchura cero, BOM, guion blando) y separadores de línea y párrafo.</summary>
    private static readonly char[] SampleSpecials =
        [(char)0x200B, (char)0x200E, (char)0x202E, (char)0x2028, (char)0x2029, (char)0x2066, (char)0xFEFF, (char)0x00AD];

    [Fact]
    public void Describe_handles_null_and_keeps_ordinary_text()
    {
        Assert.Equal("(null)", UntrustedText.Describe(null));
        Assert.Equal("", UntrustedText.Describe(""));
        Assert.Equal("Documentos/año ñ 😀.txt", UntrustedText.Describe("Documentos/año ñ 😀.txt"));
    }

    [Fact]
    public void Describe_truncates_to_200_characters_with_an_ellipsis()
    {
        Assert.Equal(200, UntrustedText.MaxLength);
        Assert.Equal(new string('a', 200), UntrustedText.Describe(new string('a', 200)));
        Assert.Equal(new string('a', 200) + "…", UntrustedText.Describe(new string('a', 201)));
        Assert.Equal(new string('a', 200) + "…", UntrustedText.Describe(new string('a', 1_000_000)));
    }

    [Fact]
    public void Describe_counts_code_points_not_utf16_units_when_truncating()
    {
        var emoji = char.ConvertFromUtf32(0x1F600);

        Assert.Equal(string.Concat(Enumerable.Repeat(emoji, 200)), UntrustedText.Describe(string.Concat(Enumerable.Repeat(emoji, 200))));
        Assert.Equal(
            string.Concat(Enumerable.Repeat(emoji, 200)) + "…",
            UntrustedText.Describe(string.Concat(Enumerable.Repeat(emoji, 201))));
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

        var described = UntrustedText.Describe(text);

        Assert.Equal(
            "a" + Esc("000a") + "b" + Esc("000d") + Esc("0000") + Esc("007f") + Esc("d800") + "c" + Esc("dc00")
                + Esc("2028") + Esc("2029") + Esc("202a") + Esc("202e") + Esc("2066") + Esc("2069") + Esc("200f"),
            described);
        Assert.DoesNotContain(described, c => char.IsControl(c) || char.IsSurrogate(c));
    }

    [Fact]
    public void Describe_escapes_a_trailing_lone_high_surrogate()
    {
        Assert.Equal("a" + Esc("d83d"), UntrustedText.Describe("a" + ((char)0xD83D).ToString()));
    }

    [Fact]
    public void Describe_counts_escaped_characters_against_the_limit()
    {
        var described = UntrustedText.Describe(new string((char)1, 500));

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

        var described = UntrustedText.Describe("a" + cf + "b");

        var expected = expectedUnits.Length == 4 ? Esc(expectedUnits) : Esc(expectedUnits[..4]) + Esc(expectedUnits[4..]);
        Assert.Equal("a" + expected + "b", described);
        Assert.DoesNotContain(cf, described, StringComparison.Ordinal);
    }

    [Fact]
    public void N4_Describe_keeps_non_format_characters_outside_the_bmp()
    {
        // U+1F600 (So) y U+20000 (Lo) no son Cf: se conservan como par de suplentes.
        Assert.Equal("😀𠀀", UntrustedText.Describe("😀𠀀"));
    }

    [Fact]
    public void N4_An_escaped_supplementary_format_character_counts_as_one_character()
    {
        var described = UntrustedText.Describe(string.Concat(Enumerable.Repeat(char.ConvertFromUtf32(0xE0001), 300)));

        Assert.Equal((200 * 12) + 1, described.Length);
        Assert.EndsWith("…", described, StringComparison.Ordinal);
    }

    [Fact]
    public void Random_input_never_leaves_controls_format_characters_surrogates_or_line_separators()
    {
        var random = new Random(20261115);
        for (var iteration = 0; iteration < 2_000; iteration++)
        {
            var units = new char[random.Next(0, 400)];
            for (var k = 0; k < units.Length; k++)
            {
                // Mezcla de ASCII, controles, Cf del plano básico y unidades suplentes (emparejadas o no).
                units[k] = random.Next(4) switch
                {
                    0 => (char)random.Next(0x00, 0x80),
                    1 => (char)random.Next(0xD800, 0xE000),
                    2 => SampleSpecials[random.Next(SampleSpecials.Length)],
                    _ => (char)random.Next(0x80, 0x10000),
                };
            }

            var input = new string(units).Replace('…', 'x');
            var described = UntrustedText.Describe(input);

            foreach (var rune in EnumerateRunes(described))
            {
                Assert.NotEqual(UnicodeCategory.Control, Rune.GetUnicodeCategory(rune));
                Assert.NotEqual(UnicodeCategory.Format, Rune.GetUnicodeCategory(rune));
                Assert.False(rune.Value is 0x2028 or 0x2029);
            }

            // Se añade "…" exactamente cuando la entrada tiene más de 200 puntos de código (una suplente suelta cuenta como uno).
            Assert.Equal(CountCodePoints(input) > UntrustedText.MaxLength, described.EndsWith('…'));
        }
    }

    private static IEnumerable<Rune> EnumerateRunes(string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            // La salida nunca lleva suplentes sueltas: si las llevara, DecodeFromUtf16 no daría Done.
            Assert.Equal(System.Buffers.OperationStatus.Done, Rune.DecodeFromUtf16(text.AsSpan(i), out var rune, out var consumed));
            yield return rune;
            i += consumed;
        }
    }

    private static int CountCodePoints(string text)
    {
        var count = 0;
        var i = 0;
        while (i < text.Length)
        {
            Rune.DecodeFromUtf16(text.AsSpan(i), out _, out var consumed);
            i += Math.Max(consumed, 1);
            count++;
        }

        return count;
    }

    private static string Esc(string hex) => "\\" + "u" + hex;
}
