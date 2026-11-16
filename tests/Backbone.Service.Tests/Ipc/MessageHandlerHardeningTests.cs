using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Backbone.Contracts;
using Backbone.Service.Ipc;
using Backbone.Service.Tests.TestSupport;
using Nerdbank.Streams;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;
using StreamJsonRpc.Reflection;

namespace Backbone.Service.Tests.Ipc;

/// <summary>
/// Endurecimiento del marco de mensajes del servidor (revisión de seguridad de HT-03): cuerpo recibido a cero tras
/// deserializar (B-1), límite de peticiones simultáneas por conexión (B-2) y respuestas por encima del límite convertidas en
/// error interno (I-b).
/// </summary>
[Trait("Story", "HT-03")]
public sealed class MessageHandlerHardeningTests
{
    private const string CredentialRequest =
        """{"jsonrpc":"2.0","id":1,"method":"SaveCredentialAsync","params":["\\\\nas\\copias",{"userName":"ana","password":"secreto-123"}]}""";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void The_defaults_bound_identification_time_and_concurrent_requests()
    {
        var options = new IpcOptions();

        Assert.Equal(TimeSpan.FromSeconds(5), options.IdentificationTimeout);
        Assert.Equal(16, options.MaxConcurrentRequestsPerConnection);
    }

    [Fact]
    public async Task The_received_body_is_zeroed_when_deserialization_completes()
    {
        var (serverStream, clientStream) = FullDuplexStream.CreatePair();
        using var formatter = new RecordingFormatter();
        await using var handler = new BoundedHeaderDelimitedMessageHandler(serverStream, formatter, 4096);
        await RawFraming.WriteAsync(clientStream, CredentialRequest, Ct);

        var message = await handler.ReadAsync(Ct);
        var body = Assert.Single(formatter.Bodies);
        Assert.Contains("secreto-123", Encoding.UTF8.GetString(body), StringComparison.Ordinal); // aún se puede deserializar

        ((IJsonRpcMessageBufferManager)handler).DeserializationComplete(message!);

        Assert.All(body, b => Assert.Equal(0, b));
    }

    [Fact]
    public async Task The_message_buffers_are_released_once_before_zeroing_the_body_as_the_base_handler_would()
    {
        // I-3: la implementación de PipeMessageHandler (que esta clase sustituye) llama a DeserializationComplete del
        // mensaje; los de SystemTextJsonFormatter liberan así su JsonDocument. Aquí se hace una sola vez, en cualquier camino.
        var (serverStream, clientStream) = FullDuplexStream.CreatePair();
        using var formatter = new TrackingFormatter();
        var handler = new BoundedHeaderDelimitedMessageHandler(serverStream, formatter, 4096);
        await RawFraming.WriteAsync(clientStream, CredentialRequest, Ct);
        await RawFraming.WriteAsync(clientStream, CredentialRequest.Replace("\"id\":1", "\"id\":2", StringComparison.Ordinal), Ct);
        await RawFraming.WriteAsync(clientStream, CredentialRequest.Replace("\"id\":1", "\"id\":3", StringComparison.Ordinal), Ct);

        var first = await handler.ReadAsync(Ct);
        ((IJsonRpcMessageBufferManager)handler).DeserializationComplete(first!);
        ((IJsonRpcMessageBufferManager)handler).DeserializationComplete(first!); // repetida: no se vuelve a liberar
        Assert.Equal([1], formatter.Messages.Select(m => m.Releases));
        Assert.True(formatter.Messages[0].BodyWasIntactWhenReleased);

        await handler.ReadAsync(Ct); // sin DeserializationComplete: se libera al leer el siguiente
        await handler.ReadAsync(Ct);
        Assert.Equal([1, 1, 0], formatter.Messages.Select(m => m.Releases));

        await handler.DisposeAsync(); // y al desechar
        Assert.Equal([1, 1, 1], formatter.Messages.Select(m => m.Releases));
        Assert.All(formatter.Messages, m => Assert.True(m.BodyWasIntactWhenReleased));
    }

    [Fact]
    public async Task The_previous_body_is_zeroed_before_reading_the_next_message_and_on_dispose()
    {
        var (serverStream, clientStream) = FullDuplexStream.CreatePair();
        using var formatter = new RecordingFormatter();
        var handler = new BoundedHeaderDelimitedMessageHandler(serverStream, formatter, 4096);
        await RawFraming.WriteAsync(clientStream, CredentialRequest, Ct);
        await RawFraming.WriteAsync(clientStream, CredentialRequest.Replace("\"id\":1", "\"id\":2", StringComparison.Ordinal), Ct);

        await handler.ReadAsync(Ct);
        await handler.ReadAsync(Ct);
        Assert.All(formatter.Bodies[0], b => Assert.Equal(0, b));

        await handler.DisposeAsync();
        Assert.All(formatter.Bodies[1], b => Assert.Equal(0, b));
    }

