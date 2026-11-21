using Backbone.Service.Ipc;
using Backbone.Service.Nas;
using Backbone.Service.Os;
using Backbone.Service.Persistence;
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
            sp.GetRequiredService<IAccountDirectory>()));
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

        // Conexión al NAS (HU-05, §8.2 ShareConnector).
        services.TryAddSingleton<IShareConnector, WindowsShareConnector>();
        services.TryAddSingleton<IHostResolver, DnsHostResolver>();
        services.AddSingleton<ShareConnectionManager>();
        services.TryAddSingleton(new ConnectionTestOptions());
        services.AddSingleton<ConnectionTester>();
        services.AddHostedService<ShareConnectionShutdown>();

        // IPC.
        services.AddSingleton<ServiceRuntimeInfo>();
        services.AddSingleton<CallerAuthorizer>();
        services.AddSingleton<BackboneOperations>();
        services.AddSingleton<IpcConnectionManager>();
        services.AddSingleton<IpcEventPublisher>();
        services.AddSingleton<IIpcEventPublisher>(sp => sp.GetRequiredService<IpcEventPublisher>());
        services.AddSingleton<IpcServer>();

        // Orden de arranque: comprobación de la carpeta y ajustes (StartingAsync) antes de crear el pipe.
        services.AddHostedService<StartupService>();
        services.AddHostedService(sp => sp.GetRequiredService<IpcServer>());
        return services;
    }
}
