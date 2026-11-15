using Nerdbank.Streams;
using StreamJsonRpc;

namespace Backbone.Contracts.Tests;

/// <summary>
/// HT-02 / RNF-08: entradas IPC malformadas y eventos. Un mensaje inválido produce un error JSON-RPC controlado y la
/// conexión sigue siendo utilizable; todos los eventos del contrato llegan al cliente con sus DTO intactos.
/// </summary>
[Trait("Story", "HT-02")]
public sealed class RpcMalformedInputTests : IAsyncDisposable
{
    private readonly FakeBackboneService _service = new();
    private readonly JsonRpc _server;
    private readonly JsonRpc _client;
    private readonly IBackboneService _proxy;

    public RpcMalformedInputTests()
    {
        var (serverStream, clientStream) = FullDuplexStream.CreatePair();

        _server = new JsonRpc(new HeaderDelimitedMessageHandler(serverStream, new SystemTextJsonFormatter()));
        _server.AddLocalRpcTarget<IBackboneService>(_service, null);
        _server.StartListening();

        _client = new JsonRpc(new HeaderDelimitedMessageHandler(clientStream, new SystemTextJsonFormatter()));
        _proxy = _client.Attach<IBackboneService>();
        _client.StartListening();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        _server.Dispose();
        await _client.Completion.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        await _server.Completion.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    private async Task AssertConnectionStillWorksAsync()
    {
        var info = await _proxy.GetServiceInfoAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ContractVersion.Value, info.ContractVersion);
    }

    [Fact]
    public async Task An_argument_of_the_wrong_type_is_rejected_and_the_connection_survives()
    {
        await Assert.ThrowsAnyAsync<RemoteRpcException>(
            () => _client.InvokeWithCancellationAsync<JobDefinition>(
                nameof(IBackboneService.SaveJobAsync), ["esto no es un trabajo"], TestContext.Current.CancellationToken));

        await AssertConnectionStillWorksAsync();
    }

    [Fact]
    public async Task A_malformed_guid_is_rejected_and_the_connection_survives()
    {
        await Assert.ThrowsAnyAsync<RemoteRpcException>(
            () => _client.InvokeWithCancellationAsync(
                nameof(IBackboneService.RunNowAsync), ["no-es-un-guid"], TestContext.Current.CancellationToken));

        await AssertConnectionStillWorksAsync();
    }

    [Fact]
    public async Task A_wrong_number_of_arguments_is_rejected_and_the_connection_survives()
    {
        await Assert.ThrowsAnyAsync<RemoteRpcException>(
            () => _client.InvokeWithCancellationAsync(
                nameof(IBackboneService.StartCheckAsync), [Guid.NewGuid(), true, "sobra", 1], TestContext.Current.CancellationToken));

        await AssertConnectionStillWorksAsync();
    }

    [Fact]
    public async Task An_unknown_method_is_reported_as_not_found()
    {
        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(
            () => _client.InvokeWithCancellationAsync("MetodoQueNoExisteAsync", [], TestContext.Current.CancellationToken));

        await AssertConnectionStillWorksAsync();
    }

    [Fact]
    public async Task Event_handlers_and_dispose_are_not_callable_remotely_as_contract_methods()
    {
        // Solo los métodos de la interfaz (los registrados con AddLocalRpcTarget<IBackboneService>) son invocables.
        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(
            () => _client.InvokeWithCancellationAsync(nameof(FakeBackboneService.RaiseOthers), [], TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(
            () => _client.InvokeWithCancellationAsync("ToString", [], TestContext.Current.CancellationToken));

        await AssertConnectionStillWorksAsync();
    }

    [Fact]
    public async Task All_the_events_of_the_contract_reach_the_client_with_their_DTO_intact()
    {
        var status = new TaskCompletionSource<StatusChangedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<RunCompletedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var restore = new TaskCompletionSource<RestoreProgressEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var conflict = new TaskCompletionSource<RestoreConflictEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        _proxy.StatusChanged += (_, e) => status.TrySetResult(e);
        _proxy.RunCompleted += (_, e) => completed.TrySetResult(e);
        _proxy.RestoreProgress += (_, e) => restore.TrySetResult(e);
        _proxy.RestoreConflict += (_, e) => conflict.TrySetResult(e);
        await AssertConnectionStillWorksAsync(); // la suscripción está activa

        var sentStatus = (StatusChangedEvent)ContractTypes.CreateSample(typeof(StatusChangedEvent));
        var sentCompleted = (RunCompletedEvent)ContractTypes.CreateSample(typeof(RunCompletedEvent));
        var sentRestore = (RestoreProgressEvent)ContractTypes.CreateSample(typeof(RestoreProgressEvent));
        var sentConflict = (RestoreConflictEvent)ContractTypes.CreateSample(typeof(RestoreConflictEvent));
        _service.RaiseStatusChanged(sentStatus);
        _service.RaiseRunCompleted(sentCompleted);
        _service.RaiseRestoreProgress(sentRestore);
        _service.RaiseRestoreConflict(sentConflict);

        var timeout = TimeSpan.FromSeconds(10);
        var ct = TestContext.Current.CancellationToken;
        Assert.Null(ContractTypes.FindDifference(sentStatus, await status.Task.WaitAsync(timeout, ct), nameof(StatusChangedEvent)));
        Assert.Null(ContractTypes.FindDifference(sentCompleted, await completed.Task.WaitAsync(timeout, ct), nameof(RunCompletedEvent)));
        Assert.Null(ContractTypes.FindDifference(sentRestore, await restore.Task.WaitAsync(timeout, ct), nameof(RestoreProgressEvent)));
        Assert.Null(ContractTypes.FindDifference(sentConflict, await conflict.Task.WaitAsync(timeout, ct), nameof(RestoreConflictEvent)));
    }

    [Fact]
    public async Task A_cancelled_call_does_not_break_the_connection()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _proxy.GetStatusAsync(cts.Token));

        await AssertConnectionStillWorksAsync();
    }
}
