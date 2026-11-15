using Backbone.Contracts;
using Backbone.Service.Ipc;
using Backbone.Service.Tests.TestSupport;

namespace Backbone.Service.Tests.Ipc;

/// <summary>
/// HT-03 criterio 4 y §7.1: límites de conexiones y liberación de recursos al desconectar (servidor completo sobre flujos en
/// memoria).
/// </summary>
[Trait("Story", "HT-03")]
public sealed class ConnectionLifecycleTests : IAsyncLifetime
{
    private readonly IpcTestHarness _server = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => new(_server.AuthorizeAsync(TestSids.Ana, TestSids.Luis));

    public ValueTask DisposeAsync() => _server.DisposeAsync();

    [Fact]
    public async Task An_abrupt_disconnect_releases_the_connection_without_affecting_the_others()
    {
        var ana = await _server.ConnectAuthorizedAsync(TestSids.Identity(TestSids.Ana));
        var luis = await _server.ConnectAuthorizedAsync(TestSids.Identity(TestSids.Luis));
        Assert.Equal(2, _server.Manager.Count);

        // Corte abrupto: se cierra el flujo del cliente sin cerrar JsonRpc ordenadamente.
        await ana.Client.Stream.DisposeAsync();

        await ana.WaitClosedAsync();
        Assert.True(ana.Channel.IsDisposed);
        Assert.Equal(1, _server.Manager.Count);
        Assert.DoesNotContain(ana.RequiredServer, _server.Manager.Connections);
        var info = await luis.Proxy.GetServiceInfoAsync(Ct);
        Assert.Equal(TestSids.Luis, info.Caller.UserSid);
    }

    [Fact]
    public async Task A_disconnect_frees_the_per_account_slot()
    {
        var connections = new List<TestConnection>();
        for (var i = 0; i < IpcOptions.DefaultMaxConnectionsPerSid; i++)
        {
            connections.Add(await _server.ConnectAuthorizedAsync(TestSids.Identity(TestSids.Ana)));
        }

        await connections[0].Client.Stream.DisposeAsync();
        await connections[0].WaitClosedAsync();

        var replacement = await _server.ConnectAuthorizedAsync(TestSids.Identity(TestSids.Ana));
        Assert.Equal(TestSids.Ana, (await replacement.Proxy.GetServiceInfoAsync(Ct)).Caller.UserSid);
    }

    [Fact]
    public async Task An_account_cannot_open_more_than_four_connections()
    {
        for (var i = 0; i < IpcOptions.DefaultMaxConnectionsPerSid; i++)
        {
            await _server.ConnectAuthorizedAsync(TestSids.Identity(TestSids.Ana));
        }

        var fifth = _server.Connect(TestSids.Identity(TestSids.Ana));
        await RpcAssert.FailsWithAsync(BackboneErrorCode.NotAuthorized, () => fifth.Proxy.GetServiceInfoAsync(Ct));
        await fifth.WaitClosedAsync();

        // Otra cuenta sigue pudiendo conectar.
        var luis = await _server.ConnectAuthorizedAsync(TestSids.Identity(TestSids.Luis));
        Assert.Equal(TestSids.Luis, (await luis.Proxy.GetServiceInfoAsync(Ct)).Caller.UserSid);
    }

    [Fact]
    public async Task The_server_does_not_accept_more_than_eight_connections()
    {
        for (var i = 0; i < BackboneIpc.MaxConnections; i++)
        {
            Assert.NotNull(_server.Connect(TestSids.Identity(i % 2 == 0 ? TestSids.Ana : TestSids.Luis)).Server);
        }

        var ninth = _server.Connect(TestSids.Identity(TestSids.Ana, elevated: true));

        Assert.Null(ninth.Server);
        Assert.True(ninth.Channel.IsDisposed);
        Assert.Equal(BackboneIpc.MaxConnections, _server.Manager.Count);
    }

    [Fact]
    public async Task Closing_the_server_closes_every_connection()
    {
        var ana = await _server.ConnectAuthorizedAsync(TestSids.Identity(TestSids.Ana));
        var luis = await _server.ConnectAuthorizedAsync(TestSids.Identity(TestSids.Luis));

        await _server.Manager.DisposeAsync();

        await ana.WaitClosedAsync();
        await luis.WaitClosedAsync();
        Assert.True(ana.Channel.IsDisposed);
        Assert.True(luis.Channel.IsDisposed);
        Assert.Equal(0, _server.Manager.Count);
    }

    [Fact]
    public async Task A_cancelled_call_does_not_break_the_connection()
    {
        var ana = await _server.ConnectAuthorizedAsync(TestSids.Identity(TestSids.Ana));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ana.Proxy.GetServiceInfoAsync(cts.Token));

        await ana.Proxy.GetServiceInfoAsync(Ct);
    }
}
