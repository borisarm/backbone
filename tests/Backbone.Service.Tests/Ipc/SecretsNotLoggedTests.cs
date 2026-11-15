using Backbone.Contracts;
using Backbone.Service.Tests.TestSupport;

namespace Backbone.Service.Tests.Ipc;

/// <summary>
/// RNF-06: una contraseña centinela enviada por IPC no aparece en los registros, en las respuestas de error ni en
/// <c>settings.json</c>, ni cuando la operación está aún sin implementar (HT-03), ni cuando la validación la rechaza.
/// </summary>
[Trait("Story", "HT-03")]
public sealed class SecretsNotLoggedTests : IAsyncLifetime
{
    private const string Sentinel = "CENTINELA-contraseña-9f3a";

    private readonly IpcTestHarness _server = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _server.DisposeAsync();

    [Fact]
    public async Task A_password_sent_over_IPC_never_reaches_logs_or_error_messages()
    {
        await _server.AuthorizeAsync(TestSids.Ana);
        var ana = await _server.ConnectAuthorizedAsync(TestSids.Identity(TestSids.Ana));

        var notSupported = await RpcAssert.FailsWithAsync(
            BackboneErrorCode.NotSupported,
            () => ana.Proxy.SaveCredentialAsync(@"\nas\copias", new CredentialInput { UserName = "ana", Password = Sentinel }, Ct));
        var rejected = await RpcAssert.FailsWithAsync(
            BackboneErrorCode.ValidationFailed,
            () => ana.Proxy.SaveCredentialAsync(@"\nas\copias", new CredentialInput { UserName = new string('u', 5000), Password = Sentinel }, Ct));

        Assert.DoesNotContain(Sentinel, _server.Logs.AllText(), StringComparison.Ordinal);
        Assert.DoesNotContain(Sentinel, notSupported.Message ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(Sentinel, rejected.Message ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(Sentinel, System.Text.Json.JsonSerializer.Serialize(rejected), StringComparison.Ordinal);
        Assert.All(_server.Logs.Entries, e => Assert.DoesNotContain(Sentinel, string.Join(';', e.Properties.Select(p => p.Value)), StringComparison.Ordinal));
        Assert.False(File.Exists(_server.Settings.SettingsPath) && File.ReadAllText(_server.Settings.SettingsPath).Contains(Sentinel, StringComparison.Ordinal));
    }
}
