using System.Diagnostics.CodeAnalysis;
using Backbone.Contracts;
using Backbone.Core.Repository;

namespace Backbone.Core.Engine;

/// <summary>
/// Patrón <i>glob</i> de exclusión de un trabajo (RF-04, HU-08), ya validado y compilado.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sintaxis</b> (se compara sin distinguir mayúsculas, ordinal, contra las rutas del snapshot, que empiezan por el alias
/// del origen, §7.5 M-3):
/// </para>
/// <list type="bullet">
/// <item><description>Separador <c>/</c>; también se admite <c>\</c>, que se trata igual (ningún nombre de Windows lo contiene).</description></item>
/// <item><description><c>*</c>: cualquier secuencia de caracteres dentro de un segmento (también vacía); no cruza <c>/</c>.</description></item>
/// <item><description><c>?</c>: exactamente un carácter (unidad UTF-16) dentro de un segmento.</description></item>
/// <item><description><c>**</c> como segmento completo: cero o más segmentos. Dentro de un segmento (<c>a**b</c>) equivale a <c>*</c>.</description></item>
/// <item><description>
/// Un patrón <b>sin</b> <c>/</c> (<c>*.tmp</c>, <c>desktop.ini</c>, <c>$RECYCLE.BIN</c>) se compara con el <b>nombre</b> de cada
/// elemento, a cualquier profundidad (equivale a <c>**/patrón</c>).
/// </description></item>
/// <item><description>
/// Un patrón <b>con</b> <c>/</c> se compara con la ruta completa desde la raíz del snapshot, así que empieza por el alias del
/// origen o por <c>**</c> (<c>Documentos/Privado/**</c>, <c>**/node_modules/**</c>).
/// </description></item>
/// <item><description>
/// Un patrón que termina en <c>/</c> o en <c>/**</c> solo coincide con <b>carpetas</b>: <c>X/**</c> excluye la carpeta
/// <c>X</c> con todo su contenido (no se recorre), pero no un archivo que se llame <c>X</c>.
/// </description></item>
/// <item><description>
/// Una carpeta excluida se excluye con todo lo que tiene debajo, y la raíz de un origen (el alias) nunca se excluye.
/// <c>[</c> y <c>]</c> son literales (no hay clases de caracteres) y no hay negación ni escape.
/// </description></item>
/// </list>
/// <para>
/// <b>Coste</b> (§7.5 M-2): no se traduce a una expresión regular. Cada segmento se compara con un algoritmo voraz por
/// trozos entre <c>*</c>, sin retroceso, O(n·m) en el peor caso (n = longitud del nombre, m = del patrón); los <c>**</c>
/// se evalúan como un autómata de estados (<see cref="ExclusionMatcher"/>), un segmento cada vez.
/// </para>
/// </remarks>
public sealed class ExclusionPattern
{
    private ExclusionPattern(string text, SegmentPattern?[] tokens, bool directoryOnly)
    {
        Text = text;
        Tokens = tokens;
        DirectoryOnly = directoryOnly;
    }

    /// <summary>Texto del patrón tal como lo escribió el usuario.</summary>
    public string Text { get; }

    /// <summary>Si solo coincide con carpetas (termina en <c>/</c> o en <c>/**</c>).</summary>
    public bool DirectoryOnly { get; }

    /// <summary>Segmentos compilados; <see langword="null"/> es <c>**</c> (cero o más segmentos).</summary>
    internal SegmentPattern?[] Tokens { get; }

    /// <summary>Valida y compila un patrón.</summary>
    /// <param name="text">Texto del patrón.</param>
    /// <param name="pattern">Patrón compilado, si es válido.</param>
    /// <param name="error">Motivo si no es válido; <see cref="ExclusionPatternError.None"/> si lo es.</param>
    /// <returns>Si el patrón es válido.</returns>
    public static bool TryParse(string? text, [NotNullWhen(true)] out ExclusionPattern? pattern, out ExclusionPatternError error)
    {
        pattern = null;
        error = Check(text);
        if (error != ExclusionPatternError.None || text is null)
        {
            return false;
        }

        var normalized = text.Replace('\\', '/');
        var directoryOnly = false;
        if (normalized.EndsWith('/'))
        {
            directoryOnly = true;
            normalized = normalized[..^1];
        }

        var segments = normalized.Split('/');
        var anchored = segments.Length > 1;
        if (anchored && segments[^1] == "**")
        {
            // X/** = la carpeta X con todo su contenido (y nada si X es un archivo).
            directoryOnly = true;
            segments = segments[..^1];
        }

        var tokens = new List<SegmentPattern?>(segments.Length + 1);
        if (!anchored)
        {
            tokens.Add(null);
        }

        foreach (var segment in segments)
        {
            if (segment == "**")
            {
                // Dos ** seguidos equivalen a uno.
                if (tokens.Count == 0 || tokens[^1] is not null)
                {
                    tokens.Add(null);
                }
            }
            else
            {
                tokens.Add(SegmentPattern.Compile(segment));
            }
        }

        pattern = new ExclusionPattern(text, [.. tokens], directoryOnly);
        return true;
    }

    /// <summary>Valida un patrón sin compilarlo.</summary>
    /// <param name="text">Texto del patrón.</param>
    /// <returns><see cref="ExclusionPatternError.None"/> si es válido; si no, el motivo.</returns>
    public static ExclusionPatternError Validate(string? text) => Check(text);

