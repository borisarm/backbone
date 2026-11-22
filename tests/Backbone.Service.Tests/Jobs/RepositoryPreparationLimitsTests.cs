using Backbone.Contracts;
using Backbone.Service.Ipc;
using Backbone.Service.Jobs;
using Backbone.Service.Nas;
using Backbone.Service.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using Outcome = Backbone.Service.Jobs.RepositoryPreparationOutcome;

namespace Backbone.Service.Tests.Jobs;

/// <summary>
/// Revisión de seguridad de HU-06: carrera con una desconexión pendiente y registro de uso (ALTA-1, §8.2 B-1), límites de
/// preparaciones a la vez y abandonadas (MEDIA-1) y parada del servicio (MEDIA-2, §8.2 B-2).
/// </summary>
[Trait("Story", "HU-06")]
public sealed class RepositoryPreparationLimitsTests : IAsyncLifetime
{
    private const string Nas1 = @"\\nas1\b\x";
    private const string Nas2 = @"\\nas2\b\x";
    private const string Nas3 = @"\\nas3\b\x";
    private const string Backups = @"\\nas\backups";

    private readonly IpcTestHarness _server = new(preparation: new RepositoryPreparationOptions { MaxConcurrent = 2 });

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private RepositoryPreparer Preparer => _server.Repositories;

    public async ValueTask InitializeAsync()
    {
        foreach (var root in new[] { @"\\nas1\b", @"\\nas2\b", @"\\nas3\b", Backups })
        {
            await _server.Credentials.SaveAsync(root, new CredentialInput { UserName = "ana", Password = "Contraseña-HU06-limites" }, Ct);
        }
    }

    public ValueTask DisposeAsync() => _server.DisposeAsync();

    private TaskCompletionSource HangConnections() =>
        _server.ShareConnector.ConnectGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task WaitForConnectionsAsync(int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (_server.ShareConnector.Connections.Count < count)
        {
            Assert.True(DateTime.UtcNow < deadline, "No se conectó a tiempo.");
            await Task.Delay(10, Ct);
        }

        await Task.Delay(100, Ct); // el plazo de la preparación ya está armado
    }

    private async Task ExpireAsync() => await Task.Run(() => _server.Time.Advance(_server.PreparationOptions.Timeout + TimeSpan.FromSeconds(1)), Ct);

    private static async Task<BackboneErrorCode> CodeOfAsync(Task<Outcome> call) =>
        (await Assert.ThrowsAsync<BackboneException>(() => call)).Code;

    // ---- ALTA-1: carrera con una desconexión pendiente (§8.2 B-1, §7.5 A-3) ----

