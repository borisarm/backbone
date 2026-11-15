using System.Security.Principal;

namespace Backbone.Service.Os;

/// <summary>
/// Identidad de un cliente del pipe tal como la ve el servidor, calculada de su token de suplantación (§7.3). Nunca se toma
/// de lo que diga el cliente.
/// </summary>
/// <param name="Sid">SID del usuario del token (<c>S-1-5-7</c> si el cliente se conectó como anónimo).</param>
/// <param name="ImpersonationLevel">Nivel de suplantación que el cliente concedió al conectar.</param>
/// <param name="IsElevated">El token tiene el grupo Administradores habilitado (no "solo denegar").</param>
internal sealed record ClientIdentity(SecurityIdentifier Sid, TokenImpersonationLevel ImpersonationLevel, bool IsElevated)
{
    /// <summary>Identidad de un cliente anónimo (el token no se puede abrir).</summary>
    public static ClientIdentity Anonymous { get; } =
        new(new SecurityIdentifier(WellKnownSidType.AnonymousSid, null), TokenImpersonationLevel.Anonymous, false);

    /// <summary>El cliente es LocalSystem (<c>S-1-5-18</c>).</summary>
    public bool IsLocalSystem => Sid.IsWellKnown(WellKnownSidType.LocalSystemSid);

    /// <summary>El nivel permite suplantar (§8.6 y §8.7 lo necesitan): <c>Impersonation</c> o <c>Delegation</c>.</summary>
    public bool AllowsImpersonation =>
        ImpersonationLevel is TokenImpersonationLevel.Impersonation or TokenImpersonationLevel.Delegation;
}
