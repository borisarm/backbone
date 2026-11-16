using Backbone.Contracts;

namespace Backbone.Core.Engine;

/// <summary>
/// Exclusiones obligatorias de una copia (ADR-16, §8.3 "Carpetas protegidas"), expresadas como rutas del snapshot
/// (<c>alias/ProgramData/Backbone</c>). Se comparan sin distinguir mayúsculas (ordinal) y por segmentos completos.
/// </summary>
/// <remarks>
/// Distintas de los patrones <i>glob</i> de RF-04: no se pueden quitar, no generan avisos y el motor no entra nunca en
/// ellas. Solo se consultan en las carpetas que contienen alguna directamente (<see cref="HasExclusionsIn"/>), para no
/// añadir trabajo por archivo al recorrido normal (RNF-03).
/// </remarks>
internal sealed class MandatoryExclusionSet
{
    private readonly HashSet<string> excluded = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> containers = new(StringComparer.OrdinalIgnoreCase);

    private MandatoryExclusionSet()
    {
    }

    /// <summary>Si no hay ninguna exclusión.</summary>
    public bool IsEmpty => excluded.Count == 0;

    /// <summary>Construye el conjunto a partir de los orígenes de la copia.</summary>
    /// <exception cref="ArgumentException">Una exclusión no es una ruta relativa válida.</exception>
    public static MandatoryExclusionSet Create(IReadOnlyList<BackupSource> sources)
    {
        var set = new MandatoryExclusionSet();
        foreach (var source in sources)
        {
            ArgumentNullException.ThrowIfNull(source.MandatoryExclusions, nameof(sources));
            foreach (var relative in source.MandatoryExclusions)
            {
                var segments = ParseRelative(relative);
                var container = source.Alias;
                for (var i = 0; i < segments.Length - 1; i++)
                {
                    container += "/" + segments[i];
                }

                set.containers.Add(container);
                set.excluded.Add(container + "/" + segments[^1]);
            }
        }

        return set;
    }

    /// <summary>
    /// Segmentos de una ruta relativa al origen (<c>\</c> o <c>/</c> como separador). Se rechazan la cadena vacía, las
    /// rutas con raíz o unidad, los segmentos vacíos, <c>.</c> y <c>..</c>: una exclusión siempre está debajo del origen
    /// (el propio origen, o algo fuera de él, es <c>SourceNotAllowed</c> en la validación del servicio, §8.3). Como defensa
    /// en profundidad, cada segmento debe ser además un segmento válido de un snapshot
    /// (<see cref="SnapshotPaths.IsValidSegment"/>): una exclusión que no lo fuera no coincidiría nunca con una ruta del
    /// recorrido y la carpeta protegida se copiaría sin que nadie lo notara.
    /// </summary>
    internal static string[] ParseRelative(string? relative)
    {
        if (string.IsNullOrEmpty(relative))
        {
            throw new ArgumentException("Una exclusión obligatoria no puede estar vacía.", nameof(relative));
        }

        var segments = relative.Split(['\\', '/']);
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment is "." or ".." || segment.Contains(':', StringComparison.Ordinal)
                || !SnapshotPaths.IsValidSegment(segment))
            {
                throw new ArgumentException(
                    "Una exclusión obligatoria debe ser una ruta relativa al origen, sin segmentos vacíos, '.', '..' ni ':', y con segmentos válidos en un snapshot (§7.5).",
                    nameof(relative));
            }
        }

        return segments;
    }

    /// <summary>Si la carpeta (ruta del snapshot sin barra final) contiene directamente alguna exclusión.</summary>
    public bool HasExclusionsIn(string directorySnapshotPath) =>
        containers.Count > 0 && containers.Contains(directorySnapshotPath);

    /// <summary>Si la ruta del snapshot (sin barra final) es exactamente una exclusión.</summary>
    public bool IsExcluded(string snapshotPath) => excluded.Contains(snapshotPath);

    /// <summary>
    /// Si una ruta de entrada del snapshot (archivo, o carpeta vacía con barra final) es una exclusión o está debajo de
    /// una. Se usa con las entradas que se conservan del snapshot padre.
    /// </summary>
    public bool CoversEntry(string entryPath)
    {
        if (excluded.Count == 0)
        {
            return false;
        }

        var lookup = excluded.GetAlternateLookup<ReadOnlySpan<char>>();
        for (var slash = entryPath.IndexOf('/', StringComparison.Ordinal); slash >= 0; slash = entryPath.IndexOf('/', slash + 1))
        {
            if (lookup.Contains(entryPath.AsSpan(0, slash)))
            {
                return true;
            }
        }

        return lookup.Contains(entryPath.AsSpan());
    }
}
