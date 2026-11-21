namespace Backbone.Service.Sources;

/// <summary>Cómo se abre un elemento de origen (§8.2 "Apertura relativa").</summary>
internal enum SourceOpenMode
{
    /// <summary>
    /// Carpeta para enumerarla o como padre de la caché: <c>FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES | SYNCHRONIZE</c>,
    /// compartición de lectura, escritura y borrado, <c>FILE_DIRECTORY_FILE | FILE_SYNCHRONOUS_IO_NONALERT</c>.
    /// </summary>
    Directory = 0,

    /// <summary>
    /// Solo metadatos (<c>GetFileInfoAsync</c>): <c>FILE_READ_ATTRIBUTES | SYNCHRONIZE</c>, sin acceso a datos, compartición de
    /// lectura y escritura, E/S síncrona; puede ser carpeta o archivo.
    /// </summary>
    Metadata = 1,

    /// <summary>
    /// Contenido (<c>OpenReadAsync</c>): <c>GENERIC_READ</c>, compartición de lectura y escritura sin borrado,
    /// <c>FILE_NON_DIRECTORY_FILE</c> y E/S asíncrona (sin <c>FILE_SYNCHRONOUS_IO_*</c>; se cancela con <c>CancelIoEx</c>).
    /// </summary>
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

/// <summary><c>FILE_FS_DEVICE_INFORMATION</c> del volumen de un handle (§8.3 paso 1).</summary>
/// <param name="DeviceType"><c>DeviceType</c> (<c>FILE_DEVICE_DISK</c> = 7).</param>
/// <param name="Characteristics"><c>Characteristics</c> (<c>FILE_REMOTE_DEVICE</c>, <c>FILE_REMOVABLE_MEDIA</c>…).</param>
internal readonly record struct SourceDeviceInfo(uint DeviceType, uint Characteristics)
{
    public const uint FileDeviceDisk = 0x7;
    public const uint FileRemovableMedia = 0x1;
    public const uint FileRemoteDevice = 0x10;

    /// <summary>Disco local: <c>FILE_DEVICE_DISK</c> sin <c>FILE_REMOTE_DEVICE</c> ni <c>FILE_REMOVABLE_MEDIA</c>.</summary>
    public bool IsLocalDisk => DeviceType == FileDeviceDisk && (Characteristics & (FileRemoteDevice | FileRemovableMedia)) == 0;
}

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
/// ejecuta en su hilo dedicado de lectura con plazo (<see cref="SourceOperationRunner"/>, ADR-21). Los errores del sistema se
/// lanzan como <see cref="SourceFileApiException"/> con el código Win32 (y el <c>NTSTATUS</c> si lo hay).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>
/// <b>Nunca se abre un elemento por su ruta completa</b> (ADR-18, §8.2 "Apertura relativa"): solo la raíz de un volumen o de
/// la instantánea (<see cref="OpenVolumeRoot"/>) y, desde ahí, cada componente relativo al handle de su carpeta padre
/// (<see cref="OpenChild"/>, un solo componente).
/// </description></item>
/// <item><description>
/// El <see cref="CancellationToken"/> que reciben las operaciones síncronas se cancela cuando vence el plazo. La
/// implementación real no lo usa (la desbloquea <c>CancelSynchronousIo</c> o <c>CancelIoEx</c>); lo usan los simuladores.
/// </description></item>
/// </list>
/// </remarks>
internal interface ISourceFileApi
{
    /// <summary>
    /// Abre la raíz de un volumen por su ruta NT absoluta (<c>\??\C:\</c>, <c>\??\Volume{guid}\</c> o
    /// <c>\Device\HarddiskVolumeShadowCopy&lt;n&gt;\</c>) como carpeta (<see cref="SourceOpenMode.Directory"/>).
    /// </summary>
    ISourceHandle OpenVolumeRoot(string ntRootPath, CancellationToken timeoutSignal);

    /// <summary>
    /// Abre <paramref name="name"/> (un solo componente, sin <c>\</c>) relativo a la carpeta <paramref name="parent"/>, sin
    /// seguir un punto de reanálisis (<c>FILE_OPEN_REPARSE_POINT</c>), con <c>FILE_OPEN_FOR_BACKUP_INTENT</c>,
    /// <c>FILE_COMPLETE_IF_OPLOCKED</c> y SQOS de identificación.
    /// </summary>
    ISourceHandle OpenChild(ISourceHandle parent, string name, SourceOpenMode mode, CancellationToken timeoutSignal);

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

    /// <summary><c>NtQueryVolumeInformationFile(FileFsDeviceInformation)</c> sobre este handle.</summary>
    SourceDeviceInfo GetDeviceInfo(CancellationToken timeoutSignal);

    /// <summary>
    /// Siguiente lote de la enumeración (<c>FileIdBothDirectoryInfo</c>, o <c>FileIdBothDirectoryRestartInfo</c> con
    /// <paramref name="restart"/>) desde este handle; <see langword="false"/> cuando no quedan más (<c>ERROR_NO_MORE_FILES</c>).
    /// </summary>
    bool ReadDirectory(List<RawDirectoryEntry> entries, bool restart, CancellationToken timeoutSignal);

    /// <summary>
    /// Segundo handle para leer el contenido de la lista blanca (§8.2 "Apertura para leer el contenido", punto 2):
    /// <c>NtCreateFile</c> relativo a este mismo handle con el nombre vacío (reabre el mismo objeto sin volver a resolver la
    /// ruta), <b>sin</b> <c>FILE_OPEN_REPARSE_POINT</c>, con E/S asíncrona. Si el filtro propietario no está en la pila,
    /// <see cref="SourceFileApiException"/> con <c>ERROR_CANT_ACCESS_FILE</c>.
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
    /// <summary><c>STATUS_FILE_IS_A_DIRECTORY</c>.</summary>
    public const int StatusFileIsADirectory = unchecked((int)0xC000_00BA);

    /// <summary><c>STATUS_NOT_A_DIRECTORY</c>.</summary>
    public const int StatusNotADirectory = unchecked((int)0xC000_0103);

    public SourceFileApiException(int error, string operation, int ntStatus = 0)
        : base($"{operation} failed (Win32 error {error}{(ntStatus == 0 ? string.Empty : $", NTSTATUS 0x{ntStatus:X8}")}).")
    {
        Error = error;
        Operation = operation;
        NtStatus = ntStatus;
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

    /// <summary><c>NTSTATUS</c> de <c>NtCreateFile</c> o <c>NtQueryVolumeInformationFile</c>; 0 si la operación era Win32.</summary>
    public int NtStatus { get; }
}
