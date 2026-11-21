using Backbone.Contracts;
using Backbone.Core.Engine;

namespace Backbone.Service.Sources;

/// <summary>
/// Flujo de lectura secuencial sobre el handle comprobado de un archivo de origen (§8.2). Cada bloque se lee con plazo
/// (<see cref="SourceOperationRunner.ReadAsync"/>) en un búfer propio y se copia al del llamante: si una lectura se
/// abandona, la E/S pendiente solo puede escribir en ese búfer, nunca en el del motor.
/// </summary>
/// <remarks>Solo admite lectura asíncrona: la síncrona no tendría plazo.</remarks>
internal sealed class SourceFileStream : Stream
{
    private const int LockViolationHResult = unchecked((int)0x8007_0021);

    private readonly SourceOperationRunner _runner;
    private readonly string _path;
    private readonly ISourceHandle _content;
    private readonly ISourceHandle? _guard;
    private readonly byte[] _buffer;
    private long _position;
    private bool _faulted;
    private bool _disposed;

    /// <param name="runner">Ejecutor con los plazos.</param>
    /// <param name="path">Ruta para los registros.</param>
    /// <param name="content">Handle del que se lee.</param>
    /// <param name="guard">Primer handle, que se mantiene abierto sin <c>FILE_SHARE_DELETE</c> mientras se lee del segundo.</param>
    public SourceFileStream(SourceOperationRunner runner, string path, ISourceHandle content, ISourceHandle? guard)
    {
        _runner = runner;
        _path = path;
        _content = content;
        _guard = guard;
        _buffer = GC.AllocateUninitializedArray<byte>(Math.Max(4096, runner.Options.ReadBlockBytes));
    }

    public override bool CanRead => !_disposed;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_faulted)
        {
            // Tras un plazo vencido la E/S cancelada puede seguir usando el búfer propio: no se reutiliza.
            throw new SourceAccessException(RunIssueCode.ReadError, "La lectura del archivo se interrumpió por un plazo vencido.");
        }

        if (buffer.IsEmpty)
        {
            return 0;
        }

        var count = Math.Min(buffer.Length, _buffer.Length);
        int read;
        try
        {
            read = await _runner.ReadAsync(_content, _buffer.AsMemory(0, count), _position, _path, cancellationToken).ConfigureAwait(false);
        }
        catch (SourceAccessException)
        {
            _faulted = true;
            throw;
        }
        catch (SourceReadingFailedException)
        {
            _faulted = true;
            throw;
        }
        catch (SourceFileApiException ex)
        {
            throw SourceFileSystem.MapError(ex);
        }
        catch (IOException ex) when (ex.HResult == LockViolationHResult)
        {
            throw new SourceAccessException(RunIssueCode.FileLocked, "Parte del archivo está bloqueada por otro proceso.", ex);
        }

        if (read <= 0)
        {
            return 0;
        }

        _buffer.AsSpan(0, read).CopyTo(buffer.Span);
        _position += read;
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("Source files are read asynchronously, with a deadline per block (§8.2).");

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _content.Dispose();
            _guard?.Dispose();
        }

        base.Dispose(disposing);
    }
}
