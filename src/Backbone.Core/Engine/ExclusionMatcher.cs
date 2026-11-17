using System.Numerics;
using Backbone.Contracts;
using Backbone.Core.Repository;

namespace Backbone.Core.Engine;

/// <summary>
/// Lista de patrones de exclusión de un trabajo (RF-04, HU-08) compilada para evaluarla durante el recorrido, un segmento
/// cada vez.
/// </summary>
/// <remarks>
/// <para>
/// Cada patrón es un autómata cuyos estados son las posiciones de sus segmentos (el estado <c>i</c> significa "falta por
/// casar desde el segmento <c>i</c>"; el último, que ha casado entero). El estado de una carpeta es el conjunto de estados
/// de todos los patrones tras consumir los segmentos de su ruta, guardado como bits en un <see cref="ulong"/>[]
/// (<see cref="StateWords"/> palabras). El de un hijo se calcula a partir del de su carpeta y su nombre
/// (<see cref="Step"/>), así que el coste por elemento no depende de la profundidad ni de la longitud de la ruta.
/// </para>
/// <para>
/// No conoce las exclusiones obligatorias (ADR-16): esas las aplica el motor aparte (<see cref="MandatoryExclusionSet"/>),
/// antes y sin depender de esta lista, que el usuario puede vaciar.
/// </para>
/// </remarks>
public sealed class ExclusionMatcher
{
    private readonly ExclusionPattern[] patterns;
    private readonly int[] offsets;
    private readonly int[] words;
    private readonly ulong[] initial;

    private ExclusionMatcher(ExclusionPattern[] patterns)
    {
        this.patterns = patterns;
        offsets = new int[patterns.Length];
        words = new int[patterns.Length];
        var total = 0;
        for (var p = 0; p < patterns.Length; p++)
        {
            offsets[p] = total;
            words[p] = (patterns[p].Tokens.Length / 64) + 1; // estados 0..n
            total += words[p];
        }

        StateWords = total;
        initial = new ulong[total];
        for (var p = 0; p < patterns.Length; p++)
        {
            var bits = initial.AsSpan(offsets[p], words[p]);
            Set(bits, 0);
            Close(patterns[p].Tokens, bits);
        }
    }

    /// <summary>Lista vacía: no excluye nada.</summary>
    public static ExclusionMatcher Empty { get; } = new([]);

    /// <summary>Patrones, en el orden de la lista.</summary>
    public IReadOnlyList<ExclusionPattern> Patterns => patterns;

    /// <summary>Si no hay ningún patrón.</summary>
    public bool IsEmpty => patterns.Length == 0;

    /// <summary>Palabras de 64 bits del estado de una carpeta.</summary>
    internal int StateWords { get; }

    /// <summary>Compila una lista de patrones.</summary>
    /// <param name="patterns">Patrones del trabajo, como mucho <see cref="ContractLimits.MaxExclusions"/>.</param>
    /// <returns>La lista compilada.</returns>
    /// <exception cref="ArgumentException">
    /// Hay más de <see cref="ContractLimits.MaxExclusions"/> patrones o alguno no es válido (<see cref="ExclusionPattern.Validate"/>).
    /// </exception>
    public static ExclusionMatcher Create(IReadOnlyList<string> patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        if (patterns.Count == 0)
        {
            return Empty;
        }

        if (patterns.Count > ContractLimits.MaxExclusions)
        {
            throw new ArgumentException(
                $"Hay {patterns.Count} patrones de exclusión; el máximo es {ContractLimits.MaxExclusions} ({ExclusionPatternError.TooMany}).",
                nameof(patterns));
        }

        var compiled = new ExclusionPattern[patterns.Count];
        for (var i = 0; i < patterns.Count; i++)
        {
            if (!ExclusionPattern.TryParse(patterns[i], out var pattern, out var error))
            {
                throw new ArgumentException(
                    $"El patrón de exclusión {i} no es válido ({error}): {Untrusted.Describe(patterns[i])}",
                    nameof(patterns));
            }

            compiled[i] = pattern;
        }

        return new ExclusionMatcher(compiled);
    }

    /// <summary>Estado de la raíz de un origen (tras consumir su alias). El alias nunca se excluye.</summary>
    internal ulong[] RootState(string alias)
    {
        var state = new ulong[StateWords];
        if (StateWords > 0)
        {
            _ = Step(initial, alias, isDirectory: true, state);
        }

        return state;
    }

