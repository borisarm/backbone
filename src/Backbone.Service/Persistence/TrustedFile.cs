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

    /// <summary>No es un archivo normal (es una carpeta).</summary>
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
/// </list>
/// Un rechazo lanza <see cref="UntrustedFileException"/>; qué se hace con él lo decide cada almacén.
/// </remarks>
internal sealed class TrustedFile(FileTrustPolicy policy)
{
    public FileTrustPolicy Policy { get; } = policy;

    /// <summary>Lee el archivo completo, o <see langword="null"/> si no existe.</summary>
    /// <exception cref="UntrustedFileException">Se rechaza (no se lee nada).</exception>
    /// <exception cref="InvalidDataException">Mide más de <paramref name="maxBytes"/> (no se lee).</exception>
    public async Task<byte[]?> ReadAllBytesAsync(string path, int maxBytes, CancellationToken ct)
    {
        var fullPath = Path.GetFullPath(path);
        using var handle = Open(fullPath, GenericRead, FileShareRead | FileShareDelete, out var error);
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
    /// <exception cref="UntrustedFileException">El destino existente se rechaza: no se reemplaza.</exception>
    public async Task WriteAllBytesAsync(string path, ReadOnlyMemory<byte> content, CancellationToken ct)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("The path has no parent directory.", nameof(path));
        Directory.CreateDirectory(directory);

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
            EnsureReplaceable(fullPath);
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
    public bool MoveReplacing(string sourcePath, string destinationPath)
    {
        var source = Path.GetFullPath(sourcePath);
        var destination = Path.GetFullPath(destinationPath);
        using var handle = Open(source, Delete | ReadControl | FileReadAttributes | Synchronize, FileShareRead | FileShareWrite | FileShareDelete, out var error);
        if (handle is null)
        {
            return error is ErrorFileNotFound or ErrorPathNotFound ? false : throw ToException(error, source, "opened");
        }

        Validate(handle, source);
        EnsureReplaceable(destination);
        Rename(handle, destination);
        return true;
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
        using var handle = Open(path, ReadControl | FileReadAttributes | Synchronize, FileShareRead | FileShareWrite | FileShareDelete, out var error);
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
            _ = SetFileInformationByHandle(handle, FileDispositionInfo, [1], 1);
        }
    }

    private static Exception ToException(int error, string path, string action) =>
        error == ErrorAccessDenied
            ? new UnauthorizedAccessException($"The file '{path}' could not be {action}.", new Win32Exception(error))
            : new IOException($"The file '{path}' could not be {action}.", new Win32Exception(error));

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
