using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Backbone.Service.Os;
using Microsoft.Win32.SafeHandles;
using static Backbone.Service.Os.NativeMethods;

namespace Backbone.Service.Persistence;

/// <summary>Motivo por el que se rechaza un archivo de la carpeta de datos (§8.2, ADR-14).</summary>
internal enum UntrustedFileReason
{
    /// <summary>Es un punto de reanálisis (enlace simbólico, <i>junction</i>, montaje…).</summary>
    ReparsePoint = 0,

    /// <summary>No es un archivo normal (es una carpeta), o la carpeta padre no es una carpeta.</summary>
    NotAFile = 1,

    /// <summary>Su propietario no es SYSTEM ni Administradores.</summary>
    UntrustedOwner = 2,

    /// <summary>Tiene más de un nombre (enlace duro): su contenido puede ser otro archivo cualquiera del volumen.</summary>
    HardLinked = 3,
}

/// <summary>
/// Un archivo de la carpeta de datos se ha rechazado sin leer su contenido (§8.2). El mensaje solo contiene la ruta y el
/// motivo.
/// </summary>
internal sealed class UntrustedFileException : Exception
{
    public UntrustedFileException()
    {
    }

    public UntrustedFileException(string message)
        : base(message)
    {
    }

    public UntrustedFileException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public UntrustedFileException(string path, UntrustedFileReason reason)
        : base($"The file '{path}' was rejected ({reason}).")
    {
        FilePath = path;
        Reason = reason;
    }

    public string? FilePath { get; }

    public UntrustedFileReason Reason { get; }
}

/// <summary>
/// Acceso de los almacenes a los archivos de <c>%ProgramData%\Backbone</c> (§8.2, ADR-14). Pieza común para
/// <c>settings.json</c>, <c>jobs.json</c>, las credenciales y cualquier otro archivo de la carpeta de datos.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Carpeta padre</b>: no se crea nunca (las subcarpetas fijas las crea <see cref="DataDirectoryGuard"/> al
/// arrancar, §8.3); si no existe, la operación falla. Se abre por handle con <c>FILE_FLAG_OPEN_REPARSE_POINT |
/// FILE_FLAG_BACKUP_SEMANTICS</c>, con acceso a datos y <b>sin <c>FILE_SHARE_DELETE</c></b>; sobre ese handle se comprueba
/// que es una carpeta, que no es un punto de reanálisis y que su propietario es de confianza (si no, se rechaza como un
/// archivo, con la ruta de la carpeta). El handle se mantiene abierto durante toda la operación: nadie puede renombrar la
/// carpeta ni sustituirla por un <i>junction</i> a mitad, y mientras esté abierta tampoco se puede renombrar ninguna
/// carpeta por encima de ella (NTFS no renombra una carpeta con archivos abiertos debajo).</item>
/// <item><b>Lectura</b>: <c>CreateFile</c> con <c>FILE_FLAG_OPEN_REPARSE_POINT</c> (nunca se sigue un enlace) y
/// <c>FILE_FLAG_BACKUP_SEMANTICS</c> (para poder abrir, y rechazar, una carpeta o <i>junction</i> en lugar del archivo).
/// Sobre ese mismo handle, antes de leer nada: no es punto de reanálisis ni carpeta (<c>FileAttributeTagInfo</c>), tiene un
/// solo nombre (<c>FileStandardInfo</c>) y su propietario es de confianza (<c>GetSecurityInfo</c>). El contenido se lee
/// de ese handle. Compartición: lectura y borrado (nadie puede tenerlo abierto para escribir mientras se lee; el renombrado
/// atómico de un escritor no se bloquea).</item>
/// <item><b>Escritura atómica</b>: temporal en la misma carpeta creado con <c>CREATE_NEW</c> y el descriptor explícito de
/// <see cref="FileTrustPolicy.BuildCreateSecurityDescriptor"/> (nunca existe con otra ACL ni hereda ACE de la carpeta);
/// se escribe y se vuelca a disco por handle; el destino, si existe, se valida como en la lectura; y se reemplaza
/// renombrando el handle del temporal (<c>FileRenameInfoEx</c> con semántica POSIX, o <c>FileRenameInfo</c> si el volumen
/// no la admite). El renombrado sustituye la entrada del destino sin seguirla. Si algo falla, el temporal se borra por
/// handle.</item>
/// <item><b>Borrado</b>: por handle con semántica POSIX (<c>FileDispositionInfoEx</c>): el nombre desaparece al cerrar el
/// handle aunque un lector tenga el archivo abierto, sin dejarlo "pendiente de borrado".</item>
/// <item><b>Concurrencia</b>: las aperturas reintentan, de forma breve y acotada, los errores transitorios que provoca otra
/// operación sobre el mismo nombre (<see cref="IsTransientOpenError"/>): un lector ve el contenido anterior completo, el
/// nuevo completo o ninguno, nunca un error. Las excepciones de E/S llevan el código Win32.</item>
/// </list>
/// Un rechazo lanza <see cref="UntrustedFileException"/>; qué se hace con él lo decide cada almacén.
/// </remarks>
internal sealed class TrustedFile(FileTrustPolicy policy)
{
    private const uint ReplaceCheckAccess = ReadControl | FileReadAttributes | Synchronize;

