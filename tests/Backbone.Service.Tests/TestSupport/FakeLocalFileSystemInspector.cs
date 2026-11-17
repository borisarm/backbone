using Backbone.Service.Os;

namespace Backbone.Service.Tests.TestSupport;

/// <summary>
/// Doble de <see cref="ILocalFileSystemInspector"/>: carpetas declaradas con su ruta final, sistema de archivos y volumen;
/// cualquier otra ruta no existe. <c>%ProgramData%</c> y sus carpetas apartadas también se declaran.
/// </summary>
internal sealed class FakeLocalFileSystemInspector : ILocalFileSystemInspector
{
    private readonly Dictionary<string, LocalFolderInfo> _folders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _finalDirectories = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Carpetas apartadas reales que devuelve <see cref="FindRealDirectories"/> (sin filtrar por prefijo).</summary>
    public List<string> QuarantinedFolders { get; } = [];

    /// <summary>Si no es nulo, <see cref="FindRealDirectories"/> lanza esta excepción.</summary>
    public Exception? EnumerationFailure { get; set; }

    /// <summary>Número de veces que se ha calculado la lista de carpetas apartadas.</summary>
    public int EnumerationCount { get; private set; }

    /// <summary>Declara una carpeta NTFS en un disco fijo; <paramref name="finalPath"/> por defecto es la propia ruta.</summary>
    public FakeLocalFileSystemInspector AddFolder(string path, string? finalPath = null, string fileSystem = "NTFS", LocalVolumeKind volume = LocalVolumeKind.Fixed)
    {
        _folders[path] = new LocalFolderInfo(LocalFolderStatus.Ok, finalPath ?? path, fileSystem, volume);
        return this;
    }

    public FakeLocalFileSystemInspector AddStatus(string path, LocalFolderStatus status)
    {
        _folders[path] = new LocalFolderInfo(status);
        return this;
    }

    /// <summary>Ruta final de una carpeta abierta siguiendo enlaces (<c>%ProgramData%</c>).</summary>
    public FakeLocalFileSystemInspector MapFinalDirectory(string path, string finalPath)
    {
        _finalDirectories[path] = finalPath;
        return this;
    }

    public LocalFolderInfo InspectFolder(string path)
    {
        if (_folders.TryGetValue(path.TrimEnd('\\'), out var info) || _folders.TryGetValue(path, out info))
        {
            return info;
        }

        return path.StartsWith(@"\\", StringComparison.Ordinal) || !Path.IsPathFullyQualified(path)
            ? new LocalFolderInfo(LocalFolderStatus.InvalidPath)
            : new LocalFolderInfo(LocalFolderStatus.NotFound);
    }

    public string GetFinalDirectoryPath(string path) =>
        _finalDirectories.TryGetValue(path, out var final) ? final : path;

    public RealDirectoryScan FindRealDirectories(string directory, string namePrefix, int maxNames)
    {
        EnumerationCount++;
        if (EnumerationFailure is { } failure)
        {
            throw failure;
        }

        var matching = QuarantinedFolders.Where(n => n.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase)).ToList();
        return new RealDirectoryScan([.. matching.Take(maxNames)], matching.Count);
    }
}
