using System.Runtime.CompilerServices;
using Backbone.Core.Storage;

namespace Backbone.Service.Nas;

/// <summary>
/// Decorador de <see cref="IRepositoryStorage"/> atado a una <see cref="ShareLease"/> (revisión de seguridad de HU-14, M-A):
/// en cuanto la concesión se revoca (la desecha su dueño o la parada del servicio cierra la conexión a la fuerza), toda
/// operación nueva, y toda lectura o escritura de un flujo ya abierto, lanza <see cref="ShareException"/> con
/// <see cref="Backbone.Contracts.BackboneErrorCode.Interrupted"/> <b>sin tocar la red</b>. Sin el registro de uso de la
/// conexión, esa E/S saldría con la cuenta del equipo (§7.5 A-3).
/// </summary>
internal sealed class RevocableRepositoryStorage(IRepositoryStorage inner, ShareLease lease) : IRepositoryStorage
{
    public async Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
    {
        lease.ThrowIfRevoked();
        var stream = await inner.OpenReadAsync(path, cancellationToken).ConfigureAwait(false);
        return new RevocableStream(stream, lease);
    }

    public async Task<Stream> CreateNewAsync(string path, CancellationToken cancellationToken)
    {
        lease.ThrowIfRevoked();
        var stream = await inner.CreateNewAsync(path, cancellationToken).ConfigureAwait(false);
        return new RevocableStream(stream, lease);
    }

    public Task<bool> TryMoveAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken)
    {
        lease.ThrowIfRevoked();
        return inner.TryMoveAsync(sourcePath, destinationPath, overwrite, cancellationToken);
    }

    public Task DeleteFileAsync(string path, CancellationToken cancellationToken)
    {
        lease.ThrowIfRevoked();
        return inner.DeleteFileAsync(path, cancellationToken);
    }

    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        lease.ThrowIfRevoked();
        return inner.CreateDirectoryAsync(path, cancellationToken);
    }

    public Task<bool> DirectoryExistsAsync(string path, CancellationToken cancellationToken)
    {
        lease.ThrowIfRevoked();
        return inner.DirectoryExistsAsync(path, cancellationToken);
    }

    public Task<StorageEntry?> GetFileInfoAsync(string path, CancellationToken cancellationToken)
    {
        lease.ThrowIfRevoked();
        return inner.GetFileInfoAsync(path, cancellationToken);
    }

    public async IAsyncEnumerable<StorageEntry> EnumerateAsync(string directoryPath, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        lease.ThrowIfRevoked();
        await foreach (var entry in inner.EnumerateAsync(directoryPath, cancellationToken).ConfigureAwait(false))
        {
            lease.ThrowIfRevoked();
            yield return entry;
        }
    }

    /// <summary>Flujo que deja de leer, escribir y vaciar en cuanto se revoca la concesión; cerrarlo sí se permite.</summary>
    private sealed class RevocableStream(Stream inner, ShareLease lease) : Stream
    {
        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => inner.CanWrite;

        public override long Length
        {
            get
            {
                lease.ThrowIfRevoked();
                return inner.Length;
            }
        }

        public override long Position
        {
            get
            {
                lease.ThrowIfRevoked();
                return inner.Position;
            }

            set
            {
                lease.ThrowIfRevoked();
                inner.Position = value;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            lease.ThrowIfRevoked();
            return inner.Read(buffer, offset, count);
        }

        public override int Read(Span<byte> buffer)
        {
            lease.ThrowIfRevoked();
            return inner.Read(buffer);
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            lease.ThrowIfRevoked();
            return await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            lease.ThrowIfRevoked();
            return await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            lease.ThrowIfRevoked();
            inner.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            lease.ThrowIfRevoked();
            inner.Write(buffer);
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            lease.ThrowIfRevoked();
            await inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            lease.ThrowIfRevoked();
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public override void Flush()
        {
            lease.ThrowIfRevoked();
            inner.Flush();
        }

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            lease.ThrowIfRevoked();
            await inner.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            lease.ThrowIfRevoked();
            return inner.Seek(offset, origin);
        }

        public override void SetLength(long value)
        {
            lease.ThrowIfRevoked();
            inner.SetLength(value);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                CloseInner(() => inner.Dispose());
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            try
            {
                await inner.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception) when (lease.IsRevoked)
            {
                // Con la concesión revocada, cerrar el flujo puede fallar al vaciar lo pendiente: ya no hay nada que salvar.
            }

            await base.DisposeAsync().ConfigureAwait(false);
        }

        private void CloseInner(Action close)
        {
            try
            {
                close();
            }
            catch (Exception) when (lease.IsRevoked)
            {
                // Como en DisposeAsync.
            }
        }
    }
}
