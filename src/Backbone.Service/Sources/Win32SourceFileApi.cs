using System.Globalization;
using System.Runtime.InteropServices;
using Backbone.Service.Os;
using Microsoft.Win32.SafeHandles;
using N = Backbone.Service.Os.NativeMethods;
using R = Backbone.Service.Os.NativeMethods.Resolution;
using S = Backbone.Service.Os.NativeMethods.SourceIo;

namespace Backbone.Service.Sources;

/// <summary>
/// Implementación real de <see cref="ISourceFileApi"/> (§8.2 "Apertura relativa", ADR-18, ADR-21). Toda apertura pasa por
/// <see cref="SourceOpen.Open"/>: <c>NtCreateFile</c> de solo lectura con <c>FILE_OPEN</c>,
/// <c>FILE_OPEN_FOR_BACKUP_INTENT | FILE_COMPLETE_IF_OPLOCKED</c> y SQOS de identificación; los hijos, relativos al handle
/// de su carpeta padre y con un solo componente.
/// </summary>
internal sealed class Win32SourceFileApi : ISourceFileApi
{
    internal const uint DirectoryAccess = N.FileListDirectory | N.FileReadAttributes | N.Synchronize;
    internal const uint MetadataAccess = N.FileReadAttributes | N.Synchronize;
    internal const uint ContentAccess = N.GenericRead;

    /// <summary>Carpetas: compartición de lectura, escritura <b>y borrado</b> (§8.2 "Caché de carpetas").</summary>
    internal const uint DirectoryShare = N.FileShareRead | N.FileShareWrite | N.FileShareDelete;

    /// <summary>Archivos: sin <c>FILE_SHARE_DELETE</c> mientras se leen (nadie puede renombrarlos ni sustituirlos).</summary>
    internal const uint FileShare = N.FileShareRead | N.FileShareWrite;

    internal const uint DirectoryOptions = R.FileOpenReparsePoint | S.FileDirectoryFile | R.FileSynchronousIoNonAlert;
    internal const uint MetadataOptions = R.FileOpenReparsePoint | R.FileSynchronousIoNonAlert;
    internal const uint ContentOptions = R.FileOpenReparsePoint | S.FileNonDirectoryFile | S.FileSequentialOnly;

    /// <summary>Reapertura de la lista blanca: <b>sin</b> <c>FILE_OPEN_REPARSE_POINT</c>, E/S asíncrona.</summary>
    internal const uint ReOpenOptions = S.FileNonDirectoryFile | S.FileSequentialOnly;

    public ISourceHandle OpenVolumeRoot(string ntRootPath, CancellationToken timeoutSignal)
    {
        ArgumentException.ThrowIfNullOrEmpty(ntRootPath);
        if (!SourceOpen.IsAllowedAbsolutePath(ntRootPath))
        {
            throw new ArgumentException("The volume root must be the NT root of a local volume or a shadow copy.", nameof(ntRootPath));
        }

        // Copia en vivo con letra (\??\X:\): la letra se vuelve a mirar en el mapa de dispositivos del proceso antes de abrir,
        // como en la validación (§8.2, §8.3 paso 1): lo que no sea DRIVE_FIXED (una letra que ahora apunte a \Device\Mup, un
        // extraíble…) no llega a abrirse (abrir una de red podría provocar una autenticación con la cuenta del equipo). Las
        // rutas \??\Volume{guid}\ y \Device\… nunca son de red.
        if (ntRootPath.Length == 7 && ntRootPath[5] == ':' && RejectDriveType(N.GetDriveType(ntRootPath[4..])) is { } rejected)
        {
            throw rejected;
        }

        return Open(ntRootPath, null, SourceOpenMode.Directory);
    }

    /// <summary>
    /// Filtro previo de una raíz con letra (BAJA-N1): <see langword="null"/> si es <c>DRIVE_FIXED</c>; si no, la excepción que
    /// se lanza sin abrir nada (<c>DRIVE_NO_ROOT_DIR</c>: no encontrada; red: <c>ERROR_BAD_NETPATH</c>; el resto:
    /// <c>ERROR_NOT_SUPPORTED</c>).
    /// </summary>
    internal static SourceFileApiException? RejectDriveType(uint driveType) => driveType switch
    {
        N.DriveFixed => null,
        N.DriveNoRootDir => new SourceFileApiException(N.ErrorPathNotFound, "GetDriveType(no root)"),
        N.DriveRemote => new SourceFileApiException(N.ErrorBadNetPath, "GetDriveType(remote)"),
        _ => new SourceFileApiException(N.ErrorNotSupported, string.Create(CultureInfo.InvariantCulture, $"GetDriveType({driveType}, not fixed)")),
    };

