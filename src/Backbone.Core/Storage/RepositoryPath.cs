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
                throw new ArgumentException($"Ruta del repositorio no válida: '{path}'.", nameof(path));
            }
        }

        return segments;
    }

    /// <summary>
    /// Indica si <paramref name="segment"/> es un nombre de archivo o carpeta admisible en el repositorio.
    /// </summary>
    public static bool IsValidSegment(string segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        return IsValidSegment(segment.AsSpan());
    }

    /// <inheritdoc cref="IsValidSegment(string)"/>
    public static bool IsValidSegment(ReadOnlySpan<char> segment)
    {
        if (segment.Length == 0 || segment is "." or "..")
        {
            return false;
        }

        // Windows elimina puntos y espacios finales: "a." y "a" serían el mismo archivo.
        if (segment[^1] is '.' or ' ')
        {
            return false;
        }

        foreach (var c in segment)
        {
            if (c < 0x20 || c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*')
            {
                return false;
            }
        }

        return true;
    }
}
