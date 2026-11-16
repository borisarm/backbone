using System.Runtime.CompilerServices;
using Backbone.Core.Engine;

namespace Backbone.Core.Tests.Support;

/// <summary>
/// Origen sobre el disco real con <see cref="System.IO"/>, instrumentado (cuenta las aperturas). Solo para pruebas: la
/// implementación del producto vive en Backbone.Service (lectura desde VSS, semántica de copia de seguridad). Sirve para
/// comprobar que el motor funciona con rutas de más de 260 caracteres (RNF-09) sin construir rutas por su cuenta.
/// </summary>
public sealed class DiskSourceFileSystem : ISourceFileSystem
{
    private int opens;

    public int OpenCount => Volatile.Read(ref opens);

    public async IAsyncEnumerable<SourceEntry> EnumerateAsync(string directoryPath, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        foreach (var info in new DirectoryInfo(directoryPath).EnumerateFileSystemInfos())
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return ToEntry(info);
        }
    }

    public Task<Stream> OpenReadAsync(string filePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref opens);
        Stream stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Task.FromResult(stream);
    }

    public Task<SourceEntry?> GetFileInfoAsync(string filePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = new FileInfo(filePath);
        return Task.FromResult(info.Exists ? ToEntry(info) : null);
    }

    private static SourceEntry ToEntry(FileSystemInfo info) => new(
        info.Name,
        info.FullName,
        info is DirectoryInfo,
        info is FileInfo file ? file.Length : 0,
        info.LastWriteTimeUtc,
        info.Attributes);
}
