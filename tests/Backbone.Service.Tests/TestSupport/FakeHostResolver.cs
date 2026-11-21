using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Backbone.Service.Os;

namespace Backbone.Service.Tests.TestSupport;

/// <summary>
/// Doble de <see cref="IHostResolver"/> (HU-05, §7.5): por defecto todo nombre resuelve a 192.0.2.10 (TEST-NET-1, nunca del
/// equipo). Se pueden fijar direcciones o errores por nombre y dejar la resolución colgada.
/// </summary>
internal sealed class FakeHostResolver : IHostResolver
{
    public ConcurrentDictionary<string, IPAddress[]> Addresses { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ConcurrentDictionary<string, Exception> Errors { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Si no es nulo, la resolución espera a que se complete.</summary>
    public TaskCompletionSource? Gate { get; set; }

    public ConcurrentQueue<string> Queries { get; } = new();

    public async Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
    {
        Queries.Enqueue(host);
        if (Gate is { } gate)
        {
            await gate.Task.WaitAsync(ct).ConfigureAwait(false);
        }

        if (Errors.TryGetValue(host, out var error))
        {
            throw error;
        }

        return Addresses.TryGetValue(host, out var addresses) ? addresses : [IPAddress.Parse("192.0.2.10")];
    }

    /// <summary>Error de "nombre no encontrado".</summary>
    public static SocketException HostNotFound() => new((int)SocketError.HostNotFound);
}
