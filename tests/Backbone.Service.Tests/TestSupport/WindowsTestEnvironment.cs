using System.Security.Principal;

namespace Backbone.Service.Tests.TestSupport;

/// <summary>Información del usuario que ejecuta las pruebas.</summary>
internal static class WindowsTestEnvironment
{
    private static readonly SecurityIdentifier AdministratorsSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    public static SecurityIdentifier CurrentUserSid
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User ?? throw new InvalidOperationException("The test process has no user SID.");
        }
    }

    /// <summary>El proceso de pruebas tiene el grupo Administradores habilitado (consola elevada o UAC desactivado, como en CI).</summary>
    public static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(AdministratorsSid);
        }
    }

    /// <summary>
    /// El token del proceso incluye Administradores, habilitado o como "solo denegar" (administrador sin elevar). Sirve de
    /// referencia para la consulta real de pertenencia, que no depende del token.
    /// </summary>
    public static bool TokenIncludesAdministrators
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            return IsElevated || identity.Claims.Any(c => c.Value == AdministratorsSid.Value);
        }
    }
}
