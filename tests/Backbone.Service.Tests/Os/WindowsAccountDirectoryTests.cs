using System.Security.Principal;
using Backbone.Service.Os;
using Backbone.Service.Tests.TestSupport;

namespace Backbone.Service.Tests.Os;

/// <summary>
/// §7.3: consulta real de cuentas y de la pertenencia actual a Administradores (LookupAccountName/Sid y Authz). No necesita
/// elevación; el resultado esperado se toma del token del usuario que ejecuta las pruebas.
/// </summary>
[Trait("Story", "HT-03")]
public sealed class WindowsAccountDirectoryTests
{
    private readonly WindowsAccountDirectory _directory = new();

    [Fact]
    public void The_current_user_resolves_as_a_user_account_both_ways()
    {
        var sid = WindowsTestEnvironment.CurrentUserSid;

        var bySid = _directory.LookupBySid(sid);

        Assert.NotNull(bySid);
        Assert.Equal(AccountKind.User, bySid.Kind);
        Assert.Contains('\\', bySid.AccountName);
        var byName = _directory.LookupByName(bySid.AccountName);
        Assert.NotNull(byName);
        Assert.Equal(sid, byName.Sid);
        Assert.Equal(AccountKind.User, byName.Kind);
    }

    [Theory]
    [InlineData(WellKnownSidType.BuiltinAdministratorsSid)]
    [InlineData(WellKnownSidType.BuiltinUsersSid)]
    [InlineData(WellKnownSidType.WorldSid)]
    [InlineData(WellKnownSidType.AuthenticatedUserSid)]
    [InlineData(WellKnownSidType.LocalSystemSid)]
    public void Groups_and_well_known_SIDs_are_not_user_accounts(WellKnownSidType type)
    {
        var info = _directory.LookupBySid(new SecurityIdentifier(type, null));

        Assert.NotNull(info);
        Assert.Equal(AccountKind.Other, info.Kind);
    }

    [Fact]
    public void Unknown_accounts_resolve_to_null()
    {
        Assert.Null(_directory.LookupByName($@"{Environment.MachineName}\no-existe-{Guid.NewGuid():N}"[..40]));
        Assert.Null(_directory.LookupBySid(new SecurityIdentifier("S-1-5-21-1-2-3-987654")));
    }

    [Fact]
    public void Administrator_membership_matches_the_token_of_the_current_user()
    {
        // La consulta no usa el token (Authz recalcula los grupos), pero en una sesión normal ambos coinciden.
        Assert.Equal(WindowsTestEnvironment.TokenIncludesAdministrators, _directory.IsLocalAdministrator(WindowsTestEnvironment.CurrentUserSid));
    }
}
