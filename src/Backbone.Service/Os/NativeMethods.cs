using System.Runtime.InteropServices;

namespace Backbone.Service.Os;

/// <summary>
/// Llamadas a la API de Windows del servicio (P/Invoke generado en compilación con <see cref="LibraryImportAttribute"/>).
/// Firmas y códigos de error comprobados en Microsoft Learn (processthreadsapi.h, winbase.h, authz.h, dpapi.h).
/// </summary>
internal static partial class NativeMethods
{
    // ---- Códigos de error Win32 ----
    internal const int ErrorInsufficientBuffer = 122;
    internal const int ErrorNoneMapped = 1332;
    internal const int ErrorCantOpenAnonymous = 1347;

    // ---- Derechos sobre tokens (Access Rights for Access-Token Objects) ----
    internal const uint TokenDuplicate = 0x0002;
    internal const uint TokenQuery = 0x0008;

    // ---- Authz ----
    internal const uint AuthzRmFlagNoAudit = 0x1;

    /// <summary><c>AuthzContextInfoGroupsSids</c> de <c>AUTHZ_CONTEXT_INFORMATION_CLASS</c>: devuelve un <c>TOKEN_GROUPS</c>.</summary>
    internal const int AuthzContextInfoGroupsSids = 2;

    /// <summary>Valores de <c>SID_NAME_USE</c>.</summary>
    internal const int SidTypeUser = 1;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [LibraryImport("kernel32.dll")]
    internal static partial nint GetCurrentThread();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint handle);

    /// <summary>
    /// Abre el token del hilo. Con <paramref name="openAsSelf"/> = <see langword="true"/> la comprobación de acceso se hace con
    /// el contexto del proceso, lo que permite abrir un token de nivel <c>SecurityIdentification</c>. Un token anónimo no se
    /// puede abrir: falla con <see cref="ErrorCantOpenAnonymous"/>.
    /// </summary>
    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool OpenThreadToken(
        nint threadHandle,
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool openAsSelf,
        out nint tokenHandle);

    [LibraryImport("advapi32.dll", EntryPoint = "LookupAccountNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool LookupAccountName(
        string? systemName,
        string accountName,
        [Out] byte[]? sid,
        ref uint cbSid,
        [Out] char[]? referencedDomainName,
        ref uint cchReferencedDomainName,
        out int use);

    [LibraryImport("advapi32.dll", EntryPoint = "LookupAccountSidW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool LookupAccountSid(
        string? systemName,
        byte[] sid,
        [Out] char[]? name,
        ref uint cchName,
        [Out] char[]? referencedDomainName,
        ref uint cchReferencedDomainName,
        out int use);

    [LibraryImport("authz.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AuthzInitializeResourceManager(
        uint flags,
        nint pfnDynamicAccessCheck,
        nint pfnComputeDynamicGroups,
        nint pfnFreeDynamicGroups,
        string? resourceManagerName,
        out nint resourceManager);

    [LibraryImport("authz.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AuthzInitializeContextFromSid(
        uint flags,
        byte[] userSid,
        nint resourceManager,
        nint expirationTime,
        Luid identifier,
        nint dynamicGroupArgs,
        out nint clientContext);

    [LibraryImport("authz.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AuthzGetInformationFromContext(
        nint clientContext,
        int infoClass,
        uint bufferSize,
        out uint sizeRequired,
        nint buffer);

    [LibraryImport("authz.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AuthzFreeContext(nint clientContext);

    [LibraryImport("authz.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AuthzFreeResourceManager(nint resourceManager);

    // ---- Archivos y seguridad de objetos (fileapi.h, winbase.h, aclapi.h, securitybaseapi.h) ----
    internal const int ErrorSuccess = 0;
    internal const int ErrorFileNotFound = 2;
    internal const int ErrorPathNotFound = 3;
    internal const int ErrorAccessDenied = 5;
    internal const int ErrorSharingViolation = 32;
    internal const int ErrorNotSupported = 50;
    internal const int ErrorInvalidParameter = 87;
    internal const int ErrorAlreadyExists = 183;

    internal const uint GenericRead = 0x8000_0000;
    internal const uint GenericWrite = 0x4000_0000;
    internal const uint Delete = 0x0001_0000;
    internal const uint ReadControl = 0x0002_0000;
    internal const uint WriteDac = 0x0004_0000;
    internal const uint Synchronize = 0x0010_0000;
    internal const uint FileReadAttributes = 0x0080;

    /// <summary><c>FILE_LIST_DIRECTORY</c> / <c>FILE_READ_DATA</c>: acceso a datos, para que el modo de compartición cuente.</summary>
    internal const uint FileListDirectory = 0x0001;

    internal const uint FileShareRead = 0x1;
    internal const uint FileShareWrite = 0x2;
    internal const uint FileShareDelete = 0x4;

    internal const uint CreateNew = 1;
    internal const uint OpenExisting = 3;
    internal const uint FileFlagBackupSemantics = 0x0200_0000;
    internal const uint FileFlagOpenReparsePoint = 0x0020_0000;

    internal const uint FileAttributeDirectory = 0x10;
    internal const uint FileAttributeReparsePoint = 0x400;
    internal const uint FileAttributeNormal = 0x80;

    /// <summary>Valores de <c>FILE_INFO_BY_HANDLE_CLASS</c>.</summary>
    internal const int FileStandardInfo = 1;
    internal const int FileRenameInfo = 3;
    internal const int FileDispositionInfo = 4;
    internal const int FileAttributeTagInfo = 9;
    internal const int FileDispositionInfoEx = 21;
    internal const int FileRenameInfoEx = 22;

    /// <summary>
    /// Indicadores de <c>FILE_DISPOSITION_INFO_EX.Flags</c> (<c>FILE_DISPOSITION_FLAG_*</c>). Con semántica POSIX el nombre
    /// desaparece al cerrar el handle que borra, aunque otros lo tengan abierto (sin quedar "pendiente de borrado").
    /// </summary>
    internal const uint FileDispositionFlagDelete = 0x1;
    internal const uint FileDispositionFlagPosixSemantics = 0x2;

    /// <summary>Indicadores de <c>FILE_RENAME_INFO.Flags</c> con <see cref="FileRenameInfoEx"/> (ntifs.h, <c>FILE_RENAME_FLAG_*</c>).</summary>
    internal const uint FileRenameFlagReplaceIfExists = 0x1;
    internal const uint FileRenameFlagPosixSemantics = 0x2;

    /// <summary><c>SE_FILE_OBJECT</c> de <c>SE_OBJECT_TYPE</c>.</summary>
    internal const int SeFileObject = 1;

    /// <summary>Bits de <c>SECURITY_INFORMATION</c>.</summary>
    internal const uint OwnerSecurityInformation = 0x1;
    internal const uint DaclSecurityInformation = 0x4;
    internal const uint UnprotectedDaclSecurityInformation = 0x2000_0000;
    internal const uint ProtectedDaclSecurityInformation = 0x8000_0000;

    [StructLayout(LayoutKind.Sequential)]
    internal struct SecurityAttributes
    {
        public uint Length;
        public nint SecurityDescriptor;
        public int InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileAttributeTagInformation
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    /// <summary><c>FILE_STANDARD_INFO</c> (winbase.h).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct FileStandardInformation
    {
        public long AllocationSize;
        public long EndOfFile;
        public uint NumberOfLinks;
        public byte DeletePending;
        public byte Directory;
    }

    /// <summary><c>CreateFileW</c> con descriptor de seguridad explícito para el objeto que se crea.</summary>
    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileWithSecurity(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        in SecurityAttributes securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FlushFileBuffers(SafeHandle file);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    /// <summary>Crea una carpeta con el descriptor de seguridad indicado (atómico: nunca existe con otra ACL).</summary>
    [LibraryImport("kernel32.dll", EntryPoint = "CreateDirectoryW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CreateDirectory(string pathName, in SecurityAttributes securityAttributes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetFileInformationByHandleEx(
        SafeHandle file,
        int fileInformationClass,
        out FileAttributeTagInformation fileInformation,
        uint bufferSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetFileInformationByHandleEx(
        SafeHandle file,
        int fileInformationClass,
        out FileStandardInformation fileInformation,
        uint bufferSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetFileInformationByHandle(
        SafeHandle file,
        int fileInformationClass,
        byte[] fileInformation,
        uint bufferSize);

    /// <summary>Devuelve un código Win32 (no usa <c>SetLastError</c>). El descriptor se libera con <see cref="LocalFree"/>.</summary>
    [LibraryImport("advapi32.dll")]
    internal static partial int GetSecurityInfo(
        SafeHandle handle,
        int objectType,
        uint securityInfo,
        out nint owner,
        out nint group,
        out nint dacl,
        out nint sacl,
        out nint securityDescriptor);

    /// <summary>Devuelve un código Win32 (no usa <c>SetLastError</c>).</summary>
    [LibraryImport("advapi32.dll")]
    internal static partial int SetSecurityInfo(
        SafeHandle handle,
        int objectType,
        uint securityInfo,
        nint owner,
        nint group,
        byte[]? dacl,
        nint sacl);

    [LibraryImport("advapi32.dll")]
    internal static partial uint GetSecurityDescriptorLength(nint securityDescriptor);

    [LibraryImport("kernel32.dll")]
    internal static partial nint LocalFree(nint memory);

    // ---- Creación de carpetas relativa a un handle (winternl.h, ntifs.h, ntdef.h) ----

    /// <summary><c>FILE_CREATE</c>: falla si ya existe una entrada con ese nombre (no se abre ni se sigue).</summary>
    internal const uint NtFileCreate = 2;

    internal const uint NtFileDirectoryFile = 0x0000_0001;
    internal const uint NtFileSynchronousIoNonAlert = 0x0000_0020;
    internal const uint NtFileOpenReparsePoint = 0x0020_0000;

    internal const uint ObjCaseInsensitive = 0x40;

    /// <summary><c>STATUS_OBJECT_NAME_COLLISION</c>.</summary>
    internal const int StatusObjectNameCollision = unchecked((int)0xC000_0035);

    /// <summary><c>UNICODE_STRING</c> (longitudes en bytes, sin NUL final obligatorio).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public nint Buffer;
    }

    /// <summary><c>OBJECT_ATTRIBUTES</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct ObjectAttributes
    {
        public uint Length;
        public nint RootDirectory;
        public nint ObjectName;
        public uint Attributes;
        public nint SecurityDescriptor;
        public nint SecurityQualityOfService;
    }

    /// <summary><c>IO_STATUS_BLOCK</c>: union { NTSTATUS Status; PVOID Pointer; } + <c>ULONG_PTR Information</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct IoStatusBlock
    {
        public nint Status;
        public nint Information;
    }

    /// <summary>Devuelve un <c>NTSTATUS</c> (no usa <c>SetLastError</c>); se traduce con <see cref="RtlNtStatusToDosError"/>.</summary>
    [LibraryImport("ntdll.dll")]
    internal static partial int NtCreateFile(
        out Microsoft.Win32.SafeHandles.SafeFileHandle fileHandle,
        uint desiredAccess,
        in ObjectAttributes objectAttributes,
        out IoStatusBlock ioStatusBlock,
        nint allocationSize,
        uint fileAttributes,
        uint shareAccess,
        uint createDisposition,
        uint createOptions,
        nint eaBuffer,
        uint eaLength);

    [LibraryImport("ntdll.dll")]
    internal static partial int RtlNtStatusToDosError(int status);

    // ---- DPAPI (dpapi.h, crypt32.dll) ----

    /// <summary><c>CRYPTPROTECT_UI_FORBIDDEN</c>: nunca se muestra interfaz (el servicio corre en la sesión 0).</summary>
    internal const uint CryptProtectUiForbidden = 0x1;

    /// <summary><c>CRYPTPROTECT_LOCAL_MACHINE</c>: clave del equipo, no del usuario (ADR-07).</summary>
    internal const uint CryptProtectLocalMachine = 0x4;

    /// <summary><c>DATA_BLOB</c> (<c>CRYPT_INTEGER_BLOB</c>): <c>DWORD cbData; BYTE *pbData;</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct DataBlob
    {
        public uint Size;
        public nint Data;
    }

    /// <summary>
    /// <c>CryptProtectData</c>. <c>szDataDescr</c>, <c>pvReserved</c> y <c>pPromptStruct</c> se pasan siempre a NULL. El
    /// <c>pbData</c> de salida se libera con <see cref="LocalFree"/>.
    /// </summary>
    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CryptProtectData(
        in DataBlob dataIn,
        nint dataDescription,
        in DataBlob optionalEntropy,
        nint reserved,
        nint promptStruct,
        uint flags,
        out DataBlob dataOut);

    /// <summary>
    /// <c>CryptUnprotectData</c>. <c>ppszDataDescr</c>, <c>pvReserved</c> y <c>pPromptStruct</c> se pasan siempre a NULL.
    /// El <c>pbData</c> de salida (texto en claro) se pone a cero y se libera con <see cref="LocalFree"/>.
    /// </summary>
    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CryptUnprotectData(
        in DataBlob dataIn,
        nint dataDescription,
        in DataBlob optionalEntropy,
        nint reserved,
        nint promptStruct,
        uint flags,
        out DataBlob dataOut);
}
