using System.Buffers;
using System.Text;
using Backbone.Contracts;
using Backbone.Service.Ipc;
using Backbone.Service.Tests.TestSupport;
using Nerdbank.Streams;
using StreamJsonRpc;

namespace Backbone.Service.Tests.Ipc;

/// <summary>
/// §7.1: el servidor corta la conexión si un mensaje supera <see cref="BackboneIpc.MaxMessageBytes"/>, leyendo solo la
/// cabecera (sin reservar memoria para el cuerpo); el marco es compatible con <see cref="HeaderDelimitedMessageHandler"/>.
/// </summary>
[Trait("Story", "HT-03")]
public sealed class MessageSizeLimitTests : IAsyncLifetime
{
    private readonly IpcTestHarness _server = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => new(_server.AuthorizeAsync(TestSids.Ana));

    public ValueTask DisposeAsync() => _server.DisposeAsync();

    [Theory]
    [InlineData("Content-Length: 10\r\n\r\n", 10, 22)]
    [InlineData("content-length:10\r\n\r\n", 10, 21)]
    [InlineData("Content-Length: 5\r\nContent-Type: application/vscode-jsonrpc; charset=utf-8\r\n\r\n", 5, 78)]
    [InlineData("Content-Length: 4194304\r\n\r\n", BackboneIpc.MaxMessageBytes, 27)]
    public void Valid_headers_are_parsed(string header, int expectedLength, int expectedHeaderLength)
    {
        Assert.True(BoundedHeaderDelimitedMessageHandler.TryParseHeaders(Sequence(header + "{}"), BackboneIpc.MaxMessageBytes, out var headerLength, out var length));

        Assert.Equal((expectedHeaderLength, expectedLength), (headerLength, length));
    }

    [Fact]
    public void An_incomplete_header_waits_for_more_data()
    {
        Assert.False(BoundedHeaderDelimitedMessageHandler.TryParseHeaders(Sequence("Content-Length: 10\r\n"), 100, out _, out _));
    }

    [Theory]
    [InlineData("Content-Length: 4194305\r\n\r\n")] // 4 MiB + 1
    [InlineData("Content-Length: 99999999999\r\n\r\n")]
    [InlineData("Content-Length: 0\r\n\r\n")]
    [InlineData("Content-Length: -1\r\n\r\n")]
    [InlineData("Content-Length: 1e3\r\n\r\n")]
    [InlineData("Content-Length: 10\r\nContent-Length: 10\r\n\r\n")]
    [InlineData("Content-Type: application/vscode-jsonrpc\r\n\r\n")]
    [InlineData("Content-Length: 10\r\nX-Other: 1\r\n\r\n")]
    [InlineData("Content-Length: 10\r\nContent-Type: application/vscode-jsonrpc; charset=utf-16\r\n\r\n")]
    [InlineData("Content-Length 10\r\n\r\n")]
    [InlineData("Content-Length: 1é0\r\n\r\n")]
    public void Invalid_or_oversized_headers_are_rejected(string header)
    {
        Assert.Throws<BadRpcHeaderException>(
            () => BoundedHeaderDelimitedMessageHandler.TryParseHeaders(Sequence(header), BackboneIpc.MaxMessageBytes, out _, out _));
    }

    [Fact]
    public void A_header_block_without_terminator_beyond_the_maximum_is_rejected()
    {
        var header = "Content-Length: 10\r\n" + new string('a', BoundedHeaderDelimitedMessageHandler.MaxHeaderBytes);

        Assert.Throws<BadRpcHeaderException>(
            () => BoundedHeaderDelimitedMessageHandler.TryParseHeaders(Sequence(header), BackboneIpc.MaxMessageBytes, out _, out _));
    }

    [Fact]
    public async Task An_oversized_message_closes_the_connection_without_waiting_for_its_body()
    {
        var (serverStream, clientStream) = FullDuplexStream.CreatePair();
        var connection = _server.Manager.Accept(new InMemoryChannel(serverStream, () => TestSids.Identity(TestSids.Ana)));
        Assert.NotNull(connection);

        // Solo la cabecera: si el servidor esperara el cuerpo de 5 MB, la conexión no se cerraría.
        await clientStream.WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: {BackboneIpc.MaxMessageBytes + 1}\r\n\r\n"), Ct);
        await clientStream.FlushAsync(Ct);

        await connection.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.True(await RawFraming.WaitForEndOfStreamAsync(clientStream, TimeSpan.FromSeconds(10), Ct));
        Assert.Equal(0, _server.Manager.Count);
    }

    [Fact]
    public async Task A_message_just_under_the_limit_is_accepted()
    {
        var client = await _server.ConnectAuthorizedAsync(TestSids.Identity(TestSids.Ana));
        // ~3,9 MB de exclusiones: el mensaje cabe en 4 MiB y llega a la validación (que lo rechaza por límites, no por tamaño).
        var job = new JobDefinition { Name = "x", Exclusions = [.. Enumerable.Repeat(new string('e', 15_000), 260)] };

        await RpcAssert.ValidationFailsAsync("Exclusions", ValidationIssueCode.LimitExceeded, () => client.Proxy.SaveJobAsync(job, Ct));

        Assert.Equal(1, _server.Manager.Count);
    }

    [Fact]
    public async Task Responses_are_readable_by_the_standard_HeaderDelimitedMessageHandler()
    {
        // TestRpcClient usa HeaderDelimitedMessageHandler sin cambios, como la app.
        var client = await _server.ConnectAuthorizedAsync(TestSids.Identity(TestSids.Ana));

        var users = await client.Proxy.GetAuthorizedUsersAsync(Ct);

        Assert.Equal(TestSids.Ana, Assert.Single(users).Sid);
    }

    private static ReadOnlySequence<byte> Sequence(string text) => new(Encoding.UTF8.GetBytes(text));
}
