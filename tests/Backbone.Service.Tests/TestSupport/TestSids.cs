using System.Security.Principal;
using Backbone.Service.Os;

namespace Backbone.Service.Tests.TestSupport;

/// <summary>SID e identidades de prueba.</summary>
internal static class TestSids
{
    public const string Ana = "S-1-5-21-1111111111-2222222222-3333333333-1001";
    public const string Luis = "S-1-5-21-1111111111-2222222222-3333333333-1002";
    public const string Eva = "S-1-5-21-1111111111-2222222222-3333333333-1003";
    public const string EntraUser = "S-1-12-1-1111111111-2222222222-3333333333-4444444444";
    public const string Everyone = "S-1-1-0";
    public const string AuthenticatedUsers = "S-1-5-11";
    public const string Users = "S-1-5-32-545";
    public const string Administrators = "S-1-5-32-544";
    public const string LocalSystem = "S-1-5-18";
    public const string ServiceSid = "S-1-5-80-1111111111-2222222222-3333333333-4444444444-5555555555";

    public static ClientIdentity Identity(string sid, bool elevated = false, TokenImpersonationLevel level = TokenImpersonationLevel.Impersonation) =>
        new(new SecurityIdentifier(sid), level, elevated);

    public static ClientIdentity System => Identity(LocalSystem, elevated: true);
}
