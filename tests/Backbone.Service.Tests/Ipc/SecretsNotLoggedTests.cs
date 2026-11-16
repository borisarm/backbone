using System.Text;
using System.Text.Json;
using Backbone.Contracts;
using Backbone.Service.Tests.TestSupport;

namespace Backbone.Service.Tests.Ipc;

/// <summary>
/// RNF-06 / HU-04 criterio 3: una contraseña centinela enviada por IPC no aparece en ningún archivo de la carpeta de datos
/// (configuración, credenciales, historial, logs), en los registros capturados, en las respuestas ni en los mensajes de
/// error: ni cuando se guarda, ni cuando se consulta, ni cuando la validación la rechaza, ni al sustituirla o borrarla.
/// </summary>
[Trait("Story", "HU-04")]
public sealed class SecretsNotLoggedTests : IAsyncLifetime
{
    private const string Sentinel = "CENTINELA-contraseña-9f3a";
    private const string Share = @"\\nas\Backups";

    private readonly IpcTestHarness _server = new(); // DPAPI real con ámbito equipo

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _server.DisposeAsync();

    [Fact]
    public async Task A_password_sent_over_IPC_never_reaches_the_data_folder_logs_responses_or_error_messages()
    {
        await _server.AuthorizeAsync(TestSids.Ana);
        var ana = await _server.ConnectAuthorizedAsync(TestSids.Identity(TestSids.Ana));
        var responses = new List<string>();

        await ana.Proxy.SaveCredentialAsync(Share, new CredentialInput { UserName = "ana", Password = Sentinel }, Ct);
        responses.Add(JsonSerializer.Serialize(await ana.Proxy.GetCredentialInfoAsync(Share, Ct)));
        responses.Add(JsonSerializer.Serialize(await RpcAssert.FailsWithAsync(
            BackboneErrorCode.ValidationFailed,
            () => ana.Proxy.SaveCredentialAsync(Share, new CredentialInput { UserName = new string('u', 5000), Password = Sentinel }, Ct))));
        responses.Add(JsonSerializer.Serialize(await RpcAssert.FailsWithAsync(
            BackboneErrorCode.ValidationFailed,
            () => ana.Proxy.SaveCredentialAsync(Share, new CredentialInput { UserName = "ana", Password = Sentinel + "\0" }, Ct))));
        responses.Add(JsonSerializer.Serialize(await RpcAssert.FailsWithAsync(
            BackboneErrorCode.ValidationFailed,
            () => ana.Proxy.SaveCredentialAsync(@"\nas\" + Sentinel, new CredentialInput { UserName = "ana", Password = Sentinel }, Ct))));
        responses.Add(JsonSerializer.Serialize(await RpcAssert.FailsWithAsync(
            BackboneErrorCode.ValidationFailed,
            () => ana.Proxy.SaveCredentialAsync(Share, new CredentialInput { UserName = "ana", Password = Sentinel + new string('x', ContractLimits.MaxPasswordLength) }, Ct))));

        // Sustituirla y volver a guardarla (escritura atómica: no quedan temporales con el contenido anterior).
        await ana.Proxy.SaveCredentialAsync(Share, new CredentialInput { UserName = "ana", Password = Sentinel + "-2" }, Ct);
        await ana.Proxy.DeleteCredentialAsync(@"\\nas\otro", Ct);
        await ana.Proxy.SaveCredentialAsync(@"\\nas\otro", new CredentialInput { UserName = "luis", Password = Sentinel }, Ct);
        await ana.Proxy.DeleteCredentialAsync(@"\\nas\otro", Ct);

        Assert.True(Directory.EnumerateFiles(_server.DataRoot, "*.bin", SearchOption.AllDirectories).Any()); // hay algo que buscar
        SecretSearch.AssertNotInFolder(Sentinel, _server.DataRoot);
        SecretSearch.AssertNotInLogs(Sentinel, _server.Logs);
        Assert.All(responses, r => Assert.DoesNotContain(Sentinel, r, StringComparison.Ordinal));
    }
}

/// <summary>Búsqueda de un secreto centinela en disco y en los registros (HU-04 criterio 3, RNF-06).</summary>
internal static class SecretSearch
{
    /// <summary>
    /// Ningún archivo de <paramref name="root"/> (recursivo) contiene el secreto en UTF-8, UTF-16 ni Base64 (lo que dejaría
    /// un serializador JSON de <c>byte[]</c>).
    /// </summary>
    public static void AssertNotInFolder(string secret, string root)
    {
        var utf8 = Encoding.UTF8.GetBytes(secret);
        var needles = new[] { utf8, Encoding.Unicode.GetBytes(secret), Encoding.ASCII.GetBytes(Convert.ToBase64String(utf8)) };
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var content = File.ReadAllBytes(file);
            foreach (var needle in needles)
            {
                Assert.True(content.AsSpan().IndexOf(needle) < 0, $"El secreto aparece en {file}.");
            }
        }
    }

    public static void AssertNotInLogs(string secret, CapturingLoggerProvider logs)
    {
        Assert.DoesNotContain(secret, logs.AllText(), StringComparison.Ordinal);
        Assert.All(logs.Entries, e =>
        {
            Assert.DoesNotContain(secret, string.Join(';', e.Properties.Select(p => p.Value)), StringComparison.Ordinal);
            Assert.DoesNotContain(secret, e.Exception?.ToString() ?? string.Empty, StringComparison.Ordinal);
        });
    }
}
