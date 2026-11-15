using System.Runtime.InteropServices;

namespace Backbone.Service.Os;

/// <summary>
/// Llamadas a la API de Windows del servicio (P/Invoke generado en compilación con <see cref="LibraryImportAttribute"/>).
/// Firmas y códigos de error comprobados en Microsoft Learn (processthreadsapi.h, winbase.h, authz.h).
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
}
