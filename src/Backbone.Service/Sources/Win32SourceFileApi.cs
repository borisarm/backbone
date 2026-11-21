using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using static Backbone.Service.Os.NativeMethods.SourceIo;

namespace Backbone.Service.Sources;

/// <summary>Implementación real de <see cref="ISourceFileApi"/> (Win32, §8.2).</summary>
internal sealed class Win32SourceFileApi : ISourceFileApi
{
    public ISourceHandle Open(string win32Path, SourceOpenMode mode, CancellationToken timeoutSignal)
    {
        ArgumentException.ThrowIfNullOrEmpty(win32Path);
        var (access, flags) = mode switch
        {
            SourceOpenMode.Directory => (FileListDirectory | FileReadAttributes | Synchronize, 0u),
            SourceOpenMode.Metadata => (FileReadAttributes | Synchronize, 0u),
            SourceOpenMode.Content => (GenericRead, FileFlagOverlapped | FileFlagSequentialScan),
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };

        var handle = CreateFile(
            win32Path,
            access,
            FileShareReadWrite,
            0,
            OpenExisting,
            FileFlagOpenReparsePoint | FileFlagBackupSemantics | flags,
            0);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new SourceFileApiException(error, "CreateFile");
        }

        return new Win32SourceHandle(handle, mode == SourceOpenMode.Content);
    }

    public uint GetPlaceholderState(FileAttributes attributes, uint reparseTag)
    {
        try
        {
            return CfGetPlaceholderStateFromAttributeTag((uint)attributes, reparseTag);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Sin la Cloud Files API no se puede saber si el marcador está descargado: falla cerrado (solo en la nube).
            return CfPlaceholderStateInvalid;
        }
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

    private bool _restart = true;
    private byte[]? _directoryBuffer;

    internal SafeFileHandle Handle => handle;

    public SourceAttributeTag GetAttributeTag(CancellationToken timeoutSignal)
    {
        if (!GetFileInformationByHandleEx(handle, FileAttributeTagInfo, out FileAttributeTagInformation info, (uint)Marshal.SizeOf<FileAttributeTagInformation>()))
        {
            throw new SourceFileApiException(Marshal.GetLastPInvokeError(), "GetFileInformationByHandleEx(FileAttributeTagInfo)");
        }

        return new SourceAttributeTag((FileAttributes)info.FileAttributes, info.ReparseTag);
    }

    public unsafe string GetFinalPath(FinalPathFormat format, CancellationToken timeoutSignal)
    {
        var flags = format switch
        {
            FinalPathFormat.Nt => VolumeNameNt,
            FinalPathFormat.Dos => VolumeNameDos,
            FinalPathFormat.Guid => VolumeNameGuid,
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };

        var buffer = new char[512];
        while (true)
        {
            uint length;
            fixed (char* pointer = buffer)
            {
                length = GetFinalPathNameByHandle(handle, pointer, (uint)buffer.Length, flags);
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
                throw new SourceFileApiException(ErrorFilenameExcedRange, "GetFinalPathNameByHandle");
            }

            buffer = new char[length];
        }
    }

    public SourceFileId GetFileId(CancellationToken timeoutSignal)
    {
        if (!GetFileInformationByHandleEx(handle, FileIdInfo, out FileIdInformation info, (uint)Marshal.SizeOf<FileIdInformation>()))
        {
            throw new SourceFileApiException(Marshal.GetLastPInvokeError(), "GetFileInformationByHandleEx(FileIdInfo)");
        }

        return new SourceFileId(info.VolumeSerialNumber, info.FileIdLow, info.FileIdHigh);
    }

    public SourceFileTimes GetSizeAndTime(CancellationToken timeoutSignal)
    {
        if (!GetFileInformationByHandleEx(handle, FileBasicInfo, out FileBasicInformation basic, (uint)Marshal.SizeOf<FileBasicInformation>()))
        {
            throw new SourceFileApiException(Marshal.GetLastPInvokeError(), "GetFileInformationByHandleEx(FileBasicInfo)");
        }

        if (!GetFileInformationByHandleEx(handle, FileStandardInfo, out FileStandardInformation standard, (uint)Marshal.SizeOf<FileStandardInformation>()))
        {
            throw new SourceFileApiException(Marshal.GetLastPInvokeError(), "GetFileInformationByHandleEx(FileStandardInfo)");
        }

        var length = standard.Directory != 0 ? 0 : Math.Max(0, standard.EndOfFile);
        return new SourceFileTimes(length, ToUtc(basic.LastWriteTime));
    }

    public unsafe bool ReadDirectory(List<RawDirectoryEntry> entries, CancellationToken timeoutSignal)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _directoryBuffer ??= GC.AllocateUninitializedArray<byte>(DirectoryBufferBytes, pinned: true);
        var buffer = _directoryBuffer;
        var infoClass = _restart ? FileIdBothDirectoryRestartInfo : FileIdBothDirectoryInfo;
        bool ok;
        fixed (byte* pointer = buffer)
        {
            ok = GetFileInformationByHandleExBuffer(handle, infoClass, pointer, (uint)buffer.Length);
        }

        if (!ok)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == ErrorNoMoreFiles)
            {
                return false;
            }

            throw new SourceFileApiException(error, "GetFileInformationByHandleEx(FileIdBothDirectoryInfo)");
        }

        _restart = false;
        ParseDirectoryBuffer(buffer, entries);
        return true;
    }

    public ISourceHandle ReOpenForContent(CancellationToken timeoutSignal)
    {
        // Sin FILE_FLAG_OPEN_REPARSE_POINT: el filtro propietario de la etiqueta interviene y sirve el contenido (§8.2).
        var reopened = ReOpenFile(handle, GenericRead, FileShareReadWrite, FileFlagBackupSemantics | FileFlagOverlapped | FileFlagSequentialScan);
        if (reopened.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            reopened.Dispose();
            throw new SourceFileApiException(error, "ReOpenFile");
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
        var output = new FileAllocatedRangeBuffer[RangesPerQuery];
        while (covered < length)
        {
            var input = new FileAllocatedRangeBuffer { FileOffset = covered, Length = length - covered };
            var (error, bytes) = QueryAllocatedRanges(ref input, output);
            if (error is not 0 and not ErrorMoreData)
            {
                throw new SourceFileApiException(error, "DeviceIoControl(FSCTL_QUERY_ALLOCATED_RANGES)");
            }

            var count = (int)(bytes / (uint)sizeof(FileAllocatedRangeBuffer));
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

    public void CancelIo() => CancelIoEx(handle, 0);

    public ValueTask<int> ReadAsync(Memory<byte> buffer, long fileOffset, CancellationToken cancellationToken) =>
        // Handle con FILE_FLAG_OVERLAPPED: RandomAccess cancela con CancelIoEx al cancelarse el token (§8.2 "Plazo").
        RandomAccess.ReadAsync(handle, buffer, fileOffset, cancellationToken);

    public void Dispose() => handle.Dispose();

    internal static void ParseDirectoryBuffer(ReadOnlySpan<byte> buffer, List<RawDirectoryEntry> entries)
    {
        var offset = 0;
        while (true)
        {
            if (offset < 0 || offset > buffer.Length - OffsetFileName)
            {
                throw new SourceFileApiException(ErrorInvalidHandle, "FILE_ID_BOTH_DIR_INFO (malformed)");
            }

            var entry = buffer[offset..];
            var next = BitConverter.ToUInt32(entry[OffsetNextEntry..]);
            var nameBytes = BitConverter.ToUInt32(entry[OffsetFileNameLength..]);
            if (nameBytes % 2 != 0 || nameBytes > (uint)(entry.Length - OffsetFileName))
            {
                throw new SourceFileApiException(ErrorInvalidHandle, "FILE_ID_BOTH_DIR_INFO (malformed)");
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

            if (next < OffsetFileName || next > (uint)(buffer.Length - offset))
            {
                throw new SourceFileApiException(ErrorInvalidHandle, "FILE_ID_BOTH_DIR_INFO (malformed)");
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
    private unsafe (int Error, uint Bytes) QueryAllocatedRanges(ref FileAllocatedRangeBuffer input, FileAllocatedRangeBuffer[] output)
    {
        var inputSize = (uint)sizeof(FileAllocatedRangeBuffer);
        var outputSize = (uint)(output.Length * sizeof(FileAllocatedRangeBuffer));
        fixed (FileAllocatedRangeBuffer* inPointer = &input)
        fixed (FileAllocatedRangeBuffer* outPointer = output)
        {
            if (!overlapped)
            {
                uint returned = 0;
                var done = DeviceIoControl(handle, FsctlQueryAllocatedRanges, inPointer, inputSize, outPointer, outputSize, &returned, null);
                return (done ? 0 : Marshal.GetLastPInvokeError(), returned);
            }

            using var completed = new ManualResetEvent(initialState: false);
            var native = (NativeOverlapped*)NativeMemory.AllocZeroed((nuint)sizeof(NativeOverlapped));
            try
            {
                native->EventHandle = completed.SafeWaitHandle.DangerousGetHandle() | 1;
                if (!DeviceIoControl(handle, FsctlQueryAllocatedRanges, inPointer, inputSize, outPointer, outputSize, null, native))
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error is not ErrorIoPending and not ErrorMoreData)
                    {
                        return (error, 0);
                    }
                }

                // Con OVERLAPPED, los bytes devueltos se leen siempre de GetOverlappedResult. Espera sin límite: este método
                // corre en el hilo de SourceOperationRunner, que lo desbloquea con CancelIoEx si vence el plazo.
                return GetOverlappedResult(handle, native, out var transferred, wait: true)
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