    public FileTrustPolicy Policy { get; } = policy;

    /// <summary>Lee el archivo completo, o <see langword="null"/> si no existe.</summary>
    /// <exception cref="UntrustedFileException">Se rechaza (no se lee nada).</exception>
    /// <exception cref="InvalidDataException">Mide más de <paramref name="maxBytes"/> (no se lee).</exception>
    /// <exception cref="DirectoryNotFoundException">La carpeta padre no existe.</exception>
    public async Task<byte[]?> ReadAllBytesAsync(string path, int maxBytes, CancellationToken ct)
    {
        var fullPath = Path.GetFullPath(path);
        using var parent = OpenParent(fullPath);
        var (handle, error) = await OpenRetryingAsync(fullPath, GenericRead, FileShareRead | FileShareDelete, ct).ConfigureAwait(false);
        using var owned = handle;
        if (handle is null)
        {
            return error is ErrorFileNotFound or ErrorPathNotFound ? null : throw ToException(error, fullPath, "opened");
        }

        var length = Validate(handle, fullPath);
        if (length > maxBytes)
        {
            throw new InvalidDataException($"The file '{fullPath}' is larger than {maxBytes} bytes.");
        }

        var buffer = new byte[length];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await RandomAccess.ReadAsync(handle, buffer.AsMemory(read), read, ct).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }

