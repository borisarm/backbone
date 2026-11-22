using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using Backbone.Contracts;
using Backbone.Service.Os;
using Backbone.Service.Persistence;
using Microsoft.Extensions.Logging;

namespace Backbone.Service.Nas;

/// <summary>
/// <see cref="IShareConnector"/> real (§8.2): <c>WNetAddConnection2</c> sin letra de unidad (conexión sin dispositivo, no
/// recordada, nunca interactiva) con la credencial explícita, y E/S de .NET sobre las rutas UNC del recurso.
/// </summary>
/// <remarks>
/// Las llamadas bloqueantes van a un hilo del grupo: <c>WNetAddConnection2</c> contra un servidor apagado puede tardar más de
/// lo que admite RF-61 y no se puede interrumpir, así que el plazo lo pone el llamante (<see cref="ConnectionTester"/>).
/// </remarks>
internal sealed partial class WindowsShareConnector(ILogger<WindowsShareConnector> logger) : IShareConnector
{
    public Task<IShareConnection> ConnectAsync(NasCredential credential, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(credential);
        return Task.Run<IShareConnection>(
            () =>
            {
                var error = AddConnection(credential);
                if (error != NativeMethods.ErrorSuccess)
                {
                    throw new ShareException(ShareErrors.FromWin32(error, ShareOperation.Connect), error);
                }

                return new WindowsShareConnection(credential.ShareRoot, logger);
            },
            ct);
    }

    public Task ForceDisconnectAsync(string shareRoot, CancellationToken ct) =>
        Task.Run(
            () =>
            {
                var error = NativeMethods.WNetCancelConnection2(shareRoot, 0, force: true);
                if (error is not NativeMethods.ErrorSuccess and not ErrorNotConnected)
                {
                    throw new ShareException(ShareErrors.FromWin32(error, ShareOperation.Connect), error);
                }
            },
            ct);

    private static unsafe int AddConnection(NasCredential credential)
    {
        // Usuario y contraseña siempre explícitos (nunca NULL: se usaría la cuenta del equipo, §7.5). La contraseña se pasa
        // desde el array fijado de NasCredential, sin copiarla a un string.
        fixed (char* remoteName = credential.ShareRoot)
        fixed (char* userName = credential.UserName)
        fixed (char* password = credential.PasswordWithNullTerminator)
        {
            var resource = new NativeMethods.NetResource
            {
                Type = NativeMethods.ResourceTypeDisk,
                RemoteName = remoteName,
            };
            return NativeMethods.WNetAddConnection2(resource, password, userName, NativeMethods.ConnectTemporary);
        }
    }

    /// <summary>Dialecto de un <c>FILE_REMOTE_PROTOCOL_INFO</c> (<c>3.1.1</c>, <c>3.0.2</c>, <c>2.1</c>); <see langword="null"/> si no es SMB.</summary>
    internal static string? FormatDialect(ReadOnlySpan<byte> info)
    {
        // USHORT StructureVersion; USHORT StructureSize; ULONG Protocol; USHORT Major; USHORT Minor; USHORT Revision; …
        var protocol = BinaryPrimitives.ReadUInt32LittleEndian(info[4..]);
        if (protocol != NativeMethods.WnncNetSmb)
        {
            return null;
        }

        var major = BinaryPrimitives.ReadUInt16LittleEndian(info[8..]);
        var minor = BinaryPrimitives.ReadUInt16LittleEndian(info[10..]);
        var revision = BinaryPrimitives.ReadUInt16LittleEndian(info[12..]);
        return revision == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{major}.{minor}")
            : string.Create(CultureInfo.InvariantCulture, $"{major}.{minor}.{revision}");
    }

