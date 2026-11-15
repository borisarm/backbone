using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Backbone.Service.Os;

/// <summary>
/// Lee la identidad del token con el que el hilo actual está suplantando (dentro de
/// <see cref="System.IO.Pipes.NamedPipeServerStream.RunAsClient"/>).
/// </summary>
/// <remarks>
/// Abre <b>solo</b> el token del hilo: si el hilo no está suplantando, falla en lugar de recurrir al token del proceso
/// (que sería el de SYSTEM). Un token anónimo no se puede abrir (<c>ERROR_CANT_OPEN_ANONYMOUS</c>) y se trata como
/// <see cref="ClientIdentity.Anonymous"/>.
/// </remarks>
internal static class TokenInspector
{
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    public static ClientIdentity InspectImpersonationToken()
    {
        if (!NativeMethods.OpenThreadToken(
                NativeMethods.GetCurrentThread(),
                NativeMethods.TokenQuery | NativeMethods.TokenDuplicate,
                openAsSelf: true,
                out var token))
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == NativeMethods.ErrorCantOpenAnonymous)
            {
                return ClientIdentity.Anonymous;
            }

            throw new Win32Exception(error);
        }

        try
        {
            // WindowsIdentity duplica el handle; el original se cierra en finally.
            using var identity = new WindowsIdentity(token);
            var sid = identity.User ?? throw new InvalidOperationException("The client token has no user SID.");

            // IsInRole usa CheckTokenMembership: un grupo "solo denegar" (administrador sin elevar) no cuenta.
            var elevated = new WindowsPrincipal(identity).IsInRole(Administrators);
            return new ClientIdentity(sid, identity.ImpersonationLevel, elevated);
        }
        finally
        {
            NativeMethods.CloseHandle(token);
        }
    }
}
