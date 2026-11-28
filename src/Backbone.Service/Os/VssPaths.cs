namespace Backbone.Service.Os;

/// <summary>
/// Traduce la ruta final de un origen a la de su instantánea VSS (§8.2 "Raíz del origen en VSS"). Parte de la ruta final en la
/// forma <c>VOLUME_NAME_GUID</c> (<c>\\?\Volume{guid}\carpeta\sub</c>) que obtuvo la resolución controlada (§8.3 punto 6), nunca
/// de la ruta configurada: así un origen que sea un <i>junction</i> no se resuelve dentro de la instantánea hacia el volumen
/// en vivo.
/// </summary>
internal static class VssPaths
{
    /// <summary>
    /// Separa <c>\\?\Volume{guid}\a\b</c> en el volumen (<c>\\?\Volume{guid}\</c>) y la ruta relativa (<c>a\b</c>, vacía si es
    /// la raíz del volumen). Falla si no tiene esa forma o la ruta relativa tiene segmentos vacíos, <c>.</c> o <c>..</c>.
    /// </summary>
    public static bool TrySplit(string? guidPath, out string volume, out string relative)
    {
        volume = string.Empty;
        relative = string.Empty;
        if (string.IsNullOrEmpty(guidPath) || !guidPath.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return false;
        }

        var body = guidPath.AsSpan(4);
        var separator = body.IndexOf('\\');
        var first = separator < 0 ? body : body[..separator];
        if (!SourceOpen.IsVolumeGuid(first))
        {
            return false;
        }

        var rest = separator < 0 ? string.Empty : body[(separator + 1)..].ToString().TrimEnd('\\');
        if (rest.Length > 0)
        {
            foreach (var segment in rest.Split('\\'))
            {
                if (segment.Length == 0 || segment is "." or "..")
                {
                    return false;
                }
            }
        }

        volume = @"\\?\" + first.ToString() + @"\";
        relative = rest;
        return true;
    }

    /// <summary><see cref="VolumeSnapshot.DevicePath"/> más la ruta relativa: la <c>ReadPath</c> del origen dentro de la instantánea.</summary>
    public static string Combine(string devicePath, string relative) =>
        relative.Length == 0 ? devicePath : devicePath + "\\" + relative;
}
