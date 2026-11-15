using StreamJsonRpc;

namespace Backbone.Contracts.Tests;

/// <summary>
/// Implementación mínima del contrato para las pruebas de transporte: casi todo responde
/// <see cref="BackboneErrorCode.NotSupported"/>, como hará el servicio con los métodos de fases futuras.
/// </summary>
internal sealed class FakeBackboneService : IBackboneService
{
    public static readonly ServiceInfo Info = new()
    {
        ServiceVersion = "1.0.0",
        ContractVersion = ContractVersion.Value,
        MachineName = "PC-SALON",
        StartedUtc = new DateTimeOffset(2026, 11, 15, 2, 0, 0, TimeSpan.Zero),
        Caller = new CallerInfo { UserName = @"PC-SALON\ana", UserSid = "S-1-5-21-1-2-3-1001" },
    };

    public JobDefinition? LastSavedJob { get; private set; }

    public event EventHandler<StatusChangedEvent>? StatusChanged;
    public event EventHandler<RunProgressEvent>? RunProgress;
    public event EventHandler<RunCompletedEvent>? RunCompleted;
    public event EventHandler<RestoreProgressEvent>? RestoreProgress;
    public event EventHandler<RestoreConflictEvent>? RestoreConflict;

    public void RaiseRunProgress(RunProgressEvent e) => RunProgress?.Invoke(this, e);
    public void RaiseStatusChanged(StatusChangedEvent e) => StatusChanged?.Invoke(this, e);
    public void RaiseRunCompleted(RunCompletedEvent e) => RunCompleted?.Invoke(this, e);
    public void RaiseRestoreProgress(RestoreProgressEvent e) => RestoreProgress?.Invoke(this, e);
    public void RaiseRestoreConflict(RestoreConflictEvent e) => RestoreConflict?.Invoke(this, e);

    // Evita advertencias por eventos no usados; no forman parte de las pruebas.
    public void RaiseOthers()
    {
        StatusChanged?.Invoke(this, new StatusChangedEvent());
        RunCompleted?.Invoke(this, new RunCompletedEvent());
        RestoreProgress?.Invoke(this, new RestoreProgressEvent());
        RestoreConflict?.Invoke(this, new RestoreConflictEvent());
    }

    public Task<ServiceInfo> GetServiceInfoAsync(CancellationToken ct = default) => Task.FromResult(Info);

    public Task<ServiceStatus> GetStatusAsync(CancellationToken ct = default) => Task.FromResult(new ServiceStatus());

    public Task<JobDefinition> SaveJobAsync(JobDefinition job, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(job.Name))
        {
            throw Error(BackboneErrorCode.ValidationFailed, new ValidationResult
            {
                Errors = [new ValidationIssue { Field = "Name", Code = ValidationIssueCode.NameRequired }],
            });
        }

        LastSavedJob = job;
        return Task.FromResult(job);
    }

    public Task<IReadOnlyList<JobDefinition>> GetJobsAsync(CancellationToken ct = default) => throw NotSupported();
    public Task DeleteJobAsync(Guid jobId, CancellationToken ct = default) => throw NotSupported();
    public Task<ValidationResult> ValidateJobAsync(JobDefinition job, CancellationToken ct = default) => throw NotSupported();
    public Task RunNowAsync(Guid jobId, CancellationToken ct = default) => throw NotSupported();
    public Task CancelRunAsync(Guid jobId, CancellationToken ct = default) => throw NotSupported();
    public Task SetPauseAsync(PauseRequest request, CancellationToken ct = default) => throw NotSupported();
    public Task SaveCredentialAsync(string shareRoot, CredentialInput credential, CancellationToken ct = default) => throw NotSupported();
    public Task<CredentialInfo?> GetCredentialInfoAsync(string shareRoot, CancellationToken ct = default) => throw NotSupported();
    public Task DeleteCredentialAsync(string shareRoot, CancellationToken ct = default) => throw NotSupported();
    public Task<ConnectionTestResult> TestConnectionAsync(string destination, CredentialInput? credential, CancellationToken ct = default) => throw NotSupported();
    public Task<StorageInfo> GetStorageInfoAsync(Guid jobId, CancellationToken ct = default) => throw NotSupported();
    public Task<HistoryPage> GetHistoryAsync(HistoryQuery query, CancellationToken ct = default) => throw NotSupported();
    public Task<RunDetail> GetRunDetailAsync(long runId, CancellationToken ct = default) => throw NotSupported();
    public Task<IReadOnlyList<SnapshotInfo>> GetSnapshotsAsync(Guid jobId, CancellationToken ct = default) => throw NotSupported();
    public Task<SnapshotItemPage> BrowseSnapshotAsync(Guid jobId, string snapshotId, string path, int offset, int limit, CancellationToken ct = default) => throw NotSupported();
    public Task<IReadOnlyList<SnapshotItem>> SearchSnapshotAsync(Guid jobId, string snapshotId, string text, int limit, CancellationToken ct = default) => throw NotSupported();
    public Task<IReadOnlyList<FileVersion>> GetFileVersionsAsync(Guid jobId, string path, CancellationToken ct = default) => throw NotSupported();
    public Task<Guid> StartRestoreAsync(RestoreRequest request, CancellationToken ct = default) => throw NotSupported();
    public Task ResolveRestoreConflictAsync(Guid restoreId, ConflictResolution resolution, bool applyToAll, CancellationToken ct = default) => throw NotSupported();
    public Task CancelRestoreAsync(Guid restoreId, CancellationToken ct = default) => throw NotSupported();
    public Task<RetentionResult> ApplyRetentionAsync(Guid jobId, bool dryRun, CancellationToken ct = default) => throw NotSupported();
    public Task StartPruneAsync(Guid jobId, CancellationToken ct = default) => throw NotSupported();
    public Task StartCheckAsync(Guid jobId, bool full, CancellationToken ct = default) => throw NotSupported();
    public Task<ServiceSettings> GetSettingsAsync(CancellationToken ct = default) => throw NotSupported();
    public Task SaveSettingsAsync(ServiceSettings settings, CancellationToken ct = default) => throw NotSupported();
    public Task<IReadOnlyList<AuthorizedUser>> GetAuthorizedUsersAsync(CancellationToken ct = default) => throw NotSupported();
    public Task<AuthorizedUser> AddAuthorizedUserAsync(string account, CancellationToken ct = default) =>
        throw Error(BackboneErrorCode.NotAuthorized, null);
    public Task RemoveAuthorizedUserAsync(string sid, CancellationToken ct = default) => throw NotSupported();
    public Task<DiagnosticsExportResult> ExportDiagnosticsAsync(DiagnosticsExportRequest request, CancellationToken ct = default) => throw NotSupported();

    private static LocalRpcException NotSupported() => Error(BackboneErrorCode.NotSupported, null);

    private static LocalRpcException Error(BackboneErrorCode code, ValidationResult? validation) =>
        new($"Backbone error {code}")
        {
            ErrorCode = BackboneErrors.ToJsonRpcErrorCode(code),
            ErrorData = new BackboneErrorData { Code = code, Message = $"Backbone error {code}", Validation = validation },
        };
}
