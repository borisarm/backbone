using Backbone.Service.Os;
using Backbone.Service.Persistence;

namespace Backbone.Service.Ipc;

/// <summary>Motivo por el que se deniega a un cliente.</summary>
internal enum AuthorizationFailure
{
    None = 0,

    /// <summary>El cliente concedió nivel <c>Anonymous</c> o <c>Identification</c> (§7.3).</summary>
    ImpersonationLevelTooLow = 1,

    /// <summary>La cuenta no es miembro actual del grupo local Administradores (o no se pudo comprobar).</summary>
    NotAdministrator = 2,

    /// <summary>Administrador sin elevar que no está en <c>authorizedUsers</c>.</summary>
    NotInAuthorizedList = 3,

    /// <summary>La cuenta ya tiene el máximo de conexiones (§7.1, B-4).</summary>
    TooManyConnections = 4,
}

/// <summary>
/// Regla de autorización del modelo "solo cuentas administradoras" (§7.3, RNF-08). Se evalúa al identificar la conexión y
/// en <b>cada llamada</b>, contra la lista y la pertenencia actuales:
/// <list type="number">
/// <item>Nivel de suplantación <c>Impersonation</c> o superior; si no, se deniega.</item>
/// <item>LocalSystem: se admite.</item>
/// <item>Resto: tiene que ser miembro actual de Administradores (consultado al sistema).</item>
/// <item>Con el grupo Administradores habilitado en el token (elevado): se admite. Es la vía para dar de alta al primer
/// usuario autorizado (la ACL del pipe da acceso a Administradores).</item>
/// <item>Sin elevar: tiene que estar en <c>authorizedUsers</c>.</item>
/// </list>
/// </summary>
internal sealed class CallerAuthorizer(IAuthorizedUserList authorizedUsers, IAccountDirectory accounts)
{
    public AuthorizationFailure Check(ClientIdentity identity)
    {
        if (!identity.AllowsImpersonation)
        {
            return AuthorizationFailure.ImpersonationLevelTooLow;
        }

        if (identity.IsLocalSystem)
        {
            return AuthorizationFailure.None;
        }

        bool isAdministrator;
        try
        {
            isAdministrator = accounts.IsLocalAdministrator(identity.Sid);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        {
            // Falla cerrado: sin poder comprobar la pertenencia actual, no se autoriza.
            isAdministrator = false;
        }

        if (!isAdministrator)
        {
            return AuthorizationFailure.NotAdministrator;
        }

        if (identity.IsElevated)
        {
            return AuthorizationFailure.None;
        }

        return authorizedUsers.Contains(identity.Sid) ? AuthorizationFailure.None : AuthorizationFailure.NotInAuthorizedList;
    }
}
