using Backbone.Contracts;

namespace Backbone.App.ViewModels;

/// <summary>
/// Propone un alias a partir de la ruta de una carpeta (HU-07, criterio 2). Es solo una propuesta editable: la validez
/// y la unicidad las decide el servicio (<c>ValidateJobAsync</c>); la app no las comprueba.
/// </summary>
internal static class AliasSuggester
{
    /// <summary>Alias propuesto cuando la ruta no da ninguno.</summary>
    public const string Fallback = "Carpeta";

    private static readonly char[] s_invalid = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    /// <summary>Último segmento de la ruta (o la letra de la unidad si es una raíz), sin caracteres no válidos en un nombre de carpeta.</summary>
    /// <param name="path">Ruta de la carpeta.</param>
    /// <returns>El alias propuesto.</returns>
    public static string Suggest(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Fallback;
        }

        var trimmed = path.Trim().TrimEnd('\\', '/');
        var name = trimmed;
        var cut = trimmed.LastIndexOfAny(['\\', '/']);
        if (cut >= 0)
        {
            name = trimmed[(cut + 1)..];
        }

        // Raíz de unidad ("C:\" -> "C").
        name = name.TrimEnd(':');
        var cleaned = new string(name.Where(c => !char.IsControl(c) && Array.IndexOf(s_invalid, c) < 0).ToArray())
            .Trim()
            .TrimEnd('.', ' ');
        if (cleaned.Length == 0)
        {
            return Fallback;
        }

        return cleaned.Length > ContractLimits.MaxAliasLength
            ? cleaned[..ContractLimits.MaxAliasLength].TrimEnd('.', ' ')
            : cleaned;
    }
}
