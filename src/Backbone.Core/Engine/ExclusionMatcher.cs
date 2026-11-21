using System.Numerics;
using System.Text;
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

    /// <summary>
    /// Cota del coste de una lista (suma de <see cref="ExclusionPattern.Cost"/>), es decir, del trabajo por elemento
    /// recorrido en el peor caso (revisión de seguridad de HU-08, B-1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Se acota el coste total y no el número de segmentos o de <c>*</c>, porque es lo que de verdad determina el tiempo:
    /// un segmento sin texto entre dos <c>*</c> (sin <c>*</c>, o con <c>*</c> solo al principio o al final) cuesta 1 más lo
    /// que mide, y cada segmento con texto entre dos <c>*</c> cuesta además un recorrido del nombre: 255 pasos por cada 64
    /// caracteres de su trozo intermedio más largo.
    /// </para>
    /// <para>
    /// El valor es <see cref="ContractLimits.MaxExclusions"/> × (<see cref="ContractLimits.MaxExclusionPatternLength"/> + 2)
    /// = 256 × 514: el coste máximo de un patrón sin texto entre dos <c>*</c> es 514 (<c>**</c> implícito, 1, más un
    /// segmento de 512 caracteres, 513), así que <b>una lista de patrones sin <c>*…*</c> se admite siempre</b>, sea cual
    /// sea. Caben, por ejemplo, 256 patrones como <c>*factura*</c> (257 cada uno) y en total unos 500 segmentos con texto
    /// entre <c>*</c>; el patrón adversario de la revisión (<c>**/*aaa…ab*/**/*aaa…ab*/…</c>, 1 799) repetido 256 veces
    /// (unos 460 000) se rechaza con <see cref="ExclusionPatternError.TooComplex"/>. En el peor caso admitido son unos
    /// 130 000 pasos sencillos por elemento recorrido (fracciones de milisegundo), frente a los ~10⁷ de antes.
    /// </para>
    /// </remarks>
    public const long MaxListCost = (long)ContractLimits.MaxExclusions * (ContractLimits.MaxExclusionPatternLength + 2);

    /// <summary>
    /// Valida una lista de patrones: cada patrón (<see cref="ExclusionPattern.Validate"/>), el número de patrones y el coste
    /// total (<see cref="MaxListCost"/>). El servicio la usa en la validación del trabajo.
    /// </summary>
    /// <param name="patterns">Patrones del trabajo.</param>
    /// <param name="index">
    /// Índice del primer patrón no válido; -1 si el error es de la lista entera (<see cref="ExclusionPatternError.TooMany"/>,
    /// <see cref="ExclusionPatternError.TooComplex"/>) o si no hay error.
    /// </param>
    /// <returns><see cref="ExclusionPatternError.None"/> si la lista es válida; si no, el primer motivo.</returns>
    public static ExclusionPatternError ValidateList(IReadOnlyList<string?> patterns, out int index)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        return TryCompile(patterns, out _, out index);
    }

    /// <summary>Compila una lista de patrones.</summary>
    /// <param name="patterns">Patrones del trabajo, como mucho <see cref="ContractLimits.MaxExclusions"/>.</param>
    /// <returns>La lista compilada.</returns>
    /// <exception cref="ArgumentException">
    /// Hay más de <see cref="ContractLimits.MaxExclusions"/> patrones, alguno no es válido
    /// (<see cref="ExclusionPattern.Validate"/>) o la lista supera <see cref="MaxListCost"/>.
    /// </exception>
    public static ExclusionMatcher Create(IReadOnlyList<string> patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        var error = TryCompile(patterns, out var matcher, out var index);
        if (error == ExclusionPatternError.None && matcher is not null)
        {
            return matcher;
        }

        throw error switch
        {
            ExclusionPatternError.TooMany => new ArgumentException(
                $"Hay {patterns.Count} patrones de exclusión; el máximo es {ContractLimits.MaxExclusions} ({error}).",
                nameof(patterns)),
            ExclusionPatternError.TooComplex => new ArgumentException(
                $"La lista de exclusiones es demasiado compleja: su coste supera {MaxListCost} ({error}).",
                nameof(patterns)),
            _ => new ArgumentException(
                $"El patrón de exclusión {index} no es válido ({error}): {Untrusted.Describe(patterns[index])}",
                nameof(patterns)),
        };
    }

    private static ExclusionPatternError TryCompile(IReadOnlyList<string?> patterns, out ExclusionMatcher? matcher, out int index)
    {
        matcher = null;
        index = -1;
        if (patterns.Count == 0)
        {
            matcher = Empty;
            return ExclusionPatternError.None;
        }

        if (patterns.Count > ContractLimits.MaxExclusions)
        {
            return ExclusionPatternError.TooMany;
        }

        var compiled = new ExclusionPattern[patterns.Count];
        var cost = 0L;
        for (var i = 0; i < patterns.Count; i++)
        {
            if (!ExclusionPattern.TryParse(patterns[i], out var pattern, out var error))
            {
                index = i;
                return error;
            }

            compiled[i] = pattern;
            cost += pattern.Cost;
        }

        if (cost > MaxListCost)
        {
            return ExclusionPatternError.TooComplex;
        }

        matcher = new ExclusionMatcher(compiled);
        return ExclusionPatternError.None;
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
        name = ToNfc(name);
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

    /// <summary>
    /// El nombre en NFC (B-2), como el patrón. Sin copia si todos sus caracteres son anteriores a U+0300, que no cambian al
    /// normalizar; un nombre que no es UTF-16 válido se devuelve tal cual (no se puede normalizar, y un patrón válido
    /// nunca casa con una suplente suelta).
    /// </summary>
    internal static ReadOnlySpan<char> ToNfc(ReadOnlySpan<char> name)
    {
        if (!name.ContainsAnyInRange((char)0x0300, char.MaxValue))
        {
            return name;
        }

        try
        {
            // La comprobación no copia: solo se crea una cadena si el nombre no está ya en NFC (lo raro en Windows).
            return name.IsNormalized(NormalizationForm.FormC) ? name : name.ToString().Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            return name;
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
