using Backbone.Contracts;
using Microsoft.Extensions.Logging;
using StreamJsonRpc;

namespace Backbone.Service.Ipc;

/// <summary>
/// Objetivo RPC de una conexión: el contrato <see cref="IBackboneService"/> tal como lo ve un cliente concreto.
/// </summary>
/// <remarks>
/// Cada método, en este orden:
/// <list type="number">
/// <item>autoriza la llamada contra la lista y la pertenencia actuales (§7.3) y audita las escrituras;</item>
/// <item>valida la entrada genérica (<see cref="IpcInputValidator"/>: nulos, límites, enumerados, paginación, rutas, fechas
/// a UTC);</item>
/// <item>ejecuta la operación o responde <see cref="BackboneErrorCode.NotSupported"/> si su historia aún no está hecha.</item>
/// </list>
/// Los errores de negocio salen como <see cref="LocalRpcException"/> con <see cref="BackboneErrorData"/>; cualquier otra
/// excepción, como <see cref="BackboneErrorCode.Internal"/> con un identificador de correlación (§7.4).
/// Los eventos no se emiten desde aquí: los envía <see cref="IpcConnection.Notify"/>.
/// </remarks>
internal sealed partial class ConnectionRpcTarget(IpcConnection connection, BackboneOperations operations) : IBackboneService
{
    private const bool Read = false;
    private const bool Write = true;

    // Eventos del contrato: StreamJsonRpc no los usa (NotifyClientOfEvents = false); se envían con IpcConnection.Notify.
    public event EventHandler<StatusChangedEvent>? StatusChanged { add { } remove { } }

    public event EventHandler<RunProgressEvent>? RunProgress { add { } remove { } }

    public event EventHandler<RunCompletedEvent>? RunCompleted { add { } remove { } }

    public event EventHandler<RestoreProgressEvent>? RestoreProgress { add { } remove { } }

    public event EventHandler<RestoreConflictEvent>? RestoreConflict { add { } remove { } }

    // ---- Información y estado ----

    public Task<ServiceInfo> GetServiceInfoAsync(CancellationToken ct = default) =>
        InvokeAsync(nameof(GetServiceInfoAsync), Read, caller => operations.GetServiceInfoAsync(caller, ct));

    public Task<ServiceStatus> GetStatusAsync(CancellationToken ct = default) =>
        InvokeAsync<ServiceStatus>(nameof(GetStatusAsync), Read, _ => throw NotYet(nameof(GetStatusAsync)));

    // ---- Trabajos (HU-04) ----

    public Task<IReadOnlyList<JobDefinition>> GetJobsAsync(CancellationToken ct = default) =>
        InvokeAsync<IReadOnlyList<JobDefinition>>(nameof(GetJobsAsync), Read, _ => throw NotYet(nameof(GetJobsAsync)));

    public Task<JobDefinition> SaveJobAsync(JobDefinition job, CancellationToken ct = default) =>
        InvokeAsync<JobDefinition>(nameof(SaveJobAsync), Write, _ =>
        {
            IpcInputValidator.Job(job);
            throw NotYet(nameof(SaveJobAsync));
        });

    public Task DeleteJobAsync(Guid jobId, CancellationToken ct = default) =>
        InvokeAsync(nameof(DeleteJobAsync), Write, _ => throw NotYet(nameof(DeleteJobAsync)));

    public Task<ValidationResult> ValidateJobAsync(JobDefinition job, CancellationToken ct = default) =>
        InvokeAsync<ValidationResult>(nameof(ValidateJobAsync), Read, _ =>
        {
            IpcInputValidator.Job(job);
            throw NotYet(nameof(ValidateJobAsync));
        });

    // ---- Ejecución ----

    public Task RunNowAsync(Guid jobId, CancellationToken ct = default) =>
        InvokeAsync(nameof(RunNowAsync), Write, _ => throw NotYet(nameof(RunNowAsync)));

    public Task CancelRunAsync(Guid jobId, CancellationToken ct = default) =>
        InvokeAsync(nameof(CancelRunAsync), Write, _ => throw NotYet(nameof(CancelRunAsync)));

    public Task SetPauseAsync(PauseRequest request, CancellationToken ct = default) =>
        InvokeAsync(nameof(SetPauseAsync), Write, _ =>
        {
            IpcInputValidator.Pause(request);
            throw NotYet(nameof(SetPauseAsync));
        });

    // ---- NAS y credenciales ----