    public ISourceHandle OpenChild(ISourceHandle parent, string name, SourceOpenMode mode, bool exactCase, CancellationToken timeoutSignal)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (parent is not Win32SourceHandle native)
        {
            throw new ArgumentException("The parent handle does not come from this API.", nameof(parent));
        }

        return Open(name, native.Handle, mode, exactCase);
    }

    public uint GetPlaceholderState(FileAttributes attributes, uint reparseTag)
    {
        try
        {
            return R.CfGetPlaceholderStateFromAttributeTag((uint)attributes, reparseTag);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Sin la Cloud Files API no se puede saber si el marcador está descargado: falla cerrado (solo en la nube).
            return R.CfPlaceholderStateInvalid;
        }
    }

    internal static (uint Access, uint Share, uint Options) Parameters(SourceOpenMode mode) => mode switch
    {
        SourceOpenMode.Directory => (DirectoryAccess, DirectoryShare, DirectoryOptions),
        SourceOpenMode.Metadata => (MetadataAccess, FileShare, MetadataOptions),
        SourceOpenMode.Content => (ContentAccess, FileShare, ContentOptions),
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    internal static Win32SourceHandle Open(string name, SafeFileHandle? parent, SourceOpenMode mode, bool exactCase = false)
    {
        var (access, share, options) = Parameters(mode);
        var status = SourceOpen.Open(name, parent, access, share, options, out var handle, exactCase);
        if (status < 0)
        {
            throw new SourceFileApiException(N.RtlNtStatusToDosError(status), "NtCreateFile", status);
        }

        return new Win32SourceHandle(handle, overlapped: mode == SourceOpenMode.Content);
    }
}

/// <summary>Handle real de un elemento de origen.</summary>
internal sealed class Win32SourceHandle(SafeFileHandle handle, bool overlapped) : ISourceHandle
{
    /// <summary>Tamaño del búfer de enumeración (alineado a 8 bytes, como exige <c>FILE_ID_BOTH_DIR_INFO</c>).</summary>
    private const int DirectoryBufferBytes = 64 * 1024;

    /// <summary>Desplazamientos de <c>FILE_ID_BOTH_DIR_INFO</c> (winbase.h).</summary>
    private const int OffsetNextEntry = 0;
    private const int OffsetLastWriteTime = 24;
    private const int OffsetEndOfFile = 40;
    private const int OffsetFileAttributes = 56;
    private const int OffsetFileNameLength = 60;
    private const int OffsetEaSize = 64;
    private const int OffsetFileName = 104;

    /// <summary>Rangos por llamada a <c>FSCTL_QUERY_ALLOCATED_RANGES</c>.</summary>
    private const int RangesPerQuery = 64;

    private byte[]? _directoryBuffer;

    internal SafeFileHandle Handle => handle;

    public SourceAttributeTag GetAttributeTag(CancellationToken timeoutSignal)
    {
        if (!N.GetFileInformationByHandleEx(handle, N.FileAttributeTagInfo, out N.FileAttributeTagInformation info, (uint)Marshal.SizeOf<N.FileAttributeTagInformation>()))
        {
            throw new SourceFileApiException(Marshal.GetLastPInvokeError(), "GetFileInformationByHandleEx(FileAttributeTagInfo)");
        }

        return new SourceAttributeTag((FileAttributes)info.FileAttributes, info.ReparseTag);
    }

    public unsafe string GetFinalPath(FinalPathFormat format, CancellationToken timeoutSignal)
    {
        var flags = format switch
        {
            FinalPathFormat.Nt => S.FileNameNormalizedVolumeNameNt,
            FinalPathFormat.Dos => N.FileNameNormalizedVolumeNameDos,
            FinalPathFormat.Guid => N.FileNameNormalizedVolumeNameGuid,
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };

        var buffer = new char[512];
        while (true)
        {
            uint length;
            fixed (char* pointer = buffer)
            {
                length = N.GetFinalPathNameByHandle(handle, pointer, (uint)buffer.Length, flags);
            }

            if (length == 0)
            {
                throw new SourceFileApiException(Marshal.GetLastPInvokeError(), "GetFinalPathNameByHandle");
            }

            if (length < buffer.Length)
            {
                return new string(buffer, 0, (int)length);
            }

            if (length > short.MaxValue + 1)
            {
                throw new SourceFileApiException(S.ErrorFilenameExcedRange, "GetFinalPathNameByHandle");
            }

            buffer = new char[length];
        }
    }

    public SourceFileId GetFileId(CancellationToken timeoutSignal)
    {
        if (!S.GetFileInformationByHandleEx(handle, S.FileIdInfo, out S.FileIdInformation info, (uint)Marshal.SizeOf<S.FileIdInformation>()))
        {
            throw new SourceFileApiException(Marshal.GetLastPInvokeError(), "GetFileInformationByHandleEx(FileIdInfo)");
        }

        return new SourceFileId(info.VolumeSerialNumber, info.FileIdLow, info.FileIdHigh);
    }

