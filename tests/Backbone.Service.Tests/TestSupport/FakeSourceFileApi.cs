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
    Attributes = 5,
    FinalPath = 6,
    FileId = 7,
    Metadata = 8,
    AllocatedRanges = 9,
    Device = 10,
}

/// <summary>Operación sobre un handle simulado en la que se puede inyectar un fallo.</summary>
public enum FakeOp
{
    Open = 0,
    Attributes = 1,
    FinalPath = 2,
    FileId = 3,
    Metadata = 4,
    ReadDirectory = 5,
    ReOpen = 6,
    AllocatedRanges = 7,
    Device = 8,
}

/// <summary>Cómo se bloquea: atendiendo la cancelación o sin atenderla (un controlador que no responde, R12).</summary>
internal enum FakeBlockMode
{
    HonorCancellation = 0,
    IgnoreCancellation = 1,

    /// <summary>Espera a <see cref="FakeSourceFileApi.ReleaseBlocked"/> sin atender la cancelación (operación que tarda N segundos).</summary>
    WaitForRelease = 2,
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

    /// <summary><c>FILE_FS_DEVICE_INFORMATION</c> que da el handle (por defecto, un disco local).</summary>
    public SourceDeviceInfo Device { get; set; } = new(SourceDeviceInfo.FileDeviceDisk, 0);

    /// <summary>Atributos que se ven en la segunda lectura de <c>FileAttributeTagInfo</c> del mismo handle (B-1).</summary>
    public Func<int, SourceAttributeTag?>? AttributeTagOnCall { get; set; }

    /// <summary>Estado del marcador de nube (<c>CF_PLACEHOLDER_STATE</c>).</summary>
    public uint PlaceholderState { get; set; } = 0x1 | 0x8;

    public FakeBlockAt BlockAt { get; set; }

    public FakeBlockMode BlockMode { get; set; }

    /// <summary>Errores Win32 por operación (inyección de fallos), además de <see cref="OpenError"/> y <see cref="ReOpenError"/>.</summary>
    public Dictionary<FakeOp, int> Faults { get; } = [];

    /// <summary>Entradas por llamada de enumeración (0 = todas en una).</summary>
    public int EnumerationBatchSize { get; set; }

    /// <summary>Con <see cref="FakeOp.ReadDirectory"/>, a partir de qué llamada (1 = la primera) falla.</summary>
    public int ReadDirectoryFaultFromCall { get; set; } = 1;

    /// <summary>Excepción de la lectura que empieza en ese desplazamiento (null = no falla).</summary>
    public Func<long, Exception?>? ReadFault { get; set; }

    public bool IsDirectory => (Attributes & FileAttributes.Directory) != 0;

    internal void ThrowIfFault(FakeOp op, string operation)
    {
        if (Faults.TryGetValue(op, out var error))
        {
            throw new SourceFileApiException(error, operation);
        }
    }
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

    /// <summary>Raíz NT del volumen C: simulado.</summary>
    public const string VolumeNtRoot = @"\??\C:\";

    private readonly ConcurrentDictionary<string, FakeNode> _nodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ManualResetEventSlim _release = new(false);
    private int _openHandles;
    private ulong _nextId = 100;

