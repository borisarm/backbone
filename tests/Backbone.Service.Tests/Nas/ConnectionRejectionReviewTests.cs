using Backbone.Contracts;
using Backbone.Service.Nas;
using Backbone.Service.Os;
using Backbone.Service.Persistence;
using Backbone.Service.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Backbone.Service.Tests.Nas;

/// <summary>
/// Revisión de seguridad de HU-14 sobre el límite de rechazos por SID de "Probar conexión" (M-1 de HU-05): B-1 (el contador solo
/// se reinicia con un éxito contra el mismo servidor; <c>1219</c> no cuenta) y B-2 (guardar y probar sin credencial explícita
/// cuenta igual).
/// </summary>
[Trait("Story", "HU-14")]
public sealed class ConnectionRejectionReviewTests : IDisposable
{
    private const string ShareA = @"\\nas\backups";
    private const string ShareB = @"\\otro\copias";

    private readonly TempDirectory _temp = new();
    private readonly ILoggerFactory _loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 22, 9, 0, 0, TimeSpan.Zero));
    private readonly FakeShareConnector _connector = new();
    private readonly CredentialStore _store;
    private readonly ConnectionTester _tester;

    public ConnectionRejectionReviewTests()
    {
        Directory.CreateDirectory(_temp.Combine("credentials"));
        _store = new CredentialStore(_temp.Combine("credentials"), TestFileTrust.CurrentUser, new DpapiMachineProtector(), _time, _loggerFactory.CreateLogger<CredentialStore>());
        _tester = new ConnectionTester(
            new ShareConnectionManager(_connector, _loggerFactory.CreateLogger<ShareConnectionManager>()),
            new FakeHostResolver(),
            new ShareCredentialProvider(_store, _loggerFactory.CreateLogger<ShareCredentialProvider>()),
            _time,
            new ConnectionTestOptions(),
            _loggerFactory.CreateLogger<ConnectionTester>());
    }

    public void Dispose()
    {
        _tester.Dispose();
        _store.Dispose();
        _loggerFactory.Dispose();
        _temp.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CredentialInput Wrong => new() { UserName = "ana", Password = "mala" };

    private static CredentialInput Right => new() { UserName = "ana", Password = "buena" };

    private void Reject(int win32 = ShareErrors.ErrorLogonFailure) =>
        _connector.ConnectError = new ShareException(BackboneErrorCode.CredentialsRejected, win32);

    private void Accept() => _connector.ConnectError = null;

    private async Task<BackboneErrorCode?> TestAsync(string share, string sid, CredentialInput? credential) =>
        (await _tester.TestAsync(share, credential, sid, Ct)).ErrorCode;

    private async Task RejectAsync(string share, string sid, int count, CredentialInput? credential)
    {
        Reject();
        for (var i = 0; i < count; i++)
        {
            Assert.Equal(BackboneErrorCode.CredentialsRejected, await TestAsync(share, sid, credential));
        }
    }

    // ---- B-1 ----

    [Fact]
    public async Task A_success_against_another_server_does_not_reset_the_rejections_against_this_one()
    {
        // Alternar 4 fallos contra A con un éxito contra B no reinicia el contador de A.
        await RejectAsync(ShareA, TestSids.Ana, 4, Wrong);
        Accept();
        Assert.Null(await TestAsync(ShareB, TestSids.Ana, Right));

        Reject();
        Assert.Equal(BackboneErrorCode.CredentialsRejected, await TestAsync(ShareA, TestSids.Ana, Wrong)); // el quinto contra A
        Accept();

        Assert.Equal(BackboneErrorCode.NasBusy, await TestAsync(ShareA, TestSids.Ana, Right));
        Assert.Equal(BackboneErrorCode.NasBusy, await TestAsync(ShareB, TestSids.Ana, Right)); // el bloqueo es del SID
    }

    [Fact]
    public async Task A_success_against_the_same_server_still_resets_its_count()
    {
        await RejectAsync(ShareA, TestSids.Ana, 4, Wrong);
        Accept();
        Assert.Null(await TestAsync(ShareA, TestSids.Ana, Right));

        await RejectAsync(ShareA, TestSids.Ana, 4, Wrong);
        Accept();

        Assert.Null(await TestAsync(ShareA, TestSids.Ana, Right)); // 4 + éxito + 4: nunca 5 seguidos contra A
    }

    [Fact]
    public async Task Rejections_against_different_servers_add_up_for_the_same_user()
    {
        await RejectAsync(ShareA, TestSids.Ana, 3, Wrong);
        await RejectAsync(ShareB, TestSids.Ana, 2, Wrong);
        Accept();

        Assert.Equal(BackboneErrorCode.NasBusy, await TestAsync(ShareA, TestSids.Ana, Right));
    }

    [Fact]
    public async Task ERROR_SESSION_CREDENTIAL_CONFLICT_1219_is_a_rejection_in_the_result_but_does_not_count()
    {
        Reject(ShareErrors.ErrorSessionCredentialConflict);
        for (var i = 0; i < 12; i++)
        {
            Assert.Equal(BackboneErrorCode.CredentialsRejected, await TestAsync(ShareA, TestSids.Ana, Wrong));
        }

        Accept();
        Assert.Null(await TestAsync(ShareA, TestSids.Ana, Right)); // nunca bloqueado

        // Y no suma a los rechazos reales: 4 reales + 1219 + 1 real = 5 reales.
        await RejectAsync(ShareA, TestSids.Ana, 4, Wrong);
        Reject(ShareErrors.ErrorSessionCredentialConflict);
        Assert.Equal(BackboneErrorCode.CredentialsRejected, await TestAsync(ShareA, TestSids.Ana, Wrong));
        Accept();
        Assert.Null(await TestAsync(ShareA, TestSids.Ana, Right)); // 4 reales: aún no bloqueado
    }

    // ---- B-2 ----

    [Fact]
    public async Task Rejections_of_the_saved_credential_count_when_the_same_user_saved_it_in_the_last_5_minutes()
    {
        await _store.SaveAsync(ShareA, Wrong, Ct);
        _tester.NoteCredentialSaved(TestSids.Ana, ShareA);
        Reject();
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(BackboneErrorCode.CredentialsRejected, await TestAsync(ShareA, TestSids.Ana, credential: null));
        }

        var connections = _connector.Connections.Count;
        Accept();

        Assert.Equal(BackboneErrorCode.NasBusy, await TestAsync(ShareA, TestSids.Ana, credential: null)); // sin conectar
        Assert.Equal(BackboneErrorCode.NasBusy, await TestAsync(ShareA, TestSids.Ana, Right)); // el contador es el mismo
        Assert.Equal(connections, _connector.Connections.Count);
    }

    [Fact]
    public async Task Rejections_of_a_saved_credential_do_not_count_after_the_5_minute_window_or_for_another_user()
    {
        await _store.SaveAsync(ShareA, Wrong, Ct);
        _tester.NoteCredentialSaved(TestSids.Ana, ShareA);
        Reject();

        // Otro usuario (que no ha guardado nada) prueba la credencial guardada: no cuenta.
        for (var i = 0; i < 8; i++)
        {
            Assert.Equal(BackboneErrorCode.CredentialsRejected, await TestAsync(ShareA, TestSids.Luis, credential: null));
        }

        Accept();
        Assert.Null(await TestAsync(ShareA, TestSids.Luis, Right));

        // Pasados 5 min desde el guardado, probar la guardada es lo normal (credencial vieja que ha cambiado en el NAS).
        _time.Advance(TimeSpan.FromMinutes(5));
        Reject();
        for (var i = 0; i < 8; i++)
        {
            Assert.Equal(BackboneErrorCode.CredentialsRejected, await TestAsync(ShareA, TestSids.Ana, credential: null));
        }

        Accept();
        Assert.Null(await TestAsync(ShareA, TestSids.Ana, credential: null));
    }

    [Fact]
    public async Task Counting_the_saved_credential_is_per_share()
    {
        await _store.SaveAsync(ShareA, Wrong, Ct);
        await _store.SaveAsync(ShareB, Wrong, Ct);
        _tester.NoteCredentialSaved(TestSids.Ana, ShareA); // solo lo de A se ha guardado "ahora"

        Reject();
        for (var i = 0; i < 8; i++)
        {
            Assert.Equal(BackboneErrorCode.CredentialsRejected, await TestAsync(ShareB, TestSids.Ana, credential: null));
        }

        Accept();
        Assert.Null(await TestAsync(ShareB, TestSids.Ana, credential: null)); // B no cuenta
        await RejectAsync(ShareA, TestSids.Ana, 5, credential: null);
        Accept();

        Assert.Equal(BackboneErrorCode.NasBusy, await TestAsync(ShareA, TestSids.Ana, credential: null));
    }
}
