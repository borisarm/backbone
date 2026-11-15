using System.Runtime.CompilerServices;

namespace Backbone.Core.Storage;

/// <summary>
/// <see cref="IRepositoryStorage"/> sobre <see cref="System.IO"/>: una carpeta local o un recurso UNC.
/// Las rutas largas las resuelve .NET; las rutas se construyen con <see cref="Path.Join(ReadOnlySpan{string?})"/>.
/// </summary>
public sealed class FileSystemRepositoryStorage : IRepositoryStorage
{
    private const int BufferSize = 81920;
    private readonly string root;

    /// <param name="rootPath">Carpeta raíz del repositorio (ruta absoluta, local o UNC).</param>
    public FileSystemRepositoryStorage(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        if (!Path.IsPathFullyQualified(rootPath))
        {
            throw new ArgumentException("La raíz del repositorio debe ser una ruta absoluta.", nameof(rootPath));
        }

        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
    }

    /// <summary>Ruta absoluta de la raíz.</summary>
    public string RootPath => root;

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
            yield return item is FileInfo file
                ? new StorageEntry(file.Name, IsDirectory: false, file.Length, file.LastWriteTimeUtc)
                : new StorageEntry(item.Name, IsDirectory: true, 0, item.LastWriteTimeUtc);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private string Resolve(string path, bool allowRoot)
    {
        var segments = RepositoryPath.Validate(path, allowRoot);
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
            throw new ArgumentException($"La ruta '{path}' sale de la raíz del repositorio.", nameof(path));
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
