using System.Globalization;

namespace Backbone.Contracts;

/// <summary>
/// Reglas puras de identificadores y rutas de snapshot (§6.3, §7.5), compartidas por servicio, motor y app.
/// </summary>
/// <remarks>
/// Una ruta de snapshot usa <c>/</c>, empieza por el alias y no lleva <c>/</c> inicial ni final. Cada segmento debe ser un
/// nombre válido de Windows, para que restaurar nunca pueda salir de la carpeta de destino ni crear nombres especiales.
/// El motor omite (aviso <see cref="RunIssueCode.PathInvalid"/>) los archivos de origen cuyos nombres no cumplen estas reglas.
/// </remarks>
public static class SnapshotPaths
{
    /// <summary>Separador de segmentos.</summary>
    public const char Separator = '/';

    private const string IdTimestampFormat = "yyyyMMdd'T'HHmmss'Z'";
    private const int IdLength = 21; // yyyyMMddTHHmmssZ-xxxx

    private static readonly string[] ReservedNames =
    [
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    ];

    /// <summary>
    /// Indica si <paramref name="snapshotId"/> tiene el formato <c>yyyyMMddTHHmmssZ-xxxx</c> de §6.3 (fecha UTC válida y cuatro
    /// dígitos hexadecimales en minúscula).
    /// </summary>
    /// <param name="snapshotId">Identificador.</param>
    /// <returns><see langword="true"/> si es válido.</returns>
    public static bool IsValidSnapshotId(string? snapshotId)
    {
        if (snapshotId is null || snapshotId.Length != IdLength || snapshotId[16] != '-')
        {
            return false;
        }

        for (var i = 17; i < IdLength; i++)
        {
            if (!char.IsAsciiHexDigitLower(snapshotId[i]))
            {
                return false;
            }
        }

        return DateTime.TryParseExact(
            snapshotId.AsSpan(0, 16), IdTimestampFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out _);
    }

    /// <summary>
    /// Indica si <paramref name="segment"/> es un nombre de archivo o carpeta admitido: no vacío, como mucho
    /// <see cref="ContractLimits.MaxSegmentLength"/> caracteres, distinto de <c>.</c> y <c>..</c>, sin <c>\ / : * ? " &lt; &gt; |</c>
    /// ni caracteres de control, sin punto ni espacio final y sin nombres de dispositivo reservados (<c>CON</c>, <c>NUL</c>,
    /// <c>COM1</c>, <c>CONIN$</c>… también con extensión, como <c>CON.txt</c>).
    /// </summary>
    /// <param name="segment">Nombre.</param>
    /// <returns><see langword="true"/> si es válido.</returns>
    public static bool IsValidSegment(string? segment)
    {
        if (string.IsNullOrEmpty(segment) || segment.Length > ContractLimits.MaxSegmentLength || segment is "." or "..")
        {
            return false;
        }

        foreach (var c in segment)
        {
            if (char.IsControl(c) || c is '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|')
            {
                return false;
            }
        }

        if (segment[^1] is '.' or ' ')
        {
            return false;
        }

        var dot = segment.IndexOf('.', StringComparison.Ordinal);
        var stem = (dot < 0 ? segment : segment[..dot]).TrimEnd(' ');
        return !ReservedNames.Contains(stem, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Indica si <paramref name="path"/> es una ruta de snapshot válida: segmentos válidos separados por <c>/</c>, sin
    /// <c>/</c> inicial ni final, sin segmentos vacíos y como mucho <see cref="ContractLimits.MaxSnapshotPathLength"/> caracteres.
    /// </summary>
    /// <param name="path">Ruta.</param>
    /// <param name="allowRoot">Admitir la cadena vacía (la raíz del snapshot, en <see cref="IBackboneService.BrowseSnapshotAsync"/>).</param>
    /// <returns><see langword="true"/> si es válida.</returns>
    public static bool IsValidPath(string? path, bool allowRoot = false)
    {
        if (path is null || path.Length > ContractLimits.MaxSnapshotPathLength)
        {
            return false;
        }

        if (path.Length == 0)
        {
            return allowRoot;
        }

        foreach (var segment in path.Split(Separator))
        {
            if (!IsValidSegment(segment))
            {
                return false;
            }
        }

        return true;
    }
}