    public SourceFileTimes GetSizeAndTime(CancellationToken timeoutSignal)
    {
        if (!S.GetFileInformationByHandleEx(handle, S.FileBasicInfo, out S.FileBasicInformation basic, (uint)Marshal.SizeOf<S.FileBasicInformation>()))
        {
            throw new SourceFileApiException(Marshal.GetLastPInvokeError(), "GetFileInformationByHandleEx(FileBasicInfo)");
        }

        if (!N.GetFileInformationByHandleEx(handle, N.FileStandardInfo, out N.FileStandardInformation standard, (uint)Marshal.SizeOf<N.FileStandardInformation>()))
        {
            throw new SourceFileApiException(Marshal.GetLastPInvokeError(), "GetFileInformationByHandleEx(FileStandardInfo)");
        }

        var length = standard.Directory != 0 ? 0 : Math.Max(0, standard.EndOfFile);
        return new SourceFileTimes(length, ToUtc(basic.LastWriteTime));
    }

    public SourceDeviceInfo GetDeviceInfo(CancellationToken timeoutSignal)
    {
        var status = R.NtQueryVolumeInformationFile(handle, out _, out var info, (uint)Marshal.SizeOf<R.FileFsDeviceInformation>(), R.FileFsDeviceInformationClass);
        if (status < 0)
        {
            throw new SourceFileApiException(N.RtlNtStatusToDosError(status), "NtQueryVolumeInformationFile(FileFsDeviceInformation)", status);
        }

        return new SourceDeviceInfo(info.DeviceType, info.Characteristics);
    }

