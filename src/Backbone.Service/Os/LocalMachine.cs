using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Backbone.Service.Os;

/// <summary>
/// Nombres y direcciones del propio equipo que <see cref="Contracts.UncPaths.TryParse"/> rechaza como destino (§7.5, RF-03).
/// </summary>
/// <remarks>
/// Solo lo que se obtiene sin red: nombre NetBIOS, nombre DNS del equipo (con y sin dominio) y las direcciones de todas las
/// interfaces locales, incluidas las de enlace local. La resolución por DNS del nombre del servidor (que no apunte al propio
/// equipo) es de la conexión al recurso (HU-05).
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
}
