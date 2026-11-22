using Backbone.Service.Jobs;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Backbone.Service.Nas;

/// <summary>
/// Parada del servicio (revisión de seguridad de HU-05, BAJA-2; §8.2 B-2): cierra el gestor (todo
/// <see cref="ShareConnectionManager.AcquireAsync"/> posterior falla), espera, con tope, a que terminen las limpiezas de "Probar
/// conexión", las preparaciones de repositorio de <c>SaveJobAsync</c> (HU-06) y las desconexiones en curso, y cierra a la fuerza
/// las conexiones SMB que sigan abiertas o en curso.
/// </summary>
internal sealed partial class ShareConnectionShutdown(
    ConnectionTester tester,
    RepositoryPreparer repositories,
    ShareConnectionManager connections,
    TimeProvider timeProvider,
    ILogger<ShareConnectionShutdown> logger) : IHostedService
{
    /// <summary>Tope de la espera a las limpiezas (dentro de los 20 s de <c>HostOptions.ShutdownTimeout</c>, §8.1).</summary>
    internal static readonly TimeSpan CleanupWait = TimeSpan.FromSeconds(5);

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        connections.Close();
        try
        {
            await Task.WhenAll(tester.WhenCleanupsCompleteAsync(), repositories.WhenPreparationsCompleteAsync(), connections.WhenReleasesCompleteAsync())
                .WaitAsync(CleanupWait, timeProvider, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            LogCleanupsPending(logger);
        }

        await connections.ForceDisconnectAllAsync(CancellationToken.None).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Parada del servicio: quedan limpiezas de pruebas de conexión, preparaciones de repositorio o desconexiones pendientes; se cierran a la fuerza las conexiones.")]
    private static partial void LogCleanupsPending(ILogger logger);
}
