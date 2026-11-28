using Backbone.Service.Hosting;
using Backbone.Service.Ipc;
using Backbone.Service.Jobs;
using Backbone.Service.Nas;
using Backbone.Service.Os;
using Backbone.Service.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Backbone.Service.Tests.TestSupport;

/// <summary>
/// Servidor IPC completo (gestor de conexiones, autorización, operaciones, eventos) sobre flujos en memoria y dobles del SO.
/// Cada <see cref="ConnectAsync"/> crea una conexión con la identidad indicada, como si llegara por el pipe.
/// </summary>
internal sealed class IpcTestHarness : IAsyncDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly List<TestRpcClient> _clients = [];

    public IpcTestHarness(IpcOptions? options = null, IDataProtector? protector = null, RepositoryPreparationOptions? preparation = null)
    {
        PreparationOptions = preparation ?? new RepositoryPreparationOptions();
        Options = options ?? new IpcOptions();
        LoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(b => b.AddProvider(Logs).SetMinimumLevel(LogLevel.Trace));
        Directory.CreateDirectory(_temp.Combine("config")); // la crea el servicio al arrancar (§8.3); los almacenes no crean carpetas
        Settings = new SettingsStore(_temp.Combine("config", "settings.json"), TestFileTrust.CurrentUser, LoggerFactory.CreateLogger<SettingsStore>());
        Authorizer = new CallerAuthorizer(Settings, Accounts);
        Runtime = new ServiceRuntimeInfo(Time);
        Directory.CreateDirectory(_temp.Combine("credentials")); // ídem (§8.3)
        Protector = protector ?? new DpapiMachineProtector();
        Credentials = new CredentialStore(_temp.Combine("credentials"), TestFileTrust.CurrentUser, Protector, Time, LoggerFactory.CreateLogger<CredentialStore>());
        Jobs = new JobStore(_temp.Combine("config", "jobs.json"), TestFileTrust.CurrentUser, LoggerFactory.CreateLogger<JobStore>());
        FileSystem.MapFinalDirectory((Path.GetDirectoryName(_temp.Path) ?? _temp.Path), (Path.GetDirectoryName(_temp.Path) ?? _temp.Path));
        ProtectedFolders = new ProtectedFolderResolver(new ServicePaths(_temp.Path), FileSystem, LoggerFactory.CreateLogger<ProtectedFolderResolver>());
        Runner = new SourceValidationRunner(Time, IoOptions);
        Validator = new JobValidator(FileSystem, ProtectedFolders, Runner, HostResolver, Time, LoggerFactory.CreateLogger<JobValidator>());
        RepositoryStates = new RepositoryStateTracker();
        SourceFactory.Source.PathTranslator = Snapshotter.ToLive;
        var logger = LoggerFactory.CreateLogger<JobRunner>();
        var connections = Connections = new ShareConnectionManager(ShareConnector, LoggerFactory.CreateLogger<ShareConnectionManager>());
        var shareCredentials = ShareCredentials = new ShareCredentialProvider(Credentials, LoggerFactory.CreateLogger<ShareCredentialProvider>());
        ConnectionTester = new ConnectionTester(
            connections,
            HostResolver,
            shareCredentials,
            Time,
            new ConnectionTestOptions(),
            LoggerFactory.CreateLogger<ConnectionTester>());
        Repositories = new RepositoryPreparer(
            shareCredentials,
            connections,
            RepositoryStorages,
            Runtime,
            Time,
            PreparationOptions,
            LoggerFactory.CreateLogger<RepositoryPreparer>());
        JobRunner = new JobRunner(shareCredentials, connections, RepositoryStorages, FileSystem, ProtectedFolders, Runner, SourceFactory, Snapshotter, new FakeEventLogWriter(), RepositoryStates, Runtime, Time, new JobRunnerOptions(), logger);
        Runs = new RunQueue(Jobs, JobRunner, Time, [], LoggerFactory.CreateLogger<RunQueue>());
        Activity.Fallback = Runs;
        Status = new ServiceStatusProvider(Jobs, Activity, RepositoryStates, Time, Runs);
        Operations = new BackboneOperations(Runtime, Settings, Accounts, Credentials, Jobs, Validator, Activity, Status, ConnectionTester, Repositories, RepositoryStates, Runs);
        Manager = new IpcConnectionManager(Authorizer, Accounts, Settings, Operations, Options, Time, LoggerFactory);
        Events = new IpcEventPublisher(Manager, Options, Time);
        Broadcaster = new StatusBroadcaster(Jobs, Status, Events, Runs);
        Broadcaster.Subscribe();
        RunBroadcaster = new RunEventBroadcaster(Runs, Events);
        RunBroadcaster.Subscribe();
        Runs.Start();
    }

    /// <summary>Carpetas locales simuladas para la validación de trabajos (HU-07).</summary>
    public FakeLocalFileSystemInspector FileSystem { get; } = new();

    public FakeJobActivity Activity { get; } = new();

    /// <summary>Lectura de los orígenes de las ejecuciones (HU-14).</summary>
    public FakeSourceFileSystemFactory SourceFactory { get; } = new();

    public FakeVolumeSnapshotter Snapshotter { get; } = new();

    public JobRunner JobRunner { get; }

    /// <summary>Cola de ejecuciones (HU-14), ya en marcha.</summary>
    public RunQueue Runs { get; }

    public RunEventBroadcaster RunBroadcaster { get; }

    /// <summary>Plazos de la resolución de orígenes (las pruebas de plazo los acortan antes de usar el arnés).</summary>
    public SourceIoOptions IoOptions { get; } = new();

    /// <summary>Hilos de trabajo de la validación (como mucho 4 a la vez, MEDIA-1).</summary>
    public SourceValidationRunner Runner { get; }

    public JobValidator Validator { get; }

    public JobStore Jobs { get; }

    public ProtectedFolderResolver ProtectedFolders { get; }

    public ServiceStatusProvider Status { get; }

    public StatusBroadcaster Broadcaster { get; }

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero));

    public FakeAccountDirectory Accounts { get; } = new FakeAccountDirectory()
        .AddUser(TestSids.Ana, @"PC-SALON\ana", administrator: true)
        .AddUser(TestSids.Luis, @"PC-SALON\luis", administrator: true)
        .AddUser(TestSids.Eva, @"PC-SALON\eva", administrator: false)
        .AddGroup(TestSids.Administrators, @"BUILTIN\Administradores")
        .AddGroup(TestSids.Everyone, "Todos");

    public CapturingLoggerProvider Logs { get; } = new();

    public ILoggerFactory LoggerFactory { get; }

    public IpcOptions Options { get; }

    public SettingsStore Settings { get; }

    /// <summary>Carpeta de datos de la prueba (equivale a <c>%ProgramData%\Backbone</c>).</summary>
    public string DataRoot => _temp.Path;

    /// <summary>Protector de las credenciales (por defecto, DPAPI real con ámbito equipo).</summary>
    public IDataProtector Protector { get; }

    public CredentialStore Credentials { get; }

    /// <summary>Doble de la conexión SMB (HU-05).</summary>
    public FakeShareConnector ShareConnector { get; } = new();

    /// <summary>Doble de la resolución DNS del servidor (HU-05, §7.5).</summary>
    public FakeHostResolver HostResolver { get; } = new();

    public ConnectionTester ConnectionTester { get; }

    /// <summary>Repositorios en memoria de los destinos (HU-06): por defecto, destinos vacíos.</summary>
    public FakeRepositoryStorageFactory RepositoryStorages { get; } = new();

    /// <summary>Plazo de la preparación del repositorio (las pruebas de plazo lo acortan antes de usar el arnés).</summary>
    public RepositoryPreparationOptions PreparationOptions { get; }

    /// <summary>Gestor de conexiones del servicio (el mismo que usan la prueba de conexión y el preparador).</summary>
    public ShareConnectionManager Connections { get; }

    public ShareCredentialProvider ShareCredentials { get; }

    public RepositoryPreparer Repositories { get; }

    public RepositoryStateTracker RepositoryStates { get; }

    public CallerAuthorizer Authorizer { get; }

    public ServiceRuntimeInfo Runtime { get; }

    public BackboneOperations Operations { get; }

    public IpcConnectionManager Manager { get; }

    public IpcEventPublisher Events { get; }

    /// <summary>Autoriza a <paramref name="sids"/> (escribe settings.json y relee).</summary>
    public async Task AuthorizeAsync(params string[] sids)
    {
        foreach (var sid in sids)
        {
            await Settings.AddAuthorizedUserAsync(new System.Security.Principal.SecurityIdentifier(sid), CancellationToken.None);
        }
    }

    /// <summary>Conecta un cliente con <paramref name="identity"/>.</summary>
    public TestConnection Connect(ClientIdentity identity) => Connect(() => identity);

    public TestConnection Connect(Func<ClientIdentity> identity)
    {
        var (serverStream, clientStream) = DrainingStreamPair.Create();
        var channel = new InMemoryChannel(serverStream, identity);
        var connection = Manager.Accept(channel);
        var client = new TestRpcClient(clientStream);
        _clients.Add(client);
        return new TestConnection(client, channel, connection);
    }

    /// <summary>Conecta y hace una primera llamada para identificar la conexión.</summary>
    public async Task<TestConnection> ConnectAuthorizedAsync(ClientIdentity identity)
    {
        var connection = Connect(identity);
        await connection.Proxy.GetServiceInfoAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    /// <summary>
    /// Conexión rechazada cuyo cliente no cierra: comprueba que el servidor no la corta antes de
    /// <see cref="IpcOptions.RejectionLinger"/> (el cliente tiene tiempo de leer <c>NotAuthorized</c>) y que la cierra al
    /// cumplirse ese plazo.
    /// </summary>
    public async Task WaitRejectedClosedAsync(TestConnection connection)
    {
        Assert.False(connection.RequiredServer.Completion.IsCompleted, "El servidor no debe cortar antes del plazo.");
        Time.Advance(Options.RejectionLinger);
        await connection.WaitClosedAsync();
        Assert.True(connection.Channel.IsDisposed);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
        {
            await client.DisposeAsync();
        }

        await Runs.StopAsync(CancellationToken.None);
        await Manager.DisposeAsync();
        RunBroadcaster.Dispose();
        Runs.Dispose();
        Broadcaster.Dispose();
        Events.Dispose();
        Jobs.Dispose();
        Settings.Dispose();
        Credentials.Dispose();
        LoggerFactory.Dispose();
        _temp.Dispose();
    }
}

internal sealed record TestConnection(TestRpcClient Client, InMemoryChannel Channel, IpcConnection? Server)
{
    public Contracts.IBackboneService Proxy => Client.Proxy;

    public IpcConnection RequiredServer => Server ?? throw new InvalidOperationException("The server rejected the connection.");

    /// <summary>Espera a que el servidor cierre la conexión.</summary>
    public Task WaitClosedAsync() => RequiredServer.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
}
