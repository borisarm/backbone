using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Backbone.Service.Os;

/// <summary>
/// Nombres y direcciones del propio equipo que <see cref="Contracts.UncPaths.TryParse"/> rechaza como destino (§7.5, RF-03).
/// </summary>
/// <remarks>
/// <see cref="Names"/> y <see cref="Addresses"/> se obtienen sin red: nombre NetBIOS, nombre DNS del equipo (con y sin
/// dominio) y las direcciones de todas las interfaces locales, incluidas las de enlace local. La comprobación por DNS de que el
/// nombre del servidor no apunta al propio equipo (§7.5) es <see cref="ResolvesToSelfAsync"/>: la usa "Probar conexión"
/// (HU-05) antes de conectar y la reutilizará la validación de trabajos (HU-07).
/// </remarks>
internal static class LocalMachine
{
    public static IReadOnlyList<string> Names()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Environment.MachineName };
        try
        {
            var properties = IPGlobalProperties.GetIPGlobalProperties();
            if (!string.IsNullOrEmpty(properties.HostName))
            {
                names.Add(properties.HostName);
                if (!string.IsNullOrEmpty(properties.DomainName))
                {
                    names.Add(properties.HostName + "." + properties.DomainName);
                }
            }

            names.Add(Dns.GetHostName());
        }
        catch (Exception ex) when (ex is NetworkInformationException or SocketException)
        {
            // Se queda con el nombre NetBIOS.
        }

        return [.. names];
    }

    public static IReadOnlyList<IPAddress> Addresses()
    {
        try
        {
            return
            [
                .. NetworkInterface.GetAllNetworkInterfaces()
                    .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                    .Select(a => a.Address)
                    .Distinct(),
            ];
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    /// <summary>
    /// Indica si <paramref name="server"/> resuelve (DNS, hosts, NetBIOS: lo que use <paramref name="resolver"/>) a alguna
    /// dirección del propio equipo (<see cref="IsSelf"/>), §7.5.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>Si la resolución falla (nombre inexistente, sin red), devuelve <see langword="false"/>: no se rechaza el destino y
    /// la conexión fallará después con <c>NasUnreachable</c>.</item>
    /// <item><b>Riesgo residual aceptado (DNS rebinding)</b>: la conexión SMB vuelve a resolver el nombre por su cuenta, así que
    /// un DNS hostil puede responder aquí con una dirección ajena y después con una del equipo. Solo lo puede hacer quien
    /// controle la resolución de nombres de la red del usuario, y lo más que consigue es que el servicio conecte con un recurso
    /// del propio equipo con la credencial que le ha dado el usuario autorizado; se acepta (revisión de seguridad de HU-05,
    /// ALTA-1).</item>
    /// </list>
    /// </remarks>
    /// <param name="server">Servidor del destino (nombre o IP literal).</param>
    /// <param name="resolver">Resolución de nombres (simulable).</param>
    /// <param name="localAddresses">Direcciones del equipo (<see cref="Addresses"/>).</param>
    /// <param name="ct">Cancelación (el plazo lo pone el llamante).</param>
    public static async Task<bool> ResolvesToSelfAsync(string server, IHostResolver resolver, IReadOnlyList<IPAddress> localAddresses, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        IPAddress[] addresses;
        try
        {
            addresses = await resolver.ResolveAsync(server, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            return false;
        }

        return addresses.Any(a => IsSelf(a, localAddresses));
    }

    /// <summary>
    /// Dirección del propio equipo: <i>loopback</i>, <see cref="IPAddress.Any"/> o <see cref="IPAddress.IPv6Any"/>, o una de
    /// <paramref name="localAddresses"/> (sin tener en cuenta el ámbito IPv6). Las IPv4 mapeadas en IPv6 se comparan como IPv4.
    /// </summary>
    public static bool IsSelf(IPAddress address, IReadOnlyList<IPAddress> localAddresses)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(localAddresses);
        var candidate = Normalize(address);
        if (IPAddress.IsLoopback(candidate) || candidate.Equals(IPAddress.Any) || candidate.Equals(IPAddress.IPv6Any)
            || candidate.Equals(IPAddress.IPv6None))
        {
            return true;
        }

        var bytes = candidate.GetAddressBytes();
        return localAddresses.Any(local => Normalize(local).GetAddressBytes().AsSpan().SequenceEqual(bytes));
    }

    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}

/// <summary>Resolución de nombres de servidor (§7.5); simulable en pruebas.</summary>
internal interface IHostResolver
{
    /// <summary>Direcciones de <paramref name="host"/>.</summary>
    /// <exception cref="SocketException">No se puede resolver.</exception>
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct);
}

/// <summary><see cref="IHostResolver"/> real: <see cref="Dns.GetHostAddressesAsync(string, CancellationToken)"/>.</summary>
internal sealed class DnsHostResolver : IHostResolver
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) => Dns.GetHostAddressesAsync(host, ct);
}