    [Fact]
    public async Task Preparing_while_a_previous_disconnect_is_pending_waits_and_uses_its_own_connection()
    {
        using var credential = await _server.ShareCredentials.GetRequiredAsync(Backups, Ct);
        var previous = await _server.Connections.AcquireAsync(credential, Ct);
        var gate = _server.ShareConnector.Share.DisposeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = previous.DisposeAsync().AsTask();

        var call = Preparer.PrepareAsync(Backups + @"\x", Ct);
        await Task.Delay(150, Ct);
        Assert.False(call.IsCompleted);
        Assert.Empty(_server.RepositoryStorages.Requested);

        gate.SetResult();
        Assert.Equal(Outcome.Initialized, await call.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        await release;

        Assert.Equal(["connect:" + Backups, "disconnect:" + Backups, "connect:" + Backups, "disconnect:" + Backups], _server.ShareConnector.Events);
    }

    [Fact]
    public async Task Without_the_use_record_of_the_connection_nothing_is_written_and_it_is_NasUnreachable()
    {
        // Lo que dejaría la carrera sin B-1: Windows ya no tiene el registro de uso de la conexión recién abierta.
        var gate = _server.ShareConnector.Share.Gate("UseRecord");
        var call = Preparer.PrepareAsync(Backups + @"\x", Ct);
        await WaitForConnectionsAsync(1);
        _server.ShareConnector.UseRecords.Clear();
        gate.SetResult();

        Assert.Equal(BackboneErrorCode.NasUnreachable, await CodeOfAsync(call));

        Assert.Empty(_server.RepositoryStorages.Requested);
        Assert.Empty(_server.RepositoryStorages.Memory(Backups + @"\x").FilePaths);
        Assert.Equal(0, _server.ShareConnector.OpenConnections);
        Assert.Contains(_server.Logs.Entries, e => e.Message.Contains("registro de uso", StringComparison.Ordinal));
    }

    // ---- MEDIA-1: límites ----

    [Fact]
    public async Task Beyond_the_global_limit_it_is_NasBusy_without_connecting_and_abandoned_preparations_keep_counting()
    {
        var gate = HangConnections();
        var first = Preparer.PrepareAsync(Nas1, Ct);
        var second = Preparer.PrepareAsync(Nas2, Ct);
        await WaitForConnectionsAsync(2);

        Assert.Equal(BackboneErrorCode.NasBusy, await CodeOfAsync(Preparer.PrepareAsync(Nas3, Ct)));
        Assert.Equal(2, _server.ShareConnector.Connections.Count);

        await ExpireAsync();
        Assert.Equal(BackboneErrorCode.NasUnreachable, await CodeOfAsync(first));
        Assert.Equal(BackboneErrorCode.NasUnreachable, await CodeOfAsync(second));

        // Abandonadas, pero sin haber vuelto de la E/S: siguen contando.
        Assert.Equal(BackboneErrorCode.NasBusy, await CodeOfAsync(Preparer.PrepareAsync(Nas3, Ct)));
        Assert.Equal(2, _server.ShareConnector.Connections.Count);

        gate.SetResult();
        await Preparer.WhenPreparationsCompleteAsync().WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(Outcome.Initialized, await Preparer.PrepareAsync(Nas3, Ct));
        Assert.Equal(0, _server.ShareConnector.OpenConnections);
    }

    [Fact]
    public async Task Only_one_preparation_runs_at_a_time_per_server()
    {
        var gate = HangConnections();
        var first = Preparer.PrepareAsync(Backups + @"\a", Ct);
        await WaitForConnectionsAsync(1);
        var second = Preparer.PrepareAsync(Backups + @"\b", Ct);
        await Task.Delay(150, Ct);

        Assert.False(second.IsCompleted);
        Assert.Empty(_server.RepositoryStorages.Requested);

        gate.SetResult();
        Assert.Equal(Outcome.Initialized, await first.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.Equal(Outcome.Initialized, await second.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.Equal([Backups + @"\a", Backups + @"\b"], _server.RepositoryStorages.Requested);
    }

    [Fact]
    public async Task A_preparation_waiting_for_the_server_turn_times_out_as_NasUnreachable_without_connecting_and_frees_its_slot()
    {
        var gate = HangConnections();
        var first = Preparer.PrepareAsync(Backups + @"\a", Ct);
        await WaitForConnectionsAsync(1);
        var second = Preparer.PrepareAsync(Backups + @"\b", Ct);
        await Task.Delay(150, Ct);

        await ExpireAsync();

        Assert.Equal(BackboneErrorCode.NasUnreachable, await CodeOfAsync(second));
        Assert.Equal(BackboneErrorCode.NasUnreachable, await CodeOfAsync(first));
        Assert.Single(_server.ShareConnector.Connections);

        gate.SetResult();
        await Preparer.WhenPreparationsCompleteAsync().WaitAsync(TimeSpan.FromSeconds(10), Ct);

        // Ningún cupo perdido: caben dos a la vez (el límite de la prueba).
        var again = new[] { Preparer.PrepareAsync(Nas1, Ct), Preparer.PrepareAsync(Nas2, Ct) };
        Assert.All(await Task.WhenAll(again).WaitAsync(TimeSpan.FromSeconds(10), Ct), outcome => Assert.Equal(Outcome.Initialized, outcome));
    }

    [Fact]
    public async Task Cancelling_does_not_abandon_the_connection_while_the_system_call_is_still_running()
    {
        var gate = HangConnections();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var call = Preparer.PrepareAsync(Nas1, cts.Token);
        await WaitForConnectionsAsync(1);

        await cts.CancelAsync();
        await Task.Delay(150, Ct);

        // WNetAddConnection2 sigue leyendo la contraseña: ni se responde ni se desecha la credencial.
        Assert.False(call.IsCompleted);
        Assert.False(FakeShareConnector.IsDisposed(_server.ShareConnector.LastCredential!));

        gate.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        await Preparer.WhenPreparationsCompleteAsync().WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.True(FakeShareConnector.IsDisposed(_server.ShareConnector.LastCredential!));
        Assert.Equal(0, _server.ShareConnector.OpenConnections);
        Assert.Empty(_server.RepositoryStorages.Requested);
    }

    [Fact]
    public async Task Cancelling_while_waiting_for_the_server_turn_frees_the_slot_and_the_credential()
    {
        var gate = HangConnections();
        var first = Preparer.PrepareAsync(Backups + @"\a", Ct);
        await WaitForConnectionsAsync(1);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var second = Preparer.PrepareAsync(Backups + @"\b", cts.Token);
        await Task.Delay(150, Ct);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);

        gate.SetResult();
        Assert.Equal(Outcome.Initialized, await first.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.Equal(Outcome.Initialized, await Preparer.PrepareAsync(Backups + @"\c", Ct));
    }

    // ---- Informativa 1: restaurar el estado solo si nadie lo ha cambiado ----

    [Theory]
    [InlineData(Contracts.RepositoryState.Unknown, Contracts.RepositoryState.ReadWrite)]
    [InlineData(Contracts.RepositoryState.ReadOnlyNewerFormat, Contracts.RepositoryState.ReadWrite)]
    public void A_failed_save_restores_the_previous_state_only_if_it_is_still_its_own(Contracts.RepositoryState previous, Contracts.RepositoryState mine)
    {
        var tracker = new RepositoryStateTracker();
        var id = Guid.NewGuid();

        tracker.Set(id, previous);
        tracker.Set(id, mine);
        tracker.RestoreIfUnchanged(id, mine, previous);
        Assert.Equal(previous, tracker.Get(id));

        // Otro guardado del mismo trabajo cambió el estado entretanto: no se pisa.
        tracker.Set(id, mine);
        tracker.Set(id, Contracts.RepositoryState.ReadOnlyNewerFormat == mine ? Contracts.RepositoryState.ReadWrite : Contracts.RepositoryState.ReadOnlyNewerFormat);
        var newer = tracker.Get(id);
        tracker.RestoreIfUnchanged(id, mine, previous);
        Assert.Equal(newer, tracker.Get(id));
    }

    // ---- MEDIA-2: parada del servicio (§8.2 B-2) ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stopping_waits_for_the_preparations_then_forces_the_connection_and_the_manager_stops_connecting(bool abandoned)
    {
        var gate = HangConnections();
        var call = Preparer.PrepareAsync(Nas1, Ct);
        await WaitForConnectionsAsync(1);
        if (abandoned)
        {
            await ExpireAsync();
            Assert.Equal(BackboneErrorCode.NasUnreachable, await CodeOfAsync(call));
        }

        var shutdown = new ShareConnectionShutdown(
            _server.ConnectionTester, Preparer, _server.Connections, _server.Time, _server.LoggerFactory.CreateLogger<ShareConnectionShutdown>());
        var stop = shutdown.StopAsync(Ct);
        for (var i = 0; i < 100 && !stop.IsCompleted; i++)
        {
            await Task.Delay(5, Ct);
            _server.Time.Advance(TimeSpan.FromMilliseconds(200));
        }

        await stop.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Contains(_server.Logs.Entries, e => e.Message.Contains("preparaciones de repositorio", StringComparison.Ordinal));
        Assert.Empty(_server.ShareConnector.ForcedDisconnects); // sigue conectando: se cierra cuando termine

        gate.SetResult();
        await Preparer.WhenPreparationsCompleteAsync().WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (_server.ShareConnector.ForcedDisconnects.IsEmpty && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, Ct);
        }

        Assert.Equal([@"\\nas1\b"], _server.ShareConnector.ForcedDisconnects);
        if (!abandoned)
        {
            // Según quién llegue antes, la preparación termina o ve que la parada ya cerró la conexión (nunca escribe sin ella).
            var failure = await Record.ExceptionAsync(() => call.WaitAsync(TimeSpan.FromSeconds(10), Ct));
            Assert.True(failure is null or BackboneException { Code: BackboneErrorCode.NasUnreachable }, failure?.ToString());
        }

        Assert.Equal(BackboneErrorCode.Interrupted, await CodeOfAsync(Preparer.PrepareAsync(Nas2, Ct)));
        Assert.Single(_server.ShareConnector.Connections);
    }

    [Fact]
    public async Task Stopping_with_nothing_pending_does_not_wait_and_closes_the_manager()
    {
        var shutdown = new ShareConnectionShutdown(
            _server.ConnectionTester, Preparer, _server.Connections, _server.Time, _server.LoggerFactory.CreateLogger<ShareConnectionShutdown>());

        await shutdown.StopAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Empty(_server.ShareConnector.ForcedDisconnects);
        Assert.Equal(BackboneErrorCode.Interrupted, await CodeOfAsync(Preparer.PrepareAsync(Nas1, Ct)));
    }
}
