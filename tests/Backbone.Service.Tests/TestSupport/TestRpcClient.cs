using Backbone.Contracts;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;

namespace Backbone.Service.Tests.TestSupport;

/// <summary>
/// Cliente de prueba tal como lo hará la app (§7.4): <see cref="HeaderDelimitedMessageHandler"/> estándar,
/// <see cref="SystemTextJsonFormatter"/> sin opciones y <c>error.data</c> deserializado como <see cref="BackboneErrorData"/>.
/// </summary>
internal sealed class TestRpcClient : IAsyncDisposable
{
    public TestRpcClient(Stream stream)
    {
        Stream = stream;
        Rpc = new ClientJsonRpc(new HeaderDelimitedMessageHandler(stream, new SystemTextJsonFormatter()));
        Proxy = Rpc.Attach<IBackboneService>();
        Rpc.StartListening();
    }

    public Stream Stream { get; }

    public JsonRpc Rpc { get; }

    public IBackboneService Proxy { get; }

    public async ValueTask DisposeAsync()
    {
        Rpc.Dispose();
        await Rpc.Completion.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        await Stream.DisposeAsync();
    }

    private sealed class ClientJsonRpc(IJsonRpcMessageHandler handler) : JsonRpc(handler)
    {
        protected override Type? GetErrorDetailsDataType(JsonRpcError error) =>
            error.Error is { } detail && BackboneErrors.TryGetErrorCode((int)detail.Code, out _)
                ? typeof(BackboneErrorData)
                : base.GetErrorDetailsDataType(error);
    }
}

internal static class RpcAssert
{
    /// <summary>Comprueba que la llamada falla con el código de negocio indicado y devuelve sus datos.</summary>
    public static async Task<BackboneErrorData> FailsWithAsync(BackboneErrorCode expected, Func<Task> call)
    {
        var ex = await Assert.ThrowsAsync<RemoteInvocationException>(call);
        Assert.Equal((int)expected, ex.ErrorCode);
        var data = Assert.IsType<BackboneErrorData>(ex.DeserializedErrorData);
        Assert.Equal(expected, data.Code);
        return data;
    }

    /// <summary>Comprueba un <c>ValidationFailed</c> con un único error en el campo y con el código indicados.</summary>
    public static async Task ValidationFailsAsync(string field, ValidationIssueCode code, Func<Task> call)
    {
        var data = await FailsWithAsync(BackboneErrorCode.ValidationFailed, call);
        Assert.NotNull(data.Validation);
        Assert.Contains(data.Validation.Errors, e => e.Field == field && e.Code == code);
    }
}
