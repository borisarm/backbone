using Backbone.Core.Engine;
using Backbone.Service.Os;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Backbone.Service.Sources;

/// <summary>Modo de compatibilidad de marcadores del proceso (Cloud Files API), detrás de una interfaz para las pruebas.</summary>
internal interface IPlaceholderCompatibility
{
    /// <summary>
    /// <c>RtlSetProcessPlaceholderCompatibilityMode(PHCM_EXPOSE_PLACEHOLDERS)</c>: el modo anterior, o un valor negativo si
    /// falla.
    /// </summary>
    int ExposePlaceholders();
}

/// <inheritdoc />
internal sealed class WindowsPlaceholderCompatibility : IPlaceholderCompatibility
{
    public int ExposePlaceholders()
    {
        try
        {
            return NativeMethods.SourceIo.RtlSetProcessPlaceholderCompatibilityMode(NativeMethods.SourceIo.PhcmExposePlaceholders);
        }
        catch (EntryPointNotFoundException)
        {
            return int.MinValue; // anterior a Windows 10 1803: no se puede exponer
        }
    }
}

/// <summary>Si los orígenes se pueden leer con seguridad en este proceso (§8.2 "Marcadores visibles").</summary>
internal sealed class SourceReadingEnvironment
{
    private int _state; // 0 = sin comprobar, 1 = expuestos, -1 = fallo

    /// <summary>Resultado de exponer los marcadores al arrancar; <see langword="false"/> también si aún no se ha hecho.</summary>
    public bool PlaceholdersExposed => Volatile.Read(ref _state) == 1;

    public void SetResult(bool exposed) => Volatile.Write(ref _state, exposed ? 1 : -1);
}

/// <summary>
/// Al arrancar el servicio, antes de leer ningún origen, expone los marcadores de la Cloud Files API al proceso (§8.2, R4).
/// Si falla, lo registra como error y las copias que lean orígenes fallarán con <c>Internal</c>
/// (<see cref="SourceReadingUnavailableException"/>); el servicio sigue arrancando.
/// </summary>
internal sealed partial class SourceReadingStartup(
    IPlaceholderCompatibility placeholders,
    SourceReadingEnvironment environment,
    ILogger<SourceReadingStartup> logger) : IHostedLifecycleService
{
    public Task StartingAsync(CancellationToken cancellationToken)
    {
        var previous = placeholders.ExposePlaceholders();
        if (previous < 0)
        {
            environment.SetResult(exposed: false);
            LogExposeFailed(logger, previous);
        }
        else
        {
            environment.SetResult(exposed: true);
            LogExposed(logger, previous);
        }

        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Error, Message = "No se han podido exponer los marcadores de la nube (RtlSetProcessPlaceholderCompatibilityMode devolvió {Result}); las copias que lean orígenes fallarán con error interno (§8.2, R4).")]
    private static partial void LogExposeFailed(ILogger logger, int result);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Marcadores de la nube expuestos al proceso (modo anterior {Previous}).")]
    private static partial void LogExposed(ILogger logger, int previous);
}

/// <summary>Crea el <see cref="SourceFileSystem"/> de cada ejecución con sus orígenes (§8.2 "Rutas y raíces").</summary>
internal interface ISourceFileSystemFactory
{
    /// <summary>
    /// Crea la lectura de los orígenes de una ejecución; quien la crea la desecha al terminar.
    /// </summary>
    /// <exception cref="SourceReadingUnavailableException">
    /// No se expusieron los marcadores al arrancar: la ejecución termina <c>Failed</c>/<c>Internal</c> sin leer nada.
    /// </exception>
    SourceFileSystem Create(IReadOnlyList<BackupSource> sources);
}

/// <inheritdoc />
internal sealed class SourceFileSystemFactory(
    ISourceFileApi api,
    SourceReadingEnvironment environment,
    TimeProvider time,
    SourceReadOptions options,
    ILogger<SourceFileSystem> logger) : ISourceFileSystemFactory
{
    public SourceFileSystem Create(IReadOnlyList<BackupSource> sources)
    {
        if (!environment.PlaceholdersExposed)
        {
            throw new SourceReadingUnavailableException();
        }

        return new SourceFileSystem(sources, api, time, options, logger);
    }
}

/// <summary>Registro de la lectura de los orígenes (HU-08).</summary>
internal static class SourceReadingServiceCollectionExtensions
{
    public static IServiceCollection AddSourceReading(this IServiceCollection services)
    {
        services.TryAddSingleton<IPlaceholderCompatibility, WindowsPlaceholderCompatibility>();
        services.TryAddSingleton<ISourceFileApi, Win32SourceFileApi>();
        services.TryAddSingleton(SourceReadOptions.Default);
        services.AddSingleton<SourceReadingEnvironment>();
        services.AddSingleton<ISourceFileSystemFactory, SourceFileSystemFactory>();
        services.AddHostedService<SourceReadingStartup>();
        return services;
    }
}
