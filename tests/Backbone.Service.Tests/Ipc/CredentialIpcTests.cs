using System.Text.Json;
using Backbone.Contracts;
using Backbone.Service.Tests.TestSupport;

namespace Backbone.Service.Tests.Ipc;

/// <summary>
/// HU-04 por IPC: <c>SaveCredentialAsync</c>, <c>GetCredentialInfoAsync</c> (usuario y fecha, nunca la contraseña) y
/// <c>DeleteCredentialAsync</c>, con la validación del recurso (§7.5, <see cref="UncPaths"/>) y de la credencial en el
/// servidor.
/// </summary>
[Trait("Story", "HU-04")]
public sealed class CredentialIpcTests : IAsyncLifetime
{
    private const string Password = "Contraseña-IPC-55d0";

    private readonly IpcTestHarness _server = new();
    private TestConnection _ana = null!; // Asignado en InitializeAsync.

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _server.AuthorizeAsync(TestSids.Ana);
        _ana = await _server.ConnectAuthorizedAsync(TestSids.Identity(TestSids.Ana));
    }

    public ValueTask DisposeAsync() => _server.DisposeAsync();

    [Fact]
    public async Task Saving_for_a_share_creates_its_file_in_credentials()
    {
        await _ana.Proxy.SaveCredentialAsync(@"\\nas\Backups", new CredentialInput { UserName = "ana", Password = Password }, Ct);

        var file = Assert.Single(Directory.GetFiles(Path.Combine(_server.DataRoot, "credentials")));
        Assert.Equal(_server.Credentials.PathFor(@"\\nas\backups"), file);
    }

    [Fact]
    public async Task GetCredentialInfo_returns_the_user_and_the_update_date_and_never_the_password()
    {
        await _ana.Proxy.SaveCredentialAsync(@"\\nas\Backups", new CredentialInput { UserName = "ana", Password = Password }, Ct);

        var info = await _ana.Proxy.GetCredentialInfoAsync(@"\\NAS\backups", Ct);
        var raw = await _ana.Client.Rpc.InvokeWithCancellationAsync<JsonElement>(nameof(Contracts.IBackboneService.GetCredentialInfoAsync), [@"\\nas\backups"], Ct);

        Assert.Equal(new CredentialInfo { ShareRoot = @"\\nas\backups", UserName = "ana", UpdatedUtc = _server.Time.GetUtcNow() }, info);
        Assert.Equal(["ShareRoot", "UserName", "UpdatedUtc"], raw.EnumerateObject().Select(p => p.Name));
        Assert.DoesNotContain(Password, raw.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_destination_with_a_path_uses_the_credential_of_its_share()
    {
        await _ana.Proxy.SaveCredentialAsync(@"\\nas\backups\pc-salon", new CredentialInput { UserName = "ana", Password = Password }, Ct);

        Assert.Equal("ana", (await _ana.Proxy.GetCredentialInfoAsync(@"\\nas\backups", Ct))!.UserName);
    }

    [Fact]
    public async Task Without_a_saved_credential_GetCredentialInfo_returns_null()
    {
        Assert.Null(await _ana.Proxy.GetCredentialInfoAsync(@"\\nas\backups", Ct));
    }

    [Fact]
    public async Task Delete_removes_the_credential_and_deleting_a_missing_one_is_not_an_error()
    {
        await _ana.Proxy.SaveCredentialAsync(@"\\nas\backups", new CredentialInput { UserName = "ana", Password = Password }, Ct);

        await _ana.Proxy.DeleteCredentialAsync(@"\\NAS\Backups", Ct);
        await _ana.Proxy.DeleteCredentialAsync(@"\\nas\backups", Ct);

        Assert.Null(await _ana.Proxy.GetCredentialInfoAsync(@"\\nas\backups", Ct));
        Assert.Empty(Directory.GetFiles(Path.Combine(_server.DataRoot, "credentials")));
    }

    [Theory]
    [InlineData(@"nas\backups", ValidationIssueCode.DestinationNotUnc)]
    [InlineData(@"\\nas", ValidationIssueCode.DestinationNotUnc)]
    [InlineData(@"\\localhost\backups", ValidationIssueCode.DestinationNotAllowed)]
    [InlineData(@"\\127.0.0.1\backups", ValidationIssueCode.DestinationNotAllowed)]
    [InlineData(@"\\nas\C$", ValidationIssueCode.DestinationNotAllowed)]
    [InlineData(@"\\?\UNC\nas\backups", ValidationIssueCode.DestinationNotAllowed)]
    [InlineData("", ValidationIssueCode.DestinationRequired)]
    public async Task An_invalid_share_is_rejected_by_every_credential_method(string share, ValidationIssueCode code)
    {
        var credential = new CredentialInput { UserName = "ana", Password = Password };

        await RpcAssert.ValidationFailsAsync(string.Empty, code, () => _ana.Proxy.SaveCredentialAsync(share, credential, Ct));
        await RpcAssert.ValidationFailsAsync(string.Empty, code, () => _ana.Proxy.GetCredentialInfoAsync(share, Ct));
        await RpcAssert.ValidationFailsAsync(string.Empty, code, () => _ana.Proxy.DeleteCredentialAsync(share, Ct));
        Assert.Empty(Directory.GetFiles(Path.Combine(_server.DataRoot, "credentials")));
    }

    [Fact]
    public async Task The_own_machine_name_is_rejected_as_a_share()
    {
        await RpcAssert.ValidationFailsAsync(
            string.Empty, ValidationIssueCode.DestinationNotAllowed,
            () => _ana.Proxy.SaveCredentialAsync($@"\\{Environment.MachineName}\backups", new CredentialInput { UserName = "ana", Password = Password }, Ct));
    }

    [Theory]
    [InlineData("", "Password-1", "UserName")]
    [InlineData("   ", "Password-1", "UserName")]
    [InlineData("an\na", "Password-1", "UserName")]
    [InlineData("ana", "con\0nul", "Password")]
    public async Task A_credential_that_cannot_be_stored_intact_is_rejected_without_echoing_it(string userName, string password, string field)
    {
        var data = await RpcAssert.FailsWithAsync(
            BackboneErrorCode.ValidationFailed,
            () => _ana.Proxy.SaveCredentialAsync(@"\\nas\backups", new CredentialInput { UserName = userName, Password = password }, Ct));

        var issue = Assert.Single(data.Validation!.Errors);
        Assert.Equal((field, ValidationIssueCode.ValueOutOfRange, (string?)null), (issue.Field, issue.Code, issue.Value));
        Assert.DoesNotContain(password, JsonSerializer.Serialize(data), StringComparison.Ordinal);
    }

    /// <summary>
    /// Una suplente suelta no llega nunca intacta por el cable: el <c>SystemTextJsonFormatter</c> del cliente la escribe como
    /// U+FFFD y el del servidor no deserializa un escape <c>\udc00</c> suelto. Por eso la validación del servicio se
    /// comprueba directamente (defensa en profundidad) y, por el cable, que la petición falla sin guardar nada.
    /// </summary>
    [Theory]
    [InlineData("Password")]
    [InlineData("UserName")]
    public async Task A_credential_with_a_lone_surrogate_is_rejected_by_the_service(string field)
    {
        var lone = "suelta" + (char)0xDC00;
        var credential = field == "Password"
            ? new CredentialInput { UserName = "ana", Password = lone }
            : new CredentialInput { UserName = lone, Password = Password };

        var ex = await Assert.ThrowsAsync<Service.Ipc.BackboneException>(
            () => _server.Operations.SaveCredentialAsync(@"\\nas\backups", credential, Ct));

        Assert.Equal(BackboneErrorCode.ValidationFailed, ex.Code);
        var issue = Assert.Single(ex.Validation!.Errors);
        Assert.Equal((field, ValidationIssueCode.ValueOutOfRange, (string?)null), (issue.Field, issue.Code, issue.Value));
        Assert.Empty(Directory.GetFiles(Path.Combine(_server.DataRoot, "credentials")));
    }

    [Fact]
    public async Task An_escaped_lone_surrogate_on_the_wire_fails_without_storing_anything()
    {
        var (serverStream, clientStream) = Nerdbank.Streams.FullDuplexStream.CreatePair();
        _server.Manager.Accept(new InMemoryChannel(serverStream, () => TestSids.Identity(TestSids.Ana)));

        await RawFraming.WriteAsync(
            clientStream,
            """{"jsonrpc":"2.0","id":1,"method":"SaveCredentialAsync","params":["\\\\nas\\backups",{"UserName":"ana","Password":"suelta\udc00"}]}""",
            Ct);
        var json = await RawFraming.ReadAsync(clientStream, Ct);

        if (json is not null)
        {
            using var document = JsonDocument.Parse(json);
            Assert.Equal(1, document.RootElement.GetProperty("id").GetInt32());
            Assert.True(document.RootElement.TryGetProperty("error", out _));
            Assert.DoesNotContain("suelta", json, StringComparison.Ordinal);
        }

        Assert.Empty(Directory.GetFiles(Path.Combine(_server.DataRoot, "credentials")));
        await clientStream.DisposeAsync();
    }

    [Fact]
    public async Task A_rejected_credential_file_fails_with_Internal_without_reading_or_revealing_it()
    {
        var path = _server.Credentials.PathFor(@"\\nas\backups");
        var other = Path.Combine(_server.DataRoot, "ajeno.bin");
        await File.WriteAllTextAsync(other, "contenido ajeno", Ct);
        Persistence.TrustedFileTests.RunCmd("mklink", "/H", path, other);

        var data = await RpcAssert.FailsWithAsync(BackboneErrorCode.Internal, () => _ana.Proxy.GetCredentialInfoAsync(@"\\nas\backups", Ct));

        Assert.StartsWith("Internal error. Correlation id: ", data.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("credentials", data.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Saving_and_deleting_are_audited_without_arguments_and_reading_is_not()
    {
        await _ana.Proxy.SaveCredentialAsync(@"\\nas\backups", new CredentialInput { UserName = "usuario-del-nas", Password = Password }, Ct);
        await _ana.Proxy.GetCredentialInfoAsync(@"\\nas\backups", Ct);
        await _ana.Proxy.DeleteCredentialAsync(@"\\nas\backups", Ct);

        var audit = _server.Logs.Entries.Where(e => e.Message.StartsWith("Auditoría IPC", StringComparison.Ordinal)).ToList();
        Assert.Contains(audit, e => e.Message.Contains(nameof(Contracts.IBackboneService.SaveCredentialAsync), StringComparison.Ordinal));
        Assert.Contains(audit, e => e.Message.Contains(nameof(Contracts.IBackboneService.DeleteCredentialAsync), StringComparison.Ordinal));
        Assert.DoesNotContain(audit, e => e.Message.Contains(nameof(Contracts.IBackboneService.GetCredentialInfoAsync), StringComparison.Ordinal));
        var all = _server.Logs.AllText();
        Assert.DoesNotContain("usuario-del-nas", all, StringComparison.Ordinal);
        Assert.DoesNotContain(@"\\nas\backups", all, StringComparison.Ordinal);
        SecretSearch.AssertNotInLogs(Password, _server.Logs);
    }
}
