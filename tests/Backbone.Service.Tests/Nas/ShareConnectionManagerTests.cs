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
public sealed class ShareConnectionManagerTests
{
    private readonly FakeShareConnector _connector = new();
    private readonly ShareConnectionManager _manager;

    public ShareConnectionManagerTests() =>
        _manager = new ShareConnectionManager(_connector, NullLogger<ShareConnectionManager>.Instance);

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
    public async Task Another_share_of_the_same_server_is_NasBusy_even_with_the_same_secret()
    {
        // M-1: nunca se comparan usuario ni contraseña; una conexión de otra raíz del servidor no se puede compartir.
        using var a = Credential(@"\\nas\backups", "ana", "clave");
        using var b = Credential(@"\\nas\fotos", "ana", "clave");

        await using var first = await _manager.AcquireAsync(a, CancellationToken.None);
        var ex = await Assert.ThrowsAsync<ShareException>(() => _manager.AcquireAsync(b, CancellationToken.None));

        Assert.Equal(BackboneErrorCode.NasBusy, ex.Code);
        Assert.Single(_connector.Connections);
    }

    [Fact]
    public async Task The_same_root_with_another_saved_identity_is_NasBusy_and_the_same_identity_shares()
    {
        var saved = DateTimeOffset.UnixEpoch.AddDays(1);
        using var live = Credential(@"\\nas\backups", "ana", "clave", saved);
        await using var copy = await _manager.AcquireAsync(live, CancellationToken.None);

        using var same = Credential(@"\\nas\backups", "ana", "clave", saved);
        await using var shared = await _manager.AcquireAsync(same, CancellationToken.None);
        Assert.Same(copy.Connection, shared.Connection);

        // Se ha vuelto a guardar la credencial (otro UpdatedUtc), aunque con el mismo usuario y contraseña.
        using var resaved = Credential(@"\\nas\backups", "ana", "clave", saved.AddMinutes(1));
        var ex = await Assert.ThrowsAsync<ShareException>(() => _manager.AcquireAsync(resaved, CancellationToken.None));

        Assert.Equal(BackboneErrorCode.NasBusy, ex.Code);
        Assert.Single(_connector.Connections);
    }

    [Theory]
    [InlineData("ana", "clave")]
    [InlineData("ana", "otra")]
    [InlineData("luis", "clave")]
    [InlineData("ANA", "clave")]
    public async Task An_explicit_credential_never_shares_a_live_connection_whatever_its_secret(string user, string password)
    {
        using var live = Credential(@"\\nas\backups", "ana", "clave");
        await using var copy = await _manager.AcquireAsync(live, CancellationToken.None);
        using var other = Credential(@"\\nas\backups", user, password, isExplicit: true);

        var ex = await Assert.ThrowsAsync<ShareException>(() => _manager.AcquireAsync(other, CancellationToken.None));

        // La misma respuesta con la contraseña correcta y con cualquier otra: no hay oráculo (M-1). No se llama a Windows.
        Assert.Equal((BackboneErrorCode.NasBusy, 0), (ex.Code, ex.NativeErrorCode));
        Assert.Single(_connector.Connections);
        Assert.Equal(1, _connector.OpenConnections);
    }

    [Fact]
    public async Task A_saved_credential_never_shares_a_live_connection_made_with_an_explicit_one()
    {
        using var explicitCredential = Credential(@"\\nas\backups", "ana", "clave", isExplicit: true);
        await using var live = await _manager.AcquireAsync(explicitCredential, CancellationToken.None);
        using var saved = Credential(@"\\nas\backups", "ana", "clave");

        var ex = await Assert.ThrowsAsync<ShareException>(() => _manager.AcquireAsync(saved, CancellationToken.None));

        Assert.Equal(BackboneErrorCode.NasBusy, ex.Code);
        Assert.Single(_connector.Connections);
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

    private static NasCredential Credential(string root, string user, string password, DateTimeOffset? updatedUtc = null, bool isExplicit = false)
    {
        var buffer = GC.AllocateArray<char>(password.Length + 1, pinned: true);
        password.AsSpan().CopyTo(buffer);
        return new NasCredential(root.ToLowerInvariant(), user, updatedUtc ?? DateTimeOffset.UnixEpoch, buffer, password.Length, isExplicit);
    }
}
