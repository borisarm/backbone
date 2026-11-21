using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Backbone.Service.Nas;

/// <summary>
/// Parada del servicio (revisión de seguridad de HU-05, BAJA-2): espera, con tope, a que terminen las limpiezas de "Probar
/// conexión" en segundo plano y cierra a la fuerza las conexiones SMB que sigan abiertas o en curso.
/// </summary>
internal sealed partial class ShareConnectionShutdown(
    ConnectionTester tester,
    ShareConnectionManager connections,
    TimeProvider timeProvider,
    ILogger<ShareConnectionShutdown> logger) : IHostedService
{
    /// <summary>Tope de la espera a las limpiezas (dentro de los 20 s de <c>HostOptions.ShutdownTimeout</c>, §8.1).</summary>
    internal static readonly TimeSpan CleanupWait = TimeSpan.FromSeconds(5);

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await tester.WhenCleanupsCompleteAsync().WaitAsync(CleanupWait, timeProvider, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            LogCleanupsPending(logger);
        }

        await connections.ForceDisconnectAllAsync(CancellationToken.None).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Parada del servicio: quedan limpiezas de pruebas de conexión pendientes; se cierran a la fuerza las conexiones.")]
    private static partial void LogCleanupsPending(ILogger logger);
}