    [Fact]
    public async Task More_concurrent_requests_than_the_limit_fail_the_read_and_responses_free_slots()
    {
        var (serverStream, clientStream) = FullDuplexStream.CreatePair();
        await using var handler = new BoundedHeaderDelimitedMessageHandler(serverStream, new SystemTextJsonFormatter(), 4096, maxConcurrentRequests: 2);
        for (var id = 1; id <= 4; id++)
        {
            await RawFraming.WriteAsync(clientStream, $$"""{"jsonrpc":"2.0","id":{{id}},"method":"GetStatusAsync","params":[]}""", Ct);
        }

        await handler.ReadAsync(Ct);
        await handler.ReadAsync(Ct);
        Assert.Equal(2, handler.InFlightRequests);

        // Una respuesta libera un hueco: la tercera cabe.
        await handler.WriteAsync(new JsonRpcResult { RequestId = new RequestId(1) }, Ct);
        await handler.ReadAsync(Ct);
        Assert.Equal(2, handler.InFlightRequests);

        await Assert.ThrowsAsync<TooManyConcurrentRequestsException>(async () => await handler.ReadAsync(Ct));
    }

    [Fact]
    public async Task Notifications_do_not_count_as_concurrent_requests()
    {
        var (serverStream, clientStream) = FullDuplexStream.CreatePair();
        await using var handler = new BoundedHeaderDelimitedMessageHandler(serverStream, new SystemTextJsonFormatter(), 4096, maxConcurrentRequests: 1);
        for (var i = 0; i < 3; i++)
        {
            await RawFraming.WriteAsync(clientStream, """{"jsonrpc":"2.0","method":"$/cancelRequest","params":{"id":1}}""", Ct);
        }

        for (var i = 0; i < 3; i++)
        {
            Assert.IsType<JsonRpcRequest>(await handler.ReadAsync(Ct), exactMatch: false);
        }

        Assert.Equal(0, handler.InFlightRequests);
    }

    [Fact]
    public async Task A_connection_with_too_many_requests_in_progress_is_closed()
    {
        await using var server = new IpcTestHarness(new IpcOptions { MaxConcurrentRequestsPerConnection = 2 });
        await server.AuthorizeAsync(TestSids.Ana);
        var client = await server.ConnectAuthorizedAsync(TestSids.Identity(TestSids.Ana));
        using var block = new ManualResetEventSlim(false);
        server.Accounts.MembershipBlock = block;
        try
        {
            // Cada llamada queda en curso (autorizándose); la tercera supera el límite.
            var calls = Enumerable.Range(0, 3).Select(_ => client.Proxy.GetServiceInfoAsync(Ct)).ToList();

            await client.WaitClosedAsync();
            Assert.Equal(0, server.Manager.Count);
            Assert.Contains(nameof(CloseReason.TooManyRequests), server.Logs.AllText(), StringComparison.Ordinal);
            foreach (var call in calls)
            {
                await Assert.ThrowsAnyAsync<Exception>(() => call);
            }
        }
        finally
        {
            block.Set();
        }
    }