    public Task SaveCredentialAsync(string shareRoot, CredentialInput credential, CancellationToken ct = default) =>
        InvokeAsync(nameof(SaveCredentialAsync), Write, _ =>
            operations.SaveCredentialAsync(IpcInputValidator.Destination(shareRoot), IpcInputValidator.Credential(credential), ct));

    public Task<CredentialInfo?> GetCredentialInfoAsync(string shareRoot, CancellationToken ct = default) =>
        InvokeAsync(nameof(GetCredentialInfoAsync), Read, _ =>
            operations.GetCredentialInfoAsync(IpcInputValidator.Destination(shareRoot), ct));

    public Task DeleteCredentialAsync(string shareRoot, CancellationToken ct = default) =>
        InvokeAsync(nameof(DeleteCredentialAsync), Write, _ =>
            operations.DeleteCredentialAsync(IpcInputValidator.Destination(shareRoot), ct));

    public Task<ConnectionTestResult> TestConnectionAsync(string destination, CredentialInput? credential, CancellationToken ct = default) =>
        InvokeAsync(nameof(TestConnectionAsync), Write, _ =>
            operations.TestConnectionAsync(IpcInputValidator.Destination(destination), IpcInputValidator.OptionalCredential(credential), ct));

    public Task<StorageInfo> GetStorageInfoAsync(Guid jobId, CancellationToken ct = default) =>
        InvokeAsync<StorageInfo>(nameof(GetStorageInfoAsync), Read, _ => throw NotYet(nameof(GetStorageInfoAsync)));

    // ---- Historial ----

    public Task<HistoryPage> GetHistoryAsync(HistoryQuery query, CancellationToken ct = default) =>
        InvokeAsync<HistoryPage>(nameof(GetHistoryAsync), Read, _ =>
        {
            IpcInputValidator.History(query);
            throw NotYet(nameof(GetHistoryAsync));
        });

    public Task<RunDetail> GetRunDetailAsync(long runId, CancellationToken ct = default) =>
        InvokeAsync<RunDetail>(nameof(GetRunDetailAsync), Read, _ => throw NotYet(nameof(GetRunDetailAsync)));

    // ---- Restauración ----

    public Task<IReadOnlyList<SnapshotInfo>> GetSnapshotsAsync(Guid jobId, CancellationToken ct = default) =>
        InvokeAsync<IReadOnlyList<SnapshotInfo>>(nameof(GetSnapshotsAsync), Read, _ => throw NotYet(nameof(GetSnapshotsAsync)));

    public Task<SnapshotItemPage> BrowseSnapshotAsync(Guid jobId, string snapshotId, string path, int offset, int limit, CancellationToken ct = default) =>
        InvokeAsync<SnapshotItemPage>(nameof(BrowseSnapshotAsync), Read, _ =>
        {
            IpcInputValidator.Browse(snapshotId, path, offset, limit);
            throw NotYet(nameof(BrowseSnapshotAsync));
        });

    public Task<IReadOnlyList<SnapshotItem>> SearchSnapshotAsync(Guid jobId, string snapshotId, string text, int limit, CancellationToken ct = default) =>
        InvokeAsync<IReadOnlyList<SnapshotItem>>(nameof(SearchSnapshotAsync), Read, _ =>
        {
            IpcInputValidator.Search(snapshotId, text, limit);
            throw NotYet(nameof(SearchSnapshotAsync));
        });

    public Task<IReadOnlyList<FileVersion>> GetFileVersionsAsync(Guid jobId, string path, CancellationToken ct = default) =>
        InvokeAsync<IReadOnlyList<FileVersion>>(nameof(GetFileVersionsAsync), Read, _ =>
        {
            IpcInputValidator.SnapshotFilePath(path);
            throw NotYet(nameof(GetFileVersionsAsync));
        });

    public Task<Guid> StartRestoreAsync(RestoreRequest request, CancellationToken ct = default) =>
        InvokeAsync<Guid>(nameof(StartRestoreAsync), Write, _ =>
        {
            IpcInputValidator.Restore(request);
            throw NotYet(nameof(StartRestoreAsync));
        });

    public Task ResolveRestoreConflictAsync(Guid restoreId, ConflictResolution resolution, bool applyToAll, CancellationToken ct = default) =>
        InvokeAsync(nameof(ResolveRestoreConflictAsync), Write, _ =>
        {
            IpcInputValidator.ConflictResolution(resolution);
            throw NotYet(nameof(ResolveRestoreConflictAsync));
        });

