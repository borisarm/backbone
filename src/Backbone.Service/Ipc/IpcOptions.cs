using System.Security.Principal;
using Backbone.Contracts;
using Backbone.Service.Os;

namespace Backbone.Service.Ipc;

/// <summary>
/// Parámetros del servidor IPC. Se fijan en código (HT-03 I-5: nada se lee de configuración); las pruebas cambian el nombre
/// del pipe y los tiempos.
/// </summary>
internal sealed class IpcOptions
{
    /// <summary>Máximo de conexiones simultáneas de una misma cuenta (§7.1, B-4).</summary>
    public const int DefaultMaxConnectionsPerSid = 4;

    public string PipeName { get; init; } = BackboneIpc.PipeName;

    public int MaxConnections { get; init; } = BackboneIpc.MaxConnections;

    public int MaxConnectionsPerSid { get; init; } = DefaultMaxConnectionsPerSid;

    public int MaxMessageBytes { get; init; } = BackboneIpc.MaxMessageBytes;

    /// <summary>
    /// Tiempo máximo entre la conexión y el primer mensaje (sin él no se conoce la identidad del cliente). Corto (B-2): una
    /// conexión sin identificar ocupa una de las <see cref="MaxConnections"/> instancias del pipe.
    /// </summary>
    public TimeSpan IdentificationTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Tiempo máximo que una conexión rechazada (<c>NotAuthorized</c>) sigue abierta esperando a que el cliente cierre. Da
    /// tiempo a que el cliente procese la respuesta antes de que el servidor corte (cortar antes descarta lo que el cliente no
    /// ha leído). Corto (B-2), como <see cref="IdentificationTimeout"/>: mientras tanto ocupa una instancia del pipe.
    /// </summary>
    public TimeSpan RejectionLinger { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Máximo de peticiones simultáneas (leídas y sin responder) de una conexión (B-2). Al superarlo se cierra la conexión.
    /// </summary>
    public int MaxConcurrentRequestsPerConnection { get; init; } = BoundedHeaderDelimitedMessageHandler.DefaultMaxConcurrentRequests;

    /// <summary>Cada cuánto se revalida la autorización de las conexiones abiertas (además de en cada llamada).</summary>
    public TimeSpan RevalidationInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Notificaciones pendientes de enviar a un cliente a partir de las cuales se le considera bloqueado y se le desconecta.</summary>
    public int MaxPendingNotifications { get; init; } = 64;

    /// <summary>Intervalo mínimo entre dos <c>RunProgress</c> (§7.1: como máximo 4 por segundo).</summary>
    public TimeSpan RunProgressInterval { get; init; } = TimeSpan.FromSeconds(1.0 / BackboneIpc.MaxRunProgressEventsPerSecond);

    /// <summary>
    /// Solo pruebas: SID del proceso servidor al que se da control total en la ACL para poder crear más instancias del pipe
    /// sin ser SYSTEM ni administrador elevado. En producción es <see langword="null"/>.
    /// </summary>
    public SecurityIdentifier? AdditionalServerSid { get; init; }
}

/// <summary>Llamante de una operación IPC ya autorizado.</summary>
/// <param name="ConnectionId">Conexión.</param>
/// <param name="Identity">Identidad calculada del token del cliente.</param>
/// <param name="AccountName">Nombre de la cuenta (<c>EQUIPO\usuario</c>) o el SID si no se resuelve.</param>
/// <param name="Channel">Transporte, para suplantar al cliente (§8.6, §8.7).</param>
internal sealed record CallerContext(long ConnectionId, ClientIdentity Identity, string AccountName, IClientChannel Channel)
{
    public string Sid => Identity.Sid.Value;

    /// <summary>Puede gestionar los usuarios autorizados (§7.3): elevado o SYSTEM.</summary>
    public bool CanManageAuthorizedUsers => Identity.IsElevated || Identity.IsLocalSystem;
}
