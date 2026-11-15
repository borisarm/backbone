using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Backbone.Service.Ipc;
using Backbone.Service.Tests.TestSupport;

namespace Backbone.Service.Tests.Ipc;

/// <summary>
/// HT-03 criterio 2 / §7.3 / RNF-08: ACL del pipe. Solo SYSTEM, Administradores y los SID autorizados tienen acceso;
/// nadie más (Everyone, Usuarios, Usuarios autenticados), y los usuarios autorizados no pueden crear instancias del servidor.
/// </summary>
[Trait("Story", "HT-03")]
public sealed class PipeSecurityFactoryTests
{
    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier Network = new(WellKnownSidType.NetworkSid, null);

    [Fact]
    public void The_acl_grants_full_control_to_SYSTEM_and_Administrators_and_read_write_to_each_authorized_SID()
    {
        var rules = Rules(PipeSecurityFactory.Create([TestSids.Ana, TestSids.EntraUser]));

        Assert.Equal(PipeAccessRights.FullControl, Allowed(rules, System));
        Assert.Equal(PipeAccessRights.FullControl, Allowed(rules, Administrators));
        Assert.Equal(PipeSecurityFactory.ClientRights, Allowed(rules, new SecurityIdentifier(TestSids.Ana)));
        Assert.Equal(PipeSecurityFactory.ClientRights, Allowed(rules, new SecurityIdentifier(TestSids.EntraUser)));
        Assert.Equal(4, rules.Count(r => r.AccessControlType == AccessControlType.Allow));
    }

    [Fact]
    public void Remote_access_is_denied()
    {
        var deny = Assert.Single(Rules(PipeSecurityFactory.Create([])), r => r.AccessControlType == AccessControlType.Deny);

        Assert.Equal(Network, deny.IdentityReference);
        Assert.Equal(PipeAccessRights.FullControl, deny.PipeAccessRights);
    }

    [Fact]
    public void Authorized_users_get_read_and_write_but_cannot_create_server_instances_or_change_the_acl()
    {
        var rights = PipeSecurityFactory.ClientRights;

        Assert.True(rights.HasFlag(PipeAccessRights.ReadData));
        Assert.True(rights.HasFlag(PipeAccessRights.WriteData));
        Assert.False(rights.HasFlag(PipeAccessRights.CreateNewInstance));
        Assert.False(rights.HasFlag(PipeAccessRights.ChangePermissions));
        Assert.False(rights.HasFlag(PipeAccessRights.TakeOwnership));
        Assert.False(rights.HasFlag(PipeAccessRights.WriteAttributes));
        Assert.False(rights.HasFlag(PipeAccessRights.Delete));
    }

    [Fact]
    public void Without_authorized_users_only_SYSTEM_and_Administrators_have_access()
    {
        var allowed = Rules(PipeSecurityFactory.Create([]))
            .Where(r => r.AccessControlType == AccessControlType.Allow)
            .Select(r => (SecurityIdentifier)r.IdentityReference);

        Assert.Equal([System, Administrators], allowed.OrderBy(s => s.Value));
    }

    [Theory]
    [InlineData(TestSids.Everyone)]
    [InlineData(TestSids.AuthenticatedUsers)]
    [InlineData(TestSids.Users)]
    [InlineData(TestSids.Administrators)]
    [InlineData(TestSids.ServiceSid)]
    [InlineData("S-1-5-7")] // Anónimo
    [InlineData("no es un SID")]
    public void Non_account_SIDs_in_the_list_never_get_an_entry(string sid)
    {
        var rules = Rules(PipeSecurityFactory.Create([sid]));

        Assert.Equal(2, rules.Count(r => r.AccessControlType == AccessControlType.Allow));
        Assert.DoesNotContain(rules, r => r.IdentityReference.Value == TestSids.Everyone
                                          || r.IdentityReference.Value == TestSids.Users
                                          || r.IdentityReference.Value == TestSids.AuthenticatedUsers);
    }

    [Fact]
    public void The_acl_is_protected_from_inheritance()
    {
        Assert.True(PipeSecurityFactory.Create([TestSids.Ana]).AreAccessRulesProtected);
    }

    [Fact]
    public void An_additional_server_SID_gets_full_control_only_when_given()
    {
        var withServer = Rules(PipeSecurityFactory.Create([], new SecurityIdentifier(TestSids.Luis)));

        Assert.Equal(PipeAccessRights.FullControl, Allowed(withServer, new SecurityIdentifier(TestSids.Luis)));
        Assert.Equal(2, Rules(PipeSecurityFactory.Create([], System)).Count(r => r.AccessControlType == AccessControlType.Allow));
    }

    private static List<PipeAccessRule> Rules(PipeSecurity security) =>
        [.. security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>()];

    private static PipeAccessRights Allowed(List<PipeAccessRule> rules, SecurityIdentifier sid) =>
        Assert.Single(rules, r => r.AccessControlType == AccessControlType.Allow && (SecurityIdentifier)r.IdentityReference == sid).PipeAccessRights;
}
