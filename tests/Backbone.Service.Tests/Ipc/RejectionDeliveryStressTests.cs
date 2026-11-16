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
/// Prueba de estrés acotada (unos miles de conexiones, alrededor de un segundo): corre en la batería normal. El servidor de
/// esta prueba admite muchas más conexiones que clientes simultáneos para no mezclar el corte por servidor lleno (que sí
/// desconecta sin respuesta, a propósito) con lo que se comprueba: el servidor quita cada conexión un poco después de que
/// el cliente la cierre, y con carga en la máquina (toda la batería en paralelo) ese retraso bastaba para llegar a
/// <see cref="BackboneIpc.MaxConnections"/> con solo 6 clientes. El límite de 4 por cuenta sí actúa (todas las conexiones
/// son de la misma cuenta) y también debe responder <c>NotAuthorized</c>.
/// </remarks>
[Trait("Story", "HT-03")]
public sealed class RejectionDeliveryStressTests
{
    // Con el cierre anterior fallaban unas pocas por cada mil; 3000 conexiones tardan menos de un segundo.
    private const int Iterations = 3000;
    private const int Workers = 6;
    private const int ServerMaxConnections = Workers * 8;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_rejected_client_always_receives_NotAuthorized_before_the_connection_closes(bool notInList)
    {
        Assert.SkipWhen(notInList && WindowsTestEnvironment.IsElevated, "Elevado, el usuario está autorizado por ser administrador.");
        await using var server = await PipeServerHarness.StartAsync(authorizeCurrentUser: false, maxConnections: ServerMaxConnections);
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

        var log = server.Logs.AllText();
        Assert.True(
            failures.IsEmpty,
            $"{failures.Count} fallos; servidor lleno {CountOf(log, "ya hay")} veces; demasiadas por cuenta {CountOf(log, "TooManyConnections")}; primero: {failures.FirstOrDefault()}");
        await server.WaitForConnectionCountAsync(0);
    }

    private static int CountOf(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
