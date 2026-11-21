using System.Net;
using Backbone.Contracts;
using Backbone.Service.Ipc;
using Backbone.Service.Nas;
using Backbone.Service.Os;
using Backbone.Service.Persistence;
using Backbone.Service.Tests.Ipc;
using Backbone.Service.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Backbone.Service.Tests.Nas;

/// <summary>
/// Revisión de seguridad de HU-05: resolución DNS al propio equipo (ALTA-1), gestor único de conexiones (MEDIA-1), credencial
/// distinta con una conexión viva (BAJA-1), turnos y limpiezas pendientes (BAJA-2), parada del servicio y carpeta destino
/// fuera del recurso (informativa).
/// </summary>
[Trait("Story", "HU-05")]
public sealed class ConnectionTesterSecurityReviewTests : IDisposable
{
    private const string Share = @"\\nas\backups";

    private static readonly string[] ThreeServers = [@"\\nas1\b", @"\\nas2\b", @"\\nas3\b"];

    private readonly TempDirectory _temp = new();
    private readonly CapturingLoggerProvider _logs = new();
    private readonly ILoggerFactory _loggerFactory;
    private readonly CredentialStore _store;
    private readonly FakeShareConnector _connector = new();
    private readonly FakeHostResolver _resolver = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 21, 9, 0, 0, TimeSpan.Zero));
    private readonly ShareConnectionManager _manager;
    private readonly ConnectionTester _tester;

    public ConnectionTesterSecurityReviewTests()
    {
        _loggerFactory = LoggerFactory.Create(b => b.AddProvider(_logs).SetMinimumLevel(LogLevel.Trace));
        Directory.CreateDirectory(_temp.Combine("credentials"));
        _store = new CredentialStore(_temp.Combine("credentials"), TestFileTrust.CurrentUser, new DpapiMachineProtector(), _time, _loggerFactory.CreateLogger<CredentialStore>());
        _manager = new ShareConnectionManager(_connector, _loggerFactory.CreateLogger<ShareConnectionManager>());
        _tester = new ConnectionTester(
            _manager,
            _resolver,
            new ShareCredentialProvider(_store, _loggerFactory.CreateLogger<ShareCredentialProvider>()),
            _time,
            new ConnectionTestOptions(),
            _loggerFactory.CreateLogger<ConnectionTester>());
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CredentialInput Ana => new() { UserName = "ana", Password = "Contraseña-QA-HU05-c41d" };

    private static CredentialInput Luis => new() { UserName = "luis", Password = "Otra-clave-HU05-77" };

    public void Dispose()
    {
        _tester.Dispose();
        _manager.Dispose();
        _store.Dispose();
        _loggerFactory.Dispose();
        _temp.Dispose();
    }

    // ---- ALTA-1: el servidor no puede resolver al propio equipo (§7.5) ----

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("::ffff:127.0.0.1")]
    public async Task A_server_that_resolves_to_the_machine_itself_is_rejected_without_connecting(string address)
    {
        _resolver.Addresses["nas"] = [IPAddress.Parse("192.168.1.163"), IPAddress.Parse(address)];

        var ex = await Assert.ThrowsAsync<BackboneException>(() => _tester.TestAsync(Share, Ana, Ct));

        Assert.Equal(BackboneErrorCode.ValidationFailed, ex.Code);
        Assert.Equal(ValidationIssueCode.DestinationNotAllowed, Assert.Single(ex.Validation!.Errors).Code);
        Assert.Equal(["nas"], _resolver.Queries);
        Assert.Empty(_connector.Connections);
        await AssertCleanAsync();
    }

    [Fact]
    public async Task A_server_that_resolves_to_a_local_interface_address_is_rejected()
    {
        var local = LocalMachine.Addresses().FirstOrDefault(a => !IPAddress.IsLoopback(a));
        if (local is null)
        {
            Assert.Skip("El equipo no tiene direcciones de red que no sean de bucle invertido.");
        }

        _resolver.Addresses["nas"] = [local];

        var ex = await Assert.ThrowsAsync<BackboneException>(() => _tester.TestAsync(Share, Ana, Ct));

        Assert.Equal(BackboneErrorCode.ValidationFailed, ex.Code);
        Assert.Empty(_connector.Connections);
    }

    [Fact]
    public async Task If_the_name_does_not_resolve_the_test_still_connects_and_the_connection_decides()
    {
        _resolver.Errors["nas"] = FakeHostResolver.HostNotFound();
        _connector.ConnectError = new ShareException(BackboneErrorCode.NasUnreachable, ShareErrors.ErrorBadNetPath);

        var result = await _tester.TestAsync(Share, Ana, Ct);

        Assert.Equal(BackboneErrorCode.NasUnreachable, result.ErrorCode);
        Assert.Single(_connector.Connections);
        await AssertCleanAsync();
    }

    [Fact]
    public async Task A_resolution_that_hangs_is_NasUnreachable_in_time_without_connecting()
    {
        _resolver.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var started = _time.GetUtcNow();
        var result = await AdvanceUntilDoneAsync(_tester.TestAsync(Share, Ana, Ct));

        Assert.Equal(BackboneErrorCode.NasUnreachable, result.ErrorCode);
        Assert.True(_time.GetUtcNow() - started < TimeSpan.FromSeconds(15));
        Assert.Empty(_connector.Connections);
        _resolver.Gate.SetResult();
        await AssertCleanAsync();
    }

    // ---- MEDIA-1 y BAJA-1: gestor único de conexiones ----

    [Fact]
    public async Task A_test_reuses_a_live_connection_with_the_same_credential_and_does_not_close_it()
    {
        using var live = Credential(Share, Ana);
        var copy = await _manager.AcquireAsync(live, Ct);

        var result = await _tester.TestAsync(Share, Ana, Ct);
        await _tester.WhenCleanupsCompleteAsync().WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.True(result.Success);
        Assert.Single(_connector.Connections);
        Assert.Equal(1, _connector.OpenConnections);
        Assert.False(Assert.Single(_connector.Created).IsDisposed);

        await copy.DisposeAsync();
        Assert.Equal(0, _connector.OpenConnections);
    }

    [Fact]
    public async Task A_different_credential_while_the_server_has_a_live_connection_is_CredentialsRejected_and_the_live_one_survives()
    {
        using var live = Credential(Share, Ana);
        var copy = await _manager.AcquireAsync(live, Ct);

        var result = await _tester.TestAsync(@"\\NAS\otro", Luis, Ct);
        await _tester.WhenCleanupsCompleteAsync().WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.False(result.Success);
        Assert.Equal(BackboneErrorCode.CredentialsRejected, result.ErrorCode);
        Assert.Single(_connector.Connections);
        Assert.Equal(1, _connector.OpenConnections);
        Assert.DoesNotContain(Luis.Password, _logs.AllText(), StringComparison.Ordinal);

        await copy.DisposeAsync();
    }

    // ---- BAJA-2: turnos y limpiezas pendientes ----

    [Fact]
    public async Task Tests_of_the_same_server_run_one_at_a_time()
    {
        var gate = _connector.Share.Gate("List");

        var first = _tester.TestAsync(Share, Ana, Ct);
        await WaitUntilAsync(() => !_connector.Created.IsEmpty);
        var second = _tester.TestAsync(@"\\nas\otro", Ana, Ct);
        await Task.Delay(100, Ct);

        Assert.Single(_resolver.Queries); // la segunda espera su turno antes de resolver
        Assert.False(second.IsCompleted);

        gate.SetResult();
        Assert.True((await first).Success);
        Assert.True((await second).Success);
        Assert.Equal(2, _resolver.Queries.Count);
        await AssertCleanAsync();
    }

    [Fact]
    public async Task At_most_two_tests_run_at_once_across_servers()
    {
        var gate = _connector.Share.Gate("List");

        var tests = ThreeServers.Select(s => _tester.TestAsync(s, Ana, Ct)).ToArray();
        await WaitUntilAsync(() => _connector.Created.Count == 2);
        await Task.Delay(100, Ct);

        Assert.Equal(2, _connector.Connections.Count);
        Assert.Equal(2, _resolver.Queries.Count);

        gate.SetResult();
        var results = await Task.WhenAll(tests);
        Assert.All(results, r => Assert.True(r.Success));
        Assert.Equal(3, _connector.Connections.Count);
        await AssertCleanAsync();
    }

    [Fact]
    public async Task A_pending_cleanup_of_the_same_server_makes_the_next_test_NasUnreachable_without_connecting()
    {
        _connector.Share.IgnoresDeleteOnClose = true;
        var gate = _connector.Share.Gate("Delete");
        var first = await AdvanceUntilDoneAsync(_tester.TestAsync(Share, Ana, Ct));
        Assert.Equal(BackboneErrorCode.NasUnreachable, first.ErrorCode);

        var second = await AdvanceUntilDoneAsync(_tester.TestAsync(Share, Ana, Ct));

        Assert.Equal(BackboneErrorCode.NasUnreachable, second.ErrorCode);
        Assert.Single(_connector.Connections);
        Assert.Single(_resolver.Queries);

        gate.SetResult();
        await AssertCleanAsync();
    }

    [Fact]
    public async Task A_pending_cleanup_that_ends_in_time_lets_the_next_test_run()
    {
        _connector.Share.IgnoresDeleteOnClose = true;
        var gate = _connector.Share.Gate("Delete");
        await AdvanceUntilDoneAsync(_tester.TestAsync(Share, Ana, Ct));

        var second = _tester.TestAsync(Share, Ana, Ct);
        await Task.Delay(50, Ct);
        Assert.False(second.IsCompleted);
        _connector.Share.Gates.TryRemove("Delete", out _);
        gate.SetResult();

        Assert.True((await second.WaitAsync(TimeSpan.FromSeconds(10), Ct)).Success);
        await AssertCleanAsync();
    }

    [Fact]
    public async Task Stopping_the_service_waits_for_cleanups_and_forces_the_roots_still_open()
    {
        _connector.Share.IgnoresDeleteOnClose = true;
        var gate = _connector.Share.Gate("Delete");
        await AdvanceUntilDoneAsync(_tester.TestAsync(Share, Ana, Ct));
        var shutdown = new ShareConnectionShutdown(_tester, _manager, _time, _loggerFactory.CreateLogger<ShareConnectionShutdown>());

        var stop = shutdown.StopAsync(Ct);
        for (var i = 0; i < 100 && !stop.IsCompleted; i++)
        {
            await Task.Delay(5, Ct);
            _time.Advance(TimeSpan.FromMilliseconds(200));
        }

        await stop.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal([Share], _connector.ForcedDisconnects);
        Assert.Contains(_logs.Entries, e => e.Message.Contains("limpiezas", StringComparison.Ordinal));

        gate.SetResult();
        await AssertCleanAsync();
    }

    [Fact]
    public async Task Stopping_the_service_without_pending_work_forces_nothing()
    {
        Assert.True((await _tester.TestAsync(Share, Ana, Ct)).Success);
        var shutdown = new ShareConnectionShutdown(_tester, _manager, _time, _loggerFactory.CreateLogger<ShareConnectionShutdown>());

        await shutdown.StopAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Empty(_connector.ForcedDisconnects);
    }

    // ---- Informativa: la carpeta destino sigue dentro del recurso (GetFinalPathNameByHandle) ----

    [Theory]
    [InlineData(@"\\?\UNC\otro\fuera")]
    [InlineData(@"\\?\UNC\nas\backups-otro\sub")]
    [InlineData(@"C:\Windows")]
    public async Task A_target_folder_outside_the_share_is_AccessDenied_without_writing(string finalPath)
    {
        var sub = Share + @"\sub";
        _connector.Share.Directories[sub] = 0;
        _connector.Share.FinalPaths[sub] = finalPath;

        var result = await _tester.TestAsync(sub, Ana, Ct);

        Assert.Equal(BackboneErrorCode.AccessDenied, result.ErrorCode);
        Assert.Equal((true, false, false), (result.CanRead, result.CanWrite, result.CanDelete));
        Assert.Empty(_connector.Share.ProbeFiles);
        await AssertCleanAsync();
    }

    [Fact]
    public async Task If_the_redirector_cannot_give_the_final_path_the_test_goes_on()
    {
        var sub = Share + @"\sub";
        _connector.Share.Directories[sub] = 0;
        _connector.Share.FinalPaths[sub] = null;

        var result = await _tester.TestAsync(sub, Ana, Ct);

        Assert.True(result.Success);
        await AssertCleanAsync();
    }

    [Theory]
    [InlineData(@"\\?\UNC\nas\backups\sub", @"\\?\UNC\nas\backups", true)]
    [InlineData(@"\\?\UNC\NAS\Backups", @"\\?\UNC\nas\backups\", true)]
    [InlineData(@"\\?\UNC\nas\backups-otro", @"\\?\UNC\nas\backups", false)]
    [InlineData(@"\\?\UNC\nas", @"\\?\UNC\nas\backups", false)]
    public void IsWithin_compares_whole_components(string path, string root, bool expected) =>
        Assert.Equal(expected, ConnectionTester.IsWithin(path, root));

    private static NasCredential Credential(string root, CredentialInput input)
    {
        var buffer = GC.AllocateArray<char>(input.Password.Length + 1, pinned: true);
        input.Password.AsSpan().CopyTo(buffer);
        return new NasCredential(root, input.UserName, DateTimeOffset.UnixEpoch, buffer, input.Password.Length);
    }

    private async Task AssertCleanAsync()
    {
        await _tester.WhenCleanupsCompleteAsync().WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Empty(_connector.Share.Files);
        Assert.Equal(0, _connector.OpenConnections);
        Assert.Empty(_manager.ActiveShareRoots);
        Assert.All(_connector.Created, c => Assert.False(c.DisposedWithOperationsInFlight));
    }

    private async Task<ConnectionTestResult> AdvanceUntilDoneAsync(Task<ConnectionTestResult> test)
    {
        for (var i = 0; i < 120 && !test.IsCompleted; i++)
        {
            await Task.Delay(5, Ct);
            if (!test.IsCompleted)
            {
                _time.Advance(TimeSpan.FromMilliseconds(500));
            }
        }

        return await test.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 1000 && !condition(); i++)
        {
            await Task.Delay(5, Ct);
        }

        Assert.True(condition());
    }
}
