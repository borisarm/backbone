namespace Backbone.Service.Sources;

/// <summary>Cómo se abre un elemento de origen (§8.2 "Lectura de los orígenes").</summary>
internal enum SourceOpenMode
{
    /// <summary>Carpeta para enumerarla: <c>FILE_LIST_DIRECTORY</c>, E/S síncrona.</summary>
    Directory = 0,

    /// <summary>Solo metadatos (<c>GetFileInfoAsync</c>): <c>FILE_READ_ATTRIBUTES</c>, sin acceso a datos.</summary>
    Metadata = 1,

    /// <summary>Contenido (<c>OpenReadAsync</c>): <c>GENERIC_READ</c> con <c>FILE_FLAG_OVERLAPPED</c>.</summary>
    Content = 2,
}

/// <summary>Formato de la ruta final que devuelve <c>GetFinalPathNameByHandle</c> (siempre normalizada).</summary>
internal enum FinalPathFormat
{
    /// <summary><c>VOLUME_NAME_NT</c>: <c>\Device\HarddiskVolume3\…</c>; vale también en una instantánea VSS.</summary>
    Nt = 0,

    /// <summary><c>VOLUME_NAME_DOS</c>: <c>\\?\C:\…</c>.</summary>
    Dos = 1,

    /// <summary><c>VOLUME_NAME_GUID</c>: <c>\\?\Volume{…}\…</c>.</summary>
    Guid = 2,
}

/// <summary>Atributos y etiqueta de reanálisis leídos sobre un handle (<c>FileAttributeTagInfo</c>).</summary>
internal readonly record struct SourceAttributeTag(FileAttributes Attributes, uint ReparseTag)
{
    public bool IsReparsePoint => (Attributes & FileAttributes.ReparsePoint) != 0;

    public bool IsDirectory => (Attributes & FileAttributes.Directory) != 0;
}

/// <summary>Identificador de archivo (<c>FileIdInfo</c>): número de serie del volumen e identificador de 128 bits.</summary>
internal readonly record struct SourceFileId(ulong VolumeSerialNumber, ulong Low, ulong High);

/// <summary>Tamaño y última modificación de un elemento abierto.</summary>
internal readonly record struct SourceFileTimes(long Length, DateTime LastWriteTimeUtc);

/// <summary>Una entrada de <c>FILE_ID_BOTH_DIR_INFO</c>.</summary>
/// <param name="Name">Nombre tal cual lo da el sistema.</param>
/// <param name="Attributes">Atributos tal cual.</param>
/// <param name="EaSize">
/// <c>EaSize</c>: con <c>FILE_ATTRIBUTE_REPARSE_POINT</c>, la etiqueta de reanálisis (§8.2); si no, el tamaño de los EA.
/// </param>
/// <param name="Length"><c>EndOfFile</c>.</param>
/// <param name="LastWriteTimeUtc"><c>LastWriteTime</c> en UTC.</param>
internal readonly record struct RawDirectoryEntry(string Name, FileAttributes Attributes, uint EaSize, long Length, DateTime LastWriteTimeUtc);

/// <summary>
/// Operaciones del sistema que usa <see cref="SourceFileSystem"/> (HU-08, §8.2), detrás de una interfaz para simularlas en
/// pruebas. Todas son <b>síncronas</b> salvo <see cref="ISourceHandle.ReadAsync"/>: <see cref="SourceFileSystem"/> las
/// ejecuta en su hilo propio con plazo (<see cref="SourceOperationRunner"/>). Los errores del sistema se lanzan como
/// <see cref="SourceFileApiException"/> con el código Win32.
/// </summary>
/// <remarks>
/// El <see cref="CancellationToken"/> que reciben las operaciones síncronas se cancela cuando vence el plazo. La
/// implementación real no lo usa (la desbloquea <c>CancelSynchronousIo</c> o <c>CancelIoEx</c>); lo usan los simuladores.
/// </remarks>
internal interface ISourceFileApi
{
    /// <summary>
    /// Abre por ruta con <c>FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS</c> (nunca sigue un punto de
    /// reanálisis en el último componente) y compartición de lectura y escritura <b>sin</b> <c>FILE_SHARE_DELETE</c>.
    /// </summary>
    ISourceHandle Open(string win32Path, SourceOpenMode mode, CancellationToken timeoutSignal);

    /// <summary><c>CfGetPlaceholderStateFromAttributeTag</c>; <c>CF_PLACEHOLDER_STATE_INVALID</c> si no se puede saber.</summary>
    uint GetPlaceholderState(FileAttributes attributes, uint reparseTag);
}

/// <summary>Handle abierto de un elemento de origen. Todo se consulta sobre él, nunca volviendo a abrir la ruta.</summary>
internal interface ISourceHandle : IDisposable
{
    SourceAttributeTag GetAttributeTag(CancellationToken timeoutSignal);

    string GetFinalPath(FinalPathFormat format, CancellationToken timeoutSignal);

    SourceFileId GetFileId(CancellationToken timeoutSignal);

    SourceFileTimes GetSizeAndTime(CancellationToken timeoutSignal);

    /// <summary>
    /// Siguiente lote de la enumeración (<c>FileIdBothDirectoryInfo</c>) desde este handle; <see langword="false"/> cuando
    /// no quedan más (<c>ERROR_NO_MORE_FILES</c>).
    /// </summary>
    bool ReadDirectory(List<RawDirectoryEntry> entries, CancellationToken timeoutSignal);

    /// <summary>
    /// Segundo handle para leer el contenido de la lista blanca (§8.2 "Apertura para leer el contenido", punto 2):
    /// <c>ReOpenFile</c> sin <c>FILE_FLAG_OPEN_REPARSE_POINT</c>, con <c>FILE_FLAG_OVERLAPPED</c>. Si el filtro propietario no
    /// está en la pila, <see cref="SourceFileApiException"/> con <c>ERROR_CANT_ACCESS_FILE</c>.
    /// </summary>
    ISourceHandle ReOpenForContent(CancellationToken timeoutSignal);

    /// <summary>
    /// <see langword="true"/> si <c>FSCTL_QUERY_ALLOCATED_RANGES</c> cubre todo <c>[0, length)</c> sin huecos (§8.2, R4).
    /// </summary>
    bool IsFullyAllocated(long length, CancellationToken timeoutSignal);

    /// <summary><c>CancelIoEx</c>: cancela la E/S pendiente del handle (la llama el plazo, desde otro hilo).</summary>
    void CancelIo();

    /// <summary>Lee un bloque en <paramref name="fileOffset"/> (E/S asíncrona; la cancelación llama a <c>CancelIoEx</c>).</summary>
    ValueTask<int> ReadAsync(Memory<byte> buffer, long fileOffset, CancellationToken cancellationToken);
}

/// <summary>Error del sistema en una operación de <see cref="ISourceFileApi"/>, con su código Win32.</summary>
internal sealed class SourceFileApiException : IOException
{
    public SourceFileApiException(int error, string operation)
        : base($"{operation} failed (Win32 error {error}).")
    {
        Error = error;
        Operation = operation;
        HResult = error <= 0 ? error : unchecked((int)(((uint)error & 0xFFFF) | 0x8007_0000));
    }

    public SourceFileApiException()
        : this(0, "Operation")
    {
    }

    public SourceFileApiException(string message)
        : base(message)
    {
        Operation = "Operation";
    }

    public SourceFileApiException(string message, Exception innerException)
        : base(message, innerException)
    {
        Operation = "Operation";
    }

    public int Error { get; }

    public string Operation { get; }
}
