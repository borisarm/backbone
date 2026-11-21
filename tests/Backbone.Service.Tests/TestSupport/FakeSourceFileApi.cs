using System.Collections.Concurrent;
using Backbone.Service.Sources;

namespace Backbone.Service.Tests.TestSupport;

/// <summary>Dónde se bloquea una operación simulada (plazo, §8.2).</summary>
public enum FakeBlockAt
{
    None = 0,
    Open = 1,
    Enumerate = 2,
    Read = 3,
    ReOpen = 4,
}

/// <summary>Cómo se bloquea: atendiendo la cancelación o sin atenderla (un controlador que no responde, R12).</summary>
internal enum FakeBlockMode
{
    HonorCancellation = 0,
    IgnoreCancellation = 1,
}

/// <summary>Un elemento del origen simulado.</summary>
internal sealed class FakeNode
{
    public required string Win32Path { get; init; }

    public required string FinalNt { get; set; }

    public FileAttributes Attributes { get; set; } = FileAttributes.Archive;

    public uint ReparseTag { get; set; }

    /// <summary><c>EaSize</c> que da la enumeración (por defecto, la etiqueta si hay punto de reanálisis).</summary>
    public uint? EnumerationEaSize { get; set; }

    public SourceFileId Id { get; set; }

    public byte[] Content { get; set; } = [];

    public DateTime LastWriteTimeUtc { get; set; } = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    /// <summary>Error Win32 al abrirlo (p. ej. 5, 32).</summary>
    public int OpenError { get; set; }

    /// <summary>Error Win32 de <c>ReOpenFile</c> (p. ej. 1920, <c>ERROR_CANT_ACCESS_FILE</c>).</summary>
    public int ReOpenError { get; set; }

    /// <summary>Lo que se ve a través del segundo handle; por defecto el mismo elemento.</summary>
    public FakeNode? SeenThroughSecondHandle { get; set; }

    public bool FullyAllocated { get; set; } = true;

    /// <summary>Estado del marcador de nube (<c>CF_PLACEHOLDER_STATE</c>).</summary>
    public uint PlaceholderState { get; set; } = 0x1 | 0x8;

    public FakeBlockAt BlockAt { get; set; }

    public FakeBlockMode BlockMode { get; set; }

    public bool IsDirectory => (Attributes & FileAttributes.Directory) != 0;
}

/// <summary>
/// Doble de <see cref="ISourceFileApi"/> con un árbol en memoria: cuenta handles y aperturas y permite bloquear operaciones
/// para probar el plazo (§8.2).
/// </summary>
internal sealed class FakeSourceFileApi : ISourceFileApi, IDisposable
{
    public const string ReadPath = @"C:\src";
    public const string OpenRoot = @"\\?\C:\src";
    public const string RootFinalNt = @"\Device\HarddiskVolume3\src";

    private readonly ConcurrentDictionary<string, FakeNode> _nodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ManualResetEventSlim _release = new(false);
    private int _openHandles;
    private ulong _nextId = 100;

    public FakeSourceFileApi()
    {
        Root = AddNode(OpenRoot, RootFinalNt, FileAttributes.Directory);
    }

    public FakeNode Root { get; }

    public int OpenHandles => Volatile.Read(ref _openHandles);

    public ConcurrentQueue<(string Path, SourceOpenMode Mode)> Opens { get; } = new();

    public ConcurrentQueue<string> ReOpens { get; } = new();

    public ConcurrentQueue<string> AllocatedRangeQueries { get; } = new();

    /// <summary>Handles devueltos tarde (después de vencer el plazo).</summary>
    public int LateHandles { get; private set; }

    /// <summary>Entró en una operación bloqueada.</summary>
    public ManualResetEventSlim Blocked { get; } = new(false);

    public static string Child(string relative) => ReadPath + "\\" + relative;

    public FakeNode AddFile(string relative, byte[]? content = null, FileAttributes attributes = FileAttributes.Archive, uint tag = 0) =>
        AddNode(OpenRoot + "\\" + relative, RootFinalNt + "\\" + relative, attributes, tag, content);

    public FakeNode AddDirectory(string relative, FileAttributes attributes = FileAttributes.Directory, uint tag = 0) =>
        AddNode(OpenRoot + "\\" + relative, RootFinalNt + "\\" + relative, attributes | FileAttributes.Directory, tag);

    public FakeNode AddNode(string win32Path, string finalNt, FileAttributes attributes, uint tag = 0, byte[]? content = null)
    {
        var node = new FakeNode
        {
            Win32Path = win32Path,
            FinalNt = finalNt,
            Attributes = attributes,
            ReparseTag = tag,
            Content = content ?? [],
            Id = new SourceFileId(7, Interlocked.Increment(ref _nextId), 0),
        };
        _nodes[win32Path] = node;
        return node;
    }

    public ISourceHandle Open(string win32Path, SourceOpenMode mode, CancellationToken timeoutSignal)
    {
        Opens.Enqueue((win32Path, mode));
        if (!_nodes.TryGetValue(win32Path, out var node))
        {
            var parent = win32Path[..win32Path.LastIndexOf('\\')];
            throw new SourceFileApiException(_nodes.ContainsKey(parent) ? 2 : 3, "CreateFile");
        }

        if (node.BlockAt == FakeBlockAt.Open)
        {
            Block(node, timeoutSignal);
            if (timeoutSignal.IsCancellationRequested)
            {
                // Vuelve tarde con un handle: el ejecutor tiene que liberarlo.
                LateHandles++;
                return NewHandle(node, overlapped: false);
            }
        }

        if (node.OpenError != 0)
        {
            throw new SourceFileApiException(node.OpenError, "CreateFile");
        }

        return NewHandle(node, mode == SourceOpenMode.Content);
    }

