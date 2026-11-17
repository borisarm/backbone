using System.Runtime.InteropServices;
using Backbone.Contracts;

namespace Backbone.Service.Nas;

/// <summary>Fase de la conexión en la que se produjo un error Win32 (decide el código por defecto).</summary>
internal enum ShareOperation
{
    /// <summary><c>WNetAddConnection2</c>: un error desconocido se trata como <see cref="BackboneErrorCode.NasUnreachable"/>.</summary>
    Connect = 0,

    /// <summary>E/S sobre el recurso ya conectado: un error desconocido se trata como <see cref="BackboneErrorCode.AccessDenied"/>.</summary>
    FileAccess = 1,
}

/// <summary>
/// Traducción de los códigos Win32 de la conexión SMB y de la E/S sobre el recurso a <see cref="BackboneErrorCode"/>
/// (§8.2 <c>ShareConnector</c>, RF-61). Códigos comprobados en Microsoft Learn (System Error Codes, WNetAddConnection2).
/// </summary>
internal static class ShareErrors
{
    // Credenciales rechazadas.
    internal const int ErrorInvalidPassword = 86;
    internal const int ErrorSessionCredentialConflict = 1219;
    internal const int ErrorNotAuthenticated = 1244;
    internal const int ErrorLogonFailure = 1326;
    internal const int ErrorAccountRestriction = 1327;
    internal const int ErrorInvalidLogonHours = 1328;
    internal const int ErrorInvalidWorkstation = 1329;
    internal const int ErrorPasswordExpired = 1330;
    internal const int ErrorAccountDisabled = 1331;
    internal const int ErrorLogonTypeNotGranted = 1385;
    internal const int ErrorAccountExpired = 1793;
    internal const int ErrorPasswordMustChange = 1907;
    internal const int ErrorAccountLockedOut = 1909;
    internal const int ErrorBadUsername = 2202;

    // Recurso inexistente.
    internal const int ErrorBadNetName = 67;
    internal const int NerrNetNameNotFound = 2310;

    // Servidor inalcanzable.
    internal const int ErrorRemNotList = 51;
    internal const int ErrorBadNetPath = 53;
    internal const int ErrorNetworkBusy = 54;
    internal const int ErrorUnexpNetErr = 59;
    internal const int ErrorNetNameDeleted = 64;
    internal const int ErrorSemTimeout = 121;
    internal const int ErrorNoNetOrBadPath = 1203;
    internal const int ErrorNoNetwork = 1222;
    internal const int ErrorConnectionRefused = 1225;
    internal const int ErrorNetworkUnreachable = 1231;
    internal const int ErrorHostUnreachable = 1232;
    internal const int ErrorPortUnreachable = 1234;
    internal const int ErrorConnectionAborted = 1236;
    internal const int ErrorRetry = 1237;
    internal const int ErrorHostDown = 1256;

    // Acceso denegado (p. ej. sin permiso de escritura).
    internal const int ErrorAccessDenied = 5;
    internal const int ErrorWriteProtect = 19;
    internal const int ErrorNetworkAccessDenied = 65;

    /// <summary>Código de negocio de un error Win32.</summary>
    /// <param name="win32Error">Código Win32.</param>
    /// <param name="operation">Fase: decide el código de un error no previsto.</param>
    public static BackboneErrorCode FromWin32(int win32Error, ShareOperation operation) => win32Error switch
    {
        // ERROR_SESSION_CREDENTIAL_CONFLICT: ya hay una sesión con ese servidor con otro usuario. No es una contraseña
        // errónea, pero lo que tiene que revisar el usuario es la credencial (el contrato no tiene un código más preciso).
        ErrorInvalidPassword or ErrorSessionCredentialConflict or ErrorNotAuthenticated or ErrorLogonFailure
            or ErrorAccountRestriction or ErrorInvalidLogonHours or ErrorInvalidWorkstation or ErrorPasswordExpired
            or ErrorAccountDisabled or ErrorLogonTypeNotGranted or ErrorAccountExpired or ErrorPasswordMustChange
            or ErrorAccountLockedOut or ErrorBadUsername => BackboneErrorCode.CredentialsRejected,

        ErrorBadNetName or NerrNetNameNotFound => BackboneErrorCode.ShareNotFound,

        ErrorRemNotList or ErrorBadNetPath or ErrorNetworkBusy or ErrorUnexpNetErr or ErrorNetNameDeleted or ErrorSemTimeout
            or ErrorNoNetOrBadPath or ErrorNoNetwork or ErrorConnectionRefused or ErrorNetworkUnreachable
            or ErrorHostUnreachable or ErrorPortUnreachable or ErrorConnectionAborted or ErrorRetry or ErrorHostDown
            => BackboneErrorCode.NasUnreachable,

        ErrorAccessDenied or ErrorWriteProtect or ErrorNetworkAccessDenied => BackboneErrorCode.AccessDenied,

        _ => operation == ShareOperation.Connect ? BackboneErrorCode.NasUnreachable : BackboneErrorCode.AccessDenied,
    };

    /// <summary>
    /// Traduce una excepción de E/S de .NET sobre el recurso. <see cref="UnauthorizedAccessException"/> es acceso denegado; de
    /// una <see cref="IOException"/> se toma el código Win32 de su <c>HResult</c>.
    /// </summary>
    /// <returns>La excepción traducida, o <see langword="null"/> si no es de E/S.</returns>
    public static ShareException? FromIoException(Exception exception) => exception switch
    {
        ShareException share => share,
        UnauthorizedAccessException => new ShareException(BackboneErrorCode.AccessDenied, ErrorAccessDenied),
        IOException io => FromHResult(io.HResult),
        _ => null,
    };

    /// <summary>Excepción para el código Win32 de la última llamada P/Invoke.</summary>
    public static ShareException FromLastPInvokeError(ShareOperation operation)
    {
        var error = Marshal.GetLastPInvokeError();
        return new ShareException(FromWin32(error, operation), error);
    }

    private static ShareException FromHResult(int hresult)
    {
        // HRESULT_FROM_WIN32: 0x8007xxxx. Cualquier otro valor no tiene código Win32: acceso denegado genérico.
        var win32 = (hresult & unchecked((int)0xFFFF_0000)) == unchecked((int)0x8007_0000) ? hresult & 0xFFFF : 0;
        return new ShareException(FromWin32(win32, ShareOperation.FileAccess), win32);
    }
}
