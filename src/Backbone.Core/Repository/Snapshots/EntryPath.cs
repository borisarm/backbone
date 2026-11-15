using System.Diagnostics.CodeAnalysis;
using Backbone.Contracts;

namespace Backbone.Core.Repository.Snapshots;

/// <summary>
/// Reglas de las rutas <c>p</c> del manifiesto (§6.3). Delegan en la regla única de <see cref="SnapshotPaths"/>
/// (§7.5) y añaden que el texto sea UTF-16 válido y que el alias no supere <see cref="ContractLimits.MaxAliasLength"/>.
/// Se aplican al escribir (<see cref="ArgumentException"/>) y al leer (<see cref="RepositoryErrorKind.Corrupt"/>):
/// un manifiesto con rutas que salen de su origen se considera corrupto, porque la restauración las convertiría en
/// rutas locales.
/// </summary>
public static class EntryPath
{
    /// <summary>Ruta de archivo: <c>alias/…/nombre</c>, al menos dos segmentos válidos.</summary>
    public static bool IsValidFilePath([NotNullWhen(true)] string? path) =>
        path is not null
        && path.Contains(SnapshotPaths.Separator, StringComparison.Ordinal)
        && IsValidRelativePath(path);

    /// <summary>
    /// Ruta de carpeta vacía: <c>alias/…/</c> (el alias solo, <c>alias/</c>, es un origen vacío). La barra final es del
    /// manifiesto; sin ella, la ruta debe cumplir <see cref="SnapshotPaths.IsValidPath"/>.
    /// </summary>
    public static bool IsValidDirectoryPath([NotNullWhen(true)] string? path) =>
        path is not null
        && path.Length > 1
        && path[^1] == SnapshotPaths.Separator
        && IsValidRelativePath(path[..^1]);

    /// <summary>Alias de origen válido: un único segmento de como mucho <see cref="ContractLimits.MaxAliasLength"/> caracteres.</summary>
    public static bool IsValidAlias([NotNullWhen(true)] string? alias) =>
        alias is not null
        && alias.Length <= ContractLimits.MaxAliasLength
        && SnapshotPaths.IsValidSegment(alias)
        && !JsonFormat.HasLoneSurrogate(alias);

    private static bool IsValidRelativePath(string path)
    {
        if (JsonFormat.HasLoneSurrogate(path) || !SnapshotPaths.IsValidPath(path))
        {
            return false;
        }

        var slash = path.IndexOf(SnapshotPaths.Separator, StringComparison.Ordinal);
        var aliasLength = slash < 0 ? path.Length : slash;
        return aliasLength <= ContractLimits.MaxAliasLength;
    }
}
