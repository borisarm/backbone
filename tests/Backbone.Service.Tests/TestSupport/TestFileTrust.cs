using System.Security.Principal;
using Backbone.Service.Os;
using Backbone.Service.Persistence;

namespace Backbone.Service.Tests.TestSupport;

/// <summary>
/// <see cref="TrustedFile"/> para pruebas sobre carpetas temporales: sin elevar no se puede asignar Administradores como
/// propietario ni leer un archivo cuya DACL solo da acceso a SYSTEM y Administradores, así que se confía también en el
/// usuario de las pruebas, que es además el propietario de lo que se crea. El resto de comprobaciones (punto de
/// reanálisis, carpeta, enlace duro, propietario distinto) son las de producción.
/// </summary>
internal static class TestFileTrust
{
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    public static FileTrustPolicy CurrentUserPolicy
    {
        get
        {
            var user = WindowsTestEnvironment.CurrentUserSid;
            return new FileTrustPolicy(user, [LocalSystem, Administrators, user], [LocalSystem, Administrators, user]);
        }
    }

    public static TrustedFile CurrentUser => new(CurrentUserPolicy);
}
