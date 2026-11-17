using Backbone.Contracts;
using Backbone.Service.Ipc;
using Backbone.Service.Nas;
using Backbone.Service.Os;
using Backbone.Service.Persistence;
using Backbone.Service.Tests.Ipc;
using Backbone.Service.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Backbone.Service.Tests.Nas;

/// <summary>
/// HU-05 (RF-61): "Probar conexión" con un doble del recurso SMB (<see cref="FakeShareConnector"/>) y reloj simulado.
/// </summary>
[Trait("Story", "HU-05")]
public sealed class ConnectionTesterTests : IDisposable
{
    private const string Password = "Contraseña-HU05-7a1c";
    private const string Share = @"\\nas\backups";

    private readonly TempDirectory _temp = new();
    private readonly CapturingLoggerProvider _logs = new();
    private readonly ILoggerFactory _loggerFactory;
    private readonly CredentialStore _store;
    private readonly FakeShareConnector _connector = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 17, 9, 0, 0, TimeSpan.Zero));
    private readonly ConnectionTestOptions _options = new();
    private readonly ConnectionTester _tester;

    public ConnectionTesterTests()
    {
        _loggerFactory = LoggerFactory.Create(b => b.AddProvider(_logs).SetMinimumLevel(LogLevel.Trace));
        Directory.CreateDirectory(_temp.Combine("credentials"));
        _store = new CredentialStore(_temp.Combine("credentials"), TestFileTrust.CurrentUser, new DpapiMachineProtector(), _time, _loggerFactory.CreateLogger<CredentialStore>());
        _tester = new ConnectionTester(
            _connector,
            new ShareCredentialProvider(_store, _loggerFactory.CreateLogger<ShareCredentialProvider>()),
            _time,
            _options,
            _loggerFactory.CreateLogger<ConnectionTester>());
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string User = "usuario-centinela-hu05";

    private static CredentialInput Ana => new() { UserName = User, Password = Password };

    public void Dispose()
    {
        _store.Dispose();
        _loggerFactory.Dispose();
        _temp.Dispose();
    }

    // ---- Criterio 1: datos correctos ----

    [Fact]
    public async Task With_correct_data_it_succeeds_with_free_and_total_space_and_the_SMB_dialect()
    {
        var result = await _tester.TestAsync(@"\\NAS\Backups", Ana, Ct);

        Assert.Equal(
            new ConnectionTestResult
            {
                Success = true,
                ShareRoot = Share,
                CanRead = true,
                CanWrite = true,
                CanDelete = true,
                FreeBytes = _connector.Share.Space.FreeBytes,
                TotalBytes = _connector.Share.Space.TotalBytes,
                SmbDialect = "3.1.1",
            },
            result);
        Assert.Equal((Share, User, Password + "\0"), Assert.Single(_connector.Connections));
        await AssertCleanAsync();
    }

    [Fact]
    public async Task Space_and_dialect_are_optional_and_their_failure_does_not_fail_the_test()
    {
        _connector.Share.SpaceError = new ShareException(BackboneErrorCode.AccessDenied, 5);
        _connector.Share.DialectError = new ShareException(BackboneErrorCode.AccessDenied, 50);

        var result = await _tester.TestAsync(Share, Ana, Ct);

        Assert.True(result.Success);
        Assert.Null(result.FreeBytes);
        Assert.Null(result.TotalBytes);
        Assert.Null(result.SmbDialect);
        await AssertCleanAsync();
    }

    [Fact]
    public async Task The_dialect_reported_by_the_share_is_sanitized()
    {
        _connector.Share.Dialect = "3.1.1\u202E\n" + new string('x', 300);

        var result = await _tester.TestAsync(Share, Ana, Ct);

        Assert.Equal(UntrustedText.Describe(_connector.Share.Dialect), result.SmbDialect);
        Assert.DoesNotContain(_logs.Entries, e => e.Message.Contains('\u202E', StringComparison.Ordinal));
    }

    // ---- Criterio 2: contraseña errónea ----

    [Fact]
    public async Task A_wrong_password_is_CredentialsRejected()
    {
        _connector.ConnectError = new ShareException(BackboneErrorCode.CredentialsRejected, ShareErrors.ErrorLogonFailure);

        var result = await _tester.TestAsync(Share, Ana with { Password = "mala" }, Ct);

        AssertFailure(result, BackboneErrorCode.CredentialsRejected, canRead: false);
        await AssertCleanAsync();
        Assert.Contains(_logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("1326", StringComparison.Ordinal));
    }

    // ---- Criterio 3: servidor apagado en menos de 15 s ----

    [Fact]
    public async Task An_unreachable_server_reported_by_the_system_is_NasUnreachable()
    {
        _connector.ConnectError = new ShareException(BackboneErrorCode.NasUnreachable, ShareErrors.ErrorBadNetPath);

        var result = await _tester.TestAsync(Share, Ana, Ct);

        AssertFailure(result, BackboneErrorCode.NasUnreachable, canRead: false);
        await AssertCleanAsync();
    }

    [Fact]
    public void The_whole_test_answers_in_less_than_15_seconds_by_default()
    {
        var defaults = new ConnectionTestOptions();

        Assert.True(defaults.Timeout + defaults.MinimumCleanupWait < TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task A_server_that_does_not_answer_is_NasUnreachable_in_less_than_15_seconds()
    {
        var gate = _connector.ConnectGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = _time.GetUtcNow();

        var result = await AdvanceUntilDoneAsync(_tester.TestAsync(Share, Ana, Ct));

        Assert.True(_time.GetUtcNow() - started < TimeSpan.FromSeconds(15), $"Respondió a los {_time.GetUtcNow() - started}.");
        AssertFailure(result, BackboneErrorCode.NasUnreachable, canRead: false);

        // La llamada al sistema sigue en curso: la contraseña no se pone a cero mientras la pueda leer.
        var credential = Assert.IsType<NasCredential>(_connector.LastCredential);
        Assert.False(FakeShareConnector.IsDisposed(credential));

        // Cuando por fin conecta, la conexión se cierra y la credencial se desecha.
        gate.SetResult();
        await _tester.WhenCleanupsCompleteAsync().WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.True(_connector.PasswordReadableAtEndOfConnect);
        Assert.True(FakeShareConnector.IsDisposed(credential));
        Assert.Single(_connector.Created);
        Assert.Equal(0, _connector.OpenConnections);
    }

    [Fact]
    public async Task A_share_that_stops_answering_after_connecting_is_NasUnreachable_in_time_and_closed_afterwards()
    {
        var gate = _connector.Share.Gate("List");
        var started = _time.GetUtcNow();

        var result = await AdvanceUntilDoneAsync(_tester.TestAsync(Share, Ana, Ct));

        Assert.True(_time.GetUtcNow() - started < TimeSpan.FromSeconds(15));
        Assert.Equal(BackboneErrorCode.NasUnreachable, result.ErrorCode);
        var connection = Assert.Single(_connector.Created);
        Assert.False(connection.IsDisposed); // no se cierra con la lectura aún en curso

        gate.SetResult();
        await AssertCleanAsync();
        Assert.False(connection.DisposedWithOperationsInFlight);
    }

    // ---- Criterio 4: recurso inexistente y sin permiso de escritura ----

    [Fact]
    public async Task A_missing_share_is_ShareNotFound()
    {
        _connector.ConnectError = new ShareException(BackboneErrorCode.ShareNotFound, ShareErrors.ErrorBadNetName);

        var result = await _tester.TestAsync(@"\\nas\no-existe", Ana, Ct);

        AssertFailure(result, BackboneErrorCode.ShareNotFound, canRead: false);
        Assert.Equal(@"\\nas\no-existe", result.ShareRoot);
        await AssertCleanAsync();
    }

    [Fact]
    public async Task Without_write_permission_it_is_AccessDenied_after_reading_and_leaves_no_file()
    {
        _connector.Share.WriteError = new ShareException(BackboneErrorCode.AccessDenied, ShareErrors.ErrorAccessDenied);

        var result = await _tester.TestAsync(Share, Ana, Ct);

        AssertFailure(result, BackboneErrorCode.AccessDenied, canRead: true);
        Assert.NotNull(result.FreeBytes); // lo que se pudo obtener se informa igualmente
        Assert.Equal("3.1.1", result.SmbDialect);
        await AssertCleanAsync();
    }

    [Fact]
    public async Task Without_read_permission_it_is_AccessDenied_and_nothing_is_written()
    {
        _connector.Share.ListError = new ShareException(BackboneErrorCode.AccessDenied, ShareErrors.ErrorAccessDenied);

        var result = await _tester.TestAsync(Share, Ana, Ct);

        AssertFailure(result, BackboneErrorCode.AccessDenied, canRead: false);
        Assert.Empty(_connector.Share.ProbeFiles);
        await AssertCleanAsync();
    }

    [Fact]
    public async Task A_share_denied_at_connection_is_AccessDenied()
    {
        _connector.ConnectError = new ShareException(BackboneErrorCode.AccessDenied, ShareErrors.ErrorAccessDenied);

        var result = await _tester.TestAsync(Share, Ana, Ct);

        AssertFailure(result, BackboneErrorCode.AccessDenied, canRead: false);
    }

    [Fact]
    public async Task Each_failure_has_its_own_error_code()
    {
        var codes = new List<BackboneErrorCode?>();
        foreach (var (error, native) in new[]
                 {
                     (BackboneErrorCode.CredentialsRejected, ShareErrors.ErrorLogonFailure),
                     (BackboneErrorCode.NasUnreachable, ShareErrors.ErrorBadNetPath),
                     (BackboneErrorCode.ShareNotFound, ShareErrors.ErrorBadNetName),
                 })
        {
            _connector.ConnectError = new ShareException(error, native);
            codes.Add((await _tester.TestAsync(Share, Ana, Ct)).ErrorCode);
        }

        _connector.ConnectError = null;
        _connector.Share.WriteError = new ShareException(BackboneErrorCode.AccessDenied, ShareErrors.ErrorAccessDenied);
        codes.Add((await _tester.TestAsync(Share, Ana, Ct)).ErrorCode);

        Assert.Equal(
            [BackboneErrorCode.CredentialsRejected, BackboneErrorCode.NasUnreachable, BackboneErrorCode.ShareNotFound, BackboneErrorCode.AccessDenied],
            codes);
    }

    // ---- Criterio 5: no deja archivos ----

    [Fact]
    public async Task The_probe_file_is_written_in_the_destination_folder_when_it_exists()
    {
        _connector.Share.Directories[@"\\nas\backups\pc-salon"] = 0;

        await _tester.TestAsync(@"\\nas\backups\pc-salon", Ana, Ct);

        var probe = Assert.Single(_connector.Share.ProbeFiles);
        Assert.StartsWith(@"\\nas\backups\pc-salon\" + ConnectionTester.ProbeFilePrefix, probe, StringComparison.Ordinal);
        await AssertCleanAsync();
    }

    [Fact]
    public async Task The_probe_file_goes_to_the_deepest_existing_folder_when_the_destination_does_not_exist_yet()
    {
        _connector.Share.Directories[@"\\nas\backups\equipos"] = 0;

        var result = await _tester.TestAsync(@"\\nas\backups\equipos\pc-salon\nuevo", Ana, Ct);

        Assert.True(result.Success);
        var probe = Assert.Single(_connector.Share.ProbeFiles);
        Assert.Equal(@"\\nas\backups\equipos", Path.GetDirectoryName(probe));
        await AssertCleanAsync();
    }

    [Fact]
    public async Task If_the_server_ignores_delete_on_close_the_probe_file_is_deleted_explicitly()
    {
        _connector.Share.IgnoresDeleteOnClose = true;

        var result = await _tester.TestAsync(Share, Ana, Ct);

        Assert.True(result.Success);
        Assert.True(result.CanDelete);
        Assert.Contains(_logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains(ConnectionTester.ProbeFilePrefix, StringComparison.Ordinal));
        await AssertCleanAsync();
    }

    [Fact]
    public async Task Without_delete_permission_it_is_AccessDenied_with_CanDelete_false()
    {
        _connector.Share.IgnoresDeleteOnClose = true;
        _connector.Share.DeleteError = new ShareException(BackboneErrorCode.AccessDenied, ShareErrors.ErrorAccessDenied);

        var result = await _tester.TestAsync(Share, Ana, Ct);

        Assert.False(result.Success);
        Assert.Equal(BackboneErrorCode.AccessDenied, result.ErrorCode);
        Assert.Equal((true, true, false), (result.CanRead, result.CanWrite, result.CanDelete));

        // Se reintenta al limpiar y, si tampoco se puede, se registra como error (el archivo queda: no hay permiso).
        await _tester.WhenCleanupsCompleteAsync().WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Contains(_logs.Entries, e => e.Level == LogLevel.Error && e.Message.Contains(ConnectionTester.ProbeFilePrefix, StringComparison.Ordinal));
        Assert.Equal(0, _connector.OpenConnections);
    }

    [Fact]
    public async Task If_the_share_stops_answering_while_writing_the_probe_file_is_removed_when_the_write_ends()
    {
        _connector.Share.IgnoresDeleteOnClose = true;
        var gate = _connector.Share.Gate("Write");

        var result = await AdvanceUntilDoneAsync(_tester.TestAsync(Share, Ana, Ct));

        Assert.Equal(BackboneErrorCode.NasUnreachable, result.ErrorCode);
        Assert.False(result.CanWrite);
        Assert.Single(_connector.Share.Files); // la escritura sigue en curso

        gate.SetResult();
        await AssertCleanAsync();
        Assert.False(Assert.Single(_connector.Created).DisposedWithOperationsInFlight);
    }

    [Fact]
    public async Task If_the_client_cancels_while_writing_the_probe_file_is_still_removed()
    {
        _connector.Share.IgnoresDeleteOnClose = true;
        var gate = _connector.Share.Gate("Write");
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var test = _tester.TestAsync(Share, Ana, cancel.Token);
        await WaitUntilAsync(() => !_connector.Share.Files.IsEmpty);
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => test);
        Assert.Single(_connector.Share.Files);

        gate.SetResult();
        await AssertCleanAsync();
        Assert.True(FakeShareConnector.IsDisposed(Assert.IsType<NasCredential>(_connector.LastCredential)));
    }

    [Fact]
    public async Task If_the_client_cancels_while_connecting_the_late_connection_is_closed()
    {
        var gate = _connector.ConnectGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var test = _tester.TestAsync(Share, Ana, cancel.Token);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => test);

        gate.SetResult();
        await AssertCleanAsync();
        Assert.Single(_connector.Created);
    }

    // ---- Credencial ----

    [Fact]
    public async Task Without_a_credential_it_uses_the_saved_one()
    {
        await _store.SaveAsync(Share, new CredentialInput { UserName = "luis", Password = Password }, Ct);

        var result = await _tester.TestAsync(@"\\nas\backups\pc", null, Ct);

        Assert.True(result.Success);
        Assert.Equal((Share, "luis", Password + "\0"), Assert.Single(_connector.Connections));
        await AssertCleanAsync();
    }

    [Fact]
    public async Task Without_a_credential_or_a_saved_one_it_is_CredentialRequired_without_connecting()
    {
        var result = await _tester.TestAsync(Share, null, Ct);

        Assert.Equal(new ConnectionTestResult { ShareRoot = Share, ErrorCode = BackboneErrorCode.CredentialRequired }, result);
        Assert.Empty(_connector.Connections);
    }

    [Fact]
    public async Task The_password_is_zeroed_after_the_test_and_never_logged()
    {
        _connector.Share.WriteError = new ShareException(BackboneErrorCode.AccessDenied, 5);

        await _tester.TestAsync(Share, Ana, Ct);

        Assert.True(FakeShareConnector.IsDisposed(Assert.IsType<NasCredential>(_connector.LastCredential)));
        SecretSearch.AssertNotInLogs(Password, _logs);
        Assert.DoesNotContain(_logs.Entries, e => e.Message.Contains(User, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("", "x", "UserName")]
    [InlineData("  ", "x", "UserName")]
    [InlineData("ana\n", "x", "UserName")]
    [InlineData("ana", "a\0b", "Password")]
    public async Task An_invalid_credential_is_ValidationFailed_without_connecting(string user, string password, string field)
    {
        var ex = await Assert.ThrowsAsync<BackboneException>(
            () => _tester.TestAsync(Share, new CredentialInput { UserName = user, Password = password }, Ct));

        Assert.Equal(BackboneErrorCode.ValidationFailed, ex.Code);
        Assert.Equal(field, Assert.Single(ex.Validation!.Errors).Field);
        Assert.Empty(_connector.Connections);
    }

    [Fact]
    public async Task A_password_with_a_lone_surrogate_is_ValidationFailed_without_connecting()
    {
        // Se construye en código: xUnit no conserva intactas las suplentes sueltas de InlineData.
        var password = "a" + (char)0xD800 + "b";

        var ex = await Assert.ThrowsAsync<BackboneException>(
            () => _tester.TestAsync(Share, new CredentialInput { UserName = User, Password = password }, Ct));

        Assert.Equal("Password", Assert.Single(ex.Validation!.Errors).Field);
        Assert.Empty(_connector.Connections);
    }

    [Theory]
    [InlineData(@"C:\copias", ValidationIssueCode.DestinationNotUnc)]
    [InlineData(@"\\localhost\copias", ValidationIssueCode.DestinationNotAllowed)]
    [InlineData(@"\\nas\C$", ValidationIssueCode.DestinationNotAllowed)]
    public async Task An_invalid_destination_is_ValidationFailed_without_connecting(string destination, ValidationIssueCode code)
    {
        var ex = await Assert.ThrowsAsync<BackboneException>(() => _tester.TestAsync(destination, Ana, Ct));

        Assert.Equal(BackboneErrorCode.ValidationFailed, ex.Code);
        Assert.Equal(code, Assert.Single(ex.Validation!.Errors).Code);
        Assert.Empty(_connector.Connections);
    }

    // ---- Textos no confiables ----

    [Fact]
    public async Task The_share_name_is_sanitized_before_logging()
    {
        var share = @"\\nas\" + new string('a', 250);
        _connector.ConnectError = new ShareException(BackboneErrorCode.ShareNotFound, ShareErrors.ErrorBadNetName);

        var result = await _tester.TestAsync(share, Ana, Ct);

        Assert.Equal(share, result.ShareRoot); // la raíz normalizada que envió el cliente, para guardar la credencial
        var entry = Assert.Single(_logs.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains(UntrustedText.Describe(share), entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(share, entry.Message, StringComparison.Ordinal);
    }

    // ---- Utilidades ----

    private static void AssertFailure(ConnectionTestResult result, BackboneErrorCode code, bool canRead)
    {
        Assert.False(result.Success);
        Assert.Equal(code, result.ErrorCode);
        Assert.Equal(canRead, result.CanRead);
        Assert.False(result.CanWrite);
        Assert.False(result.CanDelete);
    }

    /// <summary>Sin archivos en el recurso, sin conexiones abiertas y con la credencial desechada.</summary>
    private async Task AssertCleanAsync()
    {
        await _tester.WhenCleanupsCompleteAsync().WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Empty(_connector.Share.Files);
        Assert.Equal(0, _connector.OpenConnections);
        Assert.All(_connector.Created, c => Assert.False(c.DisposedWithOperationsInFlight));
        if (_connector.LastCredential is { } credential)
        {
            Assert.True(FakeShareConnector.IsDisposed(credential));
        }
    }

    /// <summary>Avanza el reloj simulado de 500 en 500 ms hasta que la prueba responde.</summary>
    private async Task<ConnectionTestResult> AdvanceUntilDoneAsync(Task<ConnectionTestResult> test)
    {
        for (var i = 0; i < 120 && !test.IsCompleted; i++)
        {
            await Task.Delay(5, Ct);
            if (!test.IsCompleted)
            {
                _time.Advance(TimeSpan.FromMilliseconds(500));
            }
        }

        return await test.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 1000 && !condition(); i++)
        {
            await Task.Delay(5, Ct);
        }

        Assert.True(condition());
    }
}
