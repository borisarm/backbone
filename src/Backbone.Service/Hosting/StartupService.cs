using Backbone.Service.Ipc;
using Backbone.Service.Os;
using Backbone.Service.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Backbone.Service.Hosting;

/// <summary>
/// Primer servicio alojado: comprueba la carpeta de datos (HT-03 I-1) y, solo si es segura, lee <c>settings.json</c>.
/// Registra en el Visor de eventos el arranque (1000), la parada (1001) y el fallo de arranque.
/// </summary>
internal sealed partial class StartupService(
    ServicePaths paths,
    IDataDirectoryGuard guard,
    SettingsStore settings,
    IEventLogWriter eventLog,
    ServiceRuntimeInfo runtime,
    ILogger<StartupService> logger) : IHostedLifecycleService
{
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = guard.EnsureSecure(paths.Root);
            if (result == DataDirectoryCheckResult.Corrected)
            {
                LogCorrected(logger, paths.Root);
            }
        }
        catch (DataDirectoryInsecureException ex)
        {
            LogInsecure(logger, ex, paths.Root);
            eventLog.Write(
                EventLogLevel.Error,
                BackboneEventIds.ServiceStartFailed,
                $"Backbone does not start: the data directory '{paths.Root}' is not secure and could not be corrected ({ex.Message}). Repair the installation.");
            throw new ServiceStartupException("The data directory is not secure.", ex);
        }

        await settings.LoadAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken)
    {
        eventLog.Write(EventLogLevel.Information, BackboneEventIds.ServiceStarted, $"Backbone service {runtime.Version} started.");
        return Task.CompletedTask;
    }

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken)
    {
        eventLog.Write(EventLogLevel.Information, BackboneEventIds.ServiceStopped, "Backbone service stopped.");
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "La carpeta de datos {Root} no cumplía RNF-07 y se ha corregido.")]
    private static partial void LogCorrected(ILogger logger, string root);

    [LoggerMessage(Level = LogLevel.Critical, Message = "La carpeta de datos {Root} no es segura; el servicio no arranca.")]
    private static partial void LogInsecure(ILogger logger, Exception exception, string root);
}
