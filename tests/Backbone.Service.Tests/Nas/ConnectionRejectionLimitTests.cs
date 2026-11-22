using Backbone.Contracts;
using Backbone.Service.Nas;
using Backbone.Service.Os;
using Backbone.Service.Persistence;
using Backbone.Service.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Backbone.Service.Tests.Nas;

/// <summary>
/// HU-14, requisito previo (revisión final de seguridad de HU-05, M-1): tras 5 <see cref="BackboneErrorCode.CredentialsRejected"/>
/// seguidos de un mismo SID en "Probar conexión", sus pruebas con credencial explícita responden
/// <see cref="BackboneErrorCode.NasBusy"/> sin conectar durante 5 min; una prueba correcta pone el contador a cero.
/// </summary>
[Trait("Story", "HU-14")]
public sealed class ConnectionRejectionLimitTests : IDisposable
{
    private const string Share = @"\\nas\backups";

    private readonly TempDirectory _temp = new();
    private readonly ILoggerFactory _loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 22, 9, 0, 0, TimeSpan.Zero));
    private readonly FakeShareConnector _connector = new();
    private readonly CredentialStore _store;
    private readonly ConnectionTester _tester;

    public ConnectionRejectionLimitTests()
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

    private void Reject() => _connector.ConnectError = new ShareException(BackboneErrorCode.CredentialsRejected, ShareErrors.ErrorLogonFailure);

    private void Accept() => _connector.ConnectError = null;

    private async Task<BackboneErrorCode?> TestAsync(string sid, CredentialInput? credential) =>
        (await _tester.TestAsync(Share, credential, sid, Ct)).ErrorCode;

    private async Task RejectManyAsync(string sid, int count)
    {
        Reject();
        for (var i = 0; i < count; i++)
        {
            Assert.Equal(BackboneErrorCode.CredentialsRejected, await TestAsync(sid, Wrong));
        }
    }

    [Fact]
    public async Task After_five_rejections_in_a_row_the_sixth_test_of_that_user_is_NasBusy_without_connecting_whatever_the_credential()
    {
        await RejectManyAsync(TestSids.Ana, 5);
        Assert.Equal(5, _connector.Connections.Count);
        Accept();

        Assert.Equal(BackboneErrorCode.NasBusy, await TestAsync(TestSids.Ana, Wrong));
        Assert.Equal(BackboneErrorCode.NasBusy, await TestAsync(TestSids.Ana, Right)); // la misma respuesta con la contraseña correcta

        Assert.Equal(5, _connector.Connections.Count); // sin conectar
    }

    [Fact]
    public async Task The_block_is_per_user_and_does_not_affect_the_saved_credential()
    {
        await RejectManyAsync(TestSids.Ana, 5);
        Accept();
        await _store.SaveAsync(Share, Right, Ct);

        Assert.Equal(BackboneErrorCode.NasBusy, await TestAsync(TestSids.Ana, Right));
        Assert.Null(await TestAsync(TestSids.Luis, Right)); // otro usuario
        Assert.Null(await TestAsync(TestSids.Ana, credential: null)); // la credencial guardada no se bloquea
    }

    [Fact]
    public async Task The_block_lasts_five_minutes_and_then_the_count_starts_again()
    {
        await RejectManyAsync(TestSids.Ana, 5);
        Accept();

        _time.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
        Assert.Equal(BackboneErrorCode.NasBusy, await TestAsync(TestSids.Ana, Right));

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(await TestAsync(TestSids.Ana, Right));

        // Contador a cero: hacen falta otros 5 rechazos para volver a bloquear.
        await RejectManyAsync(TestSids.Ana, 4);
        Accept();
        Assert.Null(await TestAsync(TestSids.Ana, Right));
    }

    [Fact]
    public async Task A_successful_test_resets_the_count()
    {
        await RejectManyAsync(TestSids.Ana, 4);
        Accept();
        Assert.Null(await TestAsync(TestSids.Ana, Right));

        await RejectManyAsync(TestSids.Ana, 4);
        Accept();

        Assert.Null(await TestAsync(TestSids.Ana, Right)); // 4 + éxito + 4: nunca 5 seguidos
    }

    [Fact]
    public async Task Failures_that_say_nothing_about_the_credential_do_not_count()
    {
        _connector.ConnectError = new ShareException(BackboneErrorCode.NasUnreachable, ShareErrors.ErrorBadNetPath);
        for (var i = 0; i < 8; i++)
        {
            Assert.Equal(BackboneErrorCode.NasUnreachable, await TestAsync(TestSids.Ana, Wrong));
        }

        Accept();
        Assert.Null(await TestAsync(TestSids.Ana, Right));
    }
}
