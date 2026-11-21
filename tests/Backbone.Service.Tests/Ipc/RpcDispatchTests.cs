using System.Text;
using System.Text.RegularExpressions;
using Backbone.Contracts;
using Backbone.Service.Tests.TestSupport;
using Nerdbank.Streams;
using StreamJsonRpc;

namespace Backbone.Service.Tests.Ipc;

/// <summary>
/// HT-03 / §7.4 / RNF-08: despacho de llamadas, <c>NotSupported</c> de los métodos aún no implementados, validación genérica
/// por IPC y transporte seguro de errores (sin trazas ni <c>CommonErrorData</c>).
/// </summary>
[Trait("Story", "HT-03")]
public sealed partial class RpcDispatchTests : IAsyncLifetime
{
    private readonly IpcTestHarness _server = new();
    private TestConnection _ana = null!; // Asignado en InitializeAsync, antes de cada prueba.

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _server.AuthorizeAsync(TestSids.Ana);
        _ana = await _server.ConnectAuthorizedAsync(TestSids.Identity(TestSids.Ana));
    }

    public ValueTask DisposeAsync() => _server.DisposeAsync();

    public static TheoryData<string> MethodsNotYetImplemented =>
    [
        nameof(IBackboneService.GetStatusAsync), nameof(IBackboneService.GetJobsAsync), nameof(IBackboneService.SaveJobAsync),
        nameof(IBackboneService.DeleteJobAsync), nameof(IBackboneService.ValidateJobAsync), nameof(IBackboneService.RunNowAsync),
        nameof(IBackboneService.CancelRunAsync), nameof(IBackboneService.SetPauseAsync),
        nameof(IBackboneService.GetStorageInfoAsync), nameof(IBackboneService.GetHistoryAsync),
        nameof(IBackboneService.GetRunDetailAsync), nameof(IBackboneService.GetSnapshotsAsync), nameof(IBackboneService.BrowseSnapshotAsync),
        nameof(IBackboneService.SearchSnapshotAsync), nameof(IBackboneService.GetFileVersionsAsync), nameof(IBackboneService.StartRestoreAsync),
        nameof(IBackboneService.ResolveRestoreConflictAsync), nameof(IBackboneService.CancelRestoreAsync),
        nameof(IBackboneService.ApplyRetentionAsync), nameof(IBackboneService.StartPruneAsync), nameof(IBackboneService.StartCheckAsync),
        nameof(IBackboneService.GetSettingsAsync), nameof(IBackboneService.SaveSettingsAsync), nameof(IBackboneService.ExportDiagnosticsAsync),
    ];

    [Theory]
    [MemberData(nameof(MethodsNotYetImplemented))]
    public async Task Methods_of_later_stories_answer_NotSupported_with_valid_input(string method)
    {
        await RpcAssert.FailsWithAsync(BackboneErrorCode.NotSupported, () => ValidCall(_ana.Proxy, method));
    }

    [Fact]
    public async Task Every_contract_method_is_implemented_or_answers_NotSupported()
    {
        var implemented = new[]
        {
            nameof(IBackboneService.GetServiceInfoAsync), nameof(IBackboneService.GetAuthorizedUsersAsync),
            nameof(IBackboneService.AddAuthorizedUserAsync), nameof(IBackboneService.RemoveAuthorizedUserAsync),
            // HU-04.
            nameof(IBackboneService.SaveCredentialAsync), nameof(IBackboneService.GetCredentialInfoAsync),
            nameof(IBackboneService.DeleteCredentialAsync),
            // HU-05.
            nameof(IBackboneService.TestConnectionAsync),
        };
        var all = typeof(IBackboneService).GetMethods().Where(m => !m.IsSpecialName).Select(m => m.Name).ToHashSet();

        Assert.Equal(all.Order(), implemented.Concat(MethodsNotYetImplemented.Select(r => r.Data)).Order());
    }

    [Fact]
    public async Task An_undefined_enum_value_is_rejected_with_ValueOutOfRange()
    {
        var job = new JobDefinition { Name = "Docs", Schedule = new JobSchedule { Kind = (ScheduleKind)99 } };

        await RpcAssert.ValidationFailsAsync("Schedule.Kind", ValidationIssueCode.ValueOutOfRange, () => _ana.Proxy.SaveJobAsync(job, Ct));
    }

    [Fact]
    public async Task Undefined_flag_bits_are_rejected_with_ValueOutOfRange()
    {
        var job = new JobDefinition { Name = "Docs", Schedule = new JobSchedule { Days = (DaysOfWeek)128 } };

        await RpcAssert.ValidationFailsAsync("Schedule.Days", ValidationIssueCode.ValueOutOfRange, () => _ana.Proxy.ValidateJobAsync(job, Ct));
    }

    [Fact]
    public async Task A_name_over_the_limit_is_rejected_with_LimitExceeded()
    {
        var job = new JobDefinition { Name = new string('a', ContractLimits.MaxJobNameLength + 1) };

        await RpcAssert.ValidationFailsAsync("Name", ValidationIssueCode.LimitExceeded, () => _ana.Proxy.SaveJobAsync(job, Ct));
    }

    [Fact]
    public async Task A_null_DTO_argument_is_rejected_with_ValidationFailed()
    {
        await RpcAssert.FailsWithAsync(
            BackboneErrorCode.ValidationFailed,
            () => _ana.Client.Rpc.InvokeWithCancellationAsync<JobDefinition>(nameof(IBackboneService.SaveJobAsync), [null], Ct));
    }

    [Fact]
    public async Task A_page_over_the_limit_is_rejected()
    {
        await RpcAssert.ValidationFailsAsync(
            "Limit", ValidationIssueCode.LimitExceeded,
            () => _ana.Proxy.GetHistoryAsync(new HistoryQuery { Limit = ContractLimits.MaxPageSize + 1 }, Ct));
    }

    [Fact]
    public async Task An_invalid_snapshot_path_is_rejected()
    {
        await RpcAssert.ValidationFailsAsync(
            "path", ValidationIssueCode.SnapshotPathInvalid,
            () => _ana.Proxy.BrowseSnapshotAsync(Guid.NewGuid(), ValidSnapshotId, "Docs/../Windows", 0, 10, Ct));
    }

    [Fact]
    public async Task Validation_errors_never_echo_text_sent_by_the_client()
    {
        var credential = new CredentialInput { UserName = "u", Password = new string('x', ContractLimits.MaxPasswordLength + 1) };

        var data = await RpcAssert.FailsWithAsync(
            BackboneErrorCode.ValidationFailed, () => _ana.Proxy.SaveCredentialAsync(@"\\nas\copias", credential, Ct));

        var issue = Assert.Single(data.Validation!.Errors);
        Assert.Equal(("Password", ValidationIssueCode.LimitExceeded, (string?)null), (issue.Field, issue.Code, issue.Value));
    }

    [Fact]
    public async Task An_unexpected_exception_becomes_Internal_with_a_correlation_id_logged_with_the_exception()
    {
        _server.Accounts.FailLookups(new InvalidOperationException("detalle interno que no debe salir"));

        var data = await RpcAssert.FailsWithAsync(BackboneErrorCode.Internal, () => _ana.Proxy.GetAuthorizedUsersAsync(Ct));

        var match = CorrelationIdRegex().Match(data.Message ?? string.Empty);
        Assert.True(match.Success, data.Message);
        Assert.DoesNotContain("detalle interno", data.Message, StringComparison.Ordinal);
        Assert.Null(data.Validation);
        Assert.Contains(_server.Logs.Entries, e => e.Exception is InvalidOperationException
                                                   && e.Message.Contains(match.Groups[1].Value, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Error_responses_on_the_wire_carry_no_stack_trace_type_names_or_exception_messages()
    {
        _server.Accounts.FailLookups(new InvalidOperationException("detalle interno que no debe salir"));
        var raw = await RawCallAsync(
            """{"jsonrpc":"2.0","id":1,"method":"GetAuthorizedUsersAsync","params":[]}""",
            """{"jsonrpc":"2.0","id":2,"method":"RunNowAsync","params":["no-es-un-guid"]}""",
            """{"jsonrpc":"2.0","id":3,"method":"MetodoQueNoExisteAsync","params":[]}""");

        Assert.Contains("\"code\":1015", raw, StringComparison.Ordinal); // Internal
        Assert.Contains("\"code\":-32602,\"message\":\"Invalid params.\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"code\":-32601,\"message\":\"Method not found.\"", raw, StringComparison.Ordinal);
        foreach (var forbidden in new[]
                 {
                     "detalle interno", "StackTrace", "TypeName", "InvalidOperationException", " at Backbone", "   at ", "\"stack\"",
                     "\"type\"", "Exception", "no-es-un-guid", "MetodoQueNoExisteAsync",
                 })
        {
            Assert.DoesNotContain(forbidden, raw, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Server_error_handler_never_uses_CommonErrorData_for_non_business_exceptions()
    {
        var (serverStream, clientStream) = FullDuplexStream.CreatePair();
        using var serverRpc = new Backbone.Service.Ipc.BackboneServerJsonRpc(
            new HeaderDelimitedMessageHandler(serverStream, new SystemTextJsonFormatter()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        serverRpc.AddLocalRpcTarget(new ThrowingTarget());
        serverRpc.StartListening();
        await using var client = new TestRpcClient(clientStream);

        var data = await RpcAssert.FailsWithAsync(
            BackboneErrorCode.Internal, () => client.Rpc.InvokeWithCancellationAsync(nameof(ThrowingTarget.Boom), [], Ct));

        Assert.Matches(CorrelationIdRegex(), data.Message);
        Assert.DoesNotContain("secreto", data.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Server_error_handler_lets_business_errors_through_with_their_code_and_data()
    {
        await using var client = StartThrowingServer();

        var data = await RpcAssert.FailsWithAsync(
            BackboneErrorCode.NotAuthorized, () => client.Rpc.InvokeWithCancellationAsync(nameof(ThrowingTarget.Business), [], Ct));

        Assert.Equal("Caller is not in the authorized list.", data.Message);
    }

    [Fact]
    public async Task Server_error_handler_turns_foreign_LocalRpcExceptions_into_Internal()
    {
        await using var client = StartThrowingServer();

        var data = await RpcAssert.FailsWithAsync(
            BackboneErrorCode.Internal, () => client.Rpc.InvokeWithCancellationAsync(nameof(ThrowingTarget.Foreign), [], Ct));

        Assert.Matches(CorrelationIdRegex(), data.Message);
        Assert.DoesNotContain("secreto", data.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Business_errors_from_the_real_dispatcher_carry_their_code_on_the_wire()
    {
        var raw = await RawCallAsync("""{"jsonrpc":"2.0","id":1,"method":"GetStatusAsync","params":[]}""");

        Assert.Contains($"\"code\":{BackboneErrors.ToJsonRpcErrorCode(BackboneErrorCode.NotSupported)}", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("\"code\":1015", raw, StringComparison.Ordinal);
    }

    private static TestRpcClient StartThrowingServer()
    {
        var (serverStream, clientStream) = FullDuplexStream.CreatePair();
        var serverRpc = new Backbone.Service.Ipc.BackboneServerJsonRpc(
            new HeaderDelimitedMessageHandler(serverStream, new SystemTextJsonFormatter()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        serverRpc.AddLocalRpcTarget(new ThrowingTarget());
        serverRpc.StartListening();
        _ = serverRpc.Completion.ContinueWith(_ => serverRpc.Dispose(), TaskScheduler.Default);
        return new TestRpcClient(clientStream);
    }

    private const string ValidSnapshotId = "20261115T100000Z-a1b2";

    [GeneratedRegex(@"^Internal error\. Correlation id: ([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$")]
    private static partial Regex CorrelationIdRegex();

    private async Task<string> RawCallAsync(params string[] requests)
    {
        var (serverStream, clientStream) = FullDuplexStream.CreatePair();
        _server.Manager.Accept(new InMemoryChannel(serverStream, () => TestSids.Identity(TestSids.Ana)));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        var responses = new StringBuilder();
        foreach (var request in requests)
        {
            await RawFraming.WriteAsync(clientStream, request, timeout.Token);
            responses.AppendLine(await RawFraming.ReadAsync(clientStream, timeout.Token));
        }

        await clientStream.DisposeAsync();
        return responses.ToString();
    }

    private static Task ValidCall(IBackboneService p, string method)
    {
        var id = Guid.NewGuid();
        return method switch
        {
            nameof(IBackboneService.GetStatusAsync) => p.GetStatusAsync(Ct),
            nameof(IBackboneService.GetJobsAsync) => p.GetJobsAsync(Ct),
            nameof(IBackboneService.SaveJobAsync) => p.SaveJobAsync(JobDefaults.NewJob() with { Name = "Docs" }, Ct),
            nameof(IBackboneService.DeleteJobAsync) => p.DeleteJobAsync(id, Ct),
            nameof(IBackboneService.ValidateJobAsync) => p.ValidateJobAsync(JobDefaults.NewJob(), Ct),
            nameof(IBackboneService.RunNowAsync) => p.RunNowAsync(id, Ct),
            nameof(IBackboneService.CancelRunAsync) => p.CancelRunAsync(id, Ct),
            nameof(IBackboneService.SetPauseAsync) => p.SetPauseAsync(new PauseRequest { Paused = true }, Ct),
            nameof(IBackboneService.SaveCredentialAsync) => p.SaveCredentialAsync(@"\\nas\copias", new CredentialInput { UserName = "u", Password = "p" }, Ct),
            nameof(IBackboneService.GetCredentialInfoAsync) => p.GetCredentialInfoAsync(@"\\nas\copias", Ct),
            nameof(IBackboneService.DeleteCredentialAsync) => p.DeleteCredentialAsync(@"\\nas\copias", Ct),
            nameof(IBackboneService.TestConnectionAsync) => p.TestConnectionAsync(@"\\nas\copias", null, Ct),
            nameof(IBackboneService.GetStorageInfoAsync) => p.GetStorageInfoAsync(id, Ct),
            nameof(IBackboneService.GetHistoryAsync) => p.GetHistoryAsync(new HistoryQuery(), Ct),
            nameof(IBackboneService.GetRunDetailAsync) => p.GetRunDetailAsync(1, Ct),
            nameof(IBackboneService.GetSnapshotsAsync) => p.GetSnapshotsAsync(id, Ct),
            nameof(IBackboneService.BrowseSnapshotAsync) => p.BrowseSnapshotAsync(id, ValidSnapshotId, string.Empty, 0, 100, Ct),
            nameof(IBackboneService.SearchSnapshotAsync) => p.SearchSnapshotAsync(id, ValidSnapshotId, "factura", 100, Ct),
            nameof(IBackboneService.GetFileVersionsAsync) => p.GetFileVersionsAsync(id, "Docs/a.txt", Ct),
            nameof(IBackboneService.StartRestoreAsync) => p.StartRestoreAsync(new RestoreRequest { SnapshotId = ValidSnapshotId, Paths = ["Docs"] }, Ct),
            nameof(IBackboneService.ResolveRestoreConflictAsync) => p.ResolveRestoreConflictAsync(id, ConflictResolution.Skip, false, Ct),
            nameof(IBackboneService.CancelRestoreAsync) => p.CancelRestoreAsync(id, Ct),
            nameof(IBackboneService.ApplyRetentionAsync) => p.ApplyRetentionAsync(id, true, Ct),
            nameof(IBackboneService.StartPruneAsync) => p.StartPruneAsync(id, Ct),
            nameof(IBackboneService.StartCheckAsync) => p.StartCheckAsync(id, false, Ct),
            nameof(IBackboneService.GetSettingsAsync) => p.GetSettingsAsync(Ct),
            nameof(IBackboneService.SaveSettingsAsync) => p.SaveSettingsAsync(new ServiceSettings(), Ct),
            nameof(IBackboneService.ExportDiagnosticsAsync) => p.ExportDiagnosticsAsync(new DiagnosticsExportRequest { TargetPath = @"C:\Temp\d.zip" }, Ct),
            _ => throw new ArgumentOutOfRangeException(nameof(method), method, null),
        };
    }

    private sealed class ThrowingTarget
    {
#pragma warning disable CA1822 // Método de instancia: StreamJsonRpc solo expone miembros de instancia del objetivo.
        public void Boom() => throw new InvalidOperationException("secreto interno");

        public void Business() => throw Backbone.Service.Ipc.ConnectionRpcTarget.ToRpcException(
            Backbone.Service.Ipc.BackboneException.NotAuthorized("Caller is not in the authorized list."));

        public void Foreign() => throw new LocalRpcException("secreto interno") { ErrorCode = -32050, ErrorData = "secreto interno" };
#pragma warning restore CA1822
    }
}
