namespace Backbone.Core.Engine;

/// <summary>
/// Envuelve el flujo de un archivo de origen para distinguir sus errores de lectura de los del repositorio: dentro de
/// <see cref="Repository.BackupRepository.WriteObjectAsync"/> se lee el origen y se escribe el repositorio, y solo los
/// primeros permiten omitir el archivo y seguir (RF-25); un error del repositorio hace fallar la copia.
/// </summary>
internal sealed class SourceReadStream(Stream inner) : Stream
{
    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        try
        {
            return inner.Read(buffer, offset, count);
        }
        catch (Exception ex) when (BackupEngine.IsSourceError(ex))
        {
            throw new SourceReadException(ex);
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try
        {
            return await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (BackupEngine.IsSourceError(ex))
        {
            throw new SourceReadException(ex);
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>Error al leer un archivo de origen durante la copia; <see cref="Exception.InnerException"/> es el original.</summary>
internal sealed class SourceReadException : Exception
{
    public SourceReadException(Exception innerException)
        : base(innerException.Message, innerException)
    {
    }

    public SourceReadException()
    {
    }

    public SourceReadException(string message)
        : base(message)
    {
    }

    public SourceReadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>El error original (o esta misma excepción si no lo hay).</summary>
    public Exception Original => InnerException ?? this;
}
