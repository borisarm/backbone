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
/// (<see cref="SourceReadingUnavailableException"/>); el servicio sigue arrancando. Además prepara el token de los hilos de
/// lectura (<see cref="SourceReadToken"/>, ADR-21) en el hilo de arranque, que no suplanta a nadie.
/// </summary>
internal sealed partial class SourceReadingStartup(
    IPlaceholderCompatibility placeholders,
    SourceReadingEnvironment environment,
    SourceReadToken readToken,
    ILogger<SourceReadingStartup> logger) : IHostedLifecycleService
{
    public Task StartingAsync(CancellationToken cancellationToken)
    {
        LogReadToken(logger, readToken.HasBackupPrivilege);

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

    [LoggerMessage(Level = LogLevel.Information, Message = "Token de lectura de orígenes preparado; SeBackupPrivilege activado en los hilos de lectura: {Enabled} (ADR-21).")]
    private static partial void LogReadToken(ILogger logger, bool enabled);
}

/// <summary>Crea el <see cref="SourceFileSystem"/> de cada ejecución con sus orígenes (§8.2 "Rutas y raíces").</summary>
internal interface ISourceFileSystemFactory
{
    /// <summary>
    /// Crea la lectura de los orígenes de una ejecución; quien la crea la desecha al terminar.
    /// </summary>
    /// <param name="sources">Orígenes, con <see cref="BackupSource.ReadPath"/> ya traducida a la instantánea si la hay.</param>
    /// <param name="snapshotDevices">
    /// Dispositivos (<c>HarddiskVolumeShadowCopy&lt;n&gt;</c>) de las instantáneas que <c>VssSnapshotter</c> ha creado para
    /// <b>esta</b> ejecución; cualquier otra instantánea se rechaza (B-2).
    /// </param>
    /// <exception cref="SourceReadingUnavailableException">
    /// No se expusieron los marcadores al arrancar: la ejecución termina <c>Failed</c>/<c>Internal</c> sin leer nada.
    /// </exception>
    /// <exception cref="SourceReadingStoppedException">
    /// Hay 4 o más hilos de lectura abandonados en el servicio (R12): <c>Failed</c>/<c>Internal</c> sin leer nada.
    /// </exception>
    /// <exception cref="ArgumentException">Un origen o una exclusión obligatoria no son válidos (B-2, I-4): <c>Internal</c>.</exception>
    SourceFileSystem Create(IReadOnlyList<BackupSource> sources, IReadOnlyCollection<string>? snapshotDevices = null);
}

/// <inheritdoc />
internal sealed class SourceFileSystemFactory(
    ISourceFileApi api,
    SourceReadingEnvironment environment,
    TimeProvider time,
    SourceReadOptions options,
    ILogger<SourceFileSystem> logger,
    SourceReadToken? token = null,
    AbandonedSourceThreads? abandonedThreads = null) : ISourceFileSystemFactory
{
    public SourceFileSystem Create(IReadOnlyList<BackupSource> sources, IReadOnlyCollection<string>? snapshotDevices = null)
    {
        if (!environment.PlaceholdersExposed)
        {
            throw new SourceReadingUnavailableException();
        }

        if (abandonedThreads is { IsExhausted: true })
        {
            throw new SourceReadingStoppedException();
        }

        return new SourceFileSystem(sources, api, time, options, logger, token, abandonedThreads, snapshotDevices);
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
        services.TryAddSingleton(sp => SourceReadToken.Create(sp.GetRequiredService<ILogger<SourceReadToken>>()));
        services.TryAddSingleton(sp => new AbandonedSourceThreads(
            sp.GetRequiredService<IEventLogWriter>(),
            sp.GetRequiredService<ILogger<AbandonedSourceThreads>>()));
        services.AddSingleton<SourceReadingEnvironment>();
        services.AddSingleton<ISourceFileSystemFactory>(sp => new SourceFileSystemFactory(
            sp.GetRequiredService<ISourceFileApi>(),
            sp.GetRequiredService<SourceReadingEnvironment>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<SourceReadOptions>(),
            sp.GetRequiredService<ILogger<SourceFileSystem>>(),
            sp.GetRequiredService<SourceReadToken>(),
            sp.GetRequiredService<AbandonedSourceThreads>()));
        services.AddHostedService<SourceReadingStartup>();
        return services;
    }
}
