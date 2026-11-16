using System.Runtime.CompilerServices;
using Backbone.Core.Engine;

namespace Backbone.Core.Tests.Support;

/// <summary>
/// Origen en memoria e instrumentado (HU-13 criterio 2): cuenta cada apertura de archivo, enumeración y consulta de
/// metadatos, y permite inyectar fallos al abrir, al leer o al enumerar, y cambiar un archivo a mitad de su lectura.
/// Las rutas son las de Windows (<c>\</c>), con comparación ordinal (admite carpetas con distinción de mayúsculas).
/// </summary>
public sealed class InMemorySourceFileSystem : ISourceFileSystem
{
    public static readonly DateTime DefaultModified = new(2026, 1, 31, 10, 22, 1, DateTimeKind.Utc);

    private readonly Lock gate = new();
    private readonly Dictionary<string, Node> nodes = new(StringComparer.Ordinal);
    private readonly List<string> opened = [];
    private int enumerations;
    private int infoQueries;

    /// <summary>Rutas de los archivos abiertos, en orden.</summary>
    public IReadOnlyList<string> Opened
    {
        get
        {
            lock (gate)
            {
                return [.. opened];
            }
        }
    }

    public int OpenCount => Opened.Count;

    public int EnumerationCount
    {
        get
        {
            lock (gate)
            {
                return enumerations;
            }
        }
    }

    public int InfoQueryCount
    {
        get
        {
            lock (gate)
            {
                return infoQueries;
            }
        }
    }

    /// <summary>Se llama en cada enumeración con la carpeta (para cancelar, avanzar el reloj…).</summary>
    public Action<string>? OnEnumerate { get; set; }

    public void ResetCounters()
    {
        lock (gate)
        {
            opened.Clear();
            enumerations = 0;
            infoQueries = 0;
        }
    }

    public void AddDirectory(string path)
    {
        lock (gate)
        {
            EnsureDirectory(path);
        }
    }

    public void AddFile(string path, byte[] content, DateTime? modifiedUtc = null, FileAttributes attributes = FileAttributes.Archive)
    {
        lock (gate)
        {
            var parent = EnsureDirectory(ParentOf(path));
            var name = NameOf(path);
            var node = new Node(path, isDirectory: false) { Content = content, ModifiedUtc = modifiedUtc ?? DefaultModified, Attributes = attributes };
            parent.Children[name] = node;
            nodes[path] = node;
        }
    }

    /// <summary>Cambia el contenido (y la fecha, por defecto un segundo más tarde), como al guardar el archivo.</summary>
    public void Modify(string path, byte[] content, DateTime? modifiedUtc = null)
    {
        lock (gate)
        {
            var node = nodes[path];
            node.Content = content;
            node.ModifiedUtc = modifiedUtc ?? node.ModifiedUtc.AddSeconds(1);
        }
    }

    public void SetAttributes(string path, FileAttributes attributes)
    {
        lock (gate)
        {
            nodes[path].Attributes = attributes;
        }
    }

    public void SetModified(string path, DateTime modifiedUtc)
    {
        lock (gate)
        {
            nodes[path].ModifiedUtc = modifiedUtc;
        }
    }

    public void Delete(string path)
    {
        lock (gate)
        {
            nodes.Remove(path);
            if (nodes.TryGetValue(ParentOf(path), out var parent))
            {
                parent.Children.Remove(NameOf(path));
            }
        }
    }

    /// <summary>La apertura del archivo falla con <paramref name="error"/> (<see langword="null"/> para quitar el fallo).</summary>
    public void FailOpen(string path, Exception? error)
    {
        lock (gate)
        {
            nodes[path].OpenError = error;
        }
    }

    /// <summary>La lectura falla con <paramref name="error"/> después de <paramref name="afterBytes"/> bytes.</summary>
    public void FailRead(string path, long afterBytes, Exception? error)
    {
        lock (gate)
        {
            var node = nodes[path];
            node.ReadError = error;
            node.ReadErrorAfter = afterBytes;
        }
    }

    /// <summary>La enumeración de la carpeta falla con <paramref name="error"/>.</summary>
    public void FailEnumerate(string path, Exception? error)
    {
        lock (gate)
        {
            EnsureDirectory(path).EnumerateError = error;
        }
    }

    /// <summary>Se llama una vez, después de la primera lectura del archivo (para cambiarlo mientras se copia).</summary>
    public void OnFirstRead(string path, Action action)
    {
        lock (gate)
        {
            nodes[path].AfterFirstRead = action;
        }
    }

    /// <summary>Marca un elemento como punto de reanálisis.</summary>
    public void MakeReparsePoint(string path)
    {
        lock (gate)
        {
            nodes[path].Attributes |= FileAttributes.ReparsePoint;
        }
    }

