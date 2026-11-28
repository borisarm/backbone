using System.ComponentModel;
using Backbone.Contracts;
using Backbone.Core.Engine;
using Backbone.Core.Repository;
using Backbone.Core.Repository.Locking;
using Backbone.Core.Repository.Snapshots;
using Backbone.Core.Storage;
using Backbone.Service.Ipc;
using Backbone.Service.Nas;
using Backbone.Service.Os;
using Backbone.Service.Persistence;
using Backbone.Service.Sources;
using Microsoft.Extensions.Logging;
using ContractRepositoryState = Backbone.Contracts.RepositoryState;
using CoreRepositoryState = Backbone.Core.Repository.RepositoryState;

namespace Backbone.Service.Jobs;

/// <summary>Plazos de la limpieza de <see cref="JobRunner"/> (paso 9 de §8.4).</summary>
internal sealed record JobRunnerOptions
{
    /// <summary>
    /// Lo que se espera a cada paso de la limpieza (soltar el bloqueo del repositorio y cerrar la conexión) antes de seguir con
    /// el siguiente. Junto con la cancelación del motor, que atiende el <see cref="CancellationToken"/> en cada archivo y bloque
    /// de 1 MiB, mantiene "Cancelar" por debajo de 5 s aunque el NAS no responda (HU-14 criterio 2): lo que no termina a tiempo
    /// sigue en segundo plano (el bloqueo caduca solo, §6.4.4, y la conexión se cierra a la fuerza al parar el servicio).
    /// </summary>
    public TimeSpan CleanupStepTimeout { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// Orquesta una ejecución de copia (§8.4, HU-14): validación previa de orígenes y credencial, conexión con el recurso,
/// repositorio, bloqueo compartido, motor y liberación de todo en <c>finally</c>. La cola, <c>RunNow</c> y <c>CancelRun</c>
/// las gestiona <see cref="RunQueue"/>.
/// </summary>
/// <remarks>
/// <list type="number">
/// <item><b>Paso 1</b> (condiciones, HU-09): no es de este tipo. Una ejecución manual no las comprueba (RF-20); el scheduler
/// las comprobará antes de llamar a <see cref="RunQueue.Enqueue"/>.</item>
/// <item><b>Paso 2</b>: repite la resolución controlada de los orígenes (ADR-20) con el mismo token de lectura y el mismo contador
/// de hilos abandonados que la validación, pero sin pasar por su límite de 4 simultáneas; y comprueba que hay credencial
/// guardada (<see cref="BackboneErrorCode.CredentialRequired"/> sin conectar). A la copia en vivo le pasa como
/// <see cref="BackupSource.ReadPath"/> la <b>ruta final</b> de esa resolución, nunca la configurada (§8.3 punto 6).</item>
/// <item><b>Paso 3</b>: conecta por <see cref="ShareConnectionManager"/> con la credencial guardada y examina el repositorio
/// (<see cref="BackupRepository.ProbeAsync"/>); el estado de <see cref="RepositoryStateTracker"/> es orientativo y nunca
/// autoriza escribir (RNF-14): un repositorio de formato superior termina <see cref="BackboneErrorCode.RepositoryIncompatible"/>
/// sin escribir.</item>
/// <item><b>Paso 4</b>: bloqueo compartido.</item>
/// <item><b>Paso 5</b> (VSS, HU-15, RF-22): un único conjunto de instantáneas, una por volumen de origen
/// (<see cref="IVolumeSnapshotter"/>). A cada origen se le pasa como <see cref="BackupSource.ReadPath"/> la ruta de su instantánea
/// calculada a partir de la <b>ruta final</b> de la resolución controlada (<see cref="VssPaths"/>), y la lista de
/// dispositivos creados para <b>esta</b> ejecución a <see cref="ISourceFileSystemFactory"/> (B-2). Si VSS falla, la copia sigue
/// en vivo, termina <see cref="RunState.CompletedWithWarnings"/> (<see cref="RunIssueCode.VssUnavailable"/>) y se registra el
/// evento 4000; una cancelación no es un fallo de VSS.</item>
/// <item><b>Pasos 6 y 7</b>: <see cref="BackupEngine"/> recorre, sube y publica el snapshot.</item>
/// <item><b>Paso 8</b> (retención, F2) y <b>paso 10</b> (historial y Visor de eventos, HU-21 y HU-22): fuera de este tipo; el
/// paso 10 tiene el punto de extensión <see cref="IRunCompletionHandler"/> en <see cref="RunQueue"/>.</item>
/// <item><b>Paso 9</b>: en <c>finally</c>, también al cancelar o fallar: bloqueo, lectura de orígenes, instantáneas VSS y
/// conexión.</item>
/// </list>
/// Una cancelación (<see cref="OperationCanceledException"/> con el token cancelado) termina <see cref="RunState.Cancelled"/>
/// sin snapshot; cualquier otro fallo, <see cref="RunState.Failed"/> con <see cref="RunRecord.ErrorCode"/> según la tabla de
/// §8.4 y sin snapshot. Nunca se registra ni se devuelve la contraseña ni el contenido de un archivo.
/// </remarks>
internal sealed partial class JobRunner
{
    private readonly ShareCredentialProvider _credentials;
    private readonly ShareConnectionManager _connections;
    private readonly IRepositoryStorageFactory _storages;
    private readonly ILocalFileSystemInspector _fileSystem;
    private readonly ProtectedFolderResolver _protectedFolders;
    private readonly SourceValidationRunner _validation;
    private readonly ISourceFileSystemFactory _sourceFactory;
    private readonly IVolumeSnapshotter _snapshotter;
    private readonly IEventLogWriter _eventLog;
    private readonly RepositoryStateTracker _repositoryStates;
    private readonly ServiceRuntimeInfo _runtime;
    private readonly TimeProvider _timeProvider;
    private readonly JobRunnerOptions _options;
    private readonly ILogger<JobRunner> _logger;

    public JobRunner(
        ShareCredentialProvider credentials,
        ShareConnectionManager connections,
        IRepositoryStorageFactory storages,
        ILocalFileSystemInspector fileSystem,
        ProtectedFolderResolver protectedFolders,
        SourceValidationRunner validation,
        ISourceFileSystemFactory sourceFactory,
        IVolumeSnapshotter snapshotter,
        IEventLogWriter eventLog,
        RepositoryStateTracker repositoryStates,
        ServiceRuntimeInfo runtime,
        TimeProvider timeProvider,
        JobRunnerOptions options,
        ILogger<JobRunner> logger)
    {
        _credentials = credentials;
        _connections = connections;
        _storages = storages;
        _fileSystem = fileSystem;
        _protectedFolders = protectedFolders;
        _validation = validation;
        _sourceFactory = sourceFactory;
        _snapshotter = snapshotter;
        _eventLog = eventLog;
        _repositoryStates = repositoryStates;
        _runtime = runtime;
        _timeProvider = timeProvider;
        _options = options;
        _logger = logger;
    }

    /// <summary>Hace la ejecución. No lanza por fallos de la propia ejecución: los devuelve como <see cref="RunOutcome"/>.</summary>
    /// <param name="request">Ejecución.</param>
    /// <param name="ct">Cancelación (el usuario, o la parada del servicio).</param>
    public async Task<RunOutcome> RunAsync(RunRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var run = new Execution(this, request, _timeProvider.GetUtcNow());
        RunOutcome outcome;
        try
        {
            outcome = await run.ExecuteAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ct.IsCancellationRequested
            && (ex is OperationCanceledException || ex is ShareException { Code: BackboneErrorCode.Interrupted }))
        {
            // Una concesión revocada por la parada del servicio (M-A) lanza Interrupted a quien siga con E/S: es la misma parada.
            outcome = run.Cancelled();
        }
        catch (Exception ex)
        {
            outcome = run.Failed(ex);
        }
        finally
        {
            await run.FinishAsync().ConfigureAwait(false);
        }

        return outcome;
    }

    /// <summary>Código de <see cref="RunRecord.ErrorCode"/> de un fallo del motor del repositorio (§8.4).</summary>
    internal static BackboneErrorCode CodeOf(RepositoryErrorKind kind) => kind switch
    {
        RepositoryErrorKind.SnapshotTooLarge => BackboneErrorCode.SnapshotTooLarge,
        RepositoryErrorKind.Corrupt or RepositoryErrorKind.SnapshotNotFound or RepositoryErrorKind.ObjectNotFound => BackboneErrorCode.RepositoryCorrupt,
        RepositoryErrorKind.Incompatible => BackboneErrorCode.RepositoryIncompatible,
        RepositoryErrorKind.Locked => BackboneErrorCode.RepositoryLocked,

        // El latido del bloqueo no ha podido escribirse: casi siempre es la red.
        RepositoryErrorKind.LockLost => BackboneErrorCode.NasUnreachable,
        RepositoryErrorKind.NotARepository or RepositoryErrorKind.NotEmpty => BackboneErrorCode.ValidationFailed,
        _ => BackboneErrorCode.Internal,
    };

    /// <summary>Pasa de un código de aviso del motor (texto) al del contrato.</summary>
    private static RunIssueCode IssueCodeOf(string code) =>
        Enum.TryParse<RunIssueCode>(code, ignoreCase: false, out var parsed) && Enum.IsDefined(parsed) ? parsed : RunIssueCode.Other;

    /// <summary>
    /// Orígenes que recibe el motor (paso 5 de §8.4, HU-15). <paramref name="readPaths"/> son las rutas desde las que se lee:
    /// <b>en vivo</b>, la ruta final de la resolución controlada; con VSS, esa misma ruta final traducida a la de la instantánea
    /// (§8.2 "Raíz del origen en VSS").
    /// </summary>
    private static List<BackupSource> BuildBackupSources(IReadOnlyList<ResolvedSource> sources, IReadOnlyList<string> readPaths) =>
        [.. sources.Select((s, i) => new BackupSource(s.Alias, s.ConfiguredPath, readPaths[i]) { MandatoryExclusions = s.MandatoryExclusions })];

    /// <summary>Posiciones de los orígenes que el paso 2 no pudo resolver: la lectura no abre su raíz nunca (M-B).</summary>
    private static int[] UnreadablePositions(IReadOnlyList<ResolvedSource> sources) =>
        [.. sources.Select((s, i) => (s, i)).Where(t => t.s.Unreadable).Select(t => t.i)];

    /// <summary>
    /// Un origen ya resuelto en el paso 2. Si <paramref name="Unreadable"/>, <paramref name="ReadPath"/> es la ruta
    /// <b>configurada</b> (la resolución falló) y la lectura no la abre nunca. <paramref name="GuidPath"/> es la ruta final en
    /// la forma <c>\\?\Volume{guid}\…</c> de la misma resolución: de ella salen el volumen que se copia con VSS y la ruta
    /// relativa dentro de la instantánea.
    /// </summary>
    private sealed record ResolvedSource(string Alias, string ConfiguredPath, string ReadPath, IReadOnlyList<string> MandatoryExclusions, bool Unreadable = false, string GuidPath = "");

    /// <summary>Resultado de la resolución de un origen en el hilo de lectura.</summary>
    private sealed record SourceResolution(ResolvedSource? Source, string? Rejection);

    /// <summary>Estado de una ejecución en curso y los recursos que el paso 9 tiene que soltar.</summary>
    private sealed class Execution(JobRunner owner, RunRequest request, DateTimeOffset startedUtc)
    {
        private readonly long _startedTimestamp = owner._timeProvider.GetTimestamp();
        private long _copyStartedTimestamp;
        private RunPhase _phase = RunPhase.Starting;

        private ShareLease? _lease;
        private IRunSourceFileSystem? _sourceFileSystem;
        private IVolumeSnapshotSet? _snapshots;
        private RepositoryLock? _lock;
        private bool _cancellationLogged;

        private JobDefinition Job => request.Job;

        public async Task<RunOutcome> ExecuteAsync(CancellationToken ct)
        {
            Report(RunPhase.Starting, 0, 0, null);
            LogStarted(owner._logger, request.RunId, Job.Id, request.Trigger);

            // Paso 2: repetir la validación antes de conectar ni leer nada.
            if (!JobValidator.TryNormalizeDestination(Job.Destination, out var destination, out var destinationIssue))
            {
                throw new BackboneException(BackboneErrorCode.ValidationFailed, $"The destination is not valid ({destinationIssue}).");
            }

            var sources = await ResolveSourcesAsync(ct).ConfigureAwait(false);

            // Sin credencial guardada no se conecta (§7.5 A-3). La ejecución usa la que lee aquí, justo antes de conectar.
            var credential = await owner._credentials.GetRequiredAsync(destination, ct).ConfigureAwait(false);

            // Paso 3.
            Report(RunPhase.Connecting, 0, 0, null);
            _lease = await ConnectAsync(credential, ct).ConfigureAwait(false);
            var connection = _lease.Connection;
            if (!await connection.HasUseRecordAsync(ct).ConfigureAwait(false))
            {
                // §8.2 B-1: sin el registro de uso, la E/S saldría con la cuenta del equipo. No se escribe nada.
                throw new BackboneException(BackboneErrorCode.NasUnreachable, "The connection to the share was lost before using it.");
            }


            // M-A: el almacenamiento queda atado a la concesión; revocada (al soltarla o al cerrar la conexión a la fuerza), toda
            // operación posterior falla sin tocar la red, también las que sigan en segundo plano tras la limpieza.
            var storage = _lease.Protect(owner._storages.Create(connection, destination));
            var repository = await OpenRepositoryAsync(storage, destination, ct).ConfigureAwait(false);

            // Paso 4.
            _lock = await repository.AcquireLockAsync(RepositoryLockType.Shared, owner: null, ct).ConfigureAwait(false);

            // Paso 5: instantáneas VSS (RF-22). Si no se pueden crear, la copia sigue en vivo con un aviso.
            var plan = await PlanSourcesAsync(sources, ct).ConfigureAwait(false);

            // Pasos 6 y 7.
            var backupSources = BuildBackupSources(sources, plan.ReadPaths);
            _sourceFileSystem = owner._sourceFactory.Create(backupSources, snapshotDevices: plan.Devices, unreadableSources: UnreadablePositions(sources));
            _phase = RunPhase.Copying;
            _copyStartedTimestamp = owner._timeProvider.GetTimestamp();
            Report(RunPhase.Copying, 0, 0, null);

            var backup = new BackupRequest
            {
                JobId = Job.Id.ToString("D"),
                JobName = Job.Name,
                Host = owner._runtime.MachineName,
                Sources = backupSources,
                Exclusions = Job.Exclusions,
                UsedVss = plan.UsedVss,
                StartedUtc = startedUtc.UtcDateTime,
                AdditionalWarnings = plan.Warnings,
            };
            var result = await new BackupEngine(owner._timeProvider)
                .RunAsync(repository, _lock, _sourceFileSystem, backup, new SyncProgress(OnCopyProgress), ct)
                .ConfigureAwait(false);

            return Completed(result);
        }

        // ---- Paso 2: orígenes ----

        /// <summary>
        /// Resolución controlada de cada origen (ADR-20, §8.3) en un hilo de lectura propio, sin el límite de las validaciones. Un
        /// origen inválido, no local, que no es NTFS o que cae en una carpeta protegida detiene la ejecución sin leer nada; uno que
        /// no existe o no se puede comprobar se trata como raíz ilegible (el motor lo avisa y conserva lo del padre).
        /// </summary>
        private async Task<IReadOnlyList<ResolvedSource>> ResolveSourcesAsync(CancellationToken ct)
        {
            var configured = new List<(JobSource Source, string Path)>(Job.Sources.Count);
            foreach (var source in Job.Sources)
            {
                switch (SourcePath.TryNormalize(source.Path, out var normalized))
                {
                    case SourcePathCheck.Ok:
                        configured.Add((source, normalized));
                        break;
                    case SourcePathCheck.NotLocal:
                        throw Rejected(source, ValidationIssueCode.SourceNotLocal);
                    default:
                        throw Rejected(source, ValidationIssueCode.SourcePathInvalid);
                }
            }

            var results = new SourceResolution?[configured.Count];
            try
            {
                await owner._validation.RunAsync(
                    guard =>
                    {
                        ProtectedFolderSet? protectedSet = null;
                        for (var k = 0; k < configured.Count; k++)
                        {
                            var (source, path) = configured[k];
                            SourceResolution resolution;
                            try
                            {
                                resolution = Check(source, path, owner._fileSystem.ResolveFolder(path, guard), ref protectedSet, guard);
                            }
                            catch (SourceIoTimeoutException)
                            {
                                // Plazo vencido en este origen: se trata como ilegible (el hilo sigue, no se ha abandonado).
                                resolution = Unreadable(source, path, "the resolution did not complete in time");
                            }

                            Volatile.Write(ref results[k], resolution);
                        }

                        return true;
                    },
                    limited: false,
                    ct).ConfigureAwait(false);
            }
            catch (SourceIoTimeoutException ex) when (ex.Abandoned)
            {
                // La resolución no ha vuelto ni tras cancelarla: su hilo se abandona y cuenta en el contador de todo el servicio
                // (evento 1005 al llegar a 4). La ejecución no puede seguir sin saber qué hay en los orígenes.
                throw new SourceOperationAbandonedException("A source resolution did not return after being cancelled and was abandoned.", ex);
            }

            var resolved = new List<ResolvedSource>(configured.Count);
            for (var k = 0; k < configured.Count; k++)
            {
                var resolution = Volatile.Read(ref results[k]) ?? throw new InvalidOperationException("A source was not resolved.");
                if (resolution.Rejection is { } rejection)
                {
                    throw new BackboneException(BackboneErrorCode.ValidationFailed, rejection);
                }

                resolved.Add(resolution.Source!);
            }

            return resolved;
        }

        private SourceResolution Check(JobSource source, string path, LocalFolderInfo folder, ref ProtectedFolderSet? protectedSet, SourceIoGuard guard)
        {
            switch (folder.Status)
            {
                case LocalFolderStatus.Ok:
                    break;
                case LocalFolderStatus.NotFound or LocalFolderStatus.Unreadable:
                    return Unreadable(source, path, folder.Detail);
                case LocalFolderStatus.NotLocal:
                    return Reject(source, ValidationIssueCode.SourceNotLocal);
                default:
                    return Reject(source, ValidationIssueCode.SourcePathInvalid);
            }

            if (!string.Equals(folder.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase))
            {
                return Reject(source, ValidationIssueCode.SourceNotNtfs);
            }

            // Carpetas protegidas (§8.3): la lista se calcula de nuevo al empezar cada ejecución, una vez.
            protectedSet ??= owner._protectedFolders.Resolve(guard);
            var protection = protectedSet.Classify(folder.GuidPath);
            switch (protection.Kind)
            {
                case SourceProtectionKind.ProtectedFoldersUnknown:
                    return Reject(source, ValidationIssueCode.ProtectedFoldersUnavailable);
                case SourceProtectionKind.InsideProtectedFolder or SourceProtectionKind.ProtectedFoldersIncomplete:
                    return Reject(source, ValidationIssueCode.SourceNotAllowed);
            }

            // A la copia en vivo, la ruta FINAL de la resolución (o la del volumen si no tiene letra), nunca la configurada.
            var readPath = IsDriveLetterPath(folder.FinalPath) ? folder.FinalPath : folder.GuidPath;
            return new SourceResolution(new ResolvedSource(source.Alias, path, readPath, protection.MandatoryExclusions, GuidPath: folder.GuidPath), null);
        }

        /// <summary>
        /// El origen no existe o no se puede comprobar (§8.3 punto 1): se pasa al motor para que lo avise como raíz ilegible y
        /// conserve lo del padre. Es la <b>única</b> vez que se le pasa la ruta configurada, y va marcado como ilegible
        /// (<see cref="ResolvedSource.Unreadable"/>): la lectura de los orígenes (<c>SourceFileSystem.OpenRootAsync</c>) no abre
        /// nunca su raíz, aunque entre la resolución y la apertura pase a ser legible, porque esa ruta no ha pasado por la
        /// comprobación de enlaces, de NTFS ni de carpetas protegidas (M-B de la revisión de seguridad de HU-14).
        /// </summary>
        private SourceResolution Unreadable(JobSource source, string path, string? detail)
        {
            LogSourceUnreadable(owner._logger, UntrustedText.Describe(source.Alias), UntrustedText.Describe(detail));
            return new SourceResolution(new ResolvedSource(source.Alias, path, path, [], Unreadable: true), null);
        }

        private static SourceResolution Reject(JobSource source, ValidationIssueCode code) =>
            new(null, $"The source '{UntrustedText.Describe(source.Alias)}' cannot be backed up ({code}).");

        private static BackboneException Rejected(JobSource source, ValidationIssueCode code) =>
            new(BackboneErrorCode.ValidationFailed, $"The source '{UntrustedText.Describe(source.Alias)}' cannot be backed up ({code}).");

        private static bool IsDriveLetterPath(string path) =>
            path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\';

        // ---- Paso 5: instantáneas VSS ----

        /// <summary>Lo que el paso 5 decide: desde dónde se lee cada origen y qué avisos lleva ya la ejecución.</summary>
        private sealed record SourcePlan(
            IReadOnlyList<string> ReadPaths,
            IReadOnlyCollection<string> Devices,
            bool UsedVss,
            IReadOnlyList<SnapshotWarning> Warnings);

        /// <summary>
        /// Crea <b>un único conjunto</b> de instantáneas con un volumen por cada uno de los orígenes legibles (los de un mismo
        /// volumen comparten instantánea) y traduce el <c>ReadPath</c> de cada uno. Cualquier fallo de VSS (también un conjunto
        /// incompleto o una ruta que no se puede traducir) deja todos los orígenes en vivo, con el aviso
        /// <see cref="RunIssueCode.VssUnavailable"/> y el evento 4000. La cancelación sí se propaga: no es un fallo de VSS.
        /// </summary>
        private async Task<SourcePlan> PlanSourcesAsync(IReadOnlyList<ResolvedSource> sources, CancellationToken ct)
        {
            var live = sources.Select(s => s.ReadPath).ToList();
            var volumes = new List<string>();
            var splits = new (string Volume, string Relative)[sources.Count];
            var wanted = new bool[sources.Count];
            for (var i = 0; i < sources.Count; i++)
            {
                // Un origen que el paso 2 no pudo resolver no se lee nunca: no necesita instantánea.
                if (sources[i].Unreadable)
                {
                    continue;
                }

                if (!VssPaths.TrySplit(sources[i].GuidPath, out var volume, out var relative))
                {
                    return await VssUnavailableAsync(live, "a source path cannot be mapped to a volume", ct).ConfigureAwait(false);
                }

                wanted[i] = true;
                splits[i] = (volume, relative);
                if (!volumes.Contains(volume, StringComparer.OrdinalIgnoreCase))
                {
                    volumes.Add(volume);
                }
            }

            if (volumes.Count == 0)
            {
                return new SourcePlan(live, [], UsedVss: false, []);
            }

            IVolumeSnapshotSet set;
            try
            {
                set = await owner._snapshotter.CreateAsync(volumes, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return await VssUnavailableAsync(live, ex is VolumeSnapshotException ? ex.Message : $"unexpected error ({ex.GetType().Name})", ct).ConfigureAwait(false);
            }

            // Desde aquí la instantánea es de esta ejecución: el paso 9 la elimina aunque algo falle o se cancele.
            _snapshots = set;
            var readPaths = new List<string>(live);
            for (var i = 0; i < sources.Count; i++)
            {
                if (!wanted[i])
                {
                    continue;
                }

                var snapshot = set.Snapshots.FirstOrDefault(s => string.Equals(s.Volume, splits[i].Volume, StringComparison.OrdinalIgnoreCase));
                if (snapshot is null)
                {
                    // Un conjunto incompleto no se mezcla con lectura en vivo: todo en vivo, y lo creado se elimina ahora.
                    return await VssUnavailableAsync(live, "the shadow copy set is incomplete", ct).ConfigureAwait(false);
                }

                readPaths[i] = VssPaths.Combine(snapshot.DevicePath, splits[i].Relative);
            }

            return new SourcePlan(readPaths, [.. set.Snapshots.Select(s => s.DeviceName)], UsedVss: true, []);
        }

        /// <summary>RF-22: sin VSS la copia sigue en vivo, con el aviso <c>VssUnavailable</c> y el evento 4000.</summary>
        private async Task<SourcePlan> VssUnavailableAsync(List<string> live, string reason, CancellationToken ct)
        {
            if (_snapshots is { } partial)
            {
                _snapshots = null;
                try
                {
                    await partial.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogReleaseFailed(owner._logger, "the VSS snapshots", ex.GetType().Name, ex.HResult);
                }
            }

            ct.ThrowIfCancellationRequested();
            LogVssUnavailable(owner._logger, request.RunId, Job.Id, reason);
            owner._eventLog.Write(
                EventLogLevel.Warning,
                BackboneEventIds.VssUnavailable,
                $"VSS is not available for the job '{UntrustedText.Describe(Job.Name)}' ({reason}). The copy continues from the live volumes and files open in other programs may be skipped.");
            return new SourcePlan(
                live,
                [],
                UsedVss: false,
                [new SnapshotWarning(string.Empty, nameof(RunIssueCode.VssUnavailable), "VSS is not available; the sources were read live and files open in other programs may have been skipped.")]);
        }

        // ---- Paso 3: conexión y repositorio ----

        /// <summary>
        /// Conecta con la credencial guardada. <c>WNetAddConnection2</c> no se interrumpe y lee la contraseña mientras dura: al
        /// cancelar, la espera se abandona; la credencial se desecha cuando la llamada vuelve y, si acaba bien, la conexión se
        /// suelta entonces (nunca queda una conexión sin dueño).
        /// </summary>
        private async Task<ShareLease> ConnectAsync(NasCredential credential, CancellationToken ct)
        {
            if (ct.IsCancellationRequested)
            {
                // B-4: ya cancelada, no se empieza una conexión (WNetAddConnection2 no se interrumpe) que habría que deshacer.
                credential.Dispose();
                ct.ThrowIfCancellationRequested();
            }

            var acquire = owner._connections.AcquireAsync(credential, CancellationToken.None);
            _ = acquire.ContinueWith(
                _ => credential.Dispose(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            try
            {
                return await acquire.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _ = acquire.ContinueWith(
                    async completed =>
                    {
                        if (completed.IsCompletedSuccessfully)
                        {
                            var late = await completed.ConfigureAwait(false); // ya terminada: no espera
                            await late.DisposeAsync().ConfigureAwait(false);
                        }
                        else
                        {
                            _ = completed.Exception; // observada: ya no interesa a nadie
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default).Unwrap();
                throw;
            }
            catch (ShareException ex)
            {
                LogConnectFailed(owner._logger, UntrustedText.Describe(Job.Destination), ex.Code, ex.NativeErrorCode);
                throw;
            }
        }

        /// <summary>
        /// Examina el repositorio de nuevo en cada ejecución (<c>RepositoryStateTracker</c> es orientativo y no autoriza
        /// escrituras, RNF-14) y lo abre; si no existe o está vacío, lo inicializa (RF-63).
        /// </summary>
        private async Task<BackupRepository> OpenRepositoryAsync(IRepositoryStorage storage, string destination, CancellationToken ct)
        {
            // Como mucho dos vueltas: si otro proceso inicializa el destino entre el examen y la inicialización, se adopta.
            for (var attempt = 0; ; attempt++)
            {
                var probe = await BackupRepository.ProbeAsync(storage, ct).ConfigureAwait(false);
                switch (probe.State)
                {
                    case CoreRepositoryState.Compatible:
                        var opened = await BackupRepository.OpenAsync(storage, owner._timeProvider, ct).ConfigureAwait(false);
                        owner._repositoryStates.Set(Job.Id, ContractRepositoryState.ReadWrite);
                        return opened;

                    case CoreRepositoryState.Missing or CoreRepositoryState.Empty:
                        try
                        {
                            var created = await BackupRepository.InitializeAsync(
                                storage,
                                new RepositoryInitializationOptions { CreatedBy = $"Backbone {owner._runtime.Version} en {owner._runtime.MachineName}" },
                                owner._timeProvider,
                                ct).ConfigureAwait(false);
                            owner._repositoryStates.Set(Job.Id, ContractRepositoryState.ReadWrite);
                            return created;
                        }
                        catch (RepositoryException ex) when (ex.Kind == RepositoryErrorKind.AlreadyInitialized && attempt == 0)
                        {
                            continue;
                        }

                    case CoreRepositoryState.NotARepository:
                        throw new BackboneException(BackboneErrorCode.ValidationFailed, $"The destination contains files that are not a Backbone repository ({ValidationIssueCode.DestinationNotRepository}).");

                    case CoreRepositoryState.Incompatible:
                        // RNF-14: solo lectura. Se anota para la app, pero lo que impide escribir es este examen, no el estado.
                        owner._repositoryStates.Set(Job.Id, ContractRepositoryState.ReadOnlyNewerFormat);
                        LogNewerFormat(owner._logger, UntrustedText.Describe(destination), probe.FormatVersion);
                        throw new BackboneException(BackboneErrorCode.RepositoryIncompatible, "The repository has a newer format; nothing was written.");

                    default:
                        throw new BackboneException(BackboneErrorCode.RepositoryCorrupt, "The repository is damaged; nothing was written.");
                }
            }
        }

        // ---- Progreso ----

        private void OnCopyProgress(BackupProgress progress)
        {
            var elapsed = owner._timeProvider.GetElapsedTime(_copyStartedTimestamp);
            Report(RunPhase.Copying, progress.FilesProcessed, progress.BytesProcessed, progress.CurrentPath, elapsed);
        }

        private void Report(RunPhase phase, long files, long bytes, string? currentPath, TimeSpan? copyElapsed = null)
        {
            _phase = phase;
            if (request.Report is not { } report)
            {
                return;
            }

            var seconds = copyElapsed?.TotalSeconds ?? 0;
            report(new RunProgressEvent
            {
                RunId = request.RunId,
                JobId = Job.Id,
                Kind = request.Kind,
                Trigger = request.Trigger,
                Phase = phase,
                StartedUtc = startedUtc,
                FilesProcessed = files,
                BytesProcessed = bytes,
                BytesPerSecond = seconds > 0 ? bytes / seconds : 0,
                CurrentPath = currentPath,
                TimestampUtc = owner._timeProvider.GetUtcNow(),
            });
        }

        // ---- Resultados ----

        private RunOutcome Completed(BackupResult result)
        {
            var stats = result.Snapshot.Stats;
            var issues = result.Warnings
                .Select(w => new RunIssue
                {
                    Severity = IssueSeverity.Warning,
                    Code = IssueCodeOf(w.Code),
                    Path = w.Path,
                    Message = w.Message,
                })
                .ToList();
            foreach (var ignored in result.IgnoredParents)
            {
                LogIgnoredParent(owner._logger, UntrustedText.Describe(ignored.SnapshotId), ignored.DateUtc, ignored.LimitUtc);
            }

            // R12: una operación abandonada puede haber dejado la lectura incompleta sin que el motor lo viera.
            if (_sourceFileSystem is { IsAbandoned: true })
            {
                var failure = Failed(new SourceOperationAbandonedException());
                return failure with { Record = failure.Record with { SnapshotId = result.Snapshot.Id } };
            }

            var record = Record(result.State) with
            {
                SnapshotId = result.Snapshot.Id,
                UsedVss = result.Snapshot.UsedVss,
                Stats = new RunStats
                {
                    Files = stats.Files,
                    Bytes = stats.Bytes,
                    NewFiles = stats.NewFiles,
                    ChangedFiles = stats.ChangedFiles,
                    NewObjects = stats.NewObjects,
                    NewBytes = stats.NewBytes,
                    Skipped = stats.Skipped,
                },
                WarningCount = (int)Math.Min(int.MaxValue, result.WarningCount),
            };
            LogCompleted(owner._logger, request.RunId, record.State, stats.Files, stats.NewBytes);
            return new RunOutcome(record, issues);
        }

        public RunOutcome Cancelled()
        {
            if (!_cancellationLogged)
            {
                _cancellationLogged = true;
                LogCancelled(owner._logger, request.RunId, _phase);
            }

            return new RunOutcome(Record(RunState.Cancelled), []);
        }

        public RunOutcome Failed(Exception ex)
        {
            var (code, message) = Translate(ex);
            LogFailed(owner._logger, request.RunId, code, _phase, ex.GetType().Name, UntrustedText.Describe(ex.Message));
            var record = Record(RunState.Failed) with { ErrorCode = code, ErrorMessage = message, ErrorCount = 1 };
            return new RunOutcome(record, [new RunIssue { Severity = IssueSeverity.Error, Code = RunIssueCode.Other, Message = message }]);
        }

        private RunRecord Record(RunState state) => new()
        {
            RunId = request.RunId,
            JobId = Job.Id,
            JobName = Job.Name,
            Kind = request.Kind,
            Trigger = request.Trigger,
            State = state,
            StartedUtc = startedUtc,
            CompletedUtc = owner._timeProvider.GetUtcNow(),
        };

        /// <summary>
        /// Código y detalle técnico (sin secretos ni rutas del usuario) de un fallo, según §8.4: el motor
        /// (<see cref="CodeOf"/>), la conexión (el código de <see cref="ShareException"/>), la E/S del destino
        /// (<see cref="ShareErrors.FromIoException"/>) y, para todo lo demás, <see cref="BackboneErrorCode.Internal"/>.
        /// </summary>
        private static (BackboneErrorCode Code, string Message) Translate(Exception ex) => ex switch
        {
            BackboneException business => (business.Code, business.Message),
            ShareException share => (share.Code, $"The share operation failed (Win32 error {share.NativeErrorCode})."),
            RepositoryException repository => (CodeOf(repository.Kind), $"Repository error: {repository.Kind}."),
            SourceReadingFailedException => (BackboneErrorCode.Internal, "Reading the sources stopped; restart the Backbone service if it persists."),
            IOException or UnauthorizedAccessException when ShareErrors.FromIoException(ex) is { } share =>
                (share.Code, $"I/O error on the destination (Win32 error {share.NativeErrorCode})."),
            Win32Exception => (BackboneErrorCode.Internal, "A system call failed."),
            _ => (BackboneErrorCode.Internal, "Unexpected error."),
        };

        // ---- Paso 9 ----

        /// <summary>
        /// Libera siempre, también tras cancelar o fallar: el bloqueo del repositorio, la lectura de los orígenes (VSS, HU-15, y
        /// la petición de energía, HU-17, se sueltan aquí) y la conexión. Cada paso con su plazo; lo que no vuelve a tiempo sigue
        /// en segundo plano y no detiene a los demás. No lanza.
        /// </summary>
        public async Task FinishAsync()
        {
            Report(RunPhase.Finishing, 0, 0, null);
            if (_lock is { } repositoryLock)
            {
                _lock = null;
                try
                {
                    // B-5: el plazo acota la espera aunque ReleaseAsync no atienda el token (espera al latido, borrado síncrono).
                    // Si no vuelve, sigue en segundo plano; el almacenamiento está atado a la concesión, que se revoca al
                    // soltarla justo después (M-A), así que no vuelve a tocar la red.
                    using var timeout = new CancellationTokenSource(owner._options.CleanupStepTimeout, owner._timeProvider);
                    var release = Observe(repositoryLock.ReleaseAsync(timeout.Token));
                    await release.WaitAsync(owner._options.CleanupStepTimeout, owner._timeProvider).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // El bloqueo caducará solo (§6.4.4); ningún fallo de limpieza detiene la liberación de lo demás.
                    LogReleaseFailed(owner._logger, "the repository lock", ex.GetType().Name, ex.HResult);
                }
            }

            if (_sourceFileSystem is { } sources)
            {
                _sourceFileSystem = null;
                try
                {
                    sources.Dispose();
                }
                catch (Exception ex)
                {
                    LogReleaseFailed(owner._logger, "the source reading", ex.GetType().Name, ex.HResult);
                }
            }

            if (_snapshots is { } snapshots)
            {
                _snapshots = null;
                try
                {
                    // Después de cerrar la lectura (sus handles están en la instantánea). Si no vuelve a tiempo, la eliminación
                    // sigue en el hilo de VSS, queda en data\vss-snapshots.json y la limpia el siguiente arranque si hiciera falta.
                    await Observe(snapshots.DisposeAsync().AsTask()).WaitAsync(owner._options.CleanupStepTimeout, owner._timeProvider).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogReleaseFailed(owner._logger, "the VSS snapshots", ex.GetType().Name, ex.HResult);
                }
            }

            if (_lease is { } lease)
            {
                _lease = null;
                try
                {
                    await Observe(lease.DisposeAsync().AsTask()).WaitAsync(owner._options.CleanupStepTimeout, owner._timeProvider).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogReleaseFailed(owner._logger, "the share connection", ex.GetType().Name, ex.HResult);
                }
            }
        }

        /// <summary>Observa el fallo de una limpieza que se abandona por plazo, para que no quede sin observar.</summary>
        private static Task Observe(Task task)
        {
            _ = task.ContinueWith(
                static t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return task;
        }

        /// <summary><see cref="IProgress{T}"/> síncrono (<see cref="Progress{T}"/> publicaría en otro hilo y desordenaría las fases).</summary>
        private sealed class SyncProgress(Action<BackupProgress> action) : IProgress<BackupProgress>
        {
            public void Report(BackupProgress value) => action(value);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Ejecución {RunId} del trabajo {JobId} iniciada ({Trigger}).")]
    private static partial void LogStarted(ILogger logger, long runId, Guid jobId, RunTrigger trigger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Ejecución {RunId} terminada: {State}, {Files} archivos, {NewBytes} bytes nuevos.")]
    private static partial void LogCompleted(ILogger logger, long runId, RunState state, long files, long newBytes);

    [LoggerMessage(Level = LogLevel.Information, Message = "Ejecución {RunId} cancelada en la fase {Phase}; no se publica ningún snapshot.")]
    private static partial void LogCancelled(ILogger logger, long runId, RunPhase phase);

    // El mensaje de una excepción puede llevar texto del NAS o de los orígenes: solo se registra saneado (UntrustedText), sin traza.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Ejecución {RunId} fallida con {ErrorCode} en la fase {Phase} ({ExceptionType}: {ExceptionMessage}).")]
    private static partial void LogFailed(ILogger logger, long runId, BackboneErrorCode errorCode, RunPhase phase, string exceptionType, string exceptionMessage);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se pudo comprobar el origen {Alias} ({Detail}); se trata como raíz ilegible y se conserva lo del snapshot anterior.")]
    private static partial void LogSourceUnreadable(ILogger logger, string alias, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ejecución {RunId} del trabajo {JobId}: VSS no está disponible ({Reason}); la copia sigue en vivo.")]
    private static partial void LogVssUnavailable(ILogger logger, long runId, Guid jobId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se pudo conectar con el destino {Destination}: {Code} (Win32 {NativeError}).")]
    private static partial void LogConnectFailed(ILogger logger, string destination, BackboneErrorCode code, int nativeError);

    [LoggerMessage(Level = LogLevel.Warning, Message = "El repositorio de {Destination} es de un formato superior (versión {FormatVersion}): solo lectura, la copia no escribe nada.")]
    private static partial void LogNewerFormat(ILogger logger, string destination, int? formatVersion);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Snapshot {SnapshotId} descartado como padre por tener fecha futura ({DateUtc:O} > {LimitUtc:O}).")]
    private static partial void LogIgnoredParent(ILogger logger, string snapshotId, DateTime dateUtc, DateTime limitUtc);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se pudo liberar {What} al terminar la ejecución ({ExceptionType}, HResult {HResult}).")]
    private static partial void LogReleaseFailed(ILogger logger, string what, string exceptionType, int hResult);
}
