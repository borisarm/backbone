using Backbone.Service.Ipc;
using Backbone.Service.Os;
using Backbone.Service.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Nerdbank.Streams;

namespace Backbone.Service.Tests.TestSupport;

/// <summary>
/// Servidor IPC completo (gestor de conexiones, autorización, operaciones, eventos) sobre flujos en memoria y dobles del SO.
/// Cada <see cref="ConnectAsync"/> crea una conexión con la identidad indicada, como si llegara por el pipe.
/// </summary>
internal sealed class IpcTestHarness : IAsyncDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly List<TestRpcClient> _clients = [];

    public IpcTestHarness(IpcOptions? options = null, IDataProtector? protector = null)
    {
        Options = options ?? new IpcOptions();
        LoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(b => b.AddProvider(Logs).SetMinimumLevel(LogLevel.Trace));
        Directory.CreateDirectory(_temp.Combine("config")); // la crea el servicio al arrancar (§8.3); los almacenes no crean carpetas
        Settings = new SettingsStore(_temp.Combine("config", "settings.json"), TestFileTrust.CurrentUser, LoggerFactory.CreateLogger<SettingsStore>());
        Authorizer = new CallerAuthorizer(Settings, Accounts);
        Runtime = new ServiceRuntimeInfo(Time);
        Directory.CreateDirectory(_temp.Combine("credentials")); // ídem (§8.3)
        Protector = protector ?? new DpapiMachineProtector();
        Credentials = new CredentialStore(_temp.Combine("credentials"), TestFileTrust.CurrentUser, Protector, Time, LoggerFactory.CreateLogger<CredentialStore>());
        Operations = new BackboneOperations(Runtime, Settings, Accounts, Credentials);
        Manager = new IpcConnectionManager(Authorizer, Accounts, Settings, Operations, Options, Time, LoggerFactory);
        Events = new IpcEventPublisher(Manager, Options, Time);
    }

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
        var (serverStream, clientStream) = FullDuplexStream.CreatePair();
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

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
        {
            await client.DisposeAsync();
        }

        await Manager.DisposeAsync();
        Events.Dispose();
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