    public uint GetPlaceholderState(FileAttributes attributes, uint reparseTag)
    {
        foreach (var node in _nodes.Values)
        {
            if (node.Attributes == attributes && node.ReparseTag == reparseTag)
            {
                return node.PlaceholderState;
            }
        }

        return 0x1;
    }

    public void Dispose()
    {
        // Desbloquea los hilos abandonados; no se desecha el evento porque pueden seguir esperándolo.
        _release.Set();
    }

    internal void Block(FakeNode node, CancellationToken timeoutSignal)
    {
        Blocked.Set();
        if (node.BlockMode == FakeBlockMode.HonorCancellation)
        {
            timeoutSignal.WaitHandle.WaitOne();
        }
        else
        {
            _release.Wait(CancellationToken.None); // no atiende la cancelación: solo vuelve al terminar la prueba
        }
    }

    internal IEnumerable<FakeNode> ChildrenOf(FakeNode directory)
    {
        var prefix = directory.Win32Path + "\\";
        return _nodes.Values.Where(n => n.Win32Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && n.Win32Path.IndexOf('\\', prefix.Length) < 0);
    }

    internal void HandleClosed() => Interlocked.Decrement(ref _openHandles);

    private FakeHandle NewHandle(FakeNode node, bool overlapped)
    {
        Interlocked.Increment(ref _openHandles);
        return new FakeHandle(this, node, overlapped);
    }

    private sealed class FakeHandle(FakeSourceFileApi api, FakeNode node, bool overlapped) : ISourceHandle
    {
        private bool _enumerated;
        private int _disposed;

        public bool Overlapped => overlapped;

        public SourceAttributeTag GetAttributeTag(CancellationToken timeoutSignal) => new(node.Attributes, node.ReparseTag);

        public string GetFinalPath(FinalPathFormat format, CancellationToken timeoutSignal) => format switch
        {
            FinalPathFormat.Nt => node.FinalNt,
            FinalPathFormat.Dos => node.FinalNt.Replace(@"\Device\HarddiskVolume3", @"\\?\C:", StringComparison.OrdinalIgnoreCase),
            _ => throw new SourceFileApiException(1, "GetFinalPathNameByHandle"),
        };

        public SourceFileId GetFileId(CancellationToken timeoutSignal) => node.Id;

        public SourceFileTimes GetSizeAndTime(CancellationToken timeoutSignal) => new(node.IsDirectory ? 0 : node.Content.Length, node.LastWriteTimeUtc);

        public bool ReadDirectory(List<RawDirectoryEntry> entries, CancellationToken timeoutSignal)
        {
            if (node.BlockAt == FakeBlockAt.Enumerate)
            {
                api.Block(node, timeoutSignal);
            }

            if (_enumerated)
            {
                return false;
            }

            _enumerated = true;
            entries.Add(new RawDirectoryEntry(".", FileAttributes.Directory, 0, 0, node.LastWriteTimeUtc));
            entries.Add(new RawDirectoryEntry("..", FileAttributes.Directory, 0, 0, node.LastWriteTimeUtc));
            foreach (var child in api.ChildrenOf(node))
            {
                var name = child.Win32Path[(child.Win32Path.LastIndexOf('\\') + 1)..];
                var ea = child.EnumerationEaSize ?? ((child.Attributes & FileAttributes.ReparsePoint) != 0 ? child.ReparseTag : 0u);
                entries.Add(new RawDirectoryEntry(name, child.Attributes, ea, child.IsDirectory ? 0 : child.Content.Length, child.LastWriteTimeUtc));
            }

            return true;
        }

        public ISourceHandle ReOpenForContent(CancellationToken timeoutSignal)
        {
            api.ReOpens.Enqueue(node.Win32Path);
            if (node.BlockAt == FakeBlockAt.ReOpen)
            {
                api.Block(node, timeoutSignal);
            }

            if (node.ReOpenError != 0)
            {
                throw new SourceFileApiException(node.ReOpenError, "ReOpenFile");
            }

            return api.NewHandle(node.SeenThroughSecondHandle ?? node, overlapped: true);
        }

        public bool IsFullyAllocated(long length, CancellationToken timeoutSignal)
        {
            api.AllocatedRangeQueries.Enqueue(node.Win32Path);
            return node.FullyAllocated;
        }

        public void CancelIo()
        {
        }

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, long fileOffset, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (node.BlockAt == FakeBlockAt.Read)
            {
                api.Blocked.Set();
                if (node.BlockMode == FakeBlockMode.HonorCancellation)
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await Task.Run(() => api._release.Wait(), CancellationToken.None).ConfigureAwait(false);
                }
            }

            if (fileOffset >= node.Content.Length)
            {
                return 0;
            }

            var count = (int)Math.Min(buffer.Length, node.Content.Length - fileOffset);
            node.Content.AsSpan((int)fileOffset, count).CopyTo(buffer.Span);
            return count;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                api.HandleClosed();
            }
        }
    }
}
