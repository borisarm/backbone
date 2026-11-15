namespace Backbone.Service.Persistence;

/// <summary>
/// Escritura atómica de archivos de configuración: temporal en la misma carpeta, volcado a disco y sustitución con
/// <see cref="File.Replace(string, string, string?)"/> (o <see cref="File.Move(string, string)"/> si aún no existe).
/// Un lector ve siempre el archivo anterior completo o el nuevo completo.
/// </summary>
internal static class AtomicFile
{
    public static async Task WriteAllBytesAsync(string path, ReadOnlyMemory<byte> content, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))
            ?? throw new ArgumentException("The path has no parent directory.", nameof(path));
        Directory.CreateDirectory(directory);

        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var stream = new FileStream(temp, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough,
            });
            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(content, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                File.Replace(temp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, path);
            }
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }
}
