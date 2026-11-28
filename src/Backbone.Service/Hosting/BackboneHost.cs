using Backbone.Service.Ipc;
using Backbone.Service.Jobs;
using Backbone.Service.Nas;
using Backbone.Service.Os;
using Backbone.Service.Persistence;
using Backbone.Service.Sources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventLog;

namespace Backbone.Service.Hosting;

/// <summary>Opciones del host fijadas en código (HT-03 I-5). Las pruebas cambian la carpeta de datos y el pipe.</summary>
internal sealed class BackboneHostOptions
{
    public const string ServiceName = "BackboneService";

    public string DataRoot { get; init; } = ServicePaths.DefaultRoot;

    public IpcOptions Ipc { get; init; } = new();

    /// <summary>Modo consola (<c>--console</c>): registro en la consola.</summary>
    public bool ConsoleMode { get; init; }
}

/// <summary>Construcción del host del servicio (§8.1).</summary>
internal static class BackboneHost
{
    /// <summary>
    /// Crea el host. HT-03 I-5: <c>ContentRootPath = AppContext.BaseDirectory</c>, entorno <c>Production</c> fijo y
    /// <b>sin</b> fuentes de configuración por defecto (ni <c>appsettings*.json</c>, ni variables de entorno, ni línea de
    /// comandos, ni secretos de usuario): nada se lee de ubicaciones que un usuario pueda escribir.
    /// </summary>
    public static HostApplicationBuilder CreateBuilder(BackboneHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true,
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production,
            ApplicationName = "Backbone.Service",
        });

        builder.Services.AddWindowsService(o => o.ServiceName = BackboneHostOptions.ServiceName);
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(20)); // §8.1

        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        if (options.ConsoleMode)
        {
            builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
        }

        // AddWindowsService añade el proveedor del Visor de eventos con el nombre de la aplicación como origen; se
        // desactiva: el Visor solo recibe los eventos de §8.5, con sus identificadores, mediante IEventLogWriter.
        builder.Logging.AddFilter<EventLogLoggerProvider>(_ => false);

        builder.Services.AddBackboneService(options);
        return builder;
    }

    public static IServiceCollection AddBackboneService(this IServiceCollection services, BackboneHostOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton(options.Ipc);
        services.AddSingleton(new ServicePaths(options.DataRoot));
        services.TryAddSingleton(TimeProvider.System);

        // Integración con el SO (dobles en pruebas).
        services.TryAddSingleton<IEventLogWriter, WindowsEventLogWriter>();
        services.TryAddSingleton<IDataDirectoryGuard, DataDirectoryGuard>();
        services.TryAddSingleton<IAccountDirectory, WindowsAccountDirectory>();

        // Persistencia: acceso por handle a la carpeta de datos (§8.2, ADR-14).
        services.TryAddSingleton(FileTrustPolicy.Default);
        services.AddSingleton<TrustedFile>();
        services.AddSingleton(sp => new SettingsStore(
            sp.GetRequiredService<ServicePaths>().SettingsFile,
            sp.GetRequiredService<TrustedFile>(),
            sp.GetRequiredService<ILogger<SettingsStore>>(),
            sp.GetRequiredService<IAccountDirectory>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IAuthorizedUserList>(sp => sp.GetRequiredService<SettingsStore>());

        // Credenciales del NAS (HU-04, ADR-07): DPAPI con ámbito equipo, un archivo por recurso en credentials\.
        services.TryAddSingleton<IDataProtector, DpapiMachineProtector>();
        services.TryAddSingleton<ICredentialStore>(sp => new CredentialStore(
            sp.GetRequiredService<ServicePaths>().CredentialsDirectory,
            sp.GetRequiredService<TrustedFile>(),
            sp.GetRequiredService<IDataProtector>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<CredentialStore>>()));
        services.AddSingleton<ShareCredentialProvider>();

        // Trabajos (HU-07): jobs.json, validación (RF-03) y carpetas protegidas (§8.3), reutilizadas por el JobRunner. La
        // resolución controlada de los orígenes (ADR-20) se hace fuera del hilo RPC, con plazo y como mucho 4 a la vez.
        services.TryAddSingleton<ILocalFileSystemInspector, WindowsLocalFileSystemInspector>();
        services.TryAddSingleton(new SourceIoOptions());
        // Un solo token de lectura (ADR-21) y un solo contador de hilos abandonados (R12) para la validación, su repetición en
        // el JobRunner y la lectura de los orígenes (AddSourceReading).
        services.AddSingleton(sp => new SourceValidationRunner(
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<SourceIoOptions>(),
            sp.GetRequiredService<SourceReadToken>(),
            sp.GetRequiredService<AbandonedSourceThreads>()));
        services.TryAddSingleton<IProcessMitigations, WindowsProcessMitigations>();
        services.AddSingleton(sp => new JobStore(
            sp.GetRequiredService<ServicePaths>().JobsFile,
            sp.GetRequiredService<TrustedFile>(),
            sp.GetRequiredService<ILogger<JobStore>>(),
            JobStore.MaxFileBytes,
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IJobList>(sp => sp.GetRequiredService<JobStore>());
        services.AddSingleton<ProtectedFolderResolver>();
        services.AddSingleton<JobValidator>();
        services.AddSingleton<RepositoryStateTracker>();

        // Ejecuciones (HU-14, §8.4): la cola de una sola ejecución a la vez (que es también la actividad y el estado de las
        // ejecuciones) y el JobRunner. El scheduler (HU-09) llamará a RunQueue.Enqueue; el historial y el Visor de eventos
        // (HU-21, HU-22) se registran como IRunCompletionHandler.
        services.TryAddSingleton(new JobRunnerOptions());
        services.AddSingleton<JobRunner>();
        services.AddSingleton<RunQueue>();
        services.TryAddSingleton<IJobActivity>(sp => sp.GetRequiredService<RunQueue>());
        services.TryAddSingleton<IRunStatusSource>(sp => sp.GetRequiredService<RunQueue>());
        services.AddSingleton(sp => new ServiceStatusProvider(
            sp.GetRequiredService<IJobList>(),
            sp.GetRequiredService<IJobActivity>(),
            sp.GetRequiredService<RepositoryStateTracker>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<IRunStatusSource>(),
            sp.GetRequiredService<INextRunSource>()));

        // Programación (HU-09, §8.2): data\schedule.json con el último punto consumido de cada trabajo y el Scheduler, que
        // encola en RunQueue. Los ajustes (periodo de gracia, pausa) son los de por defecto hasta que existan los ajustes y la pausa.
        services.AddSingleton(sp => new ScheduleStore(
            sp.GetRequiredService<ServicePaths>().ScheduleFile,
            sp.GetRequiredService<TrustedFile>(),
            sp.GetRequiredService<ILogger<ScheduleStore>>()));
        services.TryAddSingleton<ScheduleSettings>();
        services.TryAddSingleton<IScheduleSettings>(sp => sp.GetRequiredService<ScheduleSettings>());
        services.AddSingleton<IRunEnqueuer>(sp => sp.GetRequiredService<RunQueue>());
        services.AddSingleton<Scheduler>();
        services.AddSingleton<INextRunSource>(sp => sp.GetRequiredService<Scheduler>());

        // Conexión al NAS (HU-05, §8.2 ShareConnector).
        services.TryAddSingleton<IShareConnector, WindowsShareConnector>();
        services.TryAddSingleton<IHostResolver, DnsHostResolver>();
        services.AddSingleton<ShareConnectionManager>();
        services.TryAddSingleton(new ConnectionTestOptions());
        services.AddSingleton<ConnectionTester>();
        services.TryAddSingleton<IRepositoryStorageFactory, UncRepositoryStorageFactory>();
        services.TryAddSingleton(new RepositoryPreparationOptions());
        services.AddSingleton<RepositoryPreparer>();
        services.AddHostedService<ShareConnectionShutdown>();

        // Después de ShareConnectionShutdown: la parada es en orden inverso, así que las ejecuciones se cancelan y sueltan sus
        // recursos (paso 9) antes de que el gestor cierre a la fuerza lo que quede.
        services.AddSingleton<RunEventBroadcaster>();
        services.AddHostedService(sp => sp.GetRequiredService<RunEventBroadcaster>());
        services.AddHostedService(sp => sp.GetRequiredService<RunQueue>());

        // Después de RunQueue (se para antes que ella: deja de encolar antes de que la cola descarte lo pendiente).
        services.AddHostedService(sp => sp.GetRequiredService<Scheduler>());

        // IPC.
        services.AddSingleton<ServiceRuntimeInfo>();
        services.AddSingleton<CallerAuthorizer>();
        services.AddSingleton<BackboneOperations>();
        services.AddSingleton<IpcConnectionManager>();
        services.AddSingleton<IpcEventPublisher>();
        services.AddSingleton<IIpcEventPublisher>(sp => sp.GetRequiredService<IpcEventPublisher>());
        services.AddSingleton<IpcServer>();

        // Lectura de los orígenes (HU-08, §8.2): expone los marcadores de la nube al arrancar.
        services.AddSourceReading();

        // Orden de arranque: Redirection Guard (§8.3), comprobación de la carpeta y ajustes (StartingAsync) antes de crear el pipe.
        services.AddHostedService<ProcessMitigationStartup>();
        services.AddHostedService<StartupService>();
        services.AddSingleton(sp => new StatusBroadcaster(
            sp.GetRequiredService<IJobList>(),
            sp.GetRequiredService<ServiceStatusProvider>(),
            sp.GetRequiredService<IIpcEventPublisher>(),
            sp.GetRequiredService<IRunStatusSource>(),
            sp.GetRequiredService<INextRunSource>()));
        services.AddHostedService(sp => sp.GetRequiredService<StatusBroadcaster>());
        services.AddHostedService(sp => sp.GetRequiredService<IpcServer>());
        return services;
    }
}
