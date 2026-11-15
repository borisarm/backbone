using System.Runtime.CompilerServices;
using Backbone.Core.Storage;

namespace Backbone.Core.Tests.Support;

/// <summary>Cómo se comporta el almacenamiento a partir de la operación que falla.</summary>
public enum FaultMode
{
    /// <summary>Solo falla la operación N; las siguientes funcionan (error de red transitorio).</summary>
    Transient,

    /// <summary>
    /// Falla la operación N y todas las siguientes (proceso muerto o red cortada): ni siquiera la
    /// limpieza de mejor esfuerzo llega al disco. Una escritura que falla deja la mitad de sus bytes.
    /// </summary>
    Crash,

    /// <summary>En la operación N se cancela el token de la ejecución (RF-21).</summary>
    Cancel,
}

/// <summary>
/// Decorador de <see cref="IRepositoryStorage"/> que cuenta cada operación (incluidas cada escritura,
/// vaciado y lectura de los flujos) y hace fallar la N-ésima (§11, RNF-01).
/// </summary>
public sealed class FaultInjectingStorage(IRepositoryStorage inner) : IRepositoryStorage
{
    private readonly Lock gate = new();
    private readonly List<string> log = [];
    private int count;

    /// <summary>Operación (1-based) que falla; <see langword="null"/> para no fallar nunca.</summary>
    public int? FailAt { get; set; }

    public FaultMode Mode { get; set; } = FaultMode.Crash;

    /// <summary>Token que se cancela en modo <see cref="FaultMode.Cancel"/>.</summary>
    public CancellationTokenSource? CancellationSource { get; set; }

    public bool Tripped { get; private set; }

    public int OperationCount
    {
        get
        {
            lock (gate)
            {
                return count;
            }
        }
    }

    public IReadOnlyList<string> Log
    {
        get
        {
            lock (gate)
            {
                return [.. log];
            }
        }
    }

    public async Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
    {
        Step($"OpenRead {path}");
        return new FaultStream(this, await inner.OpenReadAsync(path, cancellationToken), path);
    }

    public async Task<Stream> CreateNewAsync(string path, CancellationToken cancellationToken)
    {
        Step($"CreateNew {path}");
        return new FaultStream(this, await inner.CreateNewAsync(path, cancellationToken), path);
    }

    public Task<bool> TryMoveAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken)
    {
        Step($"Move {sourcePath} -> {destinationPath}");
        return inner.TryMoveAsync(sourcePath, destinationPath, overwrite, cancellationToken);
    }

    public Task DeleteFileAsync(string path, CancellationToken cancellationToken)
    {
        Step($"Delete {path}");
        return inner.DeleteFileAsync(path, cancellationToken);
    }

    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        Step($"CreateDirectory {path}");
        return inner.CreateDirectoryAsync(path, cancellationToken);
    }

    public Task<bool> DirectoryExistsAsync(string path, CancellationToken cancellationToken)
    {
        Step($"DirectoryExists {path}");
        return inner.DirectoryExistsAsync(path, cancellationToken);
    }

    public Task<StorageEntry?> GetFileInfoAsync(string path, CancellationToken cancellationToken)
    {
        Step($"GetFileInfo {path}");
        return inner.GetFileInfoAsync(path, cancellationToken);
    }

    public async IAsyncEnumerable<StorageEntry> EnumerateAsync(string directoryPath, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Step($"Enumerate {directoryPath}");
        await foreach (var entry in inner.EnumerateAsync(directoryPath, cancellationToken))
        {
            yield return entry;
        }
    }

    /// <summary>Cuenta una operación; lanza si toca fallar. Devuelve si es la que falla (para escrituras parciales).</summary>
    private void Step(string operation, Action? beforeFailure = null)
    {
        int n;
        bool fail;
        lock (gate)
        {
            n = ++count;
            log.Add($"{n}: {operation}");
            if (Tripped && Mode == FaultMode.Crash)
            {
                throw new IOException($"Almacenamiento caído (operación {n}: {operation}).");
            }

            fail = FailAt == n;
            if (fail)
            {
                Tripped = true;
            }
        }

        if (!fail)
        {
            return;
        }

        if (Mode == FaultMode.Cancel)
        {
            (CancellationSource ?? throw new InvalidOperationException("Falta CancellationSource.")).Cancel();
            return;
        }

        beforeFailure?.Invoke();
        throw new IOException($"Fallo inyectado en la operación {n}: {operation}.");
    }

    private sealed class FaultStream(FaultInjectingStorage owner, Stream inner, string path) : Stream
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

        public override void Flush()
        {
            owner.Step($"Flush {path}");
            inner.Flush();
        }

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            owner.Step($"Flush {path}");
            await inner.FlushAsync(cancellationToken);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            owner.Step($"Read {path}");
            return inner.Read(buffer, offset, count);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            owner.Step($"Read {path}");
            return await inner.ReadAsync(buffer, cancellationToken);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            var data = buffer.AsMemory(offset, count);
            owner.Step($"Write {path}", () => inner.Write(data.Span[..(data.Length / 2)]));
            inner.Write(data.Span);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            // Escritura parcial: en un corte, la mitad de los bytes llega al disco.
            owner.Step($"Write {path}", () => inner.Write(buffer.Span[..(buffer.Length / 2)]));
            await inner.WriteAsync(buffer, cancellationToken);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            await base.DisposeAsync();
        }
    }
}
