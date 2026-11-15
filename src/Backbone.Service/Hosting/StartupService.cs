using Backbone.Service.Ipc;
using Backbone.Service.Os;
using Backbone.Service.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Backbone.Service.Hosting;

/// <summary>
/// Primer servicio alojado: comprueba la carpeta de datos (HT-03 I-1) y, solo si es segura, lee <c>settings.json</c>.
/// Registra en el Visor de eventos el arranque (1000), la parada (1001), el fallo de arranque (1002: carpeta insegura o
/// <c>settings.json</c> rechazado) y la carpeta apartada y sustituida (1003).
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
            var check = guard.EnsureSecure(paths.Root);
            if (check.Result == DataDirectoryCheckResult.Corrected)
            {
                LogCorrected(logger, paths.Root);
            }
            else if (check.Result == DataDirectoryCheckResult.Replaced)
            {
                var movedAside = string.Join("', '", check.MovedAsidePaths);
                LogReplaced(logger, paths.Root, movedAside);
                eventLog.Write(
                    EventLogLevel.Warning,
                    BackboneEventIds.DataDirectoryReplaced,
                    $"The data directory '{paths.Root}' had an untrusted owner: it was moved aside to '{movedAside}' without deleting anything and a new one was created. The previous configuration, history and credentials are no longer used; an administrator should review the moved-aside folder.");
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

        try
        {
            await settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (UntrustedFileException ex)
        {
            // §8.2 / ADR-14: settings.json rechazado (no se ha leído nada): el servicio no arranca.
            // El rechazo puede ser del propio archivo o de su carpeta padre (config\): se registra la ruta rechazada.
            var rejected = ex.FilePath ?? settings.SettingsPath;
            LogSettingsRejected(logger, ex, rejected);
            eventLog.Write(
                EventLogLevel.Error,
                BackboneEventIds.ServiceStartFailed,
                $"Backbone does not start: '{rejected}' was rejected ({ex.Reason}) while reading '{settings.SettingsPath}'; the file and its folder must be owned by SYSTEM or Administrators and must not be links. Repair the installation.");
            throw new ServiceStartupException("settings.json was rejected.", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogSettingsUnreadable(logger, ex, settings.SettingsPath);
            eventLog.Write(
                EventLogLevel.Error,
                BackboneEventIds.ServiceStartFailed,
                $"Backbone does not start: '{settings.SettingsPath}' could not be read ({ex.GetType().Name}).");
            throw new ServiceStartupException("settings.json could not be read.", ex);
        }
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "La carpeta de datos {Root} tenía un propietario no fiable: se ha apartado como {MovedAside} y se ha creado una nueva.")]
    private static partial void LogReplaced(ILogger logger, string root, string movedAside);

    [LoggerMessage(Level = LogLevel.Critical, Message = "{Path} se ha rechazado (§8.2); el servicio no arranca.")]
    private static partial void LogSettingsRejected(ILogger logger, Exception exception, string path);

    [LoggerMessage(Level = LogLevel.Critical, Message = "No se ha podido leer {Path}; el servicio no arranca.")]
    private static partial void LogSettingsUnreadable(ILogger logger, Exception exception, string path);

    [LoggerMessage(Level = LogLevel.Critical, Message = "La carpeta de datos {Root} no es segura; el servicio no arranca.")]
    private static partial void LogInsecure(ILogger logger, Exception exception, string root);
}
