using System.Buffers;
using System.Globalization;
using System.Text;

namespace Backbone.Contracts;

/// <summary>
/// Saneado de textos no confiables (§6.1 "Contenido no confiable", ADR-11) compartido por servicio y app: valores leídos del
/// repositorio o del NAS (nombres de recurso o de equipo, <c>createdBy</c>, <c>host</c>, avisos, mensajes del sistema con
/// valores leídos) que se van a registrar o mostrar.
/// </summary>
/// <remarks>
/// Mismo comportamiento que <c>Untrusted.Describe</c> de Core: así un repositorio o un NAS hostil no puede inundar los
/// registros, falsear líneas, ocultar caracteres ni reordenar visualmente el texto. Utilidad pura: no forma parte del cable IPC.
/// </remarks>
public static class UntrustedText
{
    /// <summary>Puntos de código que se conservan como mucho.</summary>
    public const int MaxLength = 200;

    /// <summary>
    /// Prepara un texto no confiable para un log, un mensaje de excepción, un evento o la interfaz.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>Recorta a <see cref="MaxLength"/> puntos de código y añade <c>…</c> detrás si sobraba texto.</item>
    /// <item>Escribe como <c>\uXXXX</c> (hexadecimal en minúscula) los controles (<c>Cc</c>), las suplentes sueltas, U+2028,
    /// U+2029 y toda la categoría de formato (<c>Cf</c>). La categoría se decide por punto de código
    /// (<see cref="Rune.GetUnicodeCategory(Rune)"/>): un <c>Cf</c> fuera del plano básico se escribe como sus dos unidades
    /// UTF-16 y cuenta como un solo punto de código.</item>
    /// <item><see langword="null"/> se describe como <c>(null)</c>.</item>
    /// </list>
    /// </remarks>
    /// <param name="value">Texto no confiable.</param>
    /// <returns>El texto saneado.</returns>
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
            if (status != OperationStatus.Done)
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
