using System.Runtime.CompilerServices;

namespace Backbone.Core.Storage;

/// <summary>
/// <see cref="IRepositoryStorage"/> en memoria, para pruebas y simulaciones. Reproduce la semántica
/// relevante de NTFS/SMB: nombres sin distinción de mayúsculas, carpetas que deben existir antes de
/// crear archivos, renombrado atómico y contenido visible a medida que se escribe (un corte deja el
/// archivo a medias, como en disco).
/// </summary>
public sealed class InMemoryRepositoryStorage : IRepositoryStorage
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, MemoryFile> files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> directories = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider timeProvider;

    /// <param name="timeProvider">Reloj para las fechas de modificación; por defecto, el del sistema.</param>
    /// <param name="createRoot">Si la carpeta raíz existe desde el principio.</param>
    public InMemoryRepositoryStorage(TimeProvider? timeProvider = null, bool createRoot = true)
    {
        this.timeProvider = timeProvider ?? TimeProvider.System;
        if (createRoot)
        {
            directories.Add(string.Empty);
        }
    }

    /// <summary>Rutas de todos los archivos (para aserciones en pruebas).</summary>
    public IReadOnlyList<string> FilePaths
    {
        get
        {
            lock (gate)
            {
                return [.. files.Keys];
            }
        }
    }

    /// <summary>Rutas de todas las carpetas, sin la raíz.</summary>
    public IReadOnlyList<string> DirectoryPaths
    {
        get
        {
            lock (gate)
            {
                return [.. directories.Where(d => d.Length > 0)];
            }
        }
    }

    /// <summary>Copia del contenido actual de un archivo.</summary>
    /// <exception cref="FileNotFoundException">No existe.</exception>
    public byte[] GetFileContent(string path)
    {
        var key = Normalize(path, allowRoot: false);
        lock (gate)
        {
            return files.TryGetValue(key, out var file)
                ? file.Data.ToArray()
                : throw new FileNotFoundException("No existe el archivo.", path);
        }
    }

    /// <summary>Crea o sustituye un archivo directamente (sin pasar por <c>tmp/</c>); útil para preparar casos de prueba.</summary>
    public void SetFileContent(string path, ReadOnlySpan<byte> content)
    {
        var key = Normalize(path, allowRoot: false);
        lock (gate)
        {
            EnsureParentExists(key);
            var file = new MemoryFile { LastWriteUtc = Now() };
            file.Data.Write(content);
            files[key] = file;
        }
    }

    /// <summary>Cambia la fecha de modificación de un archivo existente.</summary>
    public void SetLastWriteUtc(string path, DateTime lastWriteUtc)
    {
        var key = Normalize(path, allowRoot: false);
        lock (gate)
        {
            if (!files.TryGetValue(key, out var file))
            {
                throw new FileNotFoundException("No existe el archivo.", path);
            }

            file.LastWriteUtc = lastWriteUtc;
        }
    }

    /// <inheritdoc/>
    public Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = Normalize(path, allowRoot: false);
        lock (gate)
        {
            if (!files.TryGetValue(key, out var file))
            {
                throw new FileNotFoundException("No existe el archivo.", path);
            }

            Stream stream = new MemoryStream(file.Data.ToArray(), writable: false);
            return Task.FromResult(stream);
        }
    }

    /// <inheritdoc/>
    public Task<Stream> CreateNewAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = Normalize(path, allowRoot: false);
        lock (gate)
        {
            EnsureParentExists(key);
            if (files.ContainsKey(key) || directories.Contains(key))
            {
                throw new IOException($"El archivo '{path}' ya existe.");
            }

            var file = new MemoryFile { LastWriteUtc = Now() };
            files.Add(key, file);
            Stream stream = new MemoryFileWriteStream(this, file);
            return Task.FromResult(stream);
        }
    }

    /// <inheritdoc/>
    public Task<bool> TryMoveAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = Normalize(sourcePath, allowRoot: false);
        var destination = Normalize(destinationPath, allowRoot: false);
        lock (gate)
        {
            if (!files.TryGetValue(source, out var file))
            {
                throw new FileNotFoundException("No existe el archivo que se iba a renombrar.", sourcePath);
            }

            EnsureParentExists(destination);
            if (directories.Contains(destination))
            {
                throw new IOException($"Ya existe una carpeta '{destinationPath}'.");
            }

            if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(true);
            }

            if (files.ContainsKey(destination) && !overwrite)
            {
                return Task.FromResult(false);
            }

            files.Remove(source);
            files[destination] = file;
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc/>
    public Task DeleteFileAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = Normalize(path, allowRoot: false);
        lock (gate)
        {
            files.Remove(key);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = Normalize(path, allowRoot: true);
        lock (gate)
        {
            var current = string.Empty;
            directories.Add(current);
            foreach (var segment in RepositoryPath.Validate(key, allowRoot: true))
            {
                current = current.Length == 0 ? segment : current + RepositoryPath.Separator + segment;
                if (files.ContainsKey(current))
                {
                    throw new IOException($"Ya existe un archivo '{current}'.");
                }

                directories.Add(current);
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<bool> DirectoryExistsAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = Normalize(path, allowRoot: true);
        lock (gate)
        {
            return Task.FromResult(directories.Contains(key));
        }
    }

    /// <inheritdoc/>
    public Task<StorageEntry?> GetFileInfoAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = Normalize(path, allowRoot: false);
        lock (gate)
        {
            return Task.FromResult(files.TryGetValue(key, out var file)
                ? new StorageEntry(NameOf(key), IsDirectory: false, file.Data.Length, file.LastWriteUtc)
                : null);
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<StorageEntry> EnumerateAsync(
        string directoryPath,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var key = Normalize(directoryPath, allowRoot: true);
        List<StorageEntry> entries;
        lock (gate)
        {
            entries = [];
            if (directories.Contains(key))
            {
                entries.AddRange(directories
                    .Where(d => d.Length > 0 && IsDirectChild(key, d))
                    .Select(d => new StorageEntry(NameOf(d), IsDirectory: true, 0, Now())));
                entries.AddRange(files
                    .Where(f => IsDirectChild(key, f.Key))
                    .Select(f => new StorageEntry(NameOf(f.Key), IsDirectory: false, f.Value.Data.Length, f.Value.LastWriteUtc)));
            }
        }

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return entry;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static string Normalize(string path, bool allowRoot)
    {
        RepositoryPath.Validate(path, allowRoot);
        return path;
    }

    private static string ParentOf(string key)
    {
        var index = key.LastIndexOf(RepositoryPath.Separator);
        return index < 0 ? string.Empty : key[..index];
    }

    private static string NameOf(string key) => key[(key.LastIndexOf(RepositoryPath.Separator) + 1)..];

    private static bool IsDirectChild(string directory, string candidate) =>
        string.Equals(ParentOf(candidate), directory, StringComparison.OrdinalIgnoreCase);

    private void EnsureParentExists(string key)
    {
        if (!directories.Contains(ParentOf(key)))
        {
            throw new DirectoryNotFoundException($"No existe la carpeta de '{key}'.");
        }
    }

    private DateTime Now() => timeProvider.GetUtcNow().UtcDateTime;

    private sealed class MemoryFile
    {
        public MemoryStream Data { get; } = new();

        public DateTime LastWriteUtc { get; set; }
    }

    private sealed class MemoryFileWriteStream(InMemoryRepositoryStorage owner, MemoryFile file) : Stream
    {
        private bool disposed;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => !disposed;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => ObjectDisposedException.ThrowIf(disposed, this);

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Flush();
            return Task.CompletedTask;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            lock (owner.gate)
            {
                file.Data.Write(buffer);
                file.LastWriteUtc = owner.Now();
            }
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        protected override void Dispose(bool disposing)
        {
            disposed = true;
            base.Dispose(disposing);
        }
    }
}
