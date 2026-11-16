using Backbone.Contracts;
using Backbone.Service.Ipc;
using Backbone.Service.Nas;
using Backbone.Service.Os;
using Backbone.Service.Persistence;
using Backbone.Service.Tests.Ipc;
using Backbone.Service.Tests.TestSupport;
using Microsoft.Extensions.Logging;

namespace Backbone.Service.Tests.Nas;

/// <summary>
/// HU-04 criterio 5 (RF-60): una ejecución programada, sin sesión ni cliente IPC, obtiene la credencial guardada del recurso
/// del destino y se la pasa a la conexión (un doble de <c>IShareConnector</c>, que es de HU-05). Sin credencial, nunca se
/// conecta: <see cref="BackboneErrorCode.CredentialRequired"/>.
/// </summary>
[Trait("Story", "HU-04")]
public sealed class ShareCredentialProviderTests : IDisposable
{
    private const string Password = "Contraseña-del-NAS-31b7";

    private readonly TempDirectory _temp = new();
    private readonly CapturingLoggerProvider _logs = new();
    private readonly ILoggerFactory _loggerFactory;

    public ShareCredentialProviderTests()
    {
        _loggerFactory = LoggerFactory.Create(b => b.AddProvider(_logs).SetMinimumLevel(LogLevel.Trace));
        Directory.CreateDirectory(_temp.Combine("credentials"));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _loggerFactory.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public async Task A_scheduled_run_without_a_session_connects_with_the_saved_credential()
    {
        // La app la guardó por IPC (una instancia del almacén)…
        using (var saving = CreateStore())
        {
            await saving.SaveAsync(@"\\nas\backups", new CredentialInput { UserName = "ana", Password = Password }, Ct);
        }

        // …y, tras reiniciar el servicio, una ejecución programada (otra instancia, en otro hilo del grupo, sin cliente IPC ni
        // perfil de usuario: solo DPAPI con ámbito equipo) la usa para conectar al destino del trabajo.
        using var store = CreateStore();
        var provider = new ShareCredentialProvider(store, _loggerFactory.CreateLogger<ShareCredentialProvider>());
        var connector = new FakeShareConnector();

        await Task.Run(() => connector.ConnectAsync(provider, @"\\NAS\Backups\pc-salon", Ct), Ct);

        var used = Assert.Single(connector.Connections);
        Assert.Equal((@"\\nas\backups", "ana", Password + "\0"), used);
        Assert.True(connector.CredentialDisposed);
        SecretSearch.AssertNotInLogs(Password, _logs);
    }

    [Fact]
    public async Task Without_a_saved_credential_it_never_connects_and_fails_with_CredentialRequired()
    {
        using var store = CreateStore();
        var provider = new ShareCredentialProvider(store, _loggerFactory.CreateLogger<ShareCredentialProvider>());
        var connector = new FakeShareConnector();

        var ex = await Assert.ThrowsAsync<BackboneException>(() => connector.ConnectAsync(provider, @"\\nas\backups", Ct));

        Assert.Equal(BackboneErrorCode.CredentialRequired, ex.Code);
        Assert.Empty(connector.Connections);
        Assert.Contains(_logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains(@"\\nas\backups", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_unusable_saved_credential_is_treated_as_missing()
    {
        using (var saving = CreateStore())
        {
            await saving.SaveAsync(@"\\nas\backups", new CredentialInput { UserName = "ana", Password = Password }, Ct);
            var path = saving.PathFor(@"\\nas\backups");
            var content = await File.ReadAllBytesAsync(path, Ct);
            content[^1] ^= 0xFF; // blob alterado: DPAPI no lo descifra
            File.Delete(path);
            await File.WriteAllBytesAsync(path, content, Ct);
        }

        using var store = CreateStore();
        var provider = new ShareCredentialProvider(store, _loggerFactory.CreateLogger<ShareCredentialProvider>());

        var ex = await Assert.ThrowsAsync<BackboneException>(() => provider.GetRequiredAsync(@"\\nas\backups", Ct));

        Assert.Equal(BackboneErrorCode.CredentialRequired, ex.Code);
    }

    [Fact]
    public async Task Share_names_read_from_the_destination_are_sanitized_before_logging()
    {
        using var store = CreateStore();
        var provider = new ShareCredentialProvider(store, _loggerFactory.CreateLogger<ShareCredentialProvider>());
        var share = @"\\nas\" + new string('a', 250); // más de 200 caracteres: se recorta (UntrustedText)

        await Assert.ThrowsAsync<BackboneException>(() => provider.GetRequiredAsync(share, Ct));

        var entry = Assert.Single(_logs.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains(UntrustedText.Describe(share), entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(share, entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_destination_that_is_not_UNC_is_a_validation_error()
    {
        using var store = CreateStore();
        var provider = new ShareCredentialProvider(store, _loggerFactory.CreateLogger<ShareCredentialProvider>());

        var ex = await Assert.ThrowsAsync<BackboneException>(() => provider.GetRequiredAsync(@"C:\copias", Ct));

        Assert.Equal(BackboneErrorCode.ValidationFailed, ex.Code);
    }

    private CredentialStore CreateStore() =>
        new(_temp.Combine("credentials"), TestFileTrust.CurrentUser, new DpapiMachineProtector(), TimeProvider.System, _loggerFactory.CreateLogger<CredentialStore>());

    /// <summary>
    /// Doble de la conexión al recurso (HU-05): pide la credencial al proveedor, "conecta" con el usuario y la contraseña con
    /// NUL final (como <c>WNetAddConnection2</c>) y la desecha.
    /// </summary>
    private sealed class FakeShareConnector
    {
        public List<(string ShareRoot, string UserName, string PasswordWithNul)> Connections { get; } = [];

        public bool CredentialDisposed { get; private set; }

        public async Task ConnectAsync(ShareCredentialProvider provider, string destination, CancellationToken ct)
        {
            var credential = await provider.GetRequiredAsync(destination, ct);
            using (credential)
            {
                Connections.Add((credential.ShareRoot, credential.UserName, credential.PasswordWithNullTerminator.ToString()));
            }

            try
            {
                _ = credential.Password.Length;
            }
            catch (ObjectDisposedException)
            {
                CredentialDisposed = true;
            }
        }
    }
}