    /// <summary>Compila un patrón válido.</summary>
    /// <exception cref="ArgumentException">El patrón no es válido.</exception>
    public static ExclusionPattern Parse(string? text) =>
        TryParse(text, out var pattern, out var error)
            ? pattern
            : throw new ArgumentException($"Patrón de exclusión no válido ({error}): {Untrusted.Describe(text)}", nameof(text));

    /// <inheritdoc/>
    public override string ToString() => Text;

    private static ExclusionPatternError Check(string? text)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrWhiteSpace(text))
        {
            return ExclusionPatternError.Empty;
        }

        if (text.Length > ContractLimits.MaxExclusionPatternLength)
        {
            return ExclusionPatternError.TooLong;
        }

        if (char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(text[^1]))
        {
            return ExclusionPatternError.SurroundingWhitespace;
        }

        if (text[0] is '/' or '\\')
        {
            return ExclusionPatternError.Rooted;
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
                continue;
            }

            if (char.IsSurrogate(c) || char.IsControl(c) || c is ':' or '"' or '<' or '>' or '|')
            {
                return ExclusionPatternError.InvalidCharacter;
            }
        }

        var normalized = text.Replace('\\', '/');
        if (normalized.EndsWith('/'))
        {
            normalized = normalized[..^1];
        }

        foreach (var segment in normalized.Split('/'))
        {
            if (segment.Length == 0)
            {
                return ExclusionPatternError.EmptySegment;
            }

            if (segment is "." or "..")
            {
                return ExclusionPatternError.RelativeSegment;
            }
        }

        return ExclusionPatternError.None;
    }
}

/// <summary>Motivo por el que un patrón de exclusión no es válido (el servicio lo traduce a <c>ExclusionPatternInvalid</c>).</summary>
public enum ExclusionPatternError
{
    /// <summary>Válido.</summary>
    None = 0,

    /// <summary>Vacío o solo espacios.</summary>
    Empty = 1,

    /// <summary>Más de <see cref="ContractLimits.MaxExclusionPatternLength"/> caracteres.</summary>
    TooLong = 2,

    /// <summary>Empieza o termina con espacios (que ningún nombre de Windows puede tener al final y que no se ven).</summary>
    SurroundingWhitespace = 3,

    /// <summary>Empieza por <c>/</c> o <c>\</c> (ruta absoluta o UNC).</summary>
    Rooted = 4,

    /// <summary>Contiene <c>: " &lt; &gt; |</c>, caracteres de control o suplentes sueltas, que ningún nombre puede tener.</summary>
    InvalidCharacter = 5,

    /// <summary>Tiene un segmento vacío (<c>a//b</c>, o <c>//</c> al final).</summary>
    EmptySegment = 6,

    /// <summary>Tiene un segmento <c>.</c> o <c>..</c>.</summary>
    RelativeSegment = 7,

    /// <summary>La lista tiene más de <see cref="ContractLimits.MaxExclusions"/> patrones.</summary>
    TooMany = 8,
}

/// <summary>
/// Un segmento de un patrón (sin <c>/</c>), con <c>*</c> y <c>?</c>. Se compara con un algoritmo voraz por trozos: el trozo
/// anterior al primer <c>*</c> se ancla al principio, el posterior al último al final, y cada trozo intermedio se busca en
/// su posición más a la izquierda posible (lo que siempre es correcto con <c>*</c>), sin retroceso.
/// </summary>
internal sealed class SegmentPattern
{
    private readonly string prefix;
    private readonly string suffix;
    private readonly string[] middles;
    private readonly bool hasStar;
    private readonly int minLength;

    private SegmentPattern(string prefix, string suffix, string[] middles, bool hasStar)
    {
        this.prefix = prefix;
        this.suffix = suffix;
        this.middles = middles;
        this.hasStar = hasStar;
        minLength = prefix.Length + suffix.Length + middles.Sum(m => m.Length);
    }

    public static SegmentPattern Compile(string segment)
    {
        var upper = string.Create(segment.Length, segment, static (span, s) =>
        {
            for (var i = 0; i < s.Length; i++)
            {
                span[i] = char.ToUpperInvariant(s[i]);
            }
        });
        var parts = upper.Split('*');
        if (parts.Length == 1)
        {
            return new SegmentPattern(parts[0], string.Empty, [], hasStar: false);
        }

        return new SegmentPattern(parts[0], parts[^1], [.. parts[1..^1].Where(p => p.Length > 0)], hasStar: true);
    }

    public bool Matches(ReadOnlySpan<char> name)
    {
        if (!hasStar)
        {
            return name.Length == prefix.Length && MatchesAt(name, 0, prefix);
        }

        if (name.Length < minLength || !MatchesAt(name, 0, prefix))
        {
            return false;
        }

        var end = name.Length - suffix.Length;
        if (!MatchesAt(name, end, suffix))
        {
            return false;
        }

        var position = prefix.Length;
        foreach (var middle in middles)
        {
            var found = -1;
            for (var i = position; i + middle.Length <= end; i++)
            {
                if (MatchesAt(name, i, middle))
                {
                    found = i;
                    break;
                }
            }

            if (found < 0)
            {
                return false;
            }

            position = found + middle.Length;
        }

        return true;
    }

    /// <summary>El trozo (ya en mayúsculas, con <c>?</c> como comodín) coincide en esa posición.</summary>
    private static bool MatchesAt(ReadOnlySpan<char> name, int offset, string chunk)
    {
        for (var j = 0; j < chunk.Length; j++)
        {
            var c = chunk[j];
            if (c != '?' && char.ToUpperInvariant(name[offset + j]) != c)
            {
                return false;
            }
        }

        return true;
    }
}
