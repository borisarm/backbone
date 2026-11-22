using Backbone.Contracts;
using Backbone.Service.Nas;
using Backbone.Service.Tests.TestSupport;

namespace Backbone.Service.Tests.Ipc;

/// <summary>
/// Revisión de seguridad de HU-14, B-2: guardar una credencial y probarla sin credencial explícita (<c>TestConnectionAsync(null)</c>)
/// no permite probar contraseñas sin límite: los rechazos cuentan en el mismo contador por SID que los de una credencial explícita.
/// </summary>
[Trait("Story", "HU-14")]
public sealed class CredentialTestLimitIpcTests : IAsyncLifetime
{
    private const string Share = @"\\nas\backups";

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
    public async Task Saving_and_testing_the_saved_credential_five_times_blocks_the_sixth_test_without_connecting()
    {
        _server.ShareConnector.ConnectError = new ShareException(BackboneErrorCode.CredentialsRejected, ShareErrors.ErrorLogonFailure);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await _ana.Proxy.SaveCredentialAsync(Share, new CredentialInput { UserName = "ana", Password = "intento-" + attempt }, Ct);
            var result = await _ana.Proxy.TestConnectionAsync(Share, null, Ct);
            Assert.Equal(BackboneErrorCode.CredentialsRejected, result.ErrorCode);
        }

        var connections = _server.ShareConnector.Connections.Count;
        _server.ShareConnector.ConnectError = null;
        await _ana.Proxy.SaveCredentialAsync(Share, new CredentialInput { UserName = "ana", Password = "otro-intento" }, Ct);

        var blocked = await _ana.Proxy.TestConnectionAsync(Share, null, Ct);

        Assert.Equal(BackboneErrorCode.NasBusy, blocked.ErrorCode);
        Assert.Equal(connections, _server.ShareConnector.Connections.Count);
    }
}
