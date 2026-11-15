using System.Security.Principal;
using Backbone.Service.Ipc;
using Backbone.Service.Persistence;
using Backbone.Service.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backbone.Service.Tests.Ipc;

/// <summary>§7.3: regla de autorización del modelo "solo cuentas administradoras".</summary>
[Trait("Story", "HT-03")]
public sealed class CallerAuthorizerTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly SettingsStore _settings;
    private readonly FakeAccountDirectory _accounts = new FakeAccountDirectory()
        .AddUser(TestSids.Ana, @"PC\ana", administrator: true)
        .AddUser(TestSids.Luis, @"PC\luis", administrator: true)
        .AddUser(TestSids.Eva, @"PC\eva", administrator: false);

    private readonly CallerAuthorizer _authorizer;

    public CallerAuthorizerTests()
    {
        _settings = new SettingsStore(_temp.Combine("settings.json"), TestFileTrust.CurrentUser, NullLogger<SettingsStore>.Instance);
        _settings.AddAuthorizedUserAsync(new SecurityIdentifier(TestSids.Ana), CancellationToken.None).GetAwaiter().GetResult();
        _settings.AddAuthorizedUserAsync(new SecurityIdentifier(TestSids.Eva), CancellationToken.None).GetAwaiter().GetResult();
        _authorizer = new CallerAuthorizer(_settings, _accounts);
    }

    public void Dispose()
    {
        _settings.Dispose();
        _temp.Dispose();
    }

    public static TheoryData<string, bool, TokenImpersonationLevel, string> Cases => new()
    {
        // Usuario de la lista y administrador, sin elevar: sí.
        { TestSids.Ana, false, TokenImpersonationLevel.Impersonation, nameof(AuthorizationFailure.None) },
        { TestSids.Ana, false, TokenImpersonationLevel.Delegation, nameof(AuthorizationFailure.None) },
        // Niveles que no permiten suplantar: no, aunque sea SYSTEM o esté elevado.
        { TestSids.Ana, true, TokenImpersonationLevel.Identification, nameof(AuthorizationFailure.ImpersonationLevelTooLow) },
        { TestSids.Ana, true, TokenImpersonationLevel.Anonymous, nameof(AuthorizationFailure.ImpersonationLevelTooLow) },
        { TestSids.LocalSystem, true, TokenImpersonationLevel.Identification, nameof(AuthorizationFailure.ImpersonationLevelTooLow) },
        // Administrador fuera de la lista: solo elevado.
        { TestSids.Luis, false, TokenImpersonationLevel.Impersonation, nameof(AuthorizationFailure.NotInAuthorizedList) },
        { TestSids.Luis, true, TokenImpersonationLevel.Impersonation, nameof(AuthorizationFailure.None) },
        // En la lista pero ya no es administradora: no, ni elevada.
        { TestSids.Eva, false, TokenImpersonationLevel.Impersonation, nameof(AuthorizationFailure.NotAdministrator) },
        { TestSids.Eva, true, TokenImpersonationLevel.Impersonation, nameof(AuthorizationFailure.NotAdministrator) },
        // Cuenta desconocida: no.
        { TestSids.EntraUser, true, TokenImpersonationLevel.Impersonation, nameof(AuthorizationFailure.NotAdministrator) },
        // SYSTEM: sí.
        { TestSids.LocalSystem, true, TokenImpersonationLevel.Impersonation, nameof(AuthorizationFailure.None) },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Authorization_follows_the_administrators_only_model(string sid, bool elevated, TokenImpersonationLevel level, string expected)
    {
        Assert.Equal(expected, _authorizer.Check(TestSids.Identity(sid, elevated, level)).ToString());
    }

    [Fact]
    public void Membership_is_queried_now_not_taken_from_the_token()
    {
        Assert.Equal(AuthorizationFailure.None, _authorizer.Check(TestSids.Identity(TestSids.Luis, elevated: true)));

        _accounts.SetAdministrator(TestSids.Luis, false);

        // El token sigue diciendo "elevado", pero la pertenencia actual manda.
        Assert.Equal(AuthorizationFailure.NotAdministrator, _authorizer.Check(TestSids.Identity(TestSids.Luis, elevated: true)));
    }

    [Fact]
    public void A_failed_membership_query_denies()
    {
        _accounts.FailMembershipQueries(FakeAccountDirectory.QueryFailed());

        Assert.Equal(AuthorizationFailure.NotAdministrator, _authorizer.Check(TestSids.Identity(TestSids.Ana)));
        Assert.Equal(AuthorizationFailure.None, _authorizer.Check(TestSids.System));
    }

    [Fact]
    public async Task Changes_to_the_list_apply_immediately()
    {
        Assert.Equal(AuthorizationFailure.None, _authorizer.Check(TestSids.Identity(TestSids.Ana)));

        await _settings.RemoveAuthorizedUserAsync(new SecurityIdentifier(TestSids.Ana), TestContext.Current.CancellationToken);

        Assert.Equal(AuthorizationFailure.NotInAuthorizedList, _authorizer.Check(TestSids.Identity(TestSids.Ana)));
    }
}
