using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;

namespace Backbone.Core.Repository.Snapshots;

/// <summary>
/// Identificadores de snapshot: <c>yyyyMMddTHHmmssZ-&lt;4 hex&gt;</c> (§6.3), ordenables por fecha
/// con comparación ordinal. La fecha es la de inicio de la ejecución.
/// </summary>
public static class SnapshotId
{
    private const string TimestampFormat = "yyyyMMdd'T'HHmmss'Z'";
    private const int TimestampLength = 16;
    private const int Length = TimestampLength + 1 + 4;

    /// <summary>Crea un identificador nuevo para una ejecución iniciada en <paramref name="startedUtc"/>.</summary>
    public static string Create(DateTime startedUtc)
    {
        JsonFormat.RequireUtc(startedUtc, nameof(startedUtc));
        return startedUtc.ToString(TimestampFormat, CultureInfo.InvariantCulture)
            + "-"
            + RandomNumberGenerator.GetHexString(4, lowercase: true);
    }

    /// <summary>Indica si <paramref name="id"/> tiene el formato de un identificador de snapshot.</summary>
    public static bool IsValid([NotNullWhen(true)] string? id) => TryGetTimestamp(id, out _);

    /// <summary>Extrae la fecha (UTC, precisión de segundos) de un identificador.</summary>
    public static bool TryGetTimestamp([NotNullWhen(true)] string? id, out DateTime timestampUtc)
    {
        timestampUtc = default;
        if (id is null || id.Length != Length || id[TimestampLength] != '-')
        {
            return false;
        }

        foreach (var c in id.AsSpan(TimestampLength + 1))
        {
            if (!char.IsAsciiDigit(c) && c is not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        if (!DateTime.TryParseExact(
                id.AsSpan(0, TimestampLength),
                TimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out timestampUtc))
        {
            return false;
        }

        // Se rechazan formas no canónicas que TryParseExact pudiera aceptar.
        return string.Equals(
            timestampUtc.ToString(TimestampFormat, CultureInfo.InvariantCulture),
            id[..TimestampLength],
            StringComparison.Ordinal);
    }
}
