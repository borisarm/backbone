using System.Security.Principal;
using Backbone.Contracts;
using Backbone.Service.Ipc;
using Backbone.Service.Tests.TestSupport;
using Microsoft.Extensions.Logging;

namespace Backbone.Service.Tests.Ipc;

/// <summary>
/// HT-03 criterios 1 y 2 y §7.3: autorización por conexión y por llamada con el servidor IPC completo sobre flujos en
/// memoria (identidad del cliente simulada).
/// </summary>
[Trait("Story", "HT-03")]
public sealed class ConnectionAuthorizationTests : IAsyncLifetime
{
    private readonly IpcTestHarness _server = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => new(_server.AuthorizeAsync(TestSids.Ana));

    public ValueTask DisposeAsync() => _server.DisposeAsync();

    [Fact]
    public async Task An_authorized_user_gets_service_and_contract_version_and_its_own_identity()
    {
        var client = _server.Connect(TestSids.Identity(TestSids.Ana));

        var info = await client.Proxy.GetServiceInfoAsync(Ct);

        Assert.Equal(ContractVersion.Value, info.ContractVersion);
        Assert.False(string.IsNullOrEmpty(info.ServiceVersion));
        Assert.DoesNotContain('+', info.ServiceVersion);
        Assert.Equal(Environment.MachineName, info.MachineName);
        Assert.Equal(TimeSpan.Zero, info.StartedUtc.Offset);
        Assert.Equal(_server.Time.GetUtcNow(), info.StartedUtc);
        Assert.Equal(TestSids.Ana, info.Caller.UserSid);
        Assert.Equal(@"PC-SALON\ana", info.Caller.UserName);
        Assert.False(info.Caller.IsElevated);
    }

    [Fact]
    public async Task IsElevated_comes_from_the_token_of_the_client()
    {
        var client = _server.Connect(TestSids.Identity(TestSids.Ana, elevated: true));

        var info = await client.Proxy.GetServiceInfoAsync(Ct);

        Assert.True(info.Caller.IsElevated);
    }

    [Fact]
    public async Task An_administrator_not_in_the_list_and_not_elevated_is_rejected_and_disconnected()
    {
        var client = _server.Connect(TestSids.Identity(TestSids.Luis));

        await RpcAssert.FailsWithAsync(BackboneErrorCode.NotAuthorized, () => client.Proxy.GetServiceInfoAsync(Ct));

        await client.WaitClosedAsync();
        Assert.True(client.Channel.IsDisposed);
        Assert.Equal(0, _server.Manager.Count);
    }

    [Fact]
    public async Task An_elevated_administrator_is_accepted_even_if_not_in_the_list()
    {
        // Es la vía para dar de alta al primer usuario autorizado: la ACL del pipe da acceso a Administradores.
        var client = _server.Connect(TestSids.Identity(TestSids.Luis, elevated: true));

        var info = await client.Proxy.GetServiceInfoAsync(Ct);

        Assert.Equal(TestSids.Luis, info.Caller.UserSid);
    }

    [Fact]
    public async Task LocalSystem_is_accepted()
    {
        var client = _server.Connect(TestSids.System);

        var info = await client.Proxy.GetServiceInfoAsync(Ct);

        Assert.Equal(TestSids.LocalSystem, info.Caller.UserSid);
    }

    [Fact]
    public async Task A_listed_user_who_is_no_longer_an_administrator_is_rejected()
    {
        await _server.AuthorizeAsync(TestSids.Eva); // en la lista, pero no es administradora
        var client = _server.Connect(TestSids.Identity(TestSids.Eva, elevated: true));

        await RpcAssert.FailsWithAsync(BackboneErrorCode.NotAuthorized, () => client.Proxy.GetServiceInfoAsync(Ct));
        await client.WaitClosedAsync();
    }

    [Fact]
    public async Task If_the_membership_query_fails_the_client_is_rejected()
    {
        _server.Accounts.FailMembershipQueries(FakeAccountDirectory.QueryFailed());
        var client = _server.Connect(TestSids.Identity(TestSids.Ana));

        await RpcAssert.FailsWithAsync(BackboneErrorCode.NotAuthorized, () => client.Proxy.GetServiceInfoAsync(Ct));
        await client.WaitClosedAsync();
    }

    [Theory]
    [InlineData(TokenImpersonationLevel.Anonymous)]
    [InlineData(TokenImpersonationLevel.Identification)]
    [InlineData(TokenImpersonationLevel.None)]
    public async Task A_client_that_does_not_allow_impersonation_is_rejected(TokenImpersonationLevel level)
    {
        var client = _server.Connect(TestSids.Identity(TestSids.Ana, elevated: true, level));

        await RpcAssert.FailsWithAsync(BackboneErrorCode.NotAuthorized, () => client.Proxy.GetServiceInfoAsync(Ct));
        await client.WaitClosedAsync();
    }

    [Fact]
    public async Task Delegation_level_is_accepted()
    {
        var client = _server.Connect(TestSids.Identity(TestSids.Ana, level: TokenImpersonationLevel.Delegation));

        await client.Proxy.GetServiceInfoAsync(Ct);
    }

    [Fact]
    public async Task If_the_identity_cannot_be_read_the_client_is_rejected()
    {
        var client = _server.Connect(() => throw new IOException("ImpersonateNamedPipeClient failed"));

        await RpcAssert.FailsWithAsync(BackboneErrorCode.NotAuthorized, () => client.Proxy.GetServiceInfoAsync(Ct));
        await client.WaitClosedAsync();
    }

