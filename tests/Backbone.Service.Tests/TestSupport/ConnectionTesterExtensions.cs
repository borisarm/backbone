using Backbone.Contracts;
using Backbone.Service.Nas;

namespace Backbone.Service.Tests.TestSupport;

/// <summary>Las pruebas que no tratan de los rechazos seguidos por SID (M-1) piden la prueba como un usuario cualquiera.</summary>
internal static class ConnectionTesterExtensions
{
    public static Task<ConnectionTestResult> TestAsync(this ConnectionTester tester, string destination, CredentialInput? credential, CancellationToken ct) =>
        tester.TestAsync(destination, credential, TestSids.Ana, ct);
}