    public FakeSourceFileApi()
    {
        Volume = AddNode(@"\\?\C:", @"\Device\HarddiskVolume3\", FileAttributes.Directory | FileAttributes.Hidden | FileAttributes.System);
        VolumeRoots[VolumeNtRoot] = Volume;
        Root = AddNode(OpenRoot, RootFinalNt, FileAttributes.Directory);
    }

    /// <summary>Raíz del volumen C: simulado (<c>\??\C:\</c>).</summary>
    public FakeNode Volume { get; }

    public FakeNode Root { get; }

    /// <summary>Raíces de volumen que se pueden abrir por ruta NT, y su nodo.</summary>
    public ConcurrentDictionary<string, FakeNode> VolumeRoots { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int OpenHandles => Volatile.Read(ref _openHandles);

    /// <summary>Aperturas relativas (<see cref="OpenChild"/>): ruta del hijo y modo.</summary>
    public ConcurrentQueue<(string Path, SourceOpenMode Mode)> Opens { get; } = new();

    /// <summary>Aperturas de raíces de volumen por ruta NT (<see cref="OpenVolumeRoot"/>).</summary>
    public ConcurrentQueue<string> VolumeOpens { get; } = new();

    /// <summary>Padres usados en cada apertura relativa (ruta del padre, nombre).</summary>
    public ConcurrentQueue<(string Parent, string Name)> RelativeOpens { get; } = new();

    public ConcurrentQueue<string> ReOpens { get; } = new();

    public ConcurrentQueue<string> AllocatedRangeQueries { get; } = new();

    /// <summary>Longitud pedida en cada consulta de rangos asignados.</summary>
    public ConcurrentQueue<long> AllocatedRangeLengths { get; } = new();

    private int _readCalls;
    private int _readDirectoryCalls;

    /// <summary>Lecturas de contenido que han llegado a algún handle (un marcador de nube nunca debe recibir ninguna).</summary>
    public int ReadCalls => Volatile.Read(ref _readCalls);

    /// <summary>Llamadas de enumeración que han llegado a algún handle.</summary>
    public int ReadDirectoryCalls => Volatile.Read(ref _readDirectoryCalls);

    private readonly ManualResetEventSlim _gate = new(false);

    /// <summary>Libera las operaciones en <see cref="FakeBlockMode.WaitForRelease"/>.</summary>
    public void ReleaseBlocked() => _gate.Set();

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

    public ISourceHandle OpenVolumeRoot(string ntRootPath, CancellationToken timeoutSignal)
    {
        VolumeOpens.Enqueue(ntRootPath);
        if (!VolumeRoots.TryGetValue(ntRootPath, out var node))
        {
            throw new SourceFileApiException(3, "NtCreateFile", unchecked((int)0xC000_003A));
        }

        node.ThrowIfFault(FakeOp.Open, "NtCreateFile");
        return NewHandle(node, overlapped: false);
    }

    public ISourceHandle OpenChild(ISourceHandle parent, string name, SourceOpenMode mode, CancellationToken timeoutSignal)
    {
        if (parent is not FakeHandle parentHandle || parentHandle.IsDisposed)
        {
            throw new InvalidOperationException("The parent handle is not an open fake handle.");
        }

        if (name.Length == 0 || name.Contains('\\', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A relative open must name a single component.");
        }

        var win32Path = parentHandle.Node.Win32Path + "\\" + name;
        RelativeOpens.Enqueue((parentHandle.Node.Win32Path, name));
        return Open(win32Path, mode, timeoutSignal);
    }

    private FakeHandle Open(string win32Path, SourceOpenMode mode, CancellationToken timeoutSignal)
    {
        Opens.Enqueue((win32Path, mode));
        if (!_nodes.TryGetValue(win32Path, out var node))
        {
            throw new SourceFileApiException(2, "NtCreateFile", unchecked((int)0xC000_0034));
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

        node.ThrowIfFault(FakeOp.Open, "NtCreateFile");

        // FILE_DIRECTORY_FILE y FILE_NON_DIRECTORY_FILE (§8.2 "Apertura relativa").
        if (mode == SourceOpenMode.Directory && !node.IsDirectory)
        {
            throw new SourceFileApiException(267, "NtCreateFile", SourceFileApiException.StatusNotADirectory);
        }

        if (mode == SourceOpenMode.Content && node.IsDirectory)
        {
            throw new SourceFileApiException(5, "NtCreateFile", SourceFileApiException.StatusFileIsADirectory);
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
        if (node.BlockMode == FakeBlockMode.WaitForRelease)
        {
            WaitHandle.WaitAny([_gate.WaitHandle, _release.WaitHandle]);
        }
        else if (node.BlockMode == FakeBlockMode.HonorCancellation)
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
        private List<RawDirectoryEntry>? _pending;
        private int _taken;
        private int _calls;
        private int _tagCalls;
        private int _disposed;

        public bool Overlapped => overlapped;

        public FakeNode Node => node;

        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public SourceAttributeTag GetAttributeTag(CancellationToken timeoutSignal)
        {
            Gate(FakeBlockAt.Attributes, FakeOp.Attributes, "GetFileInformationByHandleEx(FileAttributeTagInfo)", timeoutSignal);
            var call = Interlocked.Increment(ref _tagCalls);
            return node.AttributeTagOnCall?.Invoke(call) ?? new(node.Attributes, node.ReparseTag);
        }

        public SourceDeviceInfo GetDeviceInfo(CancellationToken timeoutSignal)
        {
            Gate(FakeBlockAt.Device, FakeOp.Device, "NtQueryVolumeInformationFile", timeoutSignal);
            return node.Device;
        }

        public string GetFinalPath(FinalPathFormat format, CancellationToken timeoutSignal)
        {
            Gate(FakeBlockAt.FinalPath, FakeOp.FinalPath, "GetFinalPathNameByHandle", timeoutSignal);
            return FinalPathCore(format);
        }

        private void Gate(FakeBlockAt at, FakeOp op, string operation, CancellationToken timeoutSignal)
        {
            if (node.BlockAt == at)
            {
                api.Block(node, timeoutSignal);
            }

            node.ThrowIfFault(op, operation);
        }

        private string FinalPathCore(FinalPathFormat format) => format switch
        {
            FinalPathFormat.Nt => node.FinalNt,
            FinalPathFormat.Dos => node.FinalNt.Replace(@"\Device\HarddiskVolume3", @"\\?\C:", StringComparison.OrdinalIgnoreCase),
            _ => throw new SourceFileApiException(1, "GetFinalPathNameByHandle"),
        };

        public SourceFileId GetFileId(CancellationToken timeoutSignal)
        {
            Gate(FakeBlockAt.FileId, FakeOp.FileId, "GetFileInformationByHandleEx(FileIdInfo)", timeoutSignal);
            return node.Id;
        }

        public SourceFileTimes GetSizeAndTime(CancellationToken timeoutSignal)
        {
            Gate(FakeBlockAt.Metadata, FakeOp.Metadata, "GetFileInformationByHandleEx(FileStandardInfo)", timeoutSignal);
            return new(node.IsDirectory ? 0 : node.Content.Length, node.LastWriteTimeUtc);
        }

        public bool ReadDirectory(List<RawDirectoryEntry> entries, bool restart, CancellationToken timeoutSignal)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            if (restart)
            {
                _pending = null;
                _taken = 0;
            }

            Interlocked.Increment(ref api._readDirectoryCalls);
            _calls++;
            if (node.BlockAt == FakeBlockAt.Enumerate)
            {
                api.Block(node, timeoutSignal);
            }

            if (_calls >= node.ReadDirectoryFaultFromCall)
            {
                node.ThrowIfFault(FakeOp.ReadDirectory, "GetFileInformationByHandleEx(FileIdBothDirectoryInfo)");
            }

            _pending ??= BuildListing();
            if (_taken >= _pending.Count)
            {
                return false;
            }

            var count = node.EnumerationBatchSize <= 0 ? _pending.Count : Math.Min(node.EnumerationBatchSize, _pending.Count - _taken);
            entries.AddRange(_pending.GetRange(_taken, count));
            _taken += count;
            return true;
        }

        private List<RawDirectoryEntry> BuildListing()
        {
            var list = new List<RawDirectoryEntry>
            {
                new(".", FileAttributes.Directory, 0, 0, node.LastWriteTimeUtc),
                new("..", FileAttributes.Directory, 0, 0, node.LastWriteTimeUtc),
            };
            foreach (var child in api.ChildrenOf(node))
            {
                var name = child.Win32Path[(child.Win32Path.LastIndexOf('\\') + 1)..];
                var ea = child.EnumerationEaSize ?? ((child.Attributes & FileAttributes.ReparsePoint) != 0 ? child.ReparseTag : 0u);
                list.Add(new RawDirectoryEntry(name, child.Attributes, ea, child.IsDirectory ? 0 : child.Content.Length, child.LastWriteTimeUtc));
            }

            return list;
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

            node.ThrowIfFault(FakeOp.ReOpen, "ReOpenFile");

            return api.NewHandle(node.SeenThroughSecondHandle ?? node, overlapped: true);
        }

        public bool IsFullyAllocated(long length, CancellationToken timeoutSignal)
        {
            api.AllocatedRangeQueries.Enqueue(node.Win32Path);
            api.AllocatedRangeLengths.Enqueue(length);
            Gate(FakeBlockAt.AllocatedRanges, FakeOp.AllocatedRanges, "DeviceIoControl(FSCTL_QUERY_ALLOCATED_RANGES)", timeoutSignal);
            return node.FullyAllocated;
        }

        public void CancelIo()
        {
        }

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, long fileOffset, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            Interlocked.Increment(ref api._readCalls);
            if (node.ReadFault?.Invoke(fileOffset) is { } fault)
            {
                throw fault;
            }

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
