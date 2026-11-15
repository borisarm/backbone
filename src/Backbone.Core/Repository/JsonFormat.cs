using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Backbone.Core.Repository;

/// <summary>Convenciones JSON comunes a los archivos del repositorio.</summary>
internal static class JsonFormat
{
    /// <summary>Tamaño máximo de los archivos pequeños (<c>repo.json</c>, bloqueos).</summary>
    public const int MaxSmallFileBytes = 64 * 1024;

    /// <summary>
    /// Opciones de escritura: sin escapar caracteres no ASCII (los nombres de archivo en español
    /// se guardan legibles); comillas, barras invertidas y controles se escapan siempre.
    /// </summary>
    public static JsonWriterOptions WriterOptions(bool indented) => new()
    {
        Indented = indented,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        SkipValidation = false,
    };

    /// <summary>
    /// Escribe una fecha UTC en ISO 8601 con <c>Z</c>. Sin fracción si es exacta al segundo; si no, con 7 decimales (100 ns).
    /// </summary>
    public static void WriteUtc(Utf8JsonWriter writer, string propertyName, DateTime value, bool alwaysFraction = false)
    {
        Span<byte> buffer = stackalloc byte[40];
        var format = alwaysFraction || value.Ticks % TimeSpan.TicksPerSecond != 0
            ? "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'"
            : "yyyy-MM-dd'T'HH:mm:ss'Z'";
        if (!value.TryFormat(buffer, out var written, format, CultureInfo.InvariantCulture))
        {
            throw new InvalidOperationException("No se pudo formatear la fecha.");
        }

        writer.WriteString(propertyName, buffer[..written]);
    }

    /// <summary>Exige que la fecha sea UTC (las fechas persistidas son UTC, CLAUDE.md).</summary>
    public static DateTime RequireUtc(DateTime value, string paramName) =>
        value.Kind == DateTimeKind.Utc
            ? value
            : throw new ArgumentException("La fecha debe estar en UTC (DateTimeKind.Utc).", paramName);

    /// <summary>Lee una fecha ISO 8601 y la convierte a UTC.</summary>
    public static bool TryGetUtc(ref Utf8JsonReader reader, out DateTime value)
    {
        value = default;
        if (reader.TokenType != JsonTokenType.String || !reader.TryGetDateTimeOffset(out var offset))
        {
            return false;
        }

        value = offset.UtcDateTime;
        return true;
    }

    /// <summary>Lee una fecha ISO 8601 de un <see cref="JsonElement"/> y la convierte a UTC.</summary>
    public static bool TryGetUtc(JsonElement element, out DateTime value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.String || !element.TryGetDateTimeOffset(out var offset))
        {
            return false;
        }

        value = offset.UtcDateTime;
        return true;
    }

    /// <summary>Lee un archivo pequeño completo, con límite de tamaño.</summary>
    public static async Task<byte[]> ReadSmallFileAsync(Stream stream, string description, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxSmallFileBytes)
            {
                throw new RepositoryException(RepositoryErrorKind.Corrupt, $"{description} supera el tamaño máximo.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Sustituye las suplentes (surrogates) sueltas por U+FFFD para que el texto se pueda codificar en UTF-8.
    /// </summary>
    public static string SanitizeText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return HasLoneSurrogate(value)
            ? string.Create(value.Length, value, static (span, source) =>
            {
                for (var i = 0; i < source.Length; i++)
                {
                    var c = source[i];
                    if (char.IsHighSurrogate(c) && i + 1 < source.Length && char.IsLowSurrogate(source[i + 1]))
                    {
                        span[i] = c;
                        span[i + 1] = source[i + 1];
                        i++;
                    }
                    else
                    {
                        span[i] = char.IsSurrogate(c) ? '�' : c;
                    }
                }
            })
            : value;
    }

    /// <summary>Indica si el texto contiene suplentes sueltas (UTF-16 no válido, posible en nombres de NTFS).</summary>
    public static bool HasLoneSurrogate(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(c))
            {
                return true;
            }
        }

        return false;
    }
}
