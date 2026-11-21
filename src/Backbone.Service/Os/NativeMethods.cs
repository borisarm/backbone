using System.Runtime.InteropServices;

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
    /// Llamadas de la lectura de los orígenes (<c>SourceFileSystem</c>, HU-08, §8.2). Van en una clase anidada para no chocar
    /// con las constantes y firmas que añadan otras historias a <see cref="NativeMethods"/>. Firmas y valores comprobados en
    /// Microsoft Learn (fileapi.h, winbase.h, ioapiset.h, processthreadsapi.h, winioctl.h, cfapi.h, ntifs.h).
    /// </summary>
    internal static partial class SourceIo
    {
        // ---- Códigos de error Win32 ----
        internal const int ErrorFileNotFound = 2;
        internal const int ErrorPathNotFound = 3;
        internal const int ErrorAccessDenied = 5;
        internal const int ErrorInvalidHandle = 6;
        internal const int ErrorNoMoreFiles = 18;
        internal const int ErrorSharingViolation = 32;
        internal const int ErrorLockViolation = 33;
        internal const int ErrorHandleEof = 38;
        internal const int ErrorInvalidName = 123;
        internal const int ErrorBadPathName = 161;
        internal const int ErrorFilenameExcedRange = 206;
        internal const int ErrorMoreData = 234;
        internal const int ErrorDirectory = 267;
        internal const int ErrorOperationAborted = 995;
        internal const int ErrorIoPending = 997;
        internal const int ErrorNotFound = 1168;

        /// <summary>
        /// <c>ERROR_CANT_ACCESS_FILE</c>: el filtro propietario de la etiqueta no está en la pila
        /// (<c>STATUS_IO_REPARSE_TAG_NOT_HANDLED</c>), §8.2 "Apertura para leer el contenido", punto 3.
        /// </summary>
        internal const int ErrorCantAccessFile = 1920;

        // ---- Acceso, compartición y marcas de CreateFile ----
        internal const uint GenericRead = 0x8000_0000;
        internal const uint Synchronize = 0x0010_0000;
        internal const uint FileReadAttributes = 0x0080;
        internal const uint FileListDirectory = 0x0001;

        /// <summary>Lectura y escritura, <b>sin</b> <c>FILE_SHARE_DELETE</c>: nadie puede renombrar ni sustituir el elemento abierto.</summary>
        internal const uint FileShareReadWrite = 0x1 | 0x2;

        internal const uint OpenExisting = 3;
        internal const uint FileFlagBackupSemantics = 0x0200_0000;
        internal const uint FileFlagOpenReparsePoint = 0x0020_0000;
        internal const uint FileFlagOverlapped = 0x4000_0000;
        internal const uint FileFlagSequentialScan = 0x0800_0000;

        internal const uint FileAttributeDirectory = 0x10;
        internal const uint FileAttributeReparsePoint = 0x400;

        // ---- FILE_INFO_BY_HANDLE_CLASS ----
        internal const int FileBasicInfo = 0;
        internal const int FileStandardInfo = 1;
        internal const int FileAttributeTagInfo = 9;
        internal const int FileIdBothDirectoryInfo = 10;
        internal const int FileIdBothDirectoryRestartInfo = 11;
        internal const int FileIdInfo = 18;

        // ---- GetFinalPathNameByHandle ----
        /// <summary><c>FILE_NAME_NORMALIZED</c> | <c>VOLUME_NAME_DOS</c>: <c>\\?\C:\…</c>.</summary>
        internal const uint VolumeNameDos = 0x0;

        /// <summary><c>FILE_NAME_NORMALIZED</c> | <c>VOLUME_NAME_GUID</c>: <c>\\?\Volume{…}\…</c>.</summary>
        internal const uint VolumeNameGuid = 0x1;

        /// <summary><c>FILE_NAME_NORMALIZED</c> | <c>VOLUME_NAME_NT</c>: <c>\Device\HarddiskVolumeN\…</c> (también en VSS).</summary>
        internal const uint VolumeNameNt = 0x2;

        // ---- Hilos ----
        /// <summary><c>THREAD_TERMINATE</c>: el acceso que exige <c>CancelSynchronousIo</c>.</summary>
        internal const uint ThreadTerminate = 0x0001;

        // ---- FSCTL ----
        /// <summary><c>FSCTL_QUERY_ALLOCATED_RANGES</c> = CTL_CODE(FILE_DEVICE_FILE_SYSTEM, 51, METHOD_NEITHER, FILE_READ_DATA).</summary>
        internal const uint FsctlQueryAllocatedRanges = 0x0009_40CF;

        // ---- Cloud Files API (CF_PLACEHOLDER_STATE) ----
        internal const uint CfPlaceholderStatePartial = 0x10;
        internal const uint CfPlaceholderStatePartiallyOnDisk = 0x20;
        internal const uint CfPlaceholderStateInvalid = 0xFFFF_FFFF;

        // ---- Compatibilidad de marcadores (ntifs.h) ----
        /// <summary><c>PHCM_EXPOSE_PLACEHOLDERS</c>.</summary>
        internal const sbyte PhcmExposePlaceholders = 2;

        /// <summary><c>FILE_ATTRIBUTE_TAG_INFO</c>.</summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct FileAttributeTagInformation
        {
            public uint FileAttributes;
            public uint ReparseTag;
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

        /// <summary><c>FILE_STANDARD_INFO</c>.</summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct FileStandardInformation
        {
            public long AllocationSize;
            public long EndOfFile;
            public uint NumberOfLinks;
            public byte DeletePending;
            public byte Directory;
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

        [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        internal static partial Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            nint securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            nint templateFile);

        /// <summary>
        /// <c>ReOpenFile</c>: abre otra vez el mismo objeto que <paramref name="originalFile"/> sin volver a resolver la ruta;
        /// <c>INVALID_HANDLE_VALUE</c> si falla.
        /// </summary>
        [LibraryImport("kernel32.dll", SetLastError = true)]
        internal static partial Microsoft.Win32.SafeHandles.SafeFileHandle ReOpenFile(SafeHandle originalFile, uint desiredAccess, uint shareMode, uint flagsAndAttributes);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetFileInformationByHandleEx(SafeHandle file, int fileInformationClass, out FileAttributeTagInformation fileInformation, uint bufferSize);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetFileInformationByHandleEx(SafeHandle file, int fileInformationClass, out FileBasicInformation fileInformation, uint bufferSize);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetFileInformationByHandleEx(SafeHandle file, int fileInformationClass, out FileStandardInformation fileInformation, uint bufferSize);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetFileInformationByHandleEx(SafeHandle file, int fileInformationClass, out FileIdInformation fileInformation, uint bufferSize);

        /// <summary>Enumeración de carpetas (<see cref="FileIdBothDirectoryInfo"/>): el búfer debe estar alineado a 8 bytes.</summary>
        [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static unsafe partial bool GetFileInformationByHandleExBuffer(SafeHandle file, int fileInformationClass, void* fileInformation, uint bufferSize);

        /// <summary>
        /// <c>GetFinalPathNameByHandleW</c>: longitud sin el NUL si cabe; si no cabe, el tamaño necesario con el NUL; 0 si falla.
        /// </summary>
        [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
        internal static unsafe partial uint GetFinalPathNameByHandle(SafeHandle file, char* filePath, uint filePathLength, uint flags);

        /// <summary>
        /// <c>DeviceIoControl</c> con <c>OVERLAPPED</c>: obligatorio si el handle se abrió con <c>FILE_FLAG_OVERLAPPED</c>.
        /// </summary>
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static unsafe partial bool DeviceIoControl(
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
        /// <c>CancelSynchronousIo</c>: cancela la E/S síncrona pendiente del hilo (handle con <see cref="ThreadTerminate"/>);
        /// <see langword="false"/> con <see cref="ErrorNotFound"/> si no había ninguna.
        /// </summary>
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CancelSynchronousIo(nint thread);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        internal static partial nint OpenThread(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint threadId);

        [LibraryImport("kernel32.dll")]
        internal static partial uint GetCurrentThreadId();

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CloseHandle(nint handle);

        /// <summary>
        /// <c>RtlSetProcessPlaceholderCompatibilityMode</c> (Windows 10 1803+): devuelve el modo anterior (<c>PHCM_*</c>) o un
        /// valor negativo si falla (<c>PHCM_ERROR_INVALID_PARAMETER</c> = -1, <c>PHCM_ERROR_NO_TEB</c> = -2).
        /// </summary>
        [LibraryImport("ntdll.dll")]
        internal static partial sbyte RtlSetProcessPlaceholderCompatibilityMode(sbyte mode);

        /// <summary>
        /// <c>CfGetPlaceholderStateFromAttributeTag</c> (cldapi.dll): solo interpreta los dos valores, no llama al proveedor.
        /// </summary>
        [LibraryImport("cldapi.dll")]
        internal static partial uint CfGetPlaceholderStateFromAttributeTag(uint fileAttributes, uint reparseTag);
    }
}
