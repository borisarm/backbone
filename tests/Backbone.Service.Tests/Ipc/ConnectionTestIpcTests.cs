using System.Text.Json;
using Backbone.Contracts;
using Backbone.Service.Nas;
using Backbone.Service.Tests.TestSupport;

namespace Backbone.Service.Tests.Ipc;

/// <summary>
/// HU-05 por IPC: <c>TestConnectionAsync</c> responde con <see cref="ConnectionTestResult"/> (un fallo no es una excepción,
/// §7.4), valida la entrada en el servidor, se audita como escritura y nunca devuelve ni registra la contraseña.
/// </summary>
[Trait("Story", "HU-05")]
public sealed class ConnectionTestIpcTests : IAsyncLifetime
{
    private const string Password = "Contraseña-IPC-HU05-90e3";

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
    public async Task A_correct_test_returns_success_with_space_and_dialect_and_no_password()
    {
        var credential = new CredentialInput { UserName = "ana", Password = Password };

        var result = await _ana.Proxy.TestConnectionAsync(@"\\NAS\Backups\pc-salon", credential, Ct);
        var raw = await _ana.Client.Rpc.InvokeWithCancellationAsync<JsonElement>(
            nameof(IBackboneService.TestConnectionAsync), [@"\\nas\backups", credential], Ct);

        Assert.True(result.Success);
        Assert.Equal(@"\\nas\backups", result.ShareRoot);
        Assert.Equal("3.1.1", result.SmbDialect);
        Assert.Equal(_server.ShareConnector.Share.Space.FreeBytes, result.FreeBytes);
        Assert.DoesNotContain(Password, raw.GetRawText(), StringComparison.Ordinal);
        await _server.ConnectionTester.WhenCleanupsCompleteAsync();
        Assert.Empty(_server.ShareConnector.Share.Files);
        SecretSearch.AssertNotInLogs(Password, _server.Logs);
    }

    [Fact]
    public async Task A_failure_is_a_result_with_its_error_code_not_an_exception()
    {
        _server.ShareConnector.ConnectError = new ShareException(BackboneErrorCode.CredentialsRejected, ShareErrors.ErrorLogonFailure);

        var result = await _ana.Proxy.TestConnectionAsync(@"\\nas\backups", new CredentialInput { UserName = "ana", Password = "mala" }, Ct);

        Assert.False(result.Success);
        Assert.Equal(BackboneErrorCode.CredentialsRejected, result.ErrorCode);
    }

    [Fact]
    public async Task Without_credential_nor_a_saved_one_it_is_CredentialRequired()
    {
        var result = await _ana.Proxy.TestConnectionAsync(@"\\nas\backups", null, Ct);

        Assert.Equal(BackboneErrorCode.CredentialRequired, result.ErrorCode);
        Assert.Empty(_server.ShareConnector.Connections);
    }

    [Theory]
    [InlineData(@"nas\backups", ValidationIssueCode.DestinationNotUnc)]
    [InlineData(@"\\127.0.0.1\backups", ValidationIssueCode.DestinationNotAllowed)]
    [InlineData(@"\\?\UNC\nas\backups", ValidationIssueCode.DestinationNotAllowed)]
    public async Task An_invalid_destination_is_rejected_by_the_server(string destination, ValidationIssueCode code)
    {
        await RpcAssert.ValidationFailsAsync(
            string.Empty, code, () => _ana.Proxy.TestConnectionAsync(destination, new CredentialInput { UserName = "ana", Password = "x" }, Ct));
        Assert.Empty(_server.ShareConnector.Connections);
    }

    [Fact]
    public async Task A_password_over_the_limit_is_rejected_without_echoing_it()
    {
        var password = new string('x', ContractLimits.MaxPasswordLength + 1);

        var data = await RpcAssert.FailsWithAsync(
            BackboneErrorCode.ValidationFailed,
            () => _ana.Proxy.TestConnectionAsync(@"\\nas\backups", new CredentialInput { UserName = "ana", Password = password }, Ct));

        var issue = Assert.Single(data.Validation!.Errors);
        Assert.Equal(("Password", ValidationIssueCode.LimitExceeded, (string?)null), (issue.Field, issue.Code, issue.Value));
        Assert.Empty(_server.ShareConnector.Connections);
    }

    [Fact]
    public async Task The_call_is_audited_as_a_write_without_arguments()
    {
        await _ana.Proxy.TestConnectionAsync(@"\\nas\backups", new CredentialInput { UserName = "ana", Password = Password }, Ct);

        Assert.Contains(_server.Logs.Entries, e => e.Message.Contains(nameof(IBackboneService.TestConnectionAsync), StringComparison.Ordinal)
                                                   && e.Message.Contains(TestSids.Ana, StringComparison.Ordinal));
        SecretSearch.AssertNotInLogs(Password, _server.Logs);
    }
}
