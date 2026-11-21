using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Backbone.Service.Os;

/// <summary>
/// Llamadas a la API de Windows del servicio (P/Invoke generado en compilación con <see cref="LibraryImportAttribute"/>).
/// Firmas y códigos de error comprobados en Microsoft Learn (processthreadsapi.h, winbase.h, authz.h, dpapi.h, winnetwk.h, fileapi.h).
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

    // ---- Inspección de carpetas locales (HU-07, §8.3): ruta final, volumen y enumeración por handle ----

    internal const int ErrorNoMoreFiles = 18;

    /// <summary><c>FILE_FULL_DIRECTORY_INFO</c> por handle (minwinbase.h, <c>FileFullDirectoryInfo</c>).</summary>
    internal const int FileFullDirectoryInfo = 14;

    /// <summary><c>FileFullDirectoryRestartInfo</c>: como <see cref="FileFullDirectoryInfo"/>, desde el principio.</summary>
    internal const int FileFullDirectoryRestartInfo = 15;

    /// <summary><c>FILE_NAME_NORMALIZED | VOLUME_NAME_DOS</c> (0): ruta normalizada con letra de unidad, en forma <c>\\?\</c>.</summary>
    internal const uint FileNameNormalizedVolumeNameDos = 0x0;

    /// <summary><c>FILE_NAME_NORMALIZED | VOLUME_NAME_GUID</c>: ruta con <c>\\?\Volume{guid}\</c>.</summary>
    internal const uint FileNameNormalizedVolumeNameGuid = 0x1;

    internal const uint DriveUnknown = 0;
    internal const uint DriveNoRootDir = 1;
    internal const uint DriveRemovable = 2;
    internal const uint DriveFixed = 3;
    internal const uint DriveRemote = 4;

    /// <summary><c>GetFinalPathNameByHandleW</c>: longitud sin el nulo final; si no cabe, el tamaño necesario con el nulo; 0 si falla.</summary>
    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint GetFinalPathNameByHandle(SafeHandle file, [Out] char[] filePath, uint filePathLength, uint flags);

    /// <summary><c>GetVolumeInformationByHandleW</c>; solo se pide el nombre del sistema de archivos.</summary>
    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeInformationByHandleW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetVolumeInformationByHandle(
        SafeHandle file,
        nint volumeNameBuffer,
        uint volumeNameSize,
        nint volumeSerialNumber,
        nint maximumComponentLength,
        nint fileSystemFlags,
        [Out] char[] fileSystemNameBuffer,
        uint fileSystemNameSize);

    /// <summary><c>GetDriveTypeW</c>; la raíz debe terminar en <c>\</c> (letra de unidad o ruta de volumen GUID).</summary>
    [LibraryImport("kernel32.dll", EntryPoint = "GetDriveTypeW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint GetDriveType(string rootPathName);

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

    // ---- Conexión a recursos de red (winnetwk.h, mpr.dll; HU-05) ----

    /// <summary><c>RESOURCETYPE_DISK</c> de <c>NETRESOURCEW.dwType</c>.</summary>
    internal const uint ResourceTypeDisk = 0x1;

    /// <summary><c>CONNECT_TEMPORARY</c>: la conexión no se recuerda (sin letra de unidad tampoco se recordaría).</summary>
    internal const uint ConnectTemporary = 0x4;

    /// <summary><c>NETRESOURCEW</c>: cuatro <c>DWORD</c> y cuatro <c>LPWSTR</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct NetResource
    {
        public uint Scope;
        public uint Type;
        public uint DisplayType;
        public uint Usage;
        public char* LocalName;
        public char* RemoteName;
        public char* Comment;
        public char* Provider;
    }

    /// <summary>
    /// <c>WNetAddConnection2W</c>. Devuelve un código Win32 (no usa <c>SetLastError</c>). <paramref name="password"/> y
    /// <paramref name="userName"/> nunca son NULL: con NULL se usarían la contraseña o el usuario por defecto (la cuenta del
    /// equipo), y el servicio nunca conecta sin credencial explícita (§7.5).
    /// </summary>
    [LibraryImport("mpr.dll", EntryPoint = "WNetAddConnection2W")]
    internal static unsafe partial int WNetAddConnection2(in NetResource netResource, char* password, char* userName, uint flags);

    /// <summary><c>WNetCancelConnection2W</c>. Devuelve un código Win32 (no usa <c>SetLastError</c>).</summary>
    [LibraryImport("mpr.dll", EntryPoint = "WNetCancelConnection2W", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int WNetCancelConnection2(string name, uint flags, [MarshalAs(UnmanagedType.Bool)] bool force);

    /// <summary><c>GetDiskFreeSpaceExW</c>. Con una ruta UNC, la ruta debe acabar en barra invertida.</summary>
    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetDiskFreeSpaceEx(
        string directoryName,
        out ulong freeBytesAvailableToCaller,
        out ulong totalNumberOfBytes,
        out ulong totalNumberOfFreeBytes);

    /// <summary><c>FileRemoteProtocolInfo</c> de <c>FILE_INFO_BY_HANDLE_CLASS</c>: devuelve <c>FILE_REMOTE_PROTOCOL_INFO</c>.</summary>
    internal const int FileRemoteProtocolInfo = 13;

    /// <summary><c>WNNC_NET_SMB</c> de <c>FILE_REMOTE_PROTOCOL_INFO.Protocol</c>.</summary>
    internal const uint WnncNetSmb = 0x0002_0000;

    /// <summary><see cref="GetFileInformationByHandleEx(SafeHandle, int, out FileAttributeTagInformation, uint)"/> con un búfer de bytes.</summary>
    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetFileInformationByHandleExBuffer(
        SafeHandle file,
        int fileInformationClass,
        [Out] byte[] fileInformation,
        uint bufferSize);

    /// <summary><c>FILE_NAME_OPENED</c> | <c>VOLUME_NAME_DOS</c>: ruta abierta sin normalizar (SMB no admite normalizar).</summary>
    internal const uint FileNameOpened = 0x8;

    /// <summary>
    /// <c>GetFinalPathNameByHandleW</c>: longitud sin el NUL si cabe; si no cabe, el tamaño necesario con el NUL; 0 si falla.
    /// </summary>
    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    internal static unsafe partial uint GetFinalPathNameByHandle(SafeHandle file, char* filePath, uint filePathLength, uint flags);

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

    /// <summary>
    /// Resolución controlada de los orígenes (ADR-20, §8.3), plazos de las aperturas (CancelSynchronousIo) y Redirection Guard.
    /// Clase anidada propia para que sus nombres no choquen con los de otras historias que amplían <see cref="NativeMethods"/>.
    /// Firmas comprobadas en Microsoft Learn: winternl.h/ntifs.h (<c>NtCreateFile</c>, <c>NtQueryVolumeInformationFile</c>,
    /// <c>FILE_FS_DEVICE_INFORMATION</c>, <c>REPARSE_DATA_BUFFER</c>), ioapiset.h (<c>DeviceIoControl</c>,
    /// <c>CancelSynchronousIo</c>), processthreadsapi.h (<c>OpenThread</c>, <c>SetProcessMitigationPolicy</c>,
    /// <c>PROCESS_MITIGATION_REDIRECTION_TRUST_POLICY</c>) y cfapi.h
    /// (<c>CfGetPlaceholderStateFromAttributeTag</c>).
    /// </summary>
    internal static partial class Resolution
    {
        // ---- NtCreateFile (CreateDisposition y CreateOptions) ----

        /// <summary><c>FILE_OPEN</c>: abre lo que exista; falla si no existe.</summary>
        internal const uint FileOpen = 1;

        internal const uint FileSynchronousIoNonAlert = 0x0000_0020;
        internal const uint FileOpenForBackupIntent = 0x0000_4000;
        internal const uint FileOpenReparsePoint = 0x0020_0000;

        internal const uint ObjCaseInsensitive = 0x40;

        internal const uint FileReadAttributes = 0x0080;
        internal const uint FileListDirectory = 0x0001;
        internal const uint Synchronize = 0x0010_0000;
        internal const uint FileShareRead = 0x1;
        internal const uint FileShareWrite = 0x2;

        // ---- NTSTATUS ----
        internal const int StatusObjectNameInvalid = unchecked((int)0xC000_0033);
        internal const int StatusObjectNameNotFound = unchecked((int)0xC000_0034);
        internal const int StatusObjectPathNotFound = unchecked((int)0xC000_003A);
        internal const int StatusNoSuchFile = unchecked((int)0xC000_000F);
        internal const int StatusNotADirectory = unchecked((int)0xC000_0103);
        internal const int StatusCancelled = unchecked((int)0xC000_0120);

        // ---- Volumen (FS_INFORMATION_CLASS, FILE_FS_DEVICE_INFORMATION) ----

        /// <summary><c>FileFsDeviceInformation</c> de <c>FS_INFORMATION_CLASS</c>.</summary>
        internal const int FileFsDeviceInformationClass = 4;

        /// <summary><c>FILE_DEVICE_DISK</c>.</summary>
        internal const uint FileDeviceDisk = 0x0000_0007;

        /// <summary><c>FILE_REMOVABLE_MEDIA</c>.</summary>
        internal const uint FileRemovableMedia = 0x0000_0001;

        /// <summary><c>FILE_REMOTE_DEVICE</c>: sistema de archivos remoto (SMB, CIFS).</summary>
        internal const uint FileRemoteDevice = 0x0000_0010;

        /// <summary><c>FILE_FS_DEVICE_INFORMATION</c>: <c>DEVICE_TYPE DeviceType; ULONG Characteristics;</c>.</summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct FileFsDeviceInformation
        {
            public uint DeviceType;
            public uint Characteristics;
        }

        /// <summary>Devuelve un <c>NTSTATUS</c>.</summary>
        [LibraryImport("ntdll.dll")]
        internal static partial int NtQueryVolumeInformationFile(
            SafeFileHandle fileHandle,
            out IoStatusBlock ioStatusBlock,
            out FileFsDeviceInformation fsInformation,
            uint length,
            int fsInformationClass);

        // ---- Puntos de reanálisis ----

        /// <summary><c>FSCTL_GET_REPARSE_POINT</c> = <c>CTL_CODE(FILE_DEVICE_FILE_SYSTEM, 42, METHOD_BUFFERED, FILE_ANY_ACCESS)</c>.</summary>
        internal const uint FsctlGetReparsePoint = 0x0009_00A8;

        /// <summary><c>MAXIMUM_REPARSE_DATA_BUFFER_SIZE</c>.</summary>
        internal const int MaximumReparseDataBufferSize = 16 * 1024;

        internal const uint IoReparseTagMountPoint = 0xA000_0003;
        internal const uint IoReparseTagSymlink = 0xA000_000C;
        internal const uint IoReparseTagWof = 0x8000_0017;
        internal const uint IoReparseTagDedup = 0x8000_0013;
        internal const uint IoReparseTagHsm = 0xC000_0004;
        internal const uint IoReparseTagHsm2 = 0x8000_0006;

        /// <summary>Etiquetas de la Cloud Files API: <c>tag &amp; IO_REPARSE_TAG_CLOUD_MASK</c> (0xFFFF0FFF) = <c>IO_REPARSE_TAG_CLOUD</c>.</summary>
        internal const uint IoReparseTagCloudMask = 0xFFFF_0FFF;
        internal const uint IoReparseTagCloud = 0x9000_001A;

        /// <summary>Bit N de una etiqueta: sustituto de nombre (<c>IsReparseTagNameSurrogate</c>).</summary>
        internal const uint ReparseTagNameSurrogateBit = 0x2000_0000;

        /// <summary><c>SYMLINK_FLAG_RELATIVE</c>.</summary>
        internal const uint SymlinkFlagRelative = 0x1;

        internal const uint FileAttributeOffline = 0x0000_1000;
        internal const uint FileAttributeRecallOnOpen = 0x0004_0000;
        internal const uint FileAttributeRecallOnDataAccess = 0x0040_0000;

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool DeviceIoControl(
            SafeFileHandle device,
            uint ioControlCode,
            nint inBuffer,
            uint inBufferSize,
            [Out] byte[] outBuffer,
            uint outBufferSize,
            out uint bytesReturned,
            nint overlapped);

        // ---- Cloud Files API (cldapi.dll) ----

        internal const uint CfPlaceholderStatePartial = 0x10;
        internal const uint CfPlaceholderStatePartiallyOnDisk = 0x20;
        internal const uint CfPlaceholderStateInvalid = 0xFFFF_FFFF;

        /// <summary>Solo interpreta los dos valores; no llama al proveedor.</summary>
        [LibraryImport("cldapi.dll")]
        internal static partial uint CfGetPlaceholderStateFromAttributeTag(uint fileAttributes, uint reparseTag);

        // ---- Plazos: cancelación de E/S síncrona de un hilo ----

        /// <summary><c>THREAD_TERMINATE</c>: el derecho que exige <c>CancelSynchronousIo</c>.</summary>
        internal const uint ThreadTerminate = 0x0001;

        [LibraryImport("kernel32.dll")]
        internal static partial uint GetCurrentThreadId();

        [LibraryImport("kernel32.dll", SetLastError = true)]
        internal static partial SafeWaitHandle OpenThread(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint threadId);

        /// <summary>Si no hay nada que cancelar, devuelve <see langword="false"/> con <c>ERROR_NOT_FOUND</c>.</summary>
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CancelSynchronousIo(SafeWaitHandle thread);

        // ---- Redirection Guard ----

        /// <summary><c>ProcessRedirectionTrustPolicy</c> de <c>PROCESS_MITIGATION_POLICY</c> (winnt.h).</summary>
        internal const int ProcessRedirectionTrustPolicy = 16;

        /// <summary><c>PROCESS_MITIGATION_REDIRECTION_TRUST_POLICY.EnforceRedirectionTrust</c> (bit 0 de <c>Flags</c>).</summary>
        internal const uint EnforceRedirectionTrust = 0x1;

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetProcessMitigationPolicy(int mitigationPolicy, in uint buffer, nuint length);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetProcessMitigationPolicy(nint process, int mitigationPolicy, out uint buffer, nuint length);

        [LibraryImport("kernel32.dll")]
        internal static partial nint GetCurrentProcess();
    }

    /// <summary>
    /// Lectura de los orígenes (<c>SourceFileSystem</c>, HU-08, §8.2) y token de lectura (ADR-21). Solo lo que no está ya en
    /// <see cref="NativeMethods"/> ni en <see cref="Resolution"/> (<c>NtCreateFile</c>, <c>CancelSynchronousIo</c>,
    /// <c>OpenThread</c>, <c>CfGetPlaceholderStateFromAttributeTag</c>… se usan de allí). Firmas y valores comprobados en
    /// Microsoft Learn (winternl.h, ntifs.h, winbase.h, ioapiset.h, winioctl.h, securitybaseapi.h, processthreadsapi.h, winnt.h).
    /// </summary>
    internal static partial class SourceIo
    {
        // ---- Códigos de error Win32 ----
        internal const int ErrorInvalidHandle = 6;
        internal const int ErrorLockViolation = 33;
        internal const int ErrorInvalidName = 123;
        internal const int ErrorBadPathName = 161;
        internal const int ErrorFilenameExcedRange = 206;
        internal const int ErrorMoreData = 234;
        internal const int ErrorDirectory = 267;
        internal const int ErrorIoPending = 997;
        internal const int ErrorNotAllAssigned = 1300;

        /// <summary>
        /// <c>ERROR_CANT_ACCESS_FILE</c>: el filtro propietario de la etiqueta no está en la pila
        /// (<c>STATUS_IO_REPARSE_TAG_NOT_HANDLED</c>), §8.2 "Apertura para leer el contenido", punto 3.
        /// </summary>
        internal const int ErrorCantAccessFile = 1920;

        // ---- NtCreateFile: CreateOptions que añade la lectura de orígenes (ntifs.h) ----
        internal const uint FileDirectoryFile = 0x0000_0001;
        internal const uint FileNonDirectoryFile = 0x0000_0040;

        /// <summary><c>FILE_SEQUENTIAL_ONLY</c>: el equivalente de <c>FILE_FLAG_SEQUENTIAL_SCAN</c>.</summary>
        internal const uint FileSequentialOnly = 0x0000_0004;

        /// <summary><c>FILE_COMPLETE_IF_OPLOCKED</c>: no espera la ruptura de un oplock (§8.2 "Oplocks").</summary>
        internal const uint FileCompleteIfOplocked = 0x0000_0100;

        /// <summary><c>FILE_DELETE_ON_CLOSE</c>: prohibido en las aperturas de lectura (ADR-21).</summary>
        internal const uint FileDeleteOnClose = 0x0000_1000;

        /// <summary><c>FILE_OPEN_BY_FILE_ID</c>: prohibido en las aperturas de lectura (ADR-21).</summary>
        internal const uint FileOpenByFileId = 0x0000_2000;

        // ---- NTSTATUS ----
        /// <summary><c>STATUS_OPLOCK_BREAK_IN_PROGRESS</c>: un éxito (<c>NT_SUCCESS</c>) con <c>FILE_COMPLETE_IF_OPLOCKED</c>.</summary>
        internal const int StatusOplockBreakInProgress = 0x0000_0108;
        internal const int StatusAccessDenied = unchecked((int)0xC000_0022);
        internal const int StatusSharingViolation = unchecked((int)0xC000_0043);
        internal const int StatusFileIsADirectory = unchecked((int)0xC000_00BA);
        internal const int StatusIoReparseTagNotHandled = unchecked((int)0xC000_0279);

        // ---- FILE_INFO_BY_HANDLE_CLASS ----
        internal const int FileBasicInfo = 0;
        internal const int FileIdBothDirectoryInfo = 10;
        internal const int FileIdBothDirectoryRestartInfo = 11;
        internal const int FileIdInfo = 18;

        /// <summary><c>FILE_NAME_NORMALIZED</c> | <c>VOLUME_NAME_NT</c>: <c>\Device\HarddiskVolumeN\…</c> (también en VSS).</summary>
        internal const uint FileNameNormalizedVolumeNameNt = 0x2;

        /// <summary><c>FSCTL_QUERY_ALLOCATED_RANGES</c> = CTL_CODE(FILE_DEVICE_FILE_SYSTEM, 51, METHOD_NEITHER, FILE_READ_DATA).</summary>
        internal const uint FsctlQueryAllocatedRanges = 0x0009_40CF;

        /// <summary><c>PHCM_EXPOSE_PLACEHOLDERS</c>.</summary>
        internal const sbyte PhcmExposePlaceholders = 2;

        // ---- SECURITY_QUALITY_OF_SERVICE ----
        /// <summary><c>SecurityIdentification</c> de <c>SECURITY_IMPERSONATION_LEVEL</c>.</summary>
        internal const int SecurityIdentification = 1;

        /// <summary><c>SecurityImpersonation</c> de <c>SECURITY_IMPERSONATION_LEVEL</c>.</summary>
        internal const int SecurityImpersonation = 2;

        /// <summary><c>SECURITY_STATIC_TRACKING</c>.</summary>
        internal const byte SecurityStaticTracking = 0;

        // ---- Tokens ----
        internal const uint TokenAssignPrimary = 0x0001;
        internal const uint TokenImpersonate = 0x0004;
        internal const uint TokenAdjustPrivileges = 0x0020;

        /// <summary><c>TokenImpersonation</c> de <c>TOKEN_TYPE</c>.</summary>
        internal const int TokenTypeImpersonation = 2;

        /// <summary><c>TokenPrivileges</c> de <c>TOKEN_INFORMATION_CLASS</c>.</summary>
        internal const int TokenPrivilegesClass = 3;

        internal const uint SePrivilegeEnabled = 0x0000_0002;
        internal const uint SePrivilegeRemoved = 0x0000_0004;

        /// <summary><c>SECURITY_QUALITY_OF_SERVICE</c>: <c>DWORD Length; SECURITY_IMPERSONATION_LEVEL; BOOLEAN; BOOLEAN</c>.</summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct SecurityQualityOfService
        {
            public uint Length;
            public int ImpersonationLevel;
            public byte ContextTrackingMode;
            public byte EffectiveOnly;
        }

        /// <summary><c>FILE_BASIC_INFO</c> (cuatro <c>LARGE_INTEGER</c> y un <c>DWORD</c>; 40 bytes con relleno).</summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct FileBasicInformation
        {
            public long CreationTime;
            public long LastAccessTime;
            public long LastWriteTime;
            public long ChangeTime;
            public uint FileAttributes;
        }

        /// <summary><c>FILE_ID_INFO</c>: número de serie del volumen e identificador de 128 bits (<c>FILE_ID_128</c>).</summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct FileIdInformation
        {
            public ulong VolumeSerialNumber;
            public ulong FileIdLow;
            public ulong FileIdHigh;
        }

        /// <summary><c>FILE_ALLOCATED_RANGE_BUFFER</c>.</summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct FileAllocatedRangeBuffer
        {
            public long FileOffset;
            public long Length;
        }

        /// <summary><c>TOKEN_PRIVILEGES</c> con un solo <c>LUID_AND_ATTRIBUTES</c>.</summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct TokenPrivilege
        {
            public uint PrivilegeCount;
            public Luid Luid;
            public uint Attributes;
        }

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetFileInformationByHandleEx(SafeHandle file, int fileInformationClass, out FileBasicInformation fileInformation, uint bufferSize);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetFileInformationByHandleEx(SafeHandle file, int fileInformationClass, out FileIdInformation fileInformation, uint bufferSize);

        /// <summary>
        /// <c>DeviceIoControl</c> con <c>OVERLAPPED</c>: obligatorio si el handle es de E/S asíncrona (sin
        /// <c>FILE_SYNCHRONOUS_IO_*</c>). Distinta de <see cref="Resolution.DeviceIoControl"/>, que es síncrona.
        /// </summary>
        [LibraryImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static unsafe partial bool DeviceIoControlOverlapped(
            SafeHandle device,
            uint ioControlCode,
            void* inBuffer,
            uint inBufferSize,
            void* outBuffer,
            uint outBufferSize,
            uint* bytesReturned,
            NativeOverlapped* overlapped);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static unsafe partial bool GetOverlappedResult(SafeHandle file, NativeOverlapped* overlapped, out uint bytesTransferred, [MarshalAs(UnmanagedType.Bool)] bool wait);

        /// <summary><c>CancelIoEx</c> con <c>lpOverlapped</c> = NULL: cancela toda la E/S pendiente del handle, de cualquier hilo.</summary>
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CancelIoEx(SafeHandle file, nint overlapped);

        /// <summary>
        /// <c>RtlSetProcessPlaceholderCompatibilityMode</c> (Windows 10 1803+): devuelve el modo anterior (<c>PHCM_*</c>) o un
        /// valor negativo si falla (<c>PHCM_ERROR_INVALID_PARAMETER</c> = -1, <c>PHCM_ERROR_NO_TEB</c> = -2).
        /// </summary>
        [LibraryImport("ntdll.dll")]
        internal static partial sbyte RtlSetProcessPlaceholderCompatibilityMode(sbyte mode);

        [LibraryImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);

        /// <summary><c>DuplicateTokenEx</c>: <c>lpTokenAttributes</c> siempre NULL.</summary>
        [LibraryImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool DuplicateTokenEx(SafeHandle existingToken, uint desiredAccess, nint tokenAttributes, int impersonationLevel, int tokenType, out nint newToken);

        [LibraryImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool LookupPrivilegeValue(string? systemName, string name, out Luid luid);

        /// <summary>
        /// <c>AdjustTokenPrivileges</c>: devuelve <see langword="true"/> también si no ha podido asignar todo; entonces
        /// <c>GetLastError</c> es <see cref="ErrorNotAllAssigned"/>.
        /// </summary>
        [LibraryImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool AdjustTokenPrivileges(SafeHandle token, [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges, in TokenPrivilege newState, uint bufferLength, nint previousState, nint returnLength);

        [LibraryImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetTokenInformation(SafeHandle token, int tokenInformationClass, [Out] byte[]? tokenInformation, uint tokenInformationLength, out uint returnLength);

        /// <summary><c>SetThreadToken</c> con <c>Thread</c> = NULL: el hilo actual.</summary>
        [LibraryImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetThreadToken(nint thread, SafeHandle token);

        [LibraryImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool RevertToSelf();
    }
}
