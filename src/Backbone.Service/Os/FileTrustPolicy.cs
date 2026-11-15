using System.Collections.Immutable;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Backbone.Service.Os;

/// <summary>
/// Qué propietarios se aceptan al leer un archivo de la carpeta de datos y con qué descriptor se crean los archivos nuevos
/// (§8.2, ADR-14).
/// </summary>
/// <remarks>
/// En producción es siempre <see cref="Default"/>: se aceptan SYSTEM (<c>S-1-5-18</c>) y Administradores
/// (<c>S-1-5-32-544</c>), y lo creado tiene propietario Administradores y la DACL protegida de RNF-07. Las pruebas sin
/// elevar no pueden asignar Administradores como propietario ni leer un archivo cuya DACL solo da acceso a SYSTEM y
/// Administradores, así que construyen una política que añade al usuario de las pruebas.
/// </remarks>
internal sealed class FileTrustPolicy
{
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    public FileTrustPolicy(
        SecurityIdentifier createOwner,
        IEnumerable<SecurityIdentifier> trustedOwners,
        IEnumerable<SecurityIdentifier> fullControlSids)
    {
        ArgumentNullException.ThrowIfNull(createOwner);
        CreateOwner = createOwner;
        TrustedOwners = [.. trustedOwners];
        FullControlSids = [.. fullControlSids];
        if (!IsTrustedOwner(createOwner))
        {
            throw new ArgumentException("The owner of created files must be a trusted owner.", nameof(createOwner));
        }
    }

    /// <summary>Política de producción (§8.2).</summary>
    public static FileTrustPolicy Default { get; } = new(Administrators, [LocalSystem, Administrators], [LocalSystem, Administrators]);

    /// <summary>Propietario de los archivos que se crean.</summary>
    public SecurityIdentifier CreateOwner { get; }

    public ImmutableArray<SecurityIdentifier> TrustedOwners { get; }

    /// <summary>SID con control total en la DACL protegida de los archivos que se crean.</summary>
    public ImmutableArray<SecurityIdentifier> FullControlSids { get; }

    public bool IsTrustedOwner(SecurityIdentifier? owner) => owner is not null && TrustedOwners.Contains(owner);

    /// <summary>Descriptor (forma binaria autorrelativa) con que se crea un archivo: propietario y DACL protegida, sin herencia.</summary>
    public byte[] BuildCreateSecurityDescriptor()
    {
        var security = new FileSecurity();
        security.SetOwner(CreateOwner);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in FullControlSids)
        {
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        }

        return security.GetSecurityDescriptorBinaryForm();
    }
}
