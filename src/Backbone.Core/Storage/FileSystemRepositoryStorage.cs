using System.Runtime.CompilerServices;
using Backbone.Contracts;
using Backbone.Core.Repository;

namespace Backbone.Core.Storage;

/// <summary>
/// <see cref="IRepositoryStorage"/> sobre <see cref="System.IO"/>, con la raíz en un recurso UNC (RF-03).
/// Las rutas largas las resuelve .NET; las rutas se construyen con <see cref="Path.Join(ReadOnlySpan{string?})"/>.
/// </summary>
/// <remarks>
/// <para>
/// No sigue puntos de reanálisis (<i>junctions</i>, enlaces simbólicos) (M-3): antes de cada operación comprueba la raíz,
/// cada carpeta intermedia (<c>tmp/</c>, <c>locks/</c>, <c>objects/</c>, <c>objects/xx/</c>, <c>snapshots/</c>…) y el propio
/// elemento si existe; si alguno es un punto de reanálisis, la operación falla con
/// <see cref="RepositoryErrorKind.Corrupt"/> sin tocar nada. Así un repositorio manipulado no puede hacer que la limpieza
/// borre, o que una copia escriba, fuera del repositorio. Enumerar una carpeta que contiene un punto de reanálisis
/// también falla así.
/// </para>
/// <para>
/// Queda una ventana entre la comprobación y la operación (TOCTOU) que solo puede aprovechar quien ya puede escribir en
/// el recurso del NAS (§13 R7).
/// </para>
/// <para>
/// <b>Destino</b> (§6.1 "Solo destinos UNC"): el constructor solo admite rutas que cumplen <see cref="UncPaths.TryParse"/>
/// (sin rutas locales, <c>\\?\</c>, <c>\\.\</c>, <i>loopback</i> ni recursos administrativos), pero no puede saber si el
/// servidor es el propio equipo. <b>El servicio debe comprobar, antes de crearlo, que el destino no apunta al propio
/// equipo</b> (sus nombres, sus direcciones y la resolución del nombre, §7.5). La variante para carpetas locales
/// (<c>ForLocalTesting</c>) es interna y solo la usan las pruebas.
/// </para>
/// </remarks>
public sealed class FileSystemRepositoryStorage : IRepositoryStorage
{
    private const int BufferSize = 81920;
    private readonly string root;

    /// <summary>Crea el almacenamiento sobre un destino UNC <c>\\servidor\recurso[\ruta]</c>.</summary>
    /// <param name="uncRoot">Destino; debe cumplir <see cref="UncPaths.TryParse"/>.</param>
    /// <exception cref="ArgumentException">No es un destino UNC admitido.</exception>
    public FileSystemRepositoryStorage(string uncRoot)
    {
        if (!UncPaths.TryParse(uncRoot, out _, out var normalized, out var issue))
        {
            throw new ArgumentException($"El destino del repositorio no es una ruta UNC admitida ({issue}).", nameof(uncRoot));
        }

        root = Normalize(normalized);
    }

    private FileSystemRepositoryStorage(string localRoot, bool localTesting)
    {
        _ = localTesting;
        ArgumentException.ThrowIfNullOrWhiteSpace(localRoot);
        if (!Path.IsPathFullyQualified(localRoot))
        {
            throw new ArgumentException("La raíz del repositorio debe ser una ruta absoluta.", nameof(localRoot));
        }

        root = Normalize(localRoot);
    }

    /// <summary>Ruta absoluta de la raíz.</summary>
    public string RootPath => root;

    /// <summary>
    /// Crea el almacenamiento sobre una carpeta local. <b>Solo para pruebas</b> (interna; visible para los proyectos de
    /// pruebas con <c>InternalsVisibleTo</c>): Backbone nunca guarda copias en un disco local, y el servicio usa siempre el
    /// constructor UNC.
    /// </summary>
    /// <param name="localRoot">Ruta absoluta.</param>
    internal static FileSystemRepositoryStorage ForLocalTesting(string localRoot) => new(localRoot, localTesting: true);

