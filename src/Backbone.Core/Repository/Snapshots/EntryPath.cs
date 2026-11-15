using System.Diagnostics.CodeAnalysis;
using Backbone.Core.Storage;

namespace Backbone.Core.Repository.Snapshots;

/// <summary>
/// Reglas de las rutas <c>p</c> del manifiesto (§6.3). Se aplican al escribir y al leer: un manifiesto
/// con rutas que salen de su origen (<c>..</c>, rutas absolutas, unidades, <c>\</c>) se considera corrupto,
/// porque la restauración las convertiría en rutas locales.
/// </summary>
public static class EntryPath
{
    /// <summary>Ruta de archivo: <c>alias/…/nombre</c>, al menos dos segmentos válidos.</summary>
    public static bool IsValidFilePath([NotNullWhen(true)] string? path) =>
        path is not null && !path.EndsWith('/') && SegmentsAreValid(path, minimumSegments: 2);

    /// <summary>Ruta de carpeta vacía: <c>alias/…/</c> (el alias solo, <c>alias/</c>, es un origen vacío).</summary>
    public static bool IsValidDirectoryPath([NotNullWhen(true)] string? path) =>
        path is not null && path.Length > 1 && path.EndsWith('/') && SegmentsAreValid(path[..^1], minimumSegments: 1);

    /// <summary>Alias de origen válido: un único segmento.</summary>
    public static bool IsValidAlias([NotNullWhen(true)] string? alias) =>
        alias is not null && SegmentsAreValid(alias, minimumSegments: 1) && !alias.Contains('/', StringComparison.Ordinal);

    private static bool SegmentsAreValid(string path, int minimumSegments)
    {
        if (JsonFormat.HasLoneSurrogate(path))
        {
            return false;
        }

        var count = 0;
        foreach (var range in path.AsSpan().Split('/'))
        {
            if (!RepositoryPath.IsValidSegment(path.AsSpan(range)))
            {
                return false;
            }

            count++;
        }

        return count >= minimumSegments;
    }
}
