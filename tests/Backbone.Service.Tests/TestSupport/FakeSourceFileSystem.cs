using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Backbone.Core.Engine;
using Backbone.Service.Sources;

namespace Backbone.Service.Tests.TestSupport;

/// <summary>
/// Doble de <see cref="IRunSourceFileSystem"/> (HU-14): carpetas y archivos en memoria (rutas de Windows, comparación ordinal)
/// con un gancho al abrir cada archivo para cancelar, detener o fallar en el punto exacto que busca la prueba.
/// </summary>
internal sealed class FakeSourceFileSystem : IRunSourceFileSystem
{
    private static readonly DateTime Modified = new(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly HashSet<string> _directories = new(StringComparer.Ordinal);
    private int _disposals;

    /// <summary>Se ha abandonado una operación (R12).</summary>
    public bool IsAbandoned { get; set; }

    /// <summary>Veces que se ha desechado (una por ejecución).</summary>
    public int Disposals => Volatile.Read(ref _disposals);

    /// <summary>Se llama al abrir cada archivo, con su ruta y el token de la ejecución (para cancelar o esperar).</summary>
    public Func<string, CancellationToken, Task>? OnOpenRead { get; set; }

    /// <summary>Rutas abiertas para leer, en orden.</summary>
    public ConcurrentQueue<string> Opened { get; } = new();

    /// <summary>Carpetas enumeradas, en orden.</summary>
    public ConcurrentQueue<string> Enumerated { get; } = new();

    public FakeSourceFileSystem AddDirectory(string path)
    {
        lock (_gate)
        {
            EnsureDirectory(path);
        }

        return this;
    }

    public FakeSourceFileSystem AddFile(string path, byte[] content)
    {
        lock (_gate)
        {
            EnsureDirectory(path[..path.LastIndexOf('\\')]);
            _files[path] = content;
        }

        return this;
    }

    public FakeSourceFileSystem AddFile(string path, string text) => AddFile(path, System.Text.Encoding.UTF8.GetBytes(text));

    public async IAsyncEnumerable<SourceEntry> EnumerateAsync(string directoryPath, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        Enumerated.Enqueue(directoryPath);
        List<SourceEntry> children;
        lock (_gate)
        {
            if (!_directories.Contains(directoryPath))
            {
                throw new DirectoryNotFoundException("The folder does not exist.");
            }

            var prefix = directoryPath.TrimEnd('\\') + "\\";
            children =
            [
                .. _directories.Where(d => IsChild(d, prefix))
                    .Select(d => new SourceEntry(d[prefix.Length..], d, true, 0, Modified, FileAttributes.Directory)),
                .. _files.Where(f => IsChild(f.Key, prefix))
                    .Select(f => new SourceEntry(f.Key[prefix.Length..], f.Key, false, f.Value.Length, Modified, FileAttributes.Archive)),
            ];
        }

        foreach (var child in children.OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            yield return child;
        }
    }

    public async Task<Stream> OpenReadAsync(string filePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Opened.Enqueue(filePath);
        if (OnOpenRead is { } hook)
        {
            await hook(filePath, cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        byte[] content;
        lock (_gate)
        {
            content = _files.TryGetValue(filePath, out var found) ? found : throw new FileNotFoundException("The file does not exist.");
        }

        return new MemoryStream(content, writable: false);
    }

    public Task<SourceEntry?> GetFileInfoAsync(string filePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult<SourceEntry?>(_files.TryGetValue(filePath, out var content)
                ? new SourceEntry(filePath[(filePath.LastIndexOf('\\') + 1)..], filePath, false, content.Length, Modified, FileAttributes.Archive)
                : null);
        }
    }

    public void Dispose() => Interlocked.Increment(ref _disposals);

    private static bool IsChild(string path, string prefix) =>
        path.StartsWith(prefix, StringComparison.Ordinal) && path.IndexOf('\\', prefix.Length) < 0;

    private void EnsureDirectory(string path)
    {
        if (!_directories.Add(path) || path.Length <= 3)
        {
            return;
        }

        var slash = path.LastIndexOf('\\');
        if (slash > 2)
        {
            EnsureDirectory(path[..slash]);
        }
        else if (slash == 2)
        {
            _directories.Add(path[..3]); // la raíz de la unidad, X:\
        }
    }
}

/// <summary>Doble de <see cref="ISourceFileSystemFactory"/>: entrega siempre el mismo <see cref="FakeSourceFileSystem"/> y anota los orígenes.</summary>
internal sealed class FakeSourceFileSystemFactory(FakeSourceFileSystem? source = null) : ISourceFileSystemFactory
{
    /// <summary>Lectura de los orígenes que recibe cada ejecución.</summary>
    public FakeSourceFileSystem Source { get; } = source ?? new FakeSourceFileSystem();

    /// <summary>Orígenes con los que se ha creado la lectura de cada ejecución.</summary>
    public ConcurrentQueue<IReadOnlyList<BackupSource>> Created { get; } = new();

    /// <summary>Posiciones de los orígenes marcados como no resueltos (M-B) con los que se ha creado la lectura de cada ejecución.</summary>
    public ConcurrentQueue<int[]> UnreadableSources { get; } = new();

    /// <summary>Si no es nulo, <see cref="Create"/> lanza esta excepción (p. ej. hilos abandonados, R12).</summary>
    public Exception? CreateError { get; set; }

    public IRunSourceFileSystem Create(IReadOnlyList<BackupSource> sources, IReadOnlyCollection<string>? snapshotDevices = null, IReadOnlyCollection<int>? unreadableSources = null)
    {
        if (CreateError is { } error)
        {
            throw error;
        }

        Created.Enqueue(sources);
        UnreadableSources.Enqueue([.. unreadableSources ?? []]);
        return Source;
    }
}
