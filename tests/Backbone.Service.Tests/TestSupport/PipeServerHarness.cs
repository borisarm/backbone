using System.IO.Pipes;
using System.Security.Principal;
using Backbone.Service.Ipc;
using Backbone.Service.Os;
using Backbone.Service.Persistence;
using Microsoft.Extensions.Logging;

namespace Backbone.Service.Tests.TestSupport;

/// <summary>
/// <see cref="IpcServer"/> real sobre un named pipe de nombre aleatorio, en el proceso de pruebas y con la identidad real
/// del cliente (token). Para poder crear más instancias sin ser SYSTEM, la ACL incluye al usuario de las pruebas con control
/// total (<see cref="IpcOptions.AdditionalServerSid"/>). La pertenencia a Administradores se simula (configurable) para que
/// las pruebas no dependan de la cuenta que las ejecuta.
/// </summary>
internal sealed class PipeServerHarness : IAsyncDisposable
{
    private readonly TempDirectory _temp = new();

    private PipeServerHarness(IpcOptions options)
    {
        Options = options;
        LoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(b => b.AddProvider(Logs).SetMinimumLevel(LogLevel.Trace));
        Directory.CreateDirectory(_temp.Combine("config")); // la crea el servicio al arrancar (§8.3); los almacenes no crean carpetas
        Settings = new SettingsStore(_temp.Combine("config", "settings.json"), TestFileTrust.CurrentUser, LoggerFactory.CreateLogger<SettingsStore>());
        var authorizer = new CallerAuthorizer(Settings, Accounts);
        Directory.CreateDirectory(_temp.Combine("credentials"));
        Credentials = new CredentialStore(_temp.Combine("credentials"), TestFileTrust.CurrentUser, new DpapiMachineProtector(), TimeProvider.System, LoggerFactory.CreateLogger<CredentialStore>());
        var operations = new BackboneOperations(new ServiceRuntimeInfo(TimeProvider.System), Settings, Accounts, Credentials);
        Manager = new IpcConnectionManager(authorizer, Accounts, Settings, operations, options, TimeProvider.System, LoggerFactory);
        Events = new IpcEventPublisher(Manager, options, TimeProvider.System);
        Server = new IpcServer(Manager, Settings, EventLog, options, LoggerFactory.CreateLogger<IpcServer>());
    }

    public static SecurityIdentifier CurrentUser { get; } = WindowsTestEnvironment.CurrentUserSid;

    public string PipeName => Options.PipeName;

    public IpcOptions Options { get; }

    public FakeAccountDirectory Accounts { get; } = new FakeAccountDirectory()
        .AddUser(CurrentUser.Value, @"PRUEBAS\usuario-actual", administrator: true);

    public FakeEventLogWriter EventLog { get; } = new();

    public CapturingLoggerProvider Logs { get; } = new();

    public ILoggerFactory LoggerFactory { get; }

    public SettingsStore Settings { get; }

    public CredentialStore Credentials { get; }

    public IpcConnectionManager Manager { get; }

    public IpcEventPublisher Events { get; }

    public IpcServer Server { get; }

    public static string NewPipeName() => "Backbone.Tests." + Guid.NewGuid().ToString("N");

    /// <summary>Crea el servidor sin arrancarlo; con <paramref name="authorizeCurrentUser"/> el usuario de las pruebas está en la lista.</summary>
    public static async Task<PipeServerHarness> CreateAsync(bool authorizeCurrentUser = true, string? pipeName = null)
    {
        var harness = new PipeServerHarness(new IpcOptions
        {
            PipeName = pipeName ?? NewPipeName(),
            AdditionalServerSid = CurrentUser,
        });
        if (authorizeCurrentUser)
        {
            await harness.Settings.AddAuthorizedUserAsync(CurrentUser, CancellationToken.None);
        }

        return harness;
    }

    /// <summary>Crea y arranca el servidor.</summary>
    public static async Task<PipeServerHarness> StartAsync(bool authorizeCurrentUser = true, string? pipeName = null)
    {
        var harness = await CreateAsync(authorizeCurrentUser, pipeName);
        try
        {
            await harness.Server.StartAsync(CancellationToken.None);
        }
        catch
        {
            await harness.DisposeAsync();
            throw;
        }

        return harness;
    }

    /// <summary>Abre el pipe como lo hará la app: derechos explícitos (sin <c>FILE_CREATE_PIPE_INSTANCE</c>) y nivel <c>Impersonation</c>.</summary>
    public async Task<NamedPipeClientStream> OpenAsync(TokenImpersonationLevel level = TokenImpersonationLevel.Impersonation)
    {
        var client = new NamedPipeClientStream(".", PipeName, PipeSecurityFactory.ClientRights, PipeOptions.Asynchronous, level, HandleInheritability.None);
        await client.ConnectAsync(5000, TestContext.Current.CancellationToken);
        return client;
    }

    public async Task<TestRpcClient> ConnectAsync(TokenImpersonationLevel level = TokenImpersonationLevel.Impersonation) =>
        new(await OpenAsync(level));

    /// <summary>Espera a que el número de conexiones abiertas sea <paramref name="expected"/>.</summary>
    public async Task WaitForConnectionCountAsync(int expected)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (Manager.Count != expected)
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"Se esperaban {expected} conexiones y hay {Manager.Count}.");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Server.StopAsync(CancellationToken.None);
        await Server.DisposeAsync();
        Events.Dispose();
        Settings.Dispose();
        Credentials.Dispose();
        LoggerFactory.Dispose();
        _temp.Dispose();
    }
}
