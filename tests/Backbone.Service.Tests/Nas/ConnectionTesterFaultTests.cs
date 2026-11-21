using Backbone.Contracts;
using Backbone.Service.Nas;
using Backbone.Service.Os;
using Backbone.Service.Persistence;
using Backbone.Service.Tests.Ipc;
using Backbone.Service.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Backbone.Service.Tests.Nas;

/// <summary>
/// HU-05 (QA): inyección de fallos de "Probar conexión": corte a mitad de la escritura, cancelación en cada paso, conexión
/// que llega tarde, borrado y cierre que fallan, excepciones no previstas y nombres Unicode o muy largos.
/// </summary>
[Trait("Story", "HU-05")]
public sealed class ConnectionTesterFaultTests : IDisposable
{
    private const string Share = @"\\nas\backups";

    private readonly TempDirectory _temp = new();
    private readonly CapturingLoggerProvider _logs = new();
    private readonly ILoggerFactory _loggerFactory;
    private readonly CredentialStore _store;
    private readonly FakeShareConnector _connector = new();
    private readonly FakeHostResolver _resolver = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 18, 9, 0, 0, TimeSpan.Zero));
    private readonly ConnectionTester _tester;

    public ConnectionTesterFaultTests()
    {
        _loggerFactory = LoggerFactory.Create(b => b.AddProvider(_logs).SetMinimumLevel(LogLevel.Trace));
        Directory.CreateDirectory(_temp.Combine("credentials"));
        _store = new CredentialStore(_temp.Combine("credentials"), TestFileTrust.CurrentUser, new DpapiMachineProtector(), _time, _loggerFactory.CreateLogger<CredentialStore>());
        _tester = new ConnectionTester(
            new ShareConnectionManager(_connector, _loggerFactory.CreateLogger<ShareConnectionManager>()),
            _resolver,
            new ShareCredentialProvider(_store, _loggerFactory.CreateLogger<ShareCredentialProvider>()),
            _time,
            new ConnectionTestOptions(),
            _loggerFactory.CreateLogger<ConnectionTester>());
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CredentialInput Ana => new() { UserName = "ana", Password = "Contraseña-QA-HU05-c41d" };

    public void Dispose()
    {
        _store.Dispose();
        _loggerFactory.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public async Task A_network_cut_in_the_middle_of_writing_leaves_no_file_and_reports_the_cause()
    {
        _connector.Share.IgnoresDeleteOnClose = true; // el servidor no llega a borrar al cerrar
        _connector.Share.WriteErrorAfterCreate = new ShareException(BackboneErrorCode.NasUnreachable, ShareErrors.ErrorNetNameDeleted);

        var result = await _tester.TestAsync(Share, Ana, Ct);

        Assert.False(result.Success);
        Assert.Equal(BackboneErrorCode.NasUnreachable, result.ErrorCode);
        Assert.Equal((true, false, false), (result.CanRead, result.CanWrite, result.CanDelete));
        await AssertCleanAsync();
    }

    [Fact]
    public async Task An_already_canceled_token_throws_and_leaves_nothing_open()
    {
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _tester.TestAsync(Share, Ana, cancel.Token));

        await AssertCleanAsync();
    }

    [Theory]
    [InlineData("List")]
    [InlineData("Space")]
    [InlineData("Dialect")]
    [InlineData("FileExists")]
    public async Task Canceling_while_an_operation_is_pending_throws_and_closes_the_connection_when_it_ends(string operation)
    {
        _connector.Share.IgnoresDeleteOnClose = true;
        var gate = _connector.Share.Gate(operation);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var test = _tester.TestAsync(Share, Ana, cancel.Token);
        await WaitUntilAsync(() => !_connector.Created.IsEmpty);
        await Task.Delay(50, Ct);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => test);

        gate.SetResult();
        await AssertCleanAsync();
    }

    [Fact]
    public async Task A_late_connection_that_finally_fails_is_observed_and_leaves_nothing()
    {
        var gate = _connector.ConnectGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _connector.ConnectError = new ShareException(BackboneErrorCode.CredentialsRejected, ShareErrors.ErrorLogonFailure);

        var result = await AdvanceUntilDoneAsync(_tester.TestAsync(Share, Ana, Ct));
        Assert.Equal(BackboneErrorCode.NasUnreachable, result.ErrorCode); // al plazo se responde con lo que se sabe

        gate.SetResult();
        await AssertCleanAsync();
        Assert.True(_connector.Created.IsEmpty);
    }

    [Fact]
    public async Task A_late_connection_that_arrives_when_the_test_already_answered_is_closed_without_writing()
    {
        var gate = _connector.ConnectGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await AdvanceUntilDoneAsync(_tester.TestAsync(Share, Ana, Ct));
        gate.SetResult();
        await AssertCleanAsync();

        Assert.True(_connector.Share.ProbeFiles.IsEmpty);
        Assert.Single(_connector.Created);
    }

    [Fact]
    public async Task A_delete_that_hangs_is_NasUnreachable_in_time_and_the_file_is_removed_afterwards()
    {
        _connector.Share.IgnoresDeleteOnClose = true;
        var gate = _connector.Share.Gate("Delete");

        var started = _time.GetUtcNow();
        var result = await AdvanceUntilDoneAsync(_tester.TestAsync(Share, Ana, Ct));

        Assert.True(_time.GetUtcNow() - started < TimeSpan.FromSeconds(15));
        Assert.Equal(BackboneErrorCode.NasUnreachable, result.ErrorCode);
        Assert.True(result.CanWrite);
        Assert.False(result.CanDelete);

        gate.SetResult();
        await AssertCleanAsync();
    }

    [Fact]
    public async Task If_closing_the_connection_fails_the_result_is_still_returned_and_the_failure_logged()
    {
        _connector.Share.DisposeError = new IOException("boom-texto-del-servidor");

        var result = await _tester.TestAsync(Share, Ana, Ct);

        Assert.True(result.Success);
        await _tester.WhenCleanupsCompleteAsync().WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Contains(_logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("cerrar la conexión", StringComparison.Ordinal) && e.Message.Contains("IOException", StringComparison.Ordinal));
        Assert.Empty(_connector.Share.Files);

        // Informativa de la revisión: solo el tipo y el HResult, nunca el mensaje ni la excepción entera.
        Assert.DoesNotContain("boom-texto-del-servidor", _logs.AllText(), StringComparison.Ordinal);
        Assert.All(_logs.Entries, e => Assert.Null(e.Exception));
    }

    [Fact]
    public async Task An_unexpected_exception_from_the_share_is_a_result_and_does_not_leave_files_or_connections()
    {
        _connector.Share.UnexpectedListError = new InvalidOperationException("inesperado");

        // Revisión de seguridad (informativa): un error no previsto de la E/S es AccessDenied, no Internal.
        var result = await _tester.TestAsync(Share, Ana, Ct);

        Assert.Equal(BackboneErrorCode.AccessDenied, result.ErrorCode);
        Assert.False(result.Success);
        Assert.DoesNotContain("inesperado", _logs.AllText(), StringComparison.Ordinal);
        await AssertCleanAsync();
    }

    [Fact]
    public async Task Failed_runs_do_not_accumulate_pending_cleanups()
    {
        _connector.Share.WriteError = new ShareException(BackboneErrorCode.AccessDenied, ShareErrors.ErrorAccessDenied);

        for (var i = 0; i < 20; i++)
        {
            await _tester.TestAsync(Share, Ana, Ct);
        }

        await AssertCleanAsync();
        Assert.Equal(20, _connector.Created.Count);
        Assert.All(_connector.Created, c => Assert.True(c.IsDisposed));
    }

    [Fact]
    public async Task Unicode_and_very_long_destinations_put_the_probe_in_the_deepest_existing_folder()
    {
        var existing = @"\\nas\backups\Ñandú-日本語";
        _connector.Share.Directories[existing] = 0;
        var destination = existing + @"\" + new string('d', 100) + @"\" + new string('e', 100) + @"\" + new string('f', 100);

        var result = await _tester.TestAsync(destination, Ana, Ct);

        Assert.True(result.Success);
        Assert.Equal(existing, Path.GetDirectoryName(Assert.Single(_connector.Share.ProbeFiles)));
        await AssertCleanAsync();
    }

    [Fact]
    public async Task Concurrent_tests_use_distinct_probe_files_and_leave_nothing()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => _tester.TestAsync(Share, Ana, Ct)));

        Assert.All(results, r => Assert.True(r.Success));
        Assert.Equal(8, _connector.Share.ProbeFiles.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        await AssertCleanAsync();
    }

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
