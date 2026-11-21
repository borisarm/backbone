using Backbone.Contracts;
using Backbone.Service.Nas;
using Backbone.Service.Persistence;
using Backbone.Service.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backbone.Service.Tests.Nas;

/// <summary>
/// Revisión de seguridad de HU-05 (MEDIA-1, BAJA-1): una conexión por raíz con recuento de referencias; solo la última
/// liberación la cierra; una credencial distinta de la de una conexión viva con el mismo servidor se rechaza sin conectar.
/// </summary>
[Trait("Story", "HU-05")]
public sealed class ShareConnectionManagerTests : IDisposable
{
    private readonly FakeShareConnector _connector = new();
    private readonly ShareConnectionManager _manager;

    public ShareConnectionManagerTests() =>
        _manager = new ShareConnectionManager(_connector, NullLogger<ShareConnectionManager>.Instance);

    public void Dispose() => _manager.Dispose();

    [Fact]
    public async Task The_same_root_and_credential_share_one_connection_closed_by_the_last_release()
    {
        using var a = Credential(@"\\nas\backups", "ana", "clave");
        using var b = Credential(@"\\nas\backups", "ana", "clave");

        var first = await _manager.AcquireAsync(a, CancellationToken.None);
        var second = await _manager.AcquireAsync(b, CancellationToken.None);

        Assert.Single(_connector.Connections);
        Assert.Same(first.Connection, second.Connection);

        await first.DisposeAsync();
        await first.DisposeAsync(); // idempotente
        Assert.Equal(1, _connector.OpenConnections);

        await second.DisposeAsync();
        Assert.Equal(0, _connector.OpenConnections);
        Assert.Empty(_manager.ActiveShareRoots);
    }

    [Fact]
    public async Task Another_share_of_the_same_server_with_the_same_credential_gets_its_own_connection()
    {
        using var a = Credential(@"\\nas\backups", "ana", "clave");
        using var b = Credential(@"\\nas\fotos", "ana", "clave");

        await using var first = await _manager.AcquireAsync(a, CancellationToken.None);
        await using var second = await _manager.AcquireAsync(b, CancellationToken.None);

        Assert.Equal(2, _connector.Connections.Count);
    }

    [Theory]
    [InlineData("ana", "otra")]
    [InlineData("luis", "clave")]
    [InlineData("ANA", "clave")]
    public async Task Another_credential_for_a_server_with_a_live_connection_is_rejected_without_connecting(string user, string password)
    {
        using var live = Credential(@"\\nas\backups", "ana", "clave");
        await using var copy = await _manager.AcquireAsync(live, CancellationToken.None);
        using var other = Credential(@"\\NAS\fotos", user, password);

        var ex = await Assert.ThrowsAsync<ShareException>(() => _manager.AcquireAsync(other, CancellationToken.None));

        Assert.Equal((BackboneErrorCode.CredentialsRejected, ShareErrors.ErrorSessionCredentialConflict), (ex.Code, ex.NativeErrorCode));
        Assert.Single(_connector.Connections);
        Assert.Equal(1, _connector.OpenConnections);
    }

    [Fact]
    public async Task Once_released_another_credential_can_connect()
    {
        using var first = Credential(@"\\nas\backups", "ana", "clave");
        await (await _manager.AcquireAsync(first, CancellationToken.None)).DisposeAsync();
        using var second = Credential(@"\\nas\backups", "luis", "otra");

        await using var lease = await _manager.AcquireAsync(second, CancellationToken.None);

        Assert.Equal(2, _connector.Connections.Count);
    }

    [Fact]
    public async Task A_failed_connection_is_forgotten()
    {
        _connector.ConnectError = new ShareException(BackboneErrorCode.NasUnreachable, ShareErrors.ErrorBadNetPath);
        using var a = Credential(@"\\nas\backups", "ana", "clave");

        await Assert.ThrowsAsync<ShareException>(() => _manager.AcquireAsync(a, CancellationToken.None));

        Assert.Empty(_manager.ActiveShareRoots);
        _connector.ConnectError = null;
        using var b = Credential(@"\\nas\backups", "luis", "otra");
        await using var lease = await _manager.AcquireAsync(b, CancellationToken.None);
    }

    [Fact]
    public async Task Waiters_on_a_connection_in_progress_reuse_it()
    {
        var gate = _connector.ConnectGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var a = Credential(@"\\nas\backups", "ana", "clave");
        using var b = Credential(@"\\nas\backups", "ana", "clave");

        var first = _manager.AcquireAsync(a, CancellationToken.None);
        var second = _manager.AcquireAsync(b, CancellationToken.None);
        gate.SetResult();

        await using var l1 = await first;
        await using var l2 = await second;
        Assert.Single(_connector.Connections);
    }

    [Fact]
    public async Task Force_disconnect_closes_every_root_still_active()
    {
        using var a = Credential(@"\\nas\backups", "ana", "clave");
        await using var lease = await _manager.AcquireAsync(a, CancellationToken.None);

        await _manager.ForceDisconnectAllAsync(CancellationToken.None);

        Assert.Equal([@"\\nas\backups"], _connector.ForcedDisconnects);
    }

    private static NasCredential Credential(string root, string user, string password)
    {
        var buffer = GC.AllocateArray<char>(password.Length + 1, pinned: true);
        password.AsSpan().CopyTo(buffer);
        return new NasCredential(root.ToLowerInvariant(), user, DateTimeOffset.UnixEpoch, buffer, password.Length);
    }
}
