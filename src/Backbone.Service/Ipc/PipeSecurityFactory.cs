using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Backbone.Service.Persistence;

namespace Backbone.Service.Ipc;

/// <summary>
/// ACL del pipe (§7.3, RNF-08).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Denegar todo a <c>NETWORK</c> (<c>S-1-5-2</c>): el pipe es solo local.</item>
/// <item>SYSTEM y <c>BUILTIN\Administrators</c>: control total.</item>
/// <item>Cada SID de <c>authorizedUsers</c>: <see cref="ClientRights"/>, lectura y escritura <b>sin</b>
/// <see cref="PipeAccessRights.CreateNewInstance"/> (que permitiría crear instancias falsas del servidor). Por eso el cliente
/// tiene que abrir el pipe con derechos explícitos (<see cref="ClientRights"/>) y no con <c>PipeDirection.InOut</c>, que pide
/// <c>GENERIC_WRITE</c> e incluye <c>FILE_APPEND_DATA</c> (= <c>FILE_CREATE_PIPE_INSTANCE</c>).</item>
/// <item>Opcionalmente, el SID del proceso servidor con control total (solo pruebas: un proceso no elevado necesita
/// <c>FILE_CREATE_PIPE_INSTANCE</c> para crear la segunda instancia; en producción el servidor es SYSTEM).</item>
/// </list>
/// </remarks>
internal static class PipeSecurityFactory
{
    /// <summary>Derechos que se conceden a un usuario autorizado y que debe pedir el cliente al abrir el pipe.</summary>
    public const PipeAccessRights ClientRights =
        PipeAccessRights.ReadData | PipeAccessRights.WriteData | PipeAccessRights.ReadAttributes
        | PipeAccessRights.ReadPermissions | PipeAccessRights.Synchronize;

    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier Network = new(WellKnownSidType.NetworkSid, null);

    public static PipeSecurity Create(IEnumerable<string> authorizedSids, SecurityIdentifier? additionalServerSid = null)
    {
        ArgumentNullException.ThrowIfNull(authorizedSids);

        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(Network, PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(LocalSystem, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(Administrators, PipeAccessRights.FullControl, AccessControlType.Allow));

        if (additionalServerSid is not null && additionalServerSid != LocalSystem)
        {
            security.AddAccessRule(new PipeAccessRule(additionalServerSid, PipeAccessRights.FullControl, AccessControlType.Allow));
        }

        foreach (var value in authorizedSids)
        {
            SecurityIdentifier sid;
            try
            {
                sid = new SecurityIdentifier(value);
            }
            catch (ArgumentException)
            {
                continue;
            }

            // Defensa adicional: solo SID de cuenta, nunca grupos ni SID bien conocidos (Everyone, Usuarios…).
            if (SettingsStore.IsAccountSid(sid))
            {
                security.AddAccessRule(new PipeAccessRule(sid, ClientRights, AccessControlType.Allow));
            }
        }

        return security;
    }
}