    /// <inheritdoc/>
    public Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Resolve(path, allowRoot: false);
        try
        {
            Stream stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return Task.FromResult(stream);
        }
        catch (DirectoryNotFoundException ex)
        {
            throw new FileNotFoundException(ex.Message, path, ex);
        }
    }

    /// <inheritdoc/>
    public Task<Stream> CreateNewAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Resolve(path, allowRoot: false);
        Stream stream = new DurableFileStream(fullPath);
        return Task.FromResult(stream);
    }

    /// <inheritdoc/>
    public Task<bool> TryMoveAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = Resolve(sourcePath, allowRoot: false);
        var destination = Resolve(destinationPath, allowRoot: false);

        if (!File.Exists(source))
        {
            throw new FileNotFoundException("No existe el archivo que se iba a renombrar.", sourcePath);
        }

        try
        {
            File.Move(source, destination, overwrite);
            return Task.FromResult(true);
        }
        catch (IOException) when (!overwrite && File.Exists(destination) && File.Exists(source))
        {
            return Task.FromResult(false);
        }
    }

    /// <inheritdoc/>
    public Task DeleteFileAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Resolve(path, allowRoot: false);
        try
        {
            File.Delete(fullPath);
        }
        catch (DirectoryNotFoundException)
        {
            // La carpeta no existe: el archivo tampoco.
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Resolve(path, allowRoot: true));
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<bool> DirectoryExistsAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Directory.Exists(Resolve(path, allowRoot: true)));
    }

    /// <inheritdoc/>
    public Task<StorageEntry?> GetFileInfoAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = new FileInfo(Resolve(path, allowRoot: false));
        return Task.FromResult(info.Exists
            ? new StorageEntry(info.Name, IsDirectory: false, info.Length, info.LastWriteTimeUtc)
            : null);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<StorageEntry> EnumerateAsync(
        string directoryPath,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var directory = new DirectoryInfo(Resolve(directoryPath, allowRoot: true));
        if (!directory.Exists)
        {
            yield break;
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = false,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
        };

        foreach (var item in directory.EnumerateFileSystemInfos("*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw ReparsePoint(directoryPath.Length == 0 ? item.Name : directoryPath + RepositoryPath.Separator + item.Name);
            }

            yield return item is FileInfo file
                ? new StorageEntry(file.Name, IsDirectory: false, file.Length, file.LastWriteTimeUtc)
                : new StorageEntry(item.Name, IsDirectory: true, 0, item.LastWriteTimeUtc);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static RepositoryException ReparsePoint(string relativePath) =>
        new(
            RepositoryErrorKind.Corrupt,
            $"'{Untrusted.Describe(relativePath.Length == 0 ? "(raíz)" : relativePath)}' es un punto de reanálisis (junction o enlace simbólico); Backbone no los sigue.");

    /// <summary>
    /// Comprueba que <paramref name="fullPath"/>, si existe, no es un punto de reanálisis.
    /// </summary>
    /// <returns><see langword="false"/> si no existe (entonces tampoco existe nada por debajo).</returns>
    private static bool ExistsWithoutReparsePoint(string fullPath, string relativePath)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(fullPath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }

        return attributes.HasFlag(FileAttributes.ReparsePoint) ? throw ReparsePoint(relativePath) : true;
    }

    private string Resolve(string path, bool allowRoot)
    {
        var segments = RepositoryPath.Validate(path, allowRoot);

        // Ningún punto de reanálisis en la raíz, en las carpetas intermedias ni en el propio elemento.
        var current = root;
        var exists = ExistsWithoutReparsePoint(current, string.Empty);
        for (var i = 0; i < segments.Length && exists; i++)
        {
            current = Path.Join(current, segments[i]);
            exists = ExistsWithoutReparsePoint(current, string.Join(RepositoryPath.Separator, segments[..(i + 1)]));
        }

        if (segments.Length == 0)
        {
            return root;
        }

        var fullPath = Path.Join([root, .. segments]);

        // Defensa en profundidad: la validación de segmentos ya impide salir de la raíz.
        var normalized = Path.GetFullPath(fullPath);
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"La ruta '{Untrusted.Describe(path)}' sale de la raíz del repositorio.", nameof(path));
        }

        return normalized;
    }

    /// <summary>
    /// Archivo nuevo cuyo <see cref="FlushAsync(CancellationToken)"/> fuerza la escritura en el
    /// almacenamiento estable (FlushFileBuffers), para que el renombrado posterior no publique
    /// un archivo cuyo contenido aún no ha llegado al disco.
    /// </summary>
    private sealed class DurableFileStream(string path) : FileStream(
        path,
        FileMode.CreateNew,
        FileAccess.Write,
        FileShare.None,
        BufferSize,
        FileOptions.Asynchronous)
    {
        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            await base.FlushAsync(cancellationToken).ConfigureAwait(false);
            Flush(flushToDisk: true);
        }
    }
}
