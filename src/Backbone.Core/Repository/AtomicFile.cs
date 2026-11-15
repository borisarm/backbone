using Backbone.Core.Storage;

namespace Backbone.Core.Repository;

/// <summary>
/// Escritura atómica (§6.4.1): todo archivo se escribe en <c>tmp/&lt;guid&gt;.tmp</c>, se vacía al
/// almacenamiento estable y se renombra al nombre final. Si algo falla, el temporal se borra
/// (si se puede; si no, lo recoge la limpieza de <c>tmp/</c>).
/// </summary>
internal static class AtomicFile
{
    /// <summary>Escribe <paramref name="destinationPath"/> de forma atómica.</summary>
    /// <returns>
    /// <see langword="false"/> si <paramref name="overwrite"/> es <see langword="false"/> y el destino ya existía:
    /// el temporal se descarta y el destino no se toca.
    /// </returns>
    public static async Task<bool> WriteAsync(
        IRepositoryStorage storage,
        string destinationPath,
        bool overwrite,
        Func<Stream, CancellationToken, Task> writeContent,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? beforeMove = null)
    {
        var tempPath = RepositoryLayout.NewTempPath();
        var moved = false;
        try
        {
            var stream = await storage.CreateNewAsync(tempPath, cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                await writeContent(stream, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (beforeMove is not null)
            {
                await beforeMove(cancellationToken).ConfigureAwait(false);
            }

            moved = await storage.TryMoveAsync(tempPath, destinationPath, overwrite, cancellationToken).ConfigureAwait(false);
            return moved;
        }
        finally
        {
            if (!moved)
            {
                await TryDeleteAsync(storage, tempPath).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Borra un archivo sin propagar errores de E/S (limpieza de mejor esfuerzo).</summary>
    public static async Task TryDeleteAsync(IRepositoryStorage storage, string path)
    {
        try
        {
            await storage.DeleteFileAsync(path, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Queda en tmp/: lo borrará la próxima limpieza (§6.4.6).
        }
    }
}