    public Task CancelRestoreAsync(Guid restoreId, CancellationToken ct = default) =>
        InvokeAsync(nameof(CancelRestoreAsync), Write, _ => throw NotYet(nameof(CancelRestoreAsync)));

    // ---- Mantenimiento (F2) ----

    public Task<RetentionResult> ApplyRetentionAsync(Guid jobId, bool dryRun, CancellationToken ct = default) =>
        InvokeAsync<RetentionResult>(nameof(ApplyRetentionAsync), Write, _ => throw NotYet(nameof(ApplyRetentionAsync)));

    public Task StartPruneAsync(Guid jobId, CancellationToken ct = default) =>
        InvokeAsync(nameof(StartPruneAsync), Write, _ => throw NotYet(nameof(StartPruneAsync)));

    public Task StartCheckAsync(Guid jobId, bool full, CancellationToken ct = default) =>
        InvokeAsync(nameof(StartCheckAsync), Write, _ => throw NotYet(nameof(StartCheckAsync)));

    // ---- Ajustes y usuarios autorizados ----

    public Task<ServiceSettings> GetSettingsAsync(CancellationToken ct = default) =>
        InvokeAsync<ServiceSettings>(nameof(GetSettingsAsync), Read, _ => throw NotYet(nameof(GetSettingsAsync)));

    public Task SaveSettingsAsync(ServiceSettings settings, CancellationToken ct = default) =>
        InvokeAsync(nameof(SaveSettingsAsync), Write, _ =>
        {
            IpcInputValidator.Settings(settings);
            throw NotYet(nameof(SaveSettingsAsync));
        });

    public Task<IReadOnlyList<AuthorizedUser>> GetAuthorizedUsersAsync(CancellationToken ct = default) =>
        InvokeAsync(nameof(GetAuthorizedUsersAsync), Read, _ => operations.GetAuthorizedUsersAsync(ct));

    public Task<AuthorizedUser> AddAuthorizedUserAsync(string account, CancellationToken ct = default) =>
        InvokeAsync(nameof(AddAuthorizedUserAsync), Write, caller =>
            operations.AddAuthorizedUserAsync(caller, IpcInputValidator.Account(account), ct));

    public Task RemoveAuthorizedUserAsync(string sid, CancellationToken ct = default) =>
        InvokeAsync(nameof(RemoveAuthorizedUserAsync), Write, caller =>
            operations.RemoveAuthorizedUserAsync(caller, IpcInputValidator.Account(sid), ct));

    public Task<DiagnosticsExportResult> ExportDiagnosticsAsync(DiagnosticsExportRequest request, CancellationToken ct = default) =>
        InvokeAsync<DiagnosticsExportResult>(nameof(ExportDiagnosticsAsync), Write, _ =>
        {
            IpcInputValidator.Diagnostics(request);
            throw NotYet(nameof(ExportDiagnosticsAsync));
        });

    // ---- Infraestructura ----

    private static BackboneException NotYet(string method) => BackboneException.NotSupported(method);

    private Task<object?> InvokeAsync(string method, bool isWrite, Func<CallerContext, Task> body) =>
        InvokeAsync<object?>(method, isWrite, async caller =>
        {
            await body(caller).ConfigureAwait(false);
            return null;
        });

    private async Task<T> InvokeAsync<T>(string method, bool isWrite, Func<CallerContext, Task<T>> body)
    {
        try
        {
            var caller = connection.Authorize(method, isWrite);
            return await body(caller).ConfigureAwait(false);
        }
        catch (BackboneException ex)
        {
            throw ToRpcException(ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not LocalRpcException)
        {
            var correlationId = Guid.NewGuid();
            LogInternalError(connection.Logger, ex, method, correlationId);
            var data = BackboneServerJsonRpc.InternalErrorData(correlationId);
            throw new LocalRpcException(data.Message)
            {
                ErrorCode = BackboneErrors.ToJsonRpcErrorCode(BackboneErrorCode.Internal),
                ErrorData = data,
            };
        }
    }

    internal static LocalRpcException ToRpcException(BackboneException ex) =>
        new(ex.Message)
        {
            ErrorCode = BackboneErrors.ToJsonRpcErrorCode(ex.Code),
            ErrorData = new BackboneErrorData { Code = ex.Code, Message = ex.Message, Validation = ex.Validation },
        };

    [LoggerMessage(Level = LogLevel.Error, Message = "Error interno en la llamada IPC {Method}. Id. de correlación {CorrelationId}.")]
    private static partial void LogInternalError(ILogger logger, Exception exception, string method, Guid correlationId);
}
