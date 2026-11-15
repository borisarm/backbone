using System.Security.Principal;
using Backbone.Contracts;
using Backbone.Service.Tests.TestSupport;

namespace Backbone.Service.Tests.Ipc;

/// <summary>
/// HU-31 (parte de servidor) / §7.3: consulta, alta y baja de usuarios autorizados por IPC.
/// </summary>
[Trait("Story", "HU-31")]
[Trait("Story", "HT-03")]
public sealed class AuthorizedUsersTests : IAsyncLifetime
{
    private readonly IpcTestHarness _server = new();
    private TestConnection _elevated = null!; // Asignado en InitializeAsync.

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _server.Accounts
            .AddUser(TestSids.EntraUser, @"AzureAD\carmen", administrator: true)
            .AddGroup(TestSids.Users, @"BUILTIN\Usuarios")
            .AddGroup(TestSids.AuthenticatedUsers, @"NT AUTHORITY\Usuarios autentificados");
        await _server.AuthorizeAsync(TestSids.Ana);
        _elevated = await _server.ConnectAuthorizedAsync(TestSids.Identity(TestSids.Luis, elevated: true));
    }

    public ValueTask DisposeAsync() => _server.DisposeAsync();

    [Fact]
    public async Task Any_authorized_user_can_list_the_users_with_their_resolved_names()
    {
        var ana = await _server.ConnectAuthorizedAsync(TestSids.Identity(TestSids.Ana));

        var users = await ana.Proxy.GetAuthorizedUsersAsync(Ct);

        var user = Assert.Single(users);
        Assert.Equal((TestSids.Ana, @"PC-SALON\ana"), (user.Sid, user.AccountName));
    }

    [Fact]
    public async Task An_unresolvable_SID_is_listed_with_a_null_name()
    {
        await _server.AuthorizeAsync("S-1-5-21-9-9-9-1500");

        var users = await _elevated.Proxy.GetAuthorizedUsersAsync(Ct);

        Assert.Null(Assert.Single(users, u => u.Sid == "S-1-5-21-9-9-9-1500").AccountName);
    }

    [Theory]
    [InlineData(@"PC-SALON\luis")]
    [InlineData(@"pc-salon\LUIS")]
    [InlineData(TestSids.Luis)]
    [InlineData("  " + TestSids.Luis + "  ")]
    public async Task An_elevated_client_can_add_an_administrator_by_qualified_name_or_SID(string account)
    {
        var added = await _elevated.Proxy.AddAuthorizedUserAsync(account, Ct);

        Assert.Equal((TestSids.Luis, @"PC-SALON\luis"), (added.Sid, added.AccountName));
        Assert.Contains(TestSids.Luis, _server.Settings.Sids);
        Assert.Contains(TestSids.Luis, await File.ReadAllTextAsync(_server.Settings.SettingsPath, Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Microsoft_Entra_ID_administrator_can_be_added()
    {
        var added = await _elevated.Proxy.AddAuthorizedUserAsync(TestSids.EntraUser, Ct);

        Assert.Equal(TestSids.EntraUser, added.Sid);
    }

    [Fact]
    public async Task Adding_a_user_that_is_already_there_is_not_an_error()
    {
        await _elevated.Proxy.AddAuthorizedUserAsync(TestSids.Ana, Ct);

        Assert.Single(_server.Settings.Sids, TestSids.Ana);
    }

    [Theory]
    [InlineData("luis", ValidationIssueCode.AccountNotUser)]                     // sin cualificar
    [InlineData("luis@contoso.com", ValidationIssueCode.AccountNotUser)]         // UPN
    [InlineData(@"\luis", ValidationIssueCode.AccountNotUser)]
    [InlineData(@"PC-SALON\", ValidationIssueCode.AccountNotUser)]
    [InlineData(@"A\B\C", ValidationIssueCode.AccountNotUser)]
    [InlineData("", ValidationIssueCode.AccountNotUser)]
    [InlineData(@"BUILTIN\Usuarios", ValidationIssueCode.AccountNotUser)]       // grupo
    [InlineData(TestSids.Users, ValidationIssueCode.AccountNotUser)]
    [InlineData(TestSids.AuthenticatedUsers, ValidationIssueCode.AccountNotUser)]
    [InlineData(TestSids.Everyone, ValidationIssueCode.AccountNotUser)]
    [InlineData(@"PC-SALON\nadie", ValidationIssueCode.AccountNotFound)]
    [InlineData("S-1-5-21-9-9-9-1500", ValidationIssueCode.AccountNotFound)]
    [InlineData("S-no-es-un-sid", ValidationIssueCode.AccountNotFound)]
    [InlineData(@"PC-SALON\eva", ValidationIssueCode.AccountNotAdministrator)]
    [InlineData(TestSids.Eva, ValidationIssueCode.AccountNotAdministrator)]
    public async Task Only_qualified_administrator_user_accounts_can_be_added(string account, ValidationIssueCode expected)
    {
        await RpcAssert.ValidationFailsAsync(string.Empty, expected, () => _elevated.Proxy.AddAuthorizedUserAsync(account, Ct));

        Assert.Equal([TestSids.Ana], _server.Settings.Sids);
    }

    [Fact]
    public async Task A_user_account_whose_SID_is_not_an_account_SID_is_rejected()
    {
        // Defensa adicional: aunque el sistema la diera como "usuario", un SID de servicio nunca se admite.
        _server.Accounts.AddUser(TestSids.ServiceSid, @"NT SERVICE\algo", administrator: true);

        await RpcAssert.ValidationFailsAsync(
            string.Empty, ValidationIssueCode.AccountNotUser, () => _elevated.Proxy.AddAuthorizedUserAsync(TestSids.ServiceSid, Ct));
    }

    [Fact]
    public async Task A_failed_membership_query_is_reported_as_not_administrator()
    {
        _server.Accounts.FailMembershipQueries(null);
        var client = await _server.ConnectAuthorizedAsync(TestSids.System);
        _server.Accounts.FailMembershipQueries(FakeAccountDirectory.QueryFailed());

        await RpcAssert.ValidationFailsAsync(
            string.Empty, ValidationIssueCode.AccountNotAdministrator, () => client.Proxy.AddAuthorizedUserAsync(TestSids.Luis, Ct));
    }

    [Fact]
    public async Task Adding_or_removing_requires_an_elevated_client()
    {
        var ana = await _server.ConnectAuthorizedAsync(TestSids.Identity(TestSids.Ana));

        await RpcAssert.FailsWithAsync(BackboneErrorCode.NotAuthorized, () => ana.Proxy.AddAuthorizedUserAsync(TestSids.Luis, Ct));
        await RpcAssert.FailsWithAsync(BackboneErrorCode.NotAuthorized, () => ana.Proxy.RemoveAuthorizedUserAsync(TestSids.Ana, Ct));

        Assert.Equal([TestSids.Ana], _server.Settings.Sids);
    }

    [Fact]
    public async Task A_non_elevated_NotAuthorized_for_management_does_not_close_the_connection()
    {
        var ana = await _server.ConnectAuthorizedAsync(TestSids.Identity(TestSids.Ana));

        await RpcAssert.FailsWithAsync(BackboneErrorCode.NotAuthorized, () => ana.Proxy.AddAuthorizedUserAsync(TestSids.Luis, Ct));

        // Es una operación no permitida, no un cliente no autorizado: la conexión sigue.
        Assert.Equal(TestSids.Ana, (await ana.Proxy.GetServiceInfoAsync(Ct)).Caller.UserSid);
    }

    [Fact]
    public async Task Removing_a_user_closes_its_connections()
    {
        var ana = await _server.ConnectAuthorizedAsync(TestSids.Identity(TestSids.Ana));

        await _elevated.Proxy.RemoveAuthorizedUserAsync(TestSids.Ana, Ct);

        await ana.WaitClosedAsync();
        Assert.Empty(_server.Settings.Sids);
        Assert.DoesNotContain(TestSids.Ana, await File.ReadAllTextAsync(_server.Settings.SettingsPath, Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Removing_a_user_that_is_not_there_is_not_an_error()
    {
        await _elevated.Proxy.RemoveAuthorizedUserAsync(TestSids.Eva, Ct);

        Assert.Equal([TestSids.Ana], _server.Settings.Sids);
    }

    [Fact]
    public async Task Removing_with_a_malformed_SID_is_a_validation_error()
    {
        await RpcAssert.ValidationFailsAsync(
            string.Empty, ValidationIssueCode.AccountNotFound, () => _elevated.Proxy.RemoveAuthorizedUserAsync("PC-SALON\\ana", Ct));
    }

    [Fact]
    public async Task SID_comparison_ignores_case()
    {
        await _elevated.Proxy.RemoveAuthorizedUserAsync(TestSids.Ana.ToLowerInvariant(), Ct);

        Assert.Empty(_server.Settings.Sids);
        Assert.False(_server.Settings.Contains(new SecurityIdentifier(TestSids.Ana)));
    }
}