    /// <summary>
    /// Calcula en <paramref name="child"/> el estado de un elemento de la carpeta cuyo estado es <paramref name="parent"/>.
    /// </summary>
    /// <returns>Índice del primer patrón que excluye el elemento, o -1 si ninguno.</returns>
    internal int Step(ReadOnlySpan<ulong> parent, ReadOnlySpan<char> name, bool isDirectory, Span<ulong> child)
    {
        var first = -1;
        for (var p = 0; p < patterns.Length; p++)
        {
            var from = parent.Slice(offsets[p], words[p]);
            var to = child.Slice(offsets[p], words[p]);
            to.Clear();
            var tokens = patterns[p].Tokens;
            var any = false;
            for (var w = 0; w < from.Length; w++)
            {
                var bits = from[w];
                while (bits != 0)
                {
                    var i = (w * 64) + BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                    if (i >= tokens.Length)
                    {
                        continue; // ya casado entero: no consume más segmentos
                    }

                    if (tokens[i] is not { } segment)
                    {
                        Set(to, i); // ** consume este segmento y sigue disponible
                        any = true;
                    }
                    else if (segment.Matches(name))
                    {
                        Set(to, i + 1);
                        any = true;
                    }
                }
            }

            if (!any)
            {
                continue;
            }

            Close(tokens, to);
            if (first < 0 && (isDirectory || !patterns[p].DirectoryOnly) && IsSet(to, tokens.Length))
            {
                first = p;
            }
        }

        return first;
    }

    /// <summary>Si el patrón <paramref name="index"/> excluye el elemento cuyo estado es <paramref name="state"/>.</summary>
    internal bool IsMatch(ReadOnlySpan<ulong> state, int index, bool isDirectory)
    {
        var pattern = patterns[index];
        return (isDirectory || !pattern.DirectoryOnly)
            && IsSet(state.Slice(offsets[index], words[index]), pattern.Tokens.Length);
    }

    /// <summary>
    /// Si una ruta del snapshot (<c>alias/…</c>; con <c>/</c> final, una carpeta) queda excluida: ella o alguna de sus
    /// carpetas coincide con algún patrón. El alias no cuenta.
    /// </summary>
    /// <param name="snapshotPath">Ruta de una entrada del snapshot.</param>
    /// <returns>Si algún patrón la excluye.</returns>
    public bool IsExcluded(string snapshotPath) => FirstMatch(snapshotPath) >= 0;

    /// <summary>Índice del primer patrón que excluye la ruta (o una de sus carpetas), o -1. Ver <see cref="IsExcluded"/>.</summary>
    /// <param name="snapshotPath">Ruta de una entrada del snapshot.</param>
    /// <returns>El índice, o -1.</returns>
    public int FirstMatch(string snapshotPath)
    {
        ArgumentNullException.ThrowIfNull(snapshotPath);
        if (IsEmpty)
        {
            return -1;
        }

        var path = snapshotPath.AsSpan();
        var isDirectoryEntry = path.EndsWith('/');
        if (isDirectoryEntry)
        {
            path = path[..^1];
        }

        var slash = path.IndexOf('/');
        if (slash < 0)
        {
            return -1; // solo el alias
        }

        var current = RootState(path[..slash].ToString());
        var next = new ulong[StateWords];
        var rest = path[(slash + 1)..];
        while (true)
        {
            slash = rest.IndexOf('/');
            var segment = slash < 0 ? rest : rest[..slash];
            var isDirectory = slash >= 0 || isDirectoryEntry;
            var match = Step(current, segment, isDirectory, next);
            if (match >= 0)
            {
                return match;
            }

            if (slash < 0)
            {
                return -1;
            }

            (current, next) = (next, current);
            rest = rest[(slash + 1)..];
        }
    }

    /// <summary>Cierre por <c>**</c>: si falta casar un <c>**</c>, también puede haber consumido cero segmentos.</summary>
    private static void Close(SegmentPattern?[] tokens, Span<ulong> bits)
    {
        for (var i = 0; i < tokens.Length; i++)
        {
            if (tokens[i] is null && IsSet(bits, i))
            {
                Set(bits, i + 1);
            }
        }
    }

    private static void Set(Span<ulong> bits, int index) => bits[index >> 6] |= 1UL << (index & 63);

    private static bool IsSet(ReadOnlySpan<ulong> bits, int index) => (bits[index >> 6] & (1UL << (index & 63))) != 0;
}
