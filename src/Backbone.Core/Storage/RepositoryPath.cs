using System.Diagnostics.CodeAnalysis;
using Backbone.Contracts;
using Backbone.Core.Repository;

namespace Backbone.Core.Storage;

/// <summary>
/// Rutas relativas dentro del repositorio: segmentos separados por <c>/</c>, sin <c>.</c> ni <c>..</c>,
/// sin separadores de Windows, sin unidad ni flujos alternativos (<c>:</c>) y sin caracteres no válidos
/// en nombres de archivo de Windows. Impide salir de la raíz del repositorio.
/// </summary>
public static class RepositoryPath
{
    /// <summary>Separador de segmentos.</summary>
    public const char Separator = '/';

    /// <summary>Une segmentos con <see cref="Separator"/> y valida el resultado.</summary>
    public static string Combine(params ReadOnlySpan<string> segments)
    {
        var path = string.Join(Separator, segments);
        Validate(path, allowRoot: false);
        return path;
    }

    /// <summary>Valida una ruta relativa y devuelve sus segmentos.</summary>
    /// <param name="path">Ruta relativa.</param>
    /// <param name="allowRoot">Si se admite la cadena vacía (raíz del repositorio).</param>
    /// <exception cref="ArgumentException">La ruta no es válida.</exception>
    public static string[] Validate(string path, bool allowRoot)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length == 0)
        {
            return allowRoot ? [] : throw new ArgumentException("La ruta del repositorio no puede estar vacía.", nameof(path));
        }

        var segments = path.Split(Separator);
        foreach (var segment in segments)
        {
            if (!IsValidSegment(segment))
            {
                throw new ArgumentException($"Ruta del repositorio no válida: '{Untrusted.Describe(path)}'.", nameof(path));
            }
        }

        return segments;
    }

    /// <summary>
    /// Indica si <paramref name="segment"/> es un nombre de archivo o carpeta admisible en el repositorio.
    /// Es la regla única de nombres de Backbone (<see cref="SnapshotPaths.IsValidSegment"/>, §7.5): sin caracteres
    /// no válidos en Windows, sin punto ni espacio final, sin nombres de dispositivo reservados y como mucho
    /// 255 caracteres; además, UTF-16 válido.
    /// </summary>
    public static bool IsValidSegment([NotNullWhen(true)] string? segment) =>
        segment is not null && SnapshotPaths.IsValidSegment(segment) && !JsonFormat.HasLoneSurrogate(segment);
}
