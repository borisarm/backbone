using System.Collections.Concurrent;
using System.Security.Principal;
using Backbone.Contracts;
using Backbone.Service.Tests.TestSupport;
using StreamJsonRpc;

namespace Backbone.Service.Tests.Ipc;

/// <summary>
/// Cierre ordenado tras un rechazo sobre el pipe real: el cliente rechazado recibe siempre <c>NotAuthorized</c>, nunca
/// <see cref="ConnectionLostException"/> (antes, <c>DisconnectNamedPipe</c> justo después de enviar la respuesta descartaba
/// a veces los datos que el cliente aún no había leído).
/// </summary>
/// <remarks>
/// Prueba de estrés acotada (unos miles de conexiones, alrededor de un segundo): corre en la batería normal. La concurrencia se
/// mantiene por debajo de <see cref="BackboneIpc.MaxConnections"/> para no mezclar el corte por servidor lleno (que sí
/// desconecta sin respuesta, a propósito) con lo que se comprueba.
/// </remarks>
[Trait("Story", "HT-03")]
public sealed class RejectionDeliveryStressTests
{
    // Con el cierre anterior fallaban unas pocas por cada mil; 3000 conexiones tardan menos de un segundo.
    private const int Iterations = 3000;
    private const int Workers = 6;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_rejected_client_always_receives_NotAuthorized_before_the_connection_closes(bool notInList)
    {
        Assert.SkipWhen(notInList && WindowsTestEnvironment.IsElevated, "Elevado, el usuario está autorizado por ser administrador.");
        Assert.True(Workers < BackboneIpc.MaxConnections);

        await using var server = await PipeServerHarness.StartAsync(authorizeCurrentUser: false);
        var level = notInList ? TokenImpersonationLevel.Impersonation : TokenImpersonationLevel.Identification;
        var failures = new ConcurrentQueue<string>();
        var remaining = Iterations;

        await Task.WhenAll(Enumerable.Range(0, Workers).Select(_ => Task.Run(
            async () =>
            {
                while (Interlocked.Decrement(ref remaining) >= 0)
                {
                    await using var client = await server.ConnectAsync(level);
                    try
                    {
                        await client.Proxy.GetServiceInfoAsync(Ct);
                        failures.Enqueue("La llamada no se ha rechazado.");
                    }
                    catch (RemoteInvocationException ex) when (ex.ErrorCode == (int)BackboneErrorCode.NotAuthorized)
                    {
                    }
                    catch (Exception ex) when (ex is ConnectionLostException or RemoteInvocationException or IOException)
                    {
                        failures.Enqueue(ex.GetType().Name + ": " + ex.Message);
                    }
                }
            },
            Ct)));

        Assert.Empty(failures);
        await server.WaitForConnectionCountAsync(0);
    }
}
