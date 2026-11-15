using System.Buffers;
using System.Text;
using Backbone.App.Ipc;
using Backbone.Contracts;
using Nerdbank.Streams;
using StreamJsonRpc;

namespace Backbone.App.Tests.Ipc;

/// <summary>§7.1: el cliente corta la conexión si un mensaje declara más de <see cref="BackboneIpc.MaxMessageBytes"/>, sin leerlo.</summary>
[Trait("Story", "HT-03")]
public sealed class SizeBoundedMessageHandlerTests
{
    private static ReadOnlySequence<byte> Bytes(string text) => new(Encoding.ASCII.GetBytes(text));

    [Fact]
    public void A_header_within_the_limit_is_parsed()
    {
        var ok = SizeBoundedMessageHandler.TryParseHeaders(Bytes("Content-Length: 42\r\n\r\n{}"), 100, out var headerLength, out var contentLength);

        Assert.True(ok);
        Assert.Equal(42, contentLength);
        Assert.Equal("Content-Length: 42\r\n\r\n".Length, headerLength);
    }

    [Fact]
    public void An_incomplete_header_asks_for_more_data() =>
        Assert.False(SizeBoundedMessageHandler.TryParseHeaders(Bytes("Content-Length: 4"), 100, out _, out _));

    [Theory]
    [InlineData("Content-Length: 101\r\n\r\n")]
    [InlineData("Content-Length: 4194305\r\n\r\n")]
    [InlineData("Content-Length: 99999999999\r\n\r\n")]
    [InlineData("Content-Length: 0\r\n\r\n")]
    [InlineData("Content-Length: -1\r\n\r\n")]
    [InlineData("Content-Length: 1\r\nContent-Length: 2\r\n\r\n")]
    [InlineData("X-Other: 1\r\nContent-Length: 2\r\n\r\n")]
    [InlineData("Content-Type: text/plain; charset=latin1\r\nContent-Length: 2\r\n\r\n")]
    [InlineData("\r\n\r\n")]
    public void An_invalid_or_oversized_header_is_rejected(string header) =>
        Assert.Throws<BadRpcHeaderException>(() => SizeBoundedMessageHandler.TryParseHeaders(Bytes(header), 100, out _, out _));

    [Fact]
    public void A_header_that_never_ends_is_rejected() =>
        Assert.Throws<BadRpcHeaderException>(() =>
            SizeBoundedMessageHandler.TryParseHeaders(Bytes("Content-Length: " + new string('1', 2000)), 100, out _, out _));

    [Fact]
    public async Task It_interoperates_with_the_standard_header_delimited_handler()
    {
        var (clientStream, serverStream) = FullDuplexStream.CreatePair();
        var client = new JsonRpc(new SizeBoundedMessageHandler(clientStream, new SystemTextJsonFormatter(), BackboneIpc.MaxMessageBytes));
        var server = new JsonRpc(new HeaderDelimitedMessageHandler(serverStream, new SystemTextJsonFormatter()));
        server.AddLocalRpcMethod("Echo", (string text) => text);
        server.StartListening();
        client.StartListening();

        var echoed = await client.InvokeAsync<string>("Echo", ["hola"]);

        Assert.Equal("hola", echoed);
        client.Dispose();
        server.Dispose();
    }

    [Fact]
    public async Task A_peer_that_declares_an_oversized_message_closes_the_connection()
    {
        var (clientStream, serverStream) = FullDuplexStream.CreatePair();
        var client = new JsonRpc(new SizeBoundedMessageHandler(clientStream, new SystemTextJsonFormatter(), 1024));
        client.StartListening();

        var declared = Encoding.ASCII.GetBytes("Content-Length: 2000\r\n\r\n");
        await serverStream.WriteAsync(declared, TestContext.Current.CancellationToken);
        await serverStream.FlushAsync(TestContext.Current.CancellationToken);

        var finished = await Task.WhenAny(client.Completion, Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Same(client.Completion, finished);
    }

    [Fact]
    public async Task A_message_larger_than_the_limit_is_not_sent()
    {
        var (clientStream, _) = FullDuplexStream.CreatePair();
        var client = new JsonRpc(new SizeBoundedMessageHandler(clientStream, new SystemTextJsonFormatter(), 1024));
        client.StartListening();

        await Assert.ThrowsAnyAsync<Exception>(() => client.InvokeAsync("Echo", [new string('a', 5000)]));
    }

    [Fact]
    public async Task The_serialized_body_with_a_password_is_zeroed_in_memory_after_sending()
    {
        const string password = "CENTINELA-contraseña-9f3a";
        var (clientStream, serverStream) = FullDuplexStream.CreatePair();
        var handler = new SizeBoundedMessageHandler(clientStream, new SystemTextJsonFormatter(), BackboneIpc.MaxMessageBytes);
        var client = new JsonRpc(handler);
        client.StartListening();
        var server = new JsonRpc(new HeaderDelimitedMessageHandler(serverStream, new SystemTextJsonFormatter()));
        server.AddLocalRpcMethod("Save", (string secret) => secret.Length);
        server.StartListening();

        Assert.Equal(password.Length, await client.InvokeAsync<int>("Save", [password]));

        // ArrayBufferWriter<byte>.Clear() pone a cero lo escrito: el búfer interno ya no contiene la contraseña.
        var body = (ArrayBufferWriter<byte>)typeof(SizeBoundedMessageHandler)
            .GetField("_body", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(handler)!;
        var buffer = (byte[])typeof(ArrayBufferWriter<byte>)
            .GetField("_buffer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(body)!;
        Assert.NotEmpty(buffer);
        Assert.Equal(0, body.WrittenCount);
        Assert.All(buffer, b => Assert.Equal(0, b));
        client.Dispose();
        server.Dispose();
    }
}