    public byte[] Content(string path)
    {
        lock (gate)
        {
            return nodes[path].Content;
        }
    }

    /// <summary>Archivos bajo <paramref name="root"/> con su ruta relativa (con <c>/</c>) y contenido.</summary>
    public IReadOnlyList<(string RelativePath, byte[] Content)> FilesUnder(string root)
    {
        lock (gate)
        {
            var prefix = root + "\\";
            return [.. nodes.Values
                .Where(n => !n.IsDirectory && n.Path.StartsWith(prefix, StringComparison.Ordinal))
                .Select(n => (n.Path[prefix.Length..].Replace('\\', '/'), n.Content))];
        }
    }

    public async IAsyncEnumerable<SourceEntry> EnumerateAsync(string directoryPath, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        List<SourceEntry> children;
        lock (gate)
        {
            enumerations++;
        }

        OnEnumerate?.Invoke(directoryPath);
        lock (gate)
        {
            if (!nodes.TryGetValue(directoryPath, out var node) || !node.IsDirectory)
            {
                throw new DirectoryNotFoundException($"No existe la carpeta {directoryPath}.");
            }

            if (node.EnumerateError is not null)
            {
                throw node.EnumerateError;
            }

            children = [.. node.Children.Values.Select(c => c.ToEntry())];
        }

        foreach (var child in children)
        {
            yield return child;
        }
    }

    public Task<Stream> OpenReadAsync(string filePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            opened.Add(filePath);
            if (!nodes.TryGetValue(filePath, out var node) || node.IsDirectory)
            {
                throw new FileNotFoundException($"No existe el archivo {filePath}.");
            }

            if (node.OpenError is not null)
            {
                throw node.OpenError;
            }

            return Task.FromResult<Stream>(new NodeStream(this, node));
        }
    }

    public Task<SourceEntry?> GetFileInfoAsync(string filePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            infoQueries++;
            return Task.FromResult(nodes.TryGetValue(filePath, out var node) && !node.IsDirectory ? node.ToEntry() : null);
        }
    }

    private static string ParentOf(string path) => path[..path.LastIndexOf('\\')];

    private static string NameOf(string path) => path[(path.LastIndexOf('\\') + 1)..];

    private Node EnsureDirectory(string path)
    {
        if (nodes.TryGetValue(path, out var existing))
        {
            return existing;
        }

        var node = new Node(path, isDirectory: true);
        nodes[path] = node;
        var slash = path.LastIndexOf('\\');
        if (slash > 0 && path[..slash] is var parentPath && !parentPath.EndsWith(':'))
        {
            EnsureDirectory(parentPath).Children[path[(slash + 1)..]] = node;
        }

        return node;
    }

    private sealed class Node(string path, bool isDirectory)
    {
        public string Path { get; } = path;

        public bool IsDirectory { get; } = isDirectory;

        public Dictionary<string, Node> Children { get; } = new(StringComparer.Ordinal);

        public byte[] Content { get; set; } = [];

        public DateTime ModifiedUtc { get; set; } = DefaultModified;

        public FileAttributes Attributes { get; set; } = FileAttributes.Directory;

        public Exception? OpenError { get; set; }

        public Exception? ReadError { get; set; }

        public long ReadErrorAfter { get; set; }

        public Exception? EnumerateError { get; set; }

        public Action? AfterFirstRead { get; set; }

        public SourceEntry ToEntry() => new(
            System.IO.Path.GetFileName(Path),
            Path,
            IsDirectory,
            IsDirectory ? 0 : Content.Length,
            ModifiedUtc,
            IsDirectory ? Attributes | FileAttributes.Directory : Attributes);
    }

    /// <summary>Lee el contenido <b>actual</b> del nodo en cada llamada, como un archivo que otro proceso está escribiendo.</summary>
    private sealed class NodeStream(InMemorySourceFileSystem owner, Node node) : Stream
    {
        private long position;
        private bool firstReadDone;

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

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int read;
            Action? after = null;
            lock (owner.gate)
            {
                if (node.ReadError is not null && position >= node.ReadErrorAfter)
                {
                    throw node.ReadError;
                }

                var content = node.Content;
                var available = (int)Math.Max(0, Math.Min(buffer.Length, content.Length - position));
                if (node.ReadError is not null)
                {
                    available = (int)Math.Min(available, node.ReadErrorAfter - position);
                }

                content.AsSpan((int)Math.Min(position, content.Length), available).CopyTo(buffer);
                position += available;
                read = available;
                if (!firstReadDone)
                {
                    firstReadDone = true;
                    after = node.AfterFirstRead;
                    node.AfterFirstRead = null;
                }
            }

            after?.Invoke();
            return read;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
