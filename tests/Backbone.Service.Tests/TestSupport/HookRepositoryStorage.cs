using Backbone.Core.Storage;

namespace Backbone.Service.Tests.TestSupport;

/// <summary>
/// Decorador de <see cref="IRepositoryStorage"/> que llama a un gancho antes de cada operación (HU-14): la prueba cancela la
/// ejecución, la detiene o falla en el punto exacto que busca. Como un almacenamiento real, atiende el token antes de actuar.
/// </summary>
internal sealed class HookRepositoryStorage(IRepositoryStorage inner) : IRepositoryStorage
{
    /// <summary>Se llama antes de cada operación con su nombre (<c>CreateNew</c>, <c>Move</c>…) y su ruta (la de destino en <c>Move</c>).</summary>
    public Func<string, string, CancellationToken, Task>? Before { get; set; }

    public async Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
    {
        await RunHookAsync("OpenRead", path, cancellationToken).ConfigureAwait(false);
        return await inner.OpenReadAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Stream> CreateNewAsync(string path, CancellationToken cancellationToken)
    {
        await RunHookAsync("CreateNew", path, cancellationToken).ConfigureAwait(false);
        return await inner.CreateNewAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> TryMoveAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken)
    {
        await RunHookAsync("Move", destinationPath, cancellationToken).ConfigureAwait(false);
        return await inner.TryMoveAsync(sourcePath, destinationPath, overwrite, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteFileAsync(string path, CancellationToken cancellationToken)
    {
        await RunHookAsync("Delete", path, cancellationToken).ConfigureAwait(false);
        await inner.DeleteFileAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public async Task CreateDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        await RunHookAsync("CreateDirectory", path, cancellationToken).ConfigureAwait(false);
        await inner.CreateDirectoryAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> DirectoryExistsAsync(string path, CancellationToken cancellationToken) =>
        inner.DirectoryExistsAsync(path, cancellationToken);

    public Task<StorageEntry?> GetFileInfoAsync(string path, CancellationToken cancellationToken) =>
        inner.GetFileInfoAsync(path, cancellationToken);

    public IAsyncEnumerable<StorageEntry> EnumerateAsync(string directoryPath, CancellationToken cancellationToken) =>
        inner.EnumerateAsync(directoryPath, cancellationToken);

    private async Task RunHookAsync(string operation, string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Before is { } before)
        {
            await before(operation, path, cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }
}
