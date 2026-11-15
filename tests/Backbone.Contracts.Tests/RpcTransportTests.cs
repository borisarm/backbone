using Nerdbank.Streams;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;

namespace Backbone.Contracts.Tests;

/// <summary>
/// El contrato funciona con StreamJsonRpc tal como lo usarán servicio y app (§7.1, ADR-03): proxy generado a partir de
/// la interfaz, <c>HeaderDelimitedMessageHandler</c> + <c>SystemTextJsonFormatter</c>, eventos y errores con
/// <see cref="BackboneErrorCode"/> y <see cref="BackboneErrorData"/> (§7.4). Usa un flujo en memoria, sin pipes reales
/// (la prueba con pipe es de HT-03).
/// </summary>
[Trait("Story", "HT-02")]
public sealed class RpcTransportTests : IAsyncDisposable
{
    private readonly FakeBackboneService _service = new();
    private readonly JsonRpc _server;
    private readonly BackboneClientRpc _client;
    private readonly IBackboneService _proxy;

    public RpcTransportTests()
    {
        var (serverStream, clientStream) = FullDuplexStream.CreatePair();

        _server = new JsonRpc(new HeaderDelimitedMessageHandler(serverStream, new SystemTextJsonFormatter()));
        _server.AddLocalRpcTarget<IBackboneService>(_service, null);
        _server.StartListening();

        _client = new BackboneClientRpc(new HeaderDelimitedMessageHandler(clientStream, new SystemTextJsonFormatter()));
        _proxy = _client.Attach<IBackboneService>();
        _client.StartListening();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        _server.Dispose();
        // Al desechar, Completion puede terminar con la excepción de desconexión; no interesa aquí.
        await _client.Completion.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        await _server.Completion.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    [Fact]
    public async Task A_call_returns_the_DTO_intact()
    {
        var info = await _proxy.GetServiceInfoAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ContractVersion.Value, info.ContractVersion);
        Assert.Null(ContractTypes.FindDifference(FakeBackboneService.Info, info, nameof(ServiceInfo)));
    }

    [Fact]
    public async Task A_DTO_argument_reaches_the_service_intact()
    {
        var job = (JobDefinition)ContractTypes.CreateSample(typeof(JobDefinition));

        var saved = await _proxy.SaveJobAsync(job, TestContext.Current.CancellationToken);

        Assert.Null(ContractTypes.FindDifference(job, _service.LastSavedJob, "servidor"));
        Assert.Null(ContractTypes.FindDifference(job, saved, "respuesta"));
    }

    [Fact]
    public async Task A_business_error_arrives_with_its_code_and_data()
    {
        var ex = await Assert.ThrowsAsync<RemoteInvocationException>(
            () => _proxy.StartPruneAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));

        Assert.True(BackboneErrors.TryGetErrorCode(ex.ErrorCode, out var code));
        Assert.Equal(BackboneErrorCode.NotSupported, code);
        var data = Assert.IsType<BackboneErrorData>(ex.DeserializedErrorData);
        Assert.Equal(BackboneErrorCode.NotSupported, data.Code);
    }

    [Fact]
    public async Task A_non_elevated_caller_gets_NotAuthorized()
    {
        var ex = await Assert.ThrowsAsync<RemoteInvocationException>(
            () => _proxy.AddAuthorizedUserAsync(@"PC-SALON\luis", TestContext.Current.CancellationToken));

        Assert.Equal((int)BackboneErrorCode.NotAuthorized, ex.ErrorCode);
        Assert.Equal(BackboneErrorCode.NotAuthorized, Assert.IsType<BackboneErrorData>(ex.DeserializedErrorData).Code);
    }

    [Fact]
    public async Task A_validation_error_carries_the_validation_result()
    {
        var ex = await Assert.ThrowsAsync<RemoteInvocationException>(
            () => _proxy.SaveJobAsync(new JobDefinition(), TestContext.Current.CancellationToken));

        Assert.Equal((int)BackboneErrorCode.ValidationFailed, ex.ErrorCode);
        var data = Assert.IsType<BackboneErrorData>(ex.DeserializedErrorData);
        var issue = Assert.Single(data.Validation!.Errors);
        Assert.Equal(("Name", ValidationIssueCode.NameRequired), (issue.Field, issue.Code));
    }

    [Fact]
    public async Task Server_events_reach_the_client()
    {
        var received = new TaskCompletionSource<RunProgressEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        _proxy.RunProgress += (_, e) => received.TrySetResult(e);
        var sent = (RunProgressEvent)ContractTypes.CreateSample(typeof(RunProgressEvent));

        // Asegura que la suscripción está activa del lado del cliente antes de emitir.
        await _proxy.GetStatusAsync(TestContext.Current.CancellationToken);
        _service.RaiseRunProgress(sent);

        var e = await received.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Null(ContractTypes.FindDifference(sent, e, nameof(RunProgressEvent)));
    }

    /// <summary>Cliente que deserializa <c>error.data</c> como <see cref="BackboneErrorData"/> para los códigos de Backbone (§7.4).</summary>
    private sealed class BackboneClientRpc(IJsonRpcMessageHandler handler) : JsonRpc(handler)
    {
        protected override Type? GetErrorDetailsDataType(JsonRpcError error) =>
            error.Error is { } detail && BackboneErrors.TryGetErrorCode((int)detail.Code, out _)
                ? typeof(BackboneErrorData)
                : base.GetErrorDetailsDataType(error);
    }
}
