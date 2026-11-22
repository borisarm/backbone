using Backbone.Contracts;
using Backbone.Service.Nas;
using Backbone.Service.Persistence;
using Backbone.Service.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backbone.Service.Tests.Nas;

/// <summary>
/// Revisión de seguridad de HU-06 (ALTA-1, MEDIA-2), §8.2 B-1 y B-2: una raíz queda "cerrando" hasta que vuelve
/// <c>WNetCancelConnection2</c>, y un <c>AcquireAsync</c> de esa raíz espera y reconecta; con el gestor cerrado (parada) no se
/// conecta y lo que queda se cierra a la fuerza.
/// </summary>
[Trait("Story", "HU-06")]
public sealed class ShareConnectionManagerClosingTests : IDisposable
{
    private const string Root = @"\\nas\backups";

    private readonly FakeShareConnector _connector = new();
    private readonly ShareConnectionManager _manager;

    public ShareConnectionManagerClosingTests() =>
        _manager = new ShareConnectionManager(_connector, NullLogger<ShareConnectionManager>.Instance);

    public void Dispose() => _manager.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static NasCredential Credential(string root = Root, string user = "ana", string password = "clave")
    {
        var buffer = GC.AllocateArray<char>(password.Length + 1, pinned: true);
        password.AsSpan().CopyTo(buffer);
        return new NasCredential(root, user, DateTimeOffset.UnixEpoch, buffer, password.Length);
    }

    private static async Task SettleAsync() => await Task.Delay(100, Ct);

    // ---- B-1: desconexión pendiente ----

    [Fact]
    public async Task An_acquire_during_a_pending_disconnect_waits_for_it_and_then_connects_with_its_own_use_record()
    {
        using var first = Credential();
        var lease = await _manager.AcquireAsync(first, Ct);
        var gate = _connector.Share.DisposeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = lease.DisposeAsync().AsTask();
        await SettleAsync();
        Assert.False(release.IsCompleted);

        using var second = Credential();
        var acquire = _manager.AcquireAsync(second, Ct);
        await SettleAsync();

        Assert.False(acquire.IsCompleted);
        Assert.Equal(["connect:" + Root], _connector.Events);

        gate.SetResult();
        await using var lease2 = await acquire.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await release;

        // La cancelación pendiente terminó antes de conectar: el registro de uso de la segunda conexión sigue ahí.
        Assert.Equal(["connect:" + Root, "disconnect:" + Root, "connect:" + Root], _connector.Events);
        Assert.True(await lease2.Connection.HasUseRecordAsync(Ct));
        Assert.Equal(1, _connector.OpenConnections);
    }

    [Fact]
    public async Task Several_acquires_waiting_for_the_disconnect_share_one_new_connection()
    {
        using var first = Credential();
        var lease = await _manager.AcquireAsync(first, Ct);
        var gate = _connector.Share.DisposeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = lease.DisposeAsync().AsTask();
        using var a = Credential();
        using var b = Credential();
        var acquireA = _manager.AcquireAsync(a, Ct);
        var acquireB = _manager.AcquireAsync(b, Ct);
        await SettleAsync();

        gate.SetResult();
        await using var leaseA = await acquireA.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await using var leaseB = await acquireB.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await release;

        Assert.Same(leaseA.Connection, leaseB.Connection);
        Assert.Equal(2, _connector.Connections.Count);
    }

    [Fact]
    public async Task Cancelling_an_acquire_that_waits_for_the_disconnect_does_not_connect()
    {
        using var first = Credential();
        var lease = await _manager.AcquireAsync(first, Ct);
        var gate = _connector.Share.DisposeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = lease.DisposeAsync().AsTask();
        using var second = Credential();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var acquire = _manager.AcquireAsync(second, cts.Token);
        await SettleAsync();

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => acquire);
        gate.SetResult();
        await release;

        Assert.Single(_connector.Connections);
        Assert.Equal(0, _connector.OpenConnections);
    }

    [Fact]
    public async Task Another_root_does_not_wait_for_the_disconnect_of_a_different_one()
    {
        using var first = Credential();
        var lease = await _manager.AcquireAsync(first, Ct);
        var gate = _connector.Share.DisposeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = lease.DisposeAsync().AsTask();
        using var other = Credential(@"\\nas\fotos");

        await using var otherLease = await _manager.AcquireAsync(other, Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Equal(2, _connector.Connections.Count);
        gate.SetResult();
        await release;
    }

    [Fact]
    public async Task A_failed_disconnect_still_ends_the_closing_state()
    {
        using var first = Credential();
        var lease = await _manager.AcquireAsync(first, Ct);
        _connector.Share.DisposeError = new IOException("red cortada");

        await Assert.ThrowsAsync<IOException>(() => lease.DisposeAsync().AsTask());
        _connector.Share.DisposeError = null;

        using var second = Credential();
        await using var lease2 = await _manager.AcquireAsync(second, Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(2, _connector.Connections.Count);
    }

    // ---- B-2: parada ----

    [Fact]
    public async Task A_closed_manager_does_not_connect()
    {
        _manager.Close();
        using var credential = Credential();

        var ex = await Assert.ThrowsAsync<ShareException>(() => _manager.AcquireAsync(credential, Ct));

        Assert.Equal(BackboneErrorCode.Interrupted, ex.Code);
        Assert.Empty(_connector.Connections);
    }

    [Fact]
    public async Task Forcing_a_connection_still_in_progress_closes_it_when_it_finishes_connecting()
    {
        var gate = _connector.ConnectGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var credential = Credential();
        var acquire = _manager.AcquireAsync(credential, CancellationToken.None);
        await SettleAsync();
        _manager.Close();

        await _manager.ForceDisconnectAllAsync(Ct);
        Assert.Empty(_connector.ForcedDisconnects);

        gate.SetResult();
        await using var lease = await acquire.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await WaitUntilAsync(() => !_connector.ForcedDisconnects.IsEmpty);
        Assert.Equal([Root], _connector.ForcedDisconnects);
    }

    [Fact]
    public async Task Forcing_includes_the_roots_whose_disconnect_is_still_pending()
    {
        using var credential = Credential();
        var lease = await _manager.AcquireAsync(credential, Ct);
        var gate = _connector.Share.DisposeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = lease.DisposeAsync().AsTask();
        var waiting = _manager.WhenReleasesCompleteAsync();
        await SettleAsync();
        Assert.False(waiting.IsCompleted);

        await _manager.ForceDisconnectAllAsync(Ct);

        Assert.Equal([Root], _connector.ForcedDisconnects);
        gate.SetResult();
        await release;
        await waiting.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "La condición no se cumplió a tiempo.");
            await Task.Delay(10, Ct);
        }
    }
}
