using Backbone.Contracts;

namespace Backbone.Service.Jobs;

/// <summary>Resultado de <see cref="SourcePath.TryNormalize"/>.</summary>
internal enum SourcePathCheck
{
    /// <summary>Ruta local completa y normalizada.</summary>
    Ok = 0,

    /// <summary>No cumple "Ruta de origen" (<see cref="ValidationIssueCode.SourcePathInvalid"/>).</summary>
    Invalid = 1,

    /// <summary>Ruta UNC o de dispositivo (<see cref="ValidationIssueCode.SourceNotLocal"/>).</summary>
    NotLocal = 2,
}

/// <summary>
/// Normalización de la ruta de un origen (§7.5 "Ruta de origen", revisión de seguridad de HU-07, BAJA-1), <b>sin</b> la
/// normalización de Win32: <c>GetFullPathName</c> quita puntos y espacios finales y admite flujos alternativos, así que
/// <c>C:\docs.</c> abriría <c>C:\docs</c> y <c>C:\ProgramData::$INDEX_ALLOCATION</c>, <c>C:\ProgramData</c>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Se aceptan <c>/</c> como separador (se convierte en <c>\</c>), la letra en minúsculas (se pasa a mayúsculas) y una
/// <c>\</c> final (se quita, salvo en la raíz <c>X:\</c>).</item>
/// <item><see cref="SourcePathCheck.Invalid"/>: vacía, relativa o sin <c>\</c> tras la letra (<c>C:docs</c>), un <c>:</c> fuera
/// de la posición 1, segmentos vacíos, <c>.</c> o <c>..</c>, y cualquier segmento que no cumpla
/// <see cref="SnapshotPaths.IsValidSegment"/> (punto o espacio final, caracteres no válidos o de control, nombres de
/// dispositivo, más de 255 caracteres, suplentes sueltas).</item>
/// <item><see cref="SourcePathCheck.NotLocal"/>: <c>\\servidor\…</c>, <c>\\?\…</c>, <c>\\.\…</c> y <c>\??\…</c>.</item>
/// </list>
/// Lo que se guarda en <c>jobs.json</c> y devuelve <c>SaveJobAsync</c> es la ruta normalizada.
/// </remarks>
internal static class SourcePath
{
    public static SourcePathCheck TryNormalize(string? path, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrEmpty(path) || path.Length > ContractLimits.MaxLocalPathLength)
        {
            return SourcePathCheck.Invalid;
        }

        var value = path.Replace('/', '\\');
        if (value.StartsWith(@"\\", StringComparison.Ordinal) || value.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            return SourcePathCheck.NotLocal;
        }

        if (value.Length < 3 || !char.IsAsciiLetter(value[0]) || value[1] != ':' || value[2] != '\\')
        {
            return SourcePathCheck.Invalid;
        }

        var root = char.ToUpperInvariant(value[0]) + @":\";
        var rest = value[3..];
        if (rest.EndsWith('\\'))
        {
            rest = rest[..^1];
        }

        if (rest.Length == 0)
        {
            normalized = root;
            return SourcePathCheck.Ok;
        }

        foreach (var segment in rest.Split('\\'))
        {
            // IsValidSegment rechaza vacíos, "." y "..", ':' (flujos alternativos) y el resto de caracteres no válidos.
            if (!SnapshotPaths.IsValidSegment(segment))
            {
                return SourcePathCheck.Invalid;
            }
        }

        normalized = root + rest;
        return SourcePathCheck.Ok;
    }

    /// <summary>Segmentos de una ruta ya normalizada, sin la raíz <c>X:\</c>.</summary>
    public static string[] Components(string normalized) =>
        normalized.Length <= 3 ? [] : normalized[3..].Split('\\');
}