            read += n;
        }

        return read == buffer.Length ? buffer : buffer[..read];
    }

    /// <summary>Escribe el archivo de forma atómica: un lector ve el contenido anterior completo o el nuevo completo.</summary>
    /// <exception cref="UntrustedFileException">El destino existente o su carpeta se rechazan: no se escribe nada.</exception>
    /// <exception cref="DirectoryNotFoundException">La carpeta padre no existe (no se crea).</exception>
    public async Task WriteAllBytesAsync(string path, ReadOnlyMemory<byte> content, CancellationToken ct)
    {
        var fullPath = Path.GetFullPath(path);
        using var parent = OpenParent(fullPath);
        var directory = ParentOf(fullPath);

        var temp = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        using var handle = CreateTemporary(temp);
        var renamed = false;
        try
        {
            await RandomAccess.WriteAsync(handle, content, 0, ct).ConfigureAwait(false);
            if (!FlushFileBuffers(handle))
            {
                throw ToException(Marshal.GetLastPInvokeError(), temp, "flushed");
            }

            ct.ThrowIfCancellationRequested();
            await EnsureReplaceableAsync(fullPath, ct).ConfigureAwait(false);
            Rename(handle, fullPath);
            renamed = true;
        }
        finally
        {
            if (!renamed)
            {
                DeleteOnClose(handle);
            }
        }
    }

    /// <summary>
    /// Renombra <paramref name="sourcePath"/> a <paramref name="destinationPath"/> (reemplazándolo), por handle y validando
    /// los dos como en la lectura. Sirve para apartar un archivo dañado (p. ej. <c>settings.json.bad</c>).
    /// </summary>
    /// <returns><see langword="false"/> si el origen no existe.</returns>
    /// <exception cref="ArgumentException">Origen y destino no están en la misma carpeta.</exception>
    /// <exception cref="DirectoryNotFoundException">La carpeta no existe.</exception>
    public bool MoveReplacing(string sourcePath, string destinationPath)
    {
        var source = Path.GetFullPath(sourcePath);
        var destination = Path.GetFullPath(destinationPath);
        if (!string.Equals(ParentOf(source), ParentOf(destination), StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The source and the destination must be in the same directory.", nameof(destinationPath));
        }

        using var parent = OpenParent(source);
        using var handle = OpenRetrying(source, Delete | ReadControl | FileReadAttributes | Synchronize, FileShareRead | FileShareWrite | FileShareDelete, out var error);
        if (handle is null)
        {
            return error is ErrorFileNotFound or ErrorPathNotFound ? false : throw ToException(error, source, "opened");
        }

        Validate(handle, source);
        EnsureReplaceable(destination);
        Rename(handle, destination);
        return true;
    }

    /// <summary>
    /// Borra el archivo por handle (<c>FileDispositionInfo</c>), validándolo antes como en la lectura: un punto de reanálisis,
    /// una carpeta, un enlace duro o un archivo de propietario no fiable se rechazan y no se borran (no se sigue ningún enlace
    /// ni se borra nada fuera de la carpeta de datos).
    /// </summary>
    /// <returns><see langword="false"/> si no existe.</returns>
    /// <exception cref="UntrustedFileException">Se rechaza: no se borra nada.</exception>
    /// <exception cref="DirectoryNotFoundException">La carpeta padre no existe.</exception>
    public bool DeleteIfExists(string path)
    {
        var fullPath = Path.GetFullPath(path);
        using var parent = OpenParent(fullPath);
        using var handle = OpenRetrying(fullPath, Delete | ReadControl | FileReadAttributes | Synchronize, FileShareRead | FileShareWrite | FileShareDelete, out var error);
        if (handle is null)
        {
            return error is ErrorFileNotFound or ErrorPathNotFound ? false : throw ToException(error, fullPath, "opened");
        }

        Validate(handle, fullPath);
        var deleteError = MarkForDeletion(handle);
        if (deleteError != ErrorSuccess)
        {
            throw ToException(deleteError, fullPath, "deleted");
        }

        return true;
    }

    private static string ParentOf(string fullPath) =>
        Path.GetDirectoryName(fullPath) is { Length: > 0 } directory
            ? directory
            : throw new ArgumentException("The path has no parent directory.", nameof(fullPath));

    /// <summary>
    /// Abre y valida la carpeta padre de <paramref name="fullPath"/>. El handle (acceso a datos, sin
    /// <c>FILE_SHARE_DELETE</c>) debe mantenerse abierto hasta terminar la operación.
    /// </summary>
    private SafeFileHandle OpenParent(string fullPath)
    {
        var directory = ParentOf(fullPath);
        var handle = Open(directory, FileListDirectory | ReadControl | FileReadAttributes | Synchronize, FileShareRead | FileShareWrite, out var error);
        if (handle is null)
        {
            throw error is ErrorFileNotFound or ErrorPathNotFound
                ? new DirectoryNotFoundException($"The directory '{directory}' does not exist.", new Win32Exception(error))
                : ToException(error, directory, "opened");
        }

        try
        {
            if (!GetFileInformationByHandleEx(handle, FileAttributeTagInfo, out FileAttributeTagInformation tag, (uint)Marshal.SizeOf<FileAttributeTagInformation>()))
            {
                throw ToException(Marshal.GetLastPInvokeError(), directory, "inspected");
            }

            if ((tag.FileAttributes & FileAttributeReparsePoint) != 0)
            {
                throw new UntrustedFileException(directory, UntrustedFileReason.ReparsePoint);
            }

            if ((tag.FileAttributes & FileAttributeDirectory) == 0)
            {
                throw new UntrustedFileException(directory, UntrustedFileReason.NotAFile);
            }

            if (!Policy.IsTrustedOwner(ReadOwner(handle, directory)))
            {
                throw new UntrustedFileException(directory, UntrustedFileReason.UntrustedOwner);
            }

            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>Comprueba el objeto abierto y devuelve su longitud.</summary>
    private long Validate(SafeFileHandle handle, string path)
    {
        if (!GetFileInformationByHandleEx(handle, FileAttributeTagInfo, out FileAttributeTagInformation tag, (uint)Marshal.SizeOf<FileAttributeTagInformation>()))
        {
            throw ToException(Marshal.GetLastPInvokeError(), path, "inspected");
        }

        if ((tag.FileAttributes & FileAttributeReparsePoint) != 0)
        {
            throw new UntrustedFileException(path, UntrustedFileReason.ReparsePoint);
        }

        if ((tag.FileAttributes & FileAttributeDirectory) != 0)
        {
            throw new UntrustedFileException(path, UntrustedFileReason.NotAFile);
        }

        if (!GetFileInformationByHandleEx(handle, FileStandardInfo, out FileStandardInformation standard, (uint)Marshal.SizeOf<FileStandardInformation>()))
        {
            throw ToException(Marshal.GetLastPInvokeError(), path, "inspected");
        }

        if (standard.NumberOfLinks > 1)
        {
            throw new UntrustedFileException(path, UntrustedFileReason.HardLinked);
        }

        if (!Policy.IsTrustedOwner(ReadOwner(handle, path)))
        {
            throw new UntrustedFileException(path, UntrustedFileReason.UntrustedOwner);
        }

        return standard.EndOfFile;
    }

    /// <summary>El destino de un reemplazo, si existe, debe pasar la misma validación que un archivo que se lee.</summary>
    private void EnsureReplaceable(string path)
    {
        using var handle = OpenRetrying(path, ReplaceCheckAccess, FileShareRead | FileShareWrite | FileShareDelete, out var error);
        EnsureReplaceable(handle, error, path);
    }

    /// <inheritdoc cref="EnsureReplaceable(string)"/>
    private async Task EnsureReplaceableAsync(string path, CancellationToken ct)
    {
        var (handle, error) = await OpenRetryingAsync(path, ReplaceCheckAccess, FileShareRead | FileShareWrite | FileShareDelete, ct).ConfigureAwait(false);
        using var owned = handle;
        EnsureReplaceable(handle, error, path);
    }

    private void EnsureReplaceable(SafeFileHandle? handle, int error, string path)
    {
        if (handle is null)
        {
            if (error is ErrorFileNotFound or ErrorPathNotFound)
            {
                return;
            }

            throw ToException(error, path, "opened");
        }

        Validate(handle, path);
    }

    /// <summary>
    /// Error de apertura que puede deberse a otra operación en curso sobre el mismo nombre, y no a un rechazo:
    /// <c>ERROR_SHARING_VIOLATION</c> (tras el renombrado atómico, el handle de escritura del escritor sobre el archivo ya
    /// renombrado sigue abierto hasta que lo cierra) y <c>ERROR_ACCESS_DENIED</c> (es como <c>CreateFile</c> informa de un
    /// archivo pendiente de borrado, <c>STATUS_DELETE_PENDING</c>, hasta que se cierra el handle que lo borra). Se reintenta
    /// con las esperas de <see cref="TransientRetryDelaysMs"/>; si persiste, se informa del error.
    /// </summary>
    internal static bool IsTransientOpenError(int error) => error is ErrorSharingViolation or ErrorAccessDenied;

    /// <summary>Esperas (ms) entre reintentos de una apertura con error transitorio: en total, menos de medio segundo.</summary>
    internal static ReadOnlySpan<int> TransientRetryDelaysMs => [0, 1, 2, 5, 10, 20, 40, 80, 120, 150];

    /// <summary><see cref="Open"/> reintentando los errores transitorios (<see cref="IsTransientOpenError"/>).</summary>
    private static SafeFileHandle? OpenRetrying(string path, uint access, uint share, out int error)
    {
        for (var attempt = 0; ; attempt++)
        {
            var handle = Open(path, access, share, out error);
            if (handle is not null || !IsTransientOpenError(error) || attempt >= TransientRetryDelaysMs.Length)
            {
                return handle;
            }

            Thread.Sleep(TransientRetryDelaysMs[attempt]);
        }
    }

    /// <inheritdoc cref="OpenRetrying"/>
    private static async Task<(SafeFileHandle? Handle, int Error)> OpenRetryingAsync(string path, uint access, uint share, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var handle = Open(path, access, share, out var error);
            if (handle is not null || !IsTransientOpenError(error) || attempt >= TransientRetryDelaysMs.Length)
            {
                return (handle, error);
            }

            var delay = TransientRetryDelaysMs[attempt];
            if (delay == 0)
            {
                await Task.Yield();
                ct.ThrowIfCancellationRequested();
            }
            else
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Marca el archivo abierto para borrarlo con semántica POSIX (<c>FileDispositionInfoEx</c>): el nombre desaparece al
    /// cerrar este handle aunque haya lectores con el archivo abierto (que siguen leyendo el contenido anterior completo), en
    /// lugar de quedar "pendiente de borrado" hasta que cierre el último. Si el volumen no la admite, borrado clásico
    /// (<c>FileDispositionInfo</c>).
    /// </summary>
    /// <returns>El código Win32, o <see cref="ErrorSuccess"/>.</returns>
    private static int MarkForDeletion(SafeFileHandle handle)
    {
        var flags = BitConverter.GetBytes(FileDispositionFlagDelete | FileDispositionFlagPosixSemantics);
        if (SetFileInformationByHandle(handle, FileDispositionInfoEx, flags, (uint)flags.Length))
        {
            return ErrorSuccess;
        }

        var error = Marshal.GetLastPInvokeError();
        if (error is ErrorInvalidParameter or ErrorNotSupported)
        {
            return SetFileInformationByHandle(handle, FileDispositionInfo, [1], 1) ? ErrorSuccess : Marshal.GetLastPInvokeError();
        }

        return error;
    }

    private static SecurityIdentifier? ReadOwner(SafeFileHandle handle, string path)
    {
        var result = GetSecurityInfo(handle, SeFileObject, OwnerSecurityInformation, out var owner, out _, out _, out _, out var descriptor);
        if (result != ErrorSuccess)
        {
            throw ToException(result, path, "inspected");
        }

        try
        {
            return owner == 0 ? null : new SecurityIdentifier(owner);
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    private static SafeFileHandle? Open(string path, uint access, uint share, out int error)
    {
        var handle = CreateFile(path, access, share, 0, OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, 0);
        if (handle.IsInvalid)
        {
            error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            return null;
        }

        error = ErrorSuccess;
        return handle;
    }

    private SafeFileHandle CreateTemporary(string path)
    {
        var descriptor = Policy.BuildCreateSecurityDescriptor();
        var buffer = Marshal.AllocHGlobal(descriptor.Length);
        try
        {
            Marshal.Copy(descriptor, 0, buffer, descriptor.Length);
            var attributes = new SecurityAttributes
            {
                Length = (uint)Marshal.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = buffer,
                InheritHandle = 0,
            };
            var handle = CreateFileWithSecurity(
                path, GenericWrite | Delete | ReadControl | FileReadAttributes, 0, in attributes, CreateNew,
                FileAttributeNormal | FileFlagOpenReparsePoint, 0);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                handle.Dispose();
                throw ToException(error, path, "created");
            }

            return handle;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Renombra el objeto abierto a <paramref name="target"/>, reemplazando la entrada existente sin seguirla.</summary>
    private static void Rename(SafeFileHandle handle, string target)
    {
        var info = BuildRenameInfo(target, FileRenameFlagReplaceIfExists | FileRenameFlagPosixSemantics);
        if (SetFileInformationByHandle(handle, FileRenameInfoEx, info, (uint)info.Length))
        {
            return;
        }

        var error = Marshal.GetLastPInvokeError();
        if (error is ErrorInvalidParameter or ErrorNotSupported)
        {
            // Volumen sin FileRenameInfoEx: ReplaceIfExists = TRUE (BOOLEAN en el mismo desplazamiento que Flags).
            info = BuildRenameInfo(target, 1);
            if (SetFileInformationByHandle(handle, FileRenameInfo, info, (uint)info.Length))
            {
                return;
            }

            error = Marshal.GetLastPInvokeError();
        }

        throw ToException(error, target, "replaced");
    }

    /// <summary>
    /// <c>FILE_RENAME_INFO</c>: union { BOOLEAN ReplaceIfExists; DWORD Flags; } alineada al tamaño de un puntero,
    /// <c>RootDirectory</c> = NULL, <c>FileNameLength</c> en bytes y el nombre (ruta absoluta) terminado en NUL.
    /// </summary>
    private static byte[] BuildRenameInfo(string target, uint flags)
    {
        var rootOffset = IntPtr.Size;
        var lengthOffset = rootOffset + IntPtr.Size;
        var nameOffset = lengthOffset + sizeof(uint);
        var nameBytes = target.Length * sizeof(char);
        var size = Math.Max(nameOffset + nameBytes + sizeof(char), Marshal.SizeOf<RenameInfoHeader>());
        var info = new byte[size];
        BitConverter.TryWriteBytes(info.AsSpan(0), flags);
        BitConverter.TryWriteBytes(info.AsSpan(lengthOffset), (uint)nameBytes);
        MemoryMarshal.AsBytes(target.AsSpan()).CopyTo(info.AsSpan(nameOffset));
        return info;
    }

    /// <summary>Marca el temporal para borrarlo al cerrar el handle (sin volver a abrir la ruta). No lanza.</summary>
    private static void DeleteOnClose(SafeFileHandle handle)
    {
        if (!handle.IsInvalid && !handle.IsClosed)
        {
            _ = MarkForDeletion(handle);
        }
    }

    /// <summary>Excepción con la ruta, la acción y el código Win32 (nunca contenido).</summary>
    internal static Exception ToException(int error, string path, string action) =>
        error == ErrorAccessDenied
            ? new UnauthorizedAccessException($"The file '{path}' could not be {action} (Win32 error {error}).", new Win32Exception(error))
            : new IOException($"The file '{path}' could not be {action} (Win32 error {error}).", new Win32Exception(error)) { HResult = HResultFromWin32(error) };

    private static int HResultFromWin32(int error) => error <= 0 ? error : unchecked((int)(((uint)error & 0xFFFF) | 0x8007_0000));

    /// <summary>Tamaño mínimo de <c>FILE_RENAME_INFO</c> (con un carácter de nombre).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct RenameInfoHeader
    {
        public uint Flags;
        public nint RootDirectory;
        public uint FileNameLength;
        public char FileName;
    }
}
