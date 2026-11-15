using System.Globalization;
using System.Text;

namespace Backbone.Core.Repository;

/// <summary>
/// Texto no confiable (leído del repositorio o recibido de fuera) preparado para interpolarlo en mensajes de
/// excepción o de log: recortado y sin caracteres de control ni de formato, para que un manifiesto hostil no pueda
/// inundar los registros, falsear líneas, ocultar caracteres ni reordenar visualmente el texto (§6.1 "Contenido no
/// confiable", ADR-11).
/// </summary>
internal static class Untrusted
{
    /// <summary>Caracteres (puntos de código) que se conservan como mucho.</summary>
    public const int MaxLength = 200;

    /// <summary>
    /// Recorta a <see cref="MaxLength"/> puntos de código (con <c>…</c> detrás) y escapa como <c>\uXXXX</c> los controles
    /// (<c>Cc</c>), las suplentes sueltas, U+2028, U+2029 y toda la categoría de formato (<c>Cf</c>). La categoría se decide
    /// por punto de código: un <c>Cf</c> fuera del plano básico se escribe como sus dos unidades UTF-16.
    /// </summary>
    public static string Describe(string? value)
    {
        if (value is null)
        {
            return "(null)";
        }

        var builder = new StringBuilder(Math.Min(value.Length, MaxLength) + 8);
        var count = 0;
        var i = 0;
        while (i < value.Length)
        {
            if (count >= MaxLength)
            {
                builder.Append('…');
                break;
            }

            var status = Rune.DecodeFromUtf16(value.AsSpan(i), out var rune, out var consumed);
            if (status != System.Buffers.OperationStatus.Done)
            {
                // Suplente suelta: se escapa la unidad tal cual.
                AppendEscaped(builder, value[i]);
                i++;
            }
            else if (MustEscape(rune))
            {
                for (var k = 0; k < consumed; k++)
                {
                    AppendEscaped(builder, value[i + k]);
                }

                i += consumed;
            }
            else
            {
                builder.Append(value, i, consumed);
                i += consumed;
            }

            count++;
        }

        return builder.ToString();
    }

    private static bool MustEscape(Rune rune)
    {
        if (rune.Value is 0x2028 or 0x2029)
        {
            return true;
        }

        var category = Rune.GetUnicodeCategory(rune);
        return category is UnicodeCategory.Control or UnicodeCategory.Format;
    }

    private static void AppendEscaped(StringBuilder builder, char c) =>
        builder.Append('\\').Append('u').Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
}
