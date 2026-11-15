using System.Net;

namespace Backbone.Contracts;

/// <summary>
/// Reglas puras del destino UNC (RF-03, §7.5) y cálculo de la raíz del recurso con la que se guardan las credenciales.
/// </summary>
public static class UncPaths
{
    /// <summary>
    /// Valida un destino <c>\\servidor\recurso[\ruta]</c> y obtiene su raíz normalizada.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>Rechaza (<see cref="ValidationIssueCode.DestinationNotAllowed"/>) rutas <c>\\?\</c> y <c>\\.\</c>, servidores
    /// <i>loopback</i> (<c>localhost</c>, <c>*.localhost</c>, <c>127.0.0.0/8</c> en cualquier notación IPv4 que acepte
    /// <see cref="IPAddress.TryParse(string?, out IPAddress?)"/>, como <c>2130706433</c>, <c>0x7f.1</c> o <c>127.1</c>, y
    /// <c>0.0.0.0</c>), nombres <c>*.ipv6-literal.net</c>, nombres iguales a <paramref name="localMachineNames"/>, IP literales
    /// iguales a <paramref name="localAddresses"/>, recursos administrativos (<c>C$</c>, <c>ADMIN$</c>, <c>IPC$</c>) y segmentos
    /// que no cumplen <see cref="SnapshotPaths.IsValidSegment"/>.</item>
    /// <item>El servidor solo admite letras y dígitos ASCII, <c>-</c>, <c>_</c> y <c>.</c> (un nombre internacional va en
    /// punycode, <c>xn--…</c>); un punto inicial o final no se normaliza: se rechaza con
    /// <see cref="ValidationIssueCode.DestinationNotUnc"/>. Por eso las IP IPv6 entre corchetes no se admiten.</item>
    /// <item>La raíz es <c>\\servidor\recurso</c> en minúsculas invariables; la ruta normalizada conserva el resto tal cual,
    /// sin barra final.</item>
    /// </list>
    /// La comprobación de que el nombre no resuelve al propio equipo (por DNS) la hace el servicio además de esta.
    /// </remarks>
    /// <param name="destination">Destino escrito por el usuario.</param>
    /// <param name="shareRoot">Raíz normalizada, <c>\\servidor\recurso</c>.</param>
    /// <param name="normalizedDestination">Destino con servidor y recurso normalizados y sin barra final.</param>
    /// <param name="issue">Motivo del rechazo si devuelve <see langword="false"/>.</param>
    /// <param name="localMachineNames">Nombres del propio equipo (NetBIOS, DNS) que también se rechazan; opcional.</param>
    /// <param name="localAddresses">
    /// Direcciones de todas las interfaces locales (incluidas las de enlace local 169.254.0.0/16) que el servicio pasa para
    /// rechazar una IP literal del propio equipo; opcional.
    /// </param>
    /// <returns><see langword="true"/> si el destino es válido.</returns>
    public static bool TryParse(
        string? destination,
        out string shareRoot,
        out string normalizedDestination,
        out ValidationIssueCode issue,
        IReadOnlyList<string>? localMachineNames = null,
        IReadOnlyList<IPAddress>? localAddresses = null)
    {
        shareRoot = string.Empty;
        normalizedDestination = string.Empty;

        if (string.IsNullOrWhiteSpace(destination))
        {
            issue = ValidationIssueCode.DestinationRequired;
            return false;
        }

        if (destination.Length > ContractLimits.MaxDestinationLength)
        {
            issue = ValidationIssueCode.LimitExceeded;
            return false;
        }

        if (destination.StartsWith(@"\\?\", StringComparison.Ordinal) || destination.StartsWith(@"\\.\", StringComparison.Ordinal)
            || destination.StartsWith("//", StringComparison.Ordinal) || destination.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            issue = ValidationIssueCode.DestinationNotAllowed;
            return false;
        }

        if (!destination.StartsWith(@"\\", StringComparison.Ordinal) || destination.Contains('/', StringComparison.Ordinal))
        {
            issue = ValidationIssueCode.DestinationNotUnc;
            return false;
        }

        var rest = destination[2..];
        if (rest.EndsWith('\\'))
        {
            rest = rest[..^1];
        }

        var segments = rest.Split('\\');
        if (segments.Length < 2 || segments[0].Length == 0 || segments[1].Length == 0)
        {
            issue = ValidationIssueCode.DestinationNotUnc;
            return false;
        }

        var server = segments[0];
        if (!IsValidServerName(server))
        {
            issue = ValidationIssueCode.DestinationNotUnc;
            return false;
        }

        if (IsLoopbackOrLocal(server, localMachineNames, localAddresses) || IsAdministrativeShare(segments[1]))
        {
            issue = ValidationIssueCode.DestinationNotAllowed;
            return false;
        }

        for (var i = 1; i < segments.Length; i++)
        {
            if (!SnapshotPaths.IsValidSegment(segments[i]))
            {
                issue = ValidationIssueCode.DestinationNotAllowed;
                return false;
            }
        }

        shareRoot = @"\\" + server.ToLowerInvariant() + @"\" + segments[1].ToLowerInvariant();
        normalizedDestination = segments.Length == 2 ? shareRoot : shareRoot + @"\" + string.Join('\\', segments[2..]);
        issue = ValidationIssueCode.Other;
        return true;
    }

    /// <summary>Obtiene la raíz normalizada <c>\\servidor\recurso</c> de un destino válido.</summary>
    /// <param name="destination">Destino.</param>
    /// <param name="shareRoot">Raíz normalizada.</param>
    /// <returns><see langword="true"/> si el destino es válido (ver <see cref="TryParse"/>).</returns>
    public static bool TryGetShareRoot(string? destination, out string shareRoot) =>
        TryParse(destination, out shareRoot, out _, out _);

    private static bool IsValidServerName(string server)
    {
        if (server.Length > 255 || server[0] == '.' || server[^1] == '.')
        {
            return false;
        }

        foreach (var c in server)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsLoopbackOrLocal(string server, IReadOnlyList<string>? localMachineNames, IReadOnlyList<IPAddress>? localAddresses)
    {
        if (server.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || server.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || server.EndsWith(".ipv6-literal.net", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IPAddress.TryParse(server, out var address))
        {
            if (address.IsIPv4MappedToIPv6)
            {
                address = address.MapToIPv4();
            }

            if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)
                || localAddresses?.Any(a => (a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a).Equals(address)) == true)
            {
                return true;
            }
        }

        return localMachineNames?.Any(n => string.Equals(n, server, StringComparison.OrdinalIgnoreCase)) == true;
    }

    private static bool IsAdministrativeShare(string share) =>
        share.Equals("ADMIN$", StringComparison.OrdinalIgnoreCase)
        || share.Equals("IPC$", StringComparison.OrdinalIgnoreCase)
        || (share.Length == 2 && char.IsAsciiLetter(share[0]) && share[1] == '$');
}
