using System.Buffers;
using Backbone.Contracts;
using Backbone.Core.Engine;

namespace Backbone.Service.Sources;

/// <summary>
/// Flujo de lectura secuencial sobre el handle comprobado de un archivo de origen (§8.2). Cada bloque se lee con plazo
/// (<see cref="SourceOperationRunner.ReadAsync"/>) en un búfer propio y se copia al del llamante: si una lectura se
/// abandona, la E/S pendiente solo puede escribir en ese búfer, nunca en el del motor.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>Solo admite lectura asíncrona: la síncrona no tendría plazo.</description></item>
/// <item><description>
/// B-3: el búfer propio sale de un <i>pool</i> (<see cref="ArrayPool{T}.Shared"/>) con como mucho
/// <see cref="SourceReadOptions.ReadBlockBytes"/>, y <b>solo vuelve</b> a él si ninguna lectura falló ni se abandonó y no
/// hay ninguna en curso al cerrar: una lectura cancelada o abandonada puede seguir escribiendo en él cuando el controlador
/// responda, y otro usuario del <i>pool</i> recibiría esos datos o los vería cambiar.
/// </description></item>
/// </list>
/// </remarks>
internal sealed class SourceFileStream : Stream
{
    private const int LockViolationHResult = unchecked((int)0x8007_0021);

    private readonly SourceOperationRunner _runner;
    private readonly string _path;
    private readonly ISourceHandle _content;
    private readonly ISourceHandle? _guard;
    private readonly SourceBudget? _budget;
    private readonly int _blockBytes;
    private byte[]? _buffer;
    private long _position;
    private bool _faulted;
    private int _reading;
    private bool _disposed;

    /// <param name="runner">Ejecutor con los plazos.</param>
    /// <param name="path">Ruta para los registros.</param>
    /// <param name="content">Handle del que se lee.</param>
    /// <param name="guard">Primer handle, que se mantiene abierto sin <c>FILE_SHARE_DELETE</c> mientras se lee del segundo.</param>
    /// <param name="budget">Presupuesto de plazos del origen.</param>
    public SourceFileStream(SourceOperationRunner runner, string path, ISourceHandle content, ISourceHandle? guard, SourceBudget? budget = null)
    {
        _runner = runner;
        _path = path;
        _content = content;
        _guard = guard;
        _budget = budget;
        _blockBytes = Math.Max(1, runner.Options.ReadBlockBytes);
    }

    /// <summary>Si el búfer volvió al <i>pool</i> al cerrar (para pruebas).</summary>
    internal bool BufferReturnedToPool { get; private set; }

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
            // Tras un fallo la E/S cancelada puede seguir usando el búfer propio: no se reutiliza.
            throw new SourceAccessException(RunIssueCode.ReadError, "La lectura del archivo se interrumpió por un plazo vencido.");
        }

        if (buffer.IsEmpty)
        {
            return 0;
        }

        _buffer ??= ArrayPool<byte>.Shared.Rent(_blockBytes);
        var count = Math.Min(buffer.Length, Math.Min(_blockBytes, _buffer.Length));
        int read;
        Volatile.Write(ref _reading, 1);
        try
        {
            read = await _runner.ReadAsync(_content, _buffer.AsMemory(0, count), _position, _path, _budget, cancellationToken).ConfigureAwait(false);
        }
        catch (SourceFileApiException ex)
        {
            _faulted = true;
            throw SourceFileSystem.MapError(ex);
        }
        catch (IOException ex) when (ex.HResult == LockViolationHResult)
        {
            _faulted = true;
            throw new SourceAccessException(RunIssueCode.FileLocked, "Parte del archivo está bloqueada por otro proceso.", ex);
        }
        catch
        {
            _faulted = true;
            throw;
        }
        finally
        {
            Volatile.Write(ref _reading, 0);
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
            if (_buffer is { } owned && !_faulted && Volatile.Read(ref _reading) == 0 && !_runner.IsAbandoned)
            {
                ArrayPool<byte>.Shared.Return(owned);
                BufferReturnedToPool = true;
            }

            _buffer = null;
        }

        base.Dispose(disposing);
    }
}