    [Fact]
    public async Task The_identity_is_read_once_but_authorization_is_checked_on_every_call()
    {
        var client = _server.Connect(TestSids.Identity(TestSids.Ana));
        var before = _server.Accounts.MembershipQueries;

        await client.Proxy.GetServiceInfoAsync(Ct);
        await client.Proxy.GetServiceInfoAsync(Ct);
        await client.Proxy.GetAuthorizedUsersAsync(Ct);

        Assert.Equal(1, client.Channel.IdentityReads);
        Assert.Equal(before + 3, _server.Accounts.MembershipQueries);
    }

    [Fact]
    public async Task Losing_administrator_membership_rejects_the_next_call_and_closes_the_connection()
    {
        var client = await _server.ConnectAuthorizedAsync(TestSids.Identity(TestSids.Ana));

        _server.Accounts.SetAdministrator(TestSids.Ana, false);

        await RpcAssert.FailsWithAsync(BackboneErrorCode.NotAuthorized, () => client.Proxy.GetServiceInfoAsync(Ct));
        await client.WaitClosedAsync();
    }

    [Fact]
    public async Task Losing_administrator_membership_closes_an_idle_connection_at_the_periodic_revalidation()
    {
        var client = await _server.ConnectAuthorizedAsync(TestSids.Identity(TestSids.Ana));
        _server.Accounts.SetAdministrator(TestSids.Ana, false);

        _server.Time.Advance(_server.Options.RevalidationInterval);

        await client.WaitClosedAsync();
    }

    [Fact]
    public async Task Removing_a_user_from_the_list_closes_its_connections_but_not_the_others()
    {
        await _server.AuthorizeAsync(TestSids.Luis);
        var ana1 = await _server.ConnectAuthorizedAsync(TestSids.Identity(TestSids.Ana));
        var ana2 = await _server.ConnectAuthorizedAsync(TestSids.Identity(TestSids.Ana));
        var luis = await _server.ConnectAuthorizedAsync(TestSids.Identity(TestSids.Luis));

        await _server.Settings.RemoveAuthorizedUserAsync(new SecurityIdentifier(TestSids.Ana), Ct);

        await ana1.WaitClosedAsync();
        await ana2.WaitClosedAsync();
        Assert.Equal(TestSids.Luis, (await luis.Proxy.GetServiceInfoAsync(Ct)).Caller.UserSid);
        Assert.Equal(1, _server.Manager.Count);
    }

    [Fact]
    public async Task An_elevated_connection_survives_the_removal_of_its_SID_from_the_list()
    {
        var elevated = await _server.ConnectAuthorizedAsync(TestSids.Identity(TestSids.Ana, elevated: true));

        await _server.Settings.RemoveAuthorizedUserAsync(new SecurityIdentifier(TestSids.Ana), Ct);

        await elevated.Proxy.GetServiceInfoAsync(Ct);
    }

    [Fact]
    public async Task A_connection_that_never_sends_a_message_is_closed_after_the_identification_timeout()
    {
        var client = _server.Connect(TestSids.Identity(TestSids.Ana));

        _server.Time.Advance(_server.Options.IdentificationTimeout);

        await client.WaitClosedAsync();
        Assert.Equal(0, client.Channel.IdentityReads);
    }

    [Fact]
    public async Task Write_operations_are_audited_with_SID_account_and_method_but_never_arguments()
    {
        var client = await _server.ConnectAuthorizedAsync(TestSids.Identity(TestSids.Ana, elevated: true));
        const string argument = @"PC-SALON\argumento-secreto";

        await RpcAssert.FailsWithAsync(
            BackboneErrorCode.ValidationFailed, () => client.Proxy.AddAuthorizedUserAsync(argument, Ct));
        await RpcAssert.FailsWithAsync(
            BackboneErrorCode.NotSupported,
            () => client.Proxy.SaveCredentialAsync(@"\\nas\copias", new CredentialInput { UserName = "u", Password = "Contraseña-123" }, Ct));

        var audit = _server.Logs.Entries.Where(e => e.Message.StartsWith("Auditoría IPC", StringComparison.Ordinal)).ToList();
        Assert.Contains(audit, e => e.Message.Contains(nameof(IBackboneService.AddAuthorizedUserAsync), StringComparison.Ordinal)
                                    && e.Message.Contains(TestSids.Ana, StringComparison.Ordinal)
                                    && e.Message.Contains(@"PC-SALON\ana", StringComparison.Ordinal));
        Assert.Contains(audit, e => e.Message.Contains(nameof(IBackboneService.SaveCredentialAsync), StringComparison.Ordinal));
        var all = _server.Logs.AllText();
        Assert.DoesNotContain("argumento-secreto", all, StringComparison.Ordinal);
        Assert.DoesNotContain("Contraseña-123", all, StringComparison.Ordinal);
        Assert.DoesNotContain(@"\\nas\copias", all, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_operations_are_not_audited()
    {
        var client = await _server.ConnectAuthorizedAsync(TestSids.Identity(TestSids.Ana));

        await client.Proxy.GetAuthorizedUsersAsync(Ct);

        Assert.DoesNotContain(_server.Logs.Entries, e => e.Message.StartsWith("Auditoría IPC", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Rejections_are_logged_with_the_SID_and_the_reason()
    {
        var client = _server.Connect(TestSids.Identity(TestSids.Luis));

        await RpcAssert.FailsWithAsync(BackboneErrorCode.NotAuthorized, () => client.Proxy.GetServiceInfoAsync(Ct));

        Assert.Contains(_server.Logs.Entries, e => e.Level == LogLevel.Warning
                                                   && e.Message.Contains(TestSids.Luis, StringComparison.Ordinal)
                                                   && e.Message.Contains(nameof(AuthorizationFailure.NotInAuthorizedList), StringComparison.Ordinal));
    }
}