    public bool ReadDirectory(List<RawDirectoryEntry> entries, bool restart, CancellationToken timeoutSignal)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _directoryBuffer ??= GC.AllocateUninitializedArray<byte>(DirectoryBufferBytes, pinned: true);
        var buffer = _directoryBuffer;
        var infoClass = restart ? S.FileIdBothDirectoryRestartInfo : S.FileIdBothDirectoryInfo;
        if (!N.GetFileInformationByHandleExBuffer(handle, infoClass, buffer, (uint)buffer.Length))
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == N.ErrorNoMoreFiles)
            {
                return false;
            }

            throw new SourceFileApiException(error, "GetFileInformationByHandleEx(FileIdBothDirectoryInfo)");
        }

        ParseDirectoryBuffer(buffer, entries);
        return true;
    }

    public ISourceHandle ReOpenForContent(CancellationToken timeoutSignal)
    {
        // Relativo a este mismo handle con el nombre vacío (lo que hace ReOpenFile, sin volver a resolver la ruta) y sin
        // FILE_OPEN_REPARSE_POINT: el filtro propietario de la etiqueta interviene y sirve el contenido (§8.2, punto 2).
        var status = SourceOpen.Open(string.Empty, handle, Win32SourceFileApi.ContentAccess, Win32SourceFileApi.FileShare, Win32SourceFileApi.ReOpenOptions, out var reopened);
        if (status < 0)
        {
            throw new SourceFileApiException(N.RtlNtStatusToDosError(status), "NtCreateFile(reopen)", status);
        }

        return new Win32SourceHandle(reopened, overlapped: true);
    }

    public unsafe bool IsFullyAllocated(long length, CancellationToken timeoutSignal)
    {
        if (length <= 0)
        {
            return true;
        }

        var covered = 0L;
        var output = new S.FileAllocatedRangeBuffer[RangesPerQuery];
        while (covered < length)
        {
            var input = new S.FileAllocatedRangeBuffer { FileOffset = covered, Length = length - covered };
            var (error, bytes) = QueryAllocatedRanges(ref input, output);
            if (error is not 0 and not S.ErrorMoreData)
            {
                throw new SourceFileApiException(error, "DeviceIoControl(FSCTL_QUERY_ALLOCATED_RANGES)");
            }

            var count = (int)(bytes / (uint)sizeof(S.FileAllocatedRangeBuffer));
            if (count == 0)
            {
                return false; // nada asignado en lo que queda
            }

            var before = covered;
            for (var i = 0; i < count && covered < length; i++)
            {
                var range = output[i];
                if (range.FileOffset > covered || range.Length <= 0)
                {
                    return false; // hueco sin asignar
                }

                covered = Math.Max(covered, range.FileOffset + range.Length);
            }

            if (covered <= before)
            {
                return false; // sin progreso: no se puede afirmar que esté todo asignado
            }

            if (error == 0 && covered < length)
            {
                return false; // respuesta completa que no llega al final
            }
        }

        return true;
    }

    public void CancelIo() => S.CancelIoEx(handle, 0);

    public ValueTask<int> ReadAsync(Memory<byte> buffer, long fileOffset, CancellationToken cancellationToken) =>
        // Handle de E/S asíncrona: RandomAccess cancela con CancelIoEx al cancelarse el token (§8.2 "Plazo").
        RandomAccess.ReadAsync(handle, buffer, fileOffset, cancellationToken);

    public void Dispose() => handle.Dispose();

    internal static void ParseDirectoryBuffer(ReadOnlySpan<byte> buffer, List<RawDirectoryEntry> entries)
    {
        var offset = 0;
        while (true)
        {
            if (offset < 0 || offset > buffer.Length - OffsetFileName)
            {
                throw new SourceFileApiException(S.ErrorInvalidHandle, "FILE_ID_BOTH_DIR_INFO (malformed)");
            }

            var entry = buffer[offset..];
            var next = BitConverter.ToUInt32(entry[OffsetNextEntry..]);
            var nameBytes = BitConverter.ToUInt32(entry[OffsetFileNameLength..]);
            if (nameBytes % 2 != 0 || nameBytes > (uint)(entry.Length - OffsetFileName))
            {
                throw new SourceFileApiException(S.ErrorInvalidHandle, "FILE_ID_BOTH_DIR_INFO (malformed)");
            }

            var name = new string(MemoryMarshal.Cast<byte, char>(entry.Slice(OffsetFileName, (int)nameBytes)));
            entries.Add(new RawDirectoryEntry(
                name,
                (FileAttributes)BitConverter.ToUInt32(entry[OffsetFileAttributes..]),
                BitConverter.ToUInt32(entry[OffsetEaSize..]),
                Math.Max(0, BitConverter.ToInt64(entry[OffsetEndOfFile..])),
                ToUtc(BitConverter.ToInt64(entry[OffsetLastWriteTime..]))));

            if (next == 0)
            {
                return;
            }

            if (next < OffsetFileName || next > (uint)(buffer.Length - offset) || next % 8 != 0)
            {
                throw new SourceFileApiException(S.ErrorInvalidHandle, "FILE_ID_BOTH_DIR_INFO (malformed)");
            }

            offset += (int)next;
        }
    }

    private static DateTime ToUtc(long fileTime) =>
        fileTime is > 0 and <= 2_650_467_743_999_999_999 ? DateTime.FromFileTimeUtc(fileTime) : DateTime.FromFileTimeUtc(0);

    /// <summary>
    /// <c>FSCTL_QUERY_ALLOCATED_RANGES</c>. Con un handle asíncrono hace falta <c>OVERLAPPED</c>: se usa un evento con el bit
    /// bajo a 1 para que la finalización no vaya al puerto de E/S del grupo de subprocesos, y se espera con
    /// <c>GetOverlappedResult</c> (lo desbloquea <c>CancelIoEx</c> si vence el plazo).
    /// </summary>
    private unsafe (int Error, uint Bytes) QueryAllocatedRanges(ref S.FileAllocatedRangeBuffer input, S.FileAllocatedRangeBuffer[] output)
    {
        var inputSize = (uint)sizeof(S.FileAllocatedRangeBuffer);
        var outputSize = (uint)(output.Length * sizeof(S.FileAllocatedRangeBuffer));
        fixed (S.FileAllocatedRangeBuffer* inPointer = &input)
        fixed (S.FileAllocatedRangeBuffer* outPointer = output)
        {
            if (!overlapped)
            {
                uint returned = 0;
                var done = S.DeviceIoControlOverlapped(handle, S.FsctlQueryAllocatedRanges, inPointer, inputSize, outPointer, outputSize, &returned, null);
                return (done ? 0 : Marshal.GetLastPInvokeError(), returned);
            }

            using var completed = new ManualResetEvent(initialState: false);
            var native = (NativeOverlapped*)NativeMemory.AllocZeroed((nuint)sizeof(NativeOverlapped));
            try
            {
                native->EventHandle = completed.SafeWaitHandle.DangerousGetHandle() | 1;
                if (!S.DeviceIoControlOverlapped(handle, S.FsctlQueryAllocatedRanges, inPointer, inputSize, outPointer, outputSize, null, native))
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error is not S.ErrorIoPending and not S.ErrorMoreData)
                    {
                        return (error, 0);
                    }
                }

                // Con OVERLAPPED, los bytes devueltos se leen siempre de GetOverlappedResult. Espera sin límite: este método
                // corre en el hilo de SourceOperationRunner, que lo desbloquea con CancelIoEx si vence el plazo.
                return S.GetOverlappedResult(handle, native, out var transferred, wait: true)
                    ? (0, transferred)
                    : (Marshal.GetLastPInvokeError(), transferred);
            }
            finally
            {
                NativeMemory.Free(native);
            }
        }
    }
}