    [Fact]
    public async Task A_response_over_the_limit_is_replaced_by_an_internal_error_with_the_same_id()
    {
        var (serverStream, clientStream) = FullDuplexStream.CreatePair();
        await using var handler = new BoundedHeaderDelimitedMessageHandler(serverStream, new SystemTextJsonFormatter(), 512);
        await RawFraming.WriteAsync(clientStream, """{"jsonrpc":"2.0","id":7,"method":"GetStatusAsync","params":[]}""", Ct);
        await handler.ReadAsync(Ct);

        await handler.WriteAsync(new JsonRpcResult { RequestId = new RequestId(7), Result = new string('x', 2048) }, Ct);

        var json = await RawFraming.ReadAsync(clientStream, Ct);
        Assert.NotNull(json);
        Assert.True(Encoding.UTF8.GetByteCount(json) <= 512);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(7, root.GetProperty("id").GetInt32());
        Assert.False(root.TryGetProperty("result", out _));
        var error = root.GetProperty("error");
        Assert.Equal(BackboneErrors.ToJsonRpcErrorCode(BackboneErrorCode.Internal), error.GetProperty("code").GetInt32());
        Assert.StartsWith("Internal error. Correlation id: ", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(0, handler.InFlightRequests);
    }

    [Fact]
    public async Task A_notification_over_the_limit_is_dropped()
    {
        var (serverStream, clientStream) = FullDuplexStream.CreatePair();
        await using var handler = new BoundedHeaderDelimitedMessageHandler(serverStream, new SystemTextJsonFormatter(), 512);

        await handler.WriteAsync(new JsonRpcRequest { Method = "RunProgress", ArgumentsList = [new string('x', 2048)] }, Ct);
        await handler.WriteAsync(new JsonRpcRequest { Method = "StatusChanged", ArgumentsList = ["ok"] }, Ct);

        var json = await RawFraming.ReadAsync(clientStream, Ct);
        Assert.Contains("StatusChanged", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_oversized_response_reaches_the_client_as_Internal_and_the_connection_stays_open()
    {
        await using var server = new IpcTestHarness(new IpcOptions { MaxMessageBytes = 400 });
        var sids = Enumerable.Range(0, 10).Select(i => $"S-1-5-21-1111111111-2222222222-3333333333-{2000 + i}").ToArray();
        foreach (var sid in sids)
        {
            server.Accounts.AddUser(sid, $@"PC-SALON\usuario-con-nombre-largo-{sid[^4..]}", administrator: true);
        }

        await server.AuthorizeAsync([TestSids.Ana, .. sids]);
        var client = server.Connect(TestSids.Identity(TestSids.Ana));

        var data = await RpcAssert.FailsWithAsync(BackboneErrorCode.Internal, () => client.Proxy.GetAuthorizedUsersAsync(Ct));

        Assert.StartsWith("Internal error. Correlation id: ", data.Message, StringComparison.Ordinal);
        Assert.Equal(1, server.Manager.Count);
        Assert.Contains("por encima del límite", server.Logs.AllText(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Formateador real que devuelve mensajes que registran cuántas veces se liberan y si el cuerpo seguía intacto al
    /// liberarlos (para comprobar que se ponen a cero).
    /// </summary>
    private sealed class TrackingFormatter : IJsonRpcMessageFormatter, IDisposable
    {
        private readonly SystemTextJsonFormatter _inner = new();

        public List<TrackedRequest> Messages { get; } = [];

        public JsonRpcMessage Deserialize(ReadOnlySequence<byte> contentBuffer)
        {
            Assert.True(MemoryMarshal.TryGetArray(contentBuffer.First, out var segment));
            var parsed = (JsonRpcRequest)_inner.Deserialize(contentBuffer);
            var message = new TrackedRequest(segment.Array!) { RequestId = parsed.RequestId, Method = parsed.Method };
            ((IJsonRpcMessageBufferManager)parsed).DeserializationComplete(parsed);
            Messages.Add(message);
            return message;
        }

        public void Serialize(IBufferWriter<byte> bufferWriter, JsonRpcMessage message) => _inner.Serialize(bufferWriter, message);

        public object GetJsonText(JsonRpcMessage message) => _inner.GetJsonText(message);

        public void Dispose() => _inner.Dispose();
    }

    private sealed class TrackedRequest(byte[] body) : JsonRpcRequest, IJsonRpcMessageBufferManager
    {
        public int Releases { get; private set; }

        public bool BodyWasIntactWhenReleased { get; private set; }

        public void DeserializationComplete(JsonRpcMessage message)
        {
            Assert.Same(this, message);
            Releases++;
            BodyWasIntactWhenReleased = body.Any(b => b != 0);
        }
    }

    private sealed class RecordingFormatter : IJsonRpcMessageFormatter, IDisposable
    {
        private readonly SystemTextJsonFormatter _inner = new();

        public List<byte[]> Bodies { get; } = [];

        public JsonRpcMessage Deserialize(ReadOnlySequence<byte> contentBuffer)
        {
            Assert.True(contentBuffer.IsSingleSegment);
            Assert.True(MemoryMarshal.TryGetArray(contentBuffer.First, out var segment));
            Bodies.Add(segment.Array ?? throw new InvalidOperationException("The body is not backed by an array."));
            return _inner.Deserialize(contentBuffer);
        }

        public void Serialize(IBufferWriter<byte> bufferWriter, JsonRpcMessage message) => _inner.Serialize(bufferWriter, message);

        public object GetJsonText(JsonRpcMessage message) => _inner.GetJsonText(message);

        public void Dispose() => _inner.Dispose();
    }
}