    /// <summary>
    /// Indica si <paramref name="shareRoot"/> figura entre las conexiones actuales de la sesión de inicio del servicio
    /// (<c>WNetOpenEnum</c> con <c>RESOURCE_CONNECTED</c>, que también lista las conexiones sin letra de unidad).
    /// </summary>
    internal static unsafe bool UseRecordExists(string shareRoot)
    {
        var error = NativeMethods.WNetOpenEnum(NativeMethods.ResourceConnected, NativeMethods.ResourceTypeAny, 0, null, out var handle);
        if (error != NativeMethods.ErrorSuccess)
        {
            throw new ShareException(ShareErrors.FromWin32(error, ShareOperation.Connect), error);
        }

        var size = 16 * 1024u;
        var buffer = NativeMemory.Alloc(size);
        try
        {
            var wanted = shareRoot.TrimEnd('\\');
            while (true)
            {
                var count = uint.MaxValue;
                var available = size;
                error = NativeMethods.WNetEnumResource(handle, ref count, buffer, ref available);
                if (error == NativeMethods.ErrorNoMoreItems)
                {
                    return false;
                }

                if (error == NativeMethods.ErrorMoreData)
                {
                    size = Math.Max(available, size * 2);
                    buffer = NativeMemory.Realloc(buffer, size);
                    continue;
                }

                if (error != NativeMethods.ErrorSuccess)
                {
                    throw new ShareException(ShareErrors.FromWin32(error, ShareOperation.Connect), error);
                }

                var items = (NativeMethods.NetResource*)buffer;
                for (var i = 0u; i < count; i++)
                {
                    if (items[i].RemoteName is not null
                        && string.Equals(new string(items[i].RemoteName).TrimEnd('\\'), wanted, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
        }
        finally
        {
            _ = NativeMethods.WNetCloseEnum(handle);
            NativeMemory.Free(buffer);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se pudo cerrar la conexión con el recurso {ShareRoot} (error Win32 {Error}).")]
    private static partial void LogDisconnectFailed(ILogger logger, string shareRoot, int error);

    /// <summary><c>ERROR_NOT_CONNECTED</c>: ya estaba cerrada.</summary>
    private const int ErrorNotConnected = 2250;

    private sealed class WindowsShareConnection(string shareRoot, ILogger logger) : IShareConnection
    {
        private int _disposed;

        public string ShareRoot { get; } = shareRoot;

        public Task<bool> HasUseRecordAsync(CancellationToken ct) =>
            Run(() => UseRecordExists(ShareRoot), ct);

        public Task<bool> DirectoryExistsAsync(string path, CancellationToken ct) =>
            Task.Run(() => Directory.Exists(path), ct);

        public Task<string?> GetFinalPathAsync(string path, CancellationToken ct) =>
            Run(
                () =>
                {
                    using var handle = OpenDirectory(path);
                    var buffer = new char[1024];
                    var length = FinalPath(handle, buffer);
                    if (length >= buffer.Length)
                    {
                        buffer = new char[length];
                        length = FinalPath(handle, buffer);
                    }

                    return length == 0 || length >= buffer.Length ? null : new string(buffer, 0, (int)length);
                },
                ct);

        public Task ListDirectoryAsync(string path, CancellationToken ct) =>
            Run(
                () =>
                {
                    using var entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
                    _ = entries.MoveNext();
                },
                ct);

        public Task<ShareSpace> GetSpaceAsync(string path, CancellationToken ct) =>
            Run(
                () =>
                {
                    if (!NativeMethods.GetDiskFreeSpaceEx(WithTrailingSeparator(path), out var free, out var total, out _))
                    {
                        throw ShareErrors.FromLastPInvokeError(ShareOperation.FileAccess);
                    }

                    return new ShareSpace(ToInt64(free), ToInt64(total));
                },
                ct);

        public Task<string?> GetSmbDialectAsync(string path, CancellationToken ct) =>
            Run(
                () =>
                {
                    using var handle = OpenDirectory(path);

                    // FILE_REMOTE_PROTOCOL_INFO mide 116 o 180 bytes según la versión de winbase.h; un búfer mayor es válido.
                    var buffer = new byte[256];
                    if (!NativeMethods.GetFileInformationByHandleExBuffer(handle, NativeMethods.FileRemoteProtocolInfo, buffer, (uint)buffer.Length))
                    {
                        throw ShareErrors.FromLastPInvokeError(ShareOperation.FileAccess);
                    }

                    return FormatDialect(buffer);
                },
                ct);

        public Task WriteProbeFileAsync(string path, ReadOnlyMemory<byte> content, CancellationToken ct) =>
            Run(
                () =>
                {
                    var options = new FileStreamOptions
                    {
                        Mode = FileMode.CreateNew,
                        Access = FileAccess.ReadWrite,
                        Share = FileShare.None,
                        Options = FileOptions.DeleteOnClose | FileOptions.WriteThrough,
                    };
                    using var stream = new FileStream(path, options);
                    stream.Write(content.Span);
                    stream.Flush(flushToDisk: true);
                    stream.Position = 0;

                    var read = new byte[content.Length];
                    stream.ReadExactly(read);
                    if (!content.Span.SequenceEqual(read))
                    {
                        // Lo escrito no se lee igual: el recurso no es fiable para copias.
                        throw new ShareException(BackboneErrorCode.AccessDenied, 0);
                    }
                },
                ct);

        public Task<bool> FileExistsAsync(string path, CancellationToken ct) =>
            Run(() => File.Exists(path), ct);

        public Task DeleteFileAsync(string path, CancellationToken ct) =>
            Run(() => File.Delete(path), ct);

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return ValueTask.CompletedTask;
            }

            return new ValueTask(Task.Run(() =>
            {
                // Sin forzar: si otra parte del servicio tiene archivos abiertos en el recurso, no se le corta.
                var error = NativeMethods.WNetCancelConnection2(ShareRoot, 0, force: false);
                if (error is not NativeMethods.ErrorSuccess and not ErrorNotConnected)
                {
                    LogDisconnectFailed(logger, UntrustedText.Describe(ShareRoot), error);
                }
            }));
        }

        private static unsafe uint FinalPath(System.Runtime.InteropServices.SafeHandle handle, char[] buffer)
        {
            fixed (char* pointer = buffer)
            {
                return NativeMethods.GetFinalPathNameByHandle(handle, pointer, (uint)buffer.Length, NativeMethods.FileNameOpened);
            }
        }

        private static Microsoft.Win32.SafeHandles.SafeFileHandle OpenDirectory(string path)
        {
            var handle = NativeMethods.CreateFile(
                path,
                NativeMethods.FileReadAttributes,
                NativeMethods.FileShareRead | NativeMethods.FileShareWrite | NativeMethods.FileShareDelete,
                0,
                NativeMethods.OpenExisting,
                NativeMethods.FileFlagBackupSemantics,
                0);
            if (handle.IsInvalid)
            {
                var error = ShareErrors.FromLastPInvokeError(ShareOperation.FileAccess);
                handle.Dispose();
                throw error;
            }

            return handle;
        }

        private static string WithTrailingSeparator(string path) =>
            path.EndsWith('\\') ? path : path + "\\";

        private static long ToInt64(ulong value) => value > long.MaxValue ? long.MaxValue : (long)value;

        private static Task<object?> Run(Action action, CancellationToken ct) =>
            Run<object?>(
                () =>
                {
                    action();
                    return null;
                },
                ct);

        private static Task<T> Run<T>(Func<T> func, CancellationToken ct) =>
            Task.Run(
                () =>
                {
                    try
                    {
                        return func();
                    }
                    catch (Exception ex) when (ShareErrors.FromIoException(ex) is { } share && !ReferenceEquals(share, ex))
                    {
                        throw share;
                    }
                },
                ct);
    }
}
