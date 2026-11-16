using Nerdbank.Streams;

namespace Backbone.Service.Tests.TestSupport;

/// <summary>
/// Par de flujos en memoria cuyo extremo del servidor, al cerrarse, espera a que el cliente haya leído todo lo que el servidor
/// escribió. Equivale al cierre ordenado de un pipe (<c>WaitForPipeDrain</c>): sin esto, cerrar justo después de enviar la
/// respuesta <c>NotAuthorized</c> puede hacer que el cliente vea <c>ConnectionLostException</c> en vez de la respuesta, según
/// cómo reparta el planificador los hilos (las pruebas fallaban de forma intermitente bajo carga).
/// </summary>
internal static class DrainingStreamPair
{
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(20);

    public static (Stream Server, Stream Client) Create()
    {
        var (server, client) = FullDuplexStream.CreatePair();
        var counters = new Counters();
        return (new Side(server, counters, isServer: true), new Side(client, counters, isServer: false));
    }

    private sealed class Counters
    {
        public long ServerWritten;
        public long ClientRead;
    }

    private sealed class Side(Stream inner, Counters counters, bool isServer) : Stream
    {
        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => inner.CanWrite;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Count(await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false));

        public override void Write(byte[] buffer, int offset, int count)
        {
            Added(count);
            inner.Write(buffer, offset, count);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Added(buffer.Length);
            return inner.WriteAsync(buffer, cancellationToken);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Added(count);
            return inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override async ValueTask DisposeAsync()
        {
            if (isServer)
            {
                await DrainAsync().ConfigureAwait(false);
            }

            await inner.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (isServer)
                {
                    DrainAsync().GetAwaiter().GetResult();
                }

                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        private async Task DrainAsync()
        {
            // El cliente puede haberse ido ya (cierre abrupto): entonces no hay nadie que lea y no se espera.
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (Interlocked.Read(ref counters.ClientRead) < Interlocked.Read(ref counters.ServerWritten)
                   && deadline.Elapsed < DrainTimeout)
            {
                await Task.Delay(1).ConfigureAwait(false);
            }

            // Margen para que el bucle de lectura de JsonRpc del cliente despache la respuesta ya leída antes de ver el fin del flujo.
            await Task.Delay(Grace).ConfigureAwait(false);
        }

        private int Count(int read)
        {
            if (!isServer && read > 0)
            {
                Interlocked.Add(ref counters.ClientRead, read);
            }

            return read;
        }

        private void Added(int written)
        {
            if (isServer)
            {
                Interlocked.Add(ref counters.ServerWritten, written);
            }
        }
    }
}
