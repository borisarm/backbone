namespace Backbone.Contracts;

/// <summary>
/// Contrato IPC entre la app y el servicio (§7.2), expuesto con StreamJsonRpc sobre el pipe <see cref="BackboneIpc.PipeName"/>.
/// </summary>
/// <remarks>
/// <para>Errores de negocio: <c>RemoteInvocationException</c> con <c>ErrorCode</c> = <see cref="BackboneErrorCode"/>
/// y <c>error.data</c> = <see cref="BackboneErrorData"/> (§7.4). Toda entrada se valida en el servicio (RNF-08).</para>
/// <para>Los métodos marcados <b>F2</b> o <b>F3</b> forman parte del contrato desde la versión 1.0; mientras su fase no
/// esté implementada, el servicio responde con <see cref="BackboneErrorCode.NotSupported"/>.</para>
/// <para>Las operaciones de escritura se registran en el log de auditoría con el SID del usuario que las pide, sin argumentos (§7.3).</para>
/// <para>Entradas: el servicio valida límites (<see cref="ContractLimits"/>), rutas (<see cref="SnapshotPaths"/>,
/// <see cref="UncPaths"/>), rechaza valores de enumerado no definidos y normaliza las fechas a UTC (§7.5).</para>
/// </remarks>
public interface IBackboneService
{
    // ---- Información y estado ----

    /// <summary>Versión del servicio y del contrato, y autorizaciones del llamante.</summary>
    /// <param name="ct">Cancelación.</param>
    /// <returns>Información del servicio.</returns>
    Task<ServiceInfo> GetServiceInfoAsync(CancellationToken ct = default);

    /// <summary>Estado global: salud, pausa, ejecución actual, cola y estado de cada trabajo.</summary>
    /// <param name="ct">Cancelación.</param>
    /// <returns>Estado actual.</returns>
    Task<ServiceStatus> GetStatusAsync(CancellationToken ct = default);

    // ---- Trabajos ----

    /// <summary>Lista de trabajos.</summary>
    /// <param name="ct">Cancelación.</param>
    /// <returns>Los trabajos configurados.</returns>
    Task<IReadOnlyList<JobDefinition>> GetJobsAsync(CancellationToken ct = default);

    /// <summary>Crea (si <see cref="JobDefinition.Id"/> es <see cref="Guid.Empty"/>) o actualiza un trabajo.</summary>
    /// <remarks>
    /// Valida igual que <see cref="ValidateJobAsync"/>; si hay errores, falla con <see cref="BackboneErrorCode.ValidationFailed"/>.
    /// Inicializa o adopta el repositorio del destino (RF-63). En un trabajo en ejecución, los cambios se aplican desde la
    /// siguiente ejecución. Emite <see cref="StatusChanged"/>. Errores: <see cref="BackboneErrorCode.JobNotFound"/>,
    /// <see cref="BackboneErrorCode.ValidationFailed"/>, <see cref="BackboneErrorCode.CredentialRequired"/> (sin credencial para el
    /// recurso), <see cref="BackboneErrorCode.NasUnreachable"/>, <see cref="BackboneErrorCode.CredentialsRejected"/>,
    /// <see cref="BackboneErrorCode.ShareNotFound"/>, <see cref="BackboneErrorCode.AccessDenied"/>.
    /// </remarks>
    /// <param name="job">Trabajo.</param>
    /// <param name="ct">Cancelación.</param>
    /// <returns>
    /// El trabajo guardado, con su identificador y con la ruta de cada origen en forma normalizada (§7.5 "Ruta de origen"), que
    /// es la que se guarda en <c>jobs.json</c> y la que abre cada ejecución.
    /// </returns>
    Task<JobDefinition> SaveJobAsync(JobDefinition job, CancellationToken ct = default);

    /// <summary>Elimina un trabajo. El repositorio del NAS no se borra (RF-01). Emite <see cref="StatusChanged"/>.</summary>
    /// <remarks>
    /// Errores: <see cref="BackboneErrorCode.JobNotFound"/>; <see cref="BackboneErrorCode.JobAlreadyRunning"/> si está en ejecución
    /// o en cola (hay que cancelarla antes).
    /// </remarks>
    /// <param name="jobId">Trabajo.</param>
    /// <param name="ct">Cancelación.</param>
    /// <returns>Una tarea que termina al eliminarlo.</returns>
    Task DeleteJobAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>Valida un trabajo sin guardarlo (RF-03). La app no duplica estas reglas.</summary>
    /// <remarks>
    /// Es una operación de <b>escritura</b> a efectos de permisos y auditoría (§7.3), como <see cref="TestConnectionAsync"/>:
    /// abre como SYSTEM las rutas que indica el cliente. Los orígenes se comprueban con la resolución controlada de §8.3
    /// (ADR-20), que nunca sigue un enlace que no lleve a un volumen local. Errores de origen: <see cref="ValidationIssueCode.SourceNotFound"/>,
    /// <see cref="ValidationIssueCode.SourceNotNtfs"/>, <see cref="ValidationIssueCode.SourceNotLocal"/>,
    /// <see cref="ValidationIssueCode.SourceNotAllowed"/>, <see cref="ValidationIssueCode.SourceUnreadable"/>,
    /// <see cref="ValidationIssueCode.SourcePathInvalid"/> (contrato 1.3) y
    /// <see cref="ValidationIssueCode.ProtectedFoldersUnavailable"/> (contrato 1.4).
    /// </remarks>
    /// <param name="job">Trabajo.</param>
    /// <param name="ct">Cancelación.</param>
    /// <returns>Errores y avisos por campo.</returns>
    Task<ValidationResult> ValidateJobAsync(JobDefinition job, CancellationToken ct = default);

    // ---- Ejecución ----

    /// <summary>"Copiar ahora": lanza el trabajo o lo encola (RF-20). Funciona aunque el trabajo esté desactivado o haya pausa.</summary>
    /// <remarks>Errores: <see cref="BackboneErrorCode.JobNotFound"/>, <see cref="BackboneErrorCode.JobAlreadyRunning"/>.</remarks>
    /// <param name="jobId">Trabajo.</param>
    /// <param name="ct">Cancelación.</param>
    /// <returns>Una tarea que termina al aceptarse la petición (no al terminar la copia).</returns>
    Task RunNowAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>Cancela la ejecución en curso o en cola del trabajo (RF-21).</summary>
    /// <remarks>
    /// Idempotente: si el trabajo no tiene ejecución en curso ni en cola, no hace nada y no es un error.
    /// Errores: <see cref="BackboneErrorCode.JobNotFound"/>.
    /// </remarks>
    /// <param name="jobId">Trabajo.</param>
    /// <param name="ct">Cancelación.</param>
    /// <returns>Una tarea que termina al aceptarse la cancelación.</returns>
    Task CancelRunAsync(Guid jobId, CancellationToken ct = default);

    /// <summary><b>F2</b> (RF-12). Activa o quita la pausa global. El servicio puede devolver <see cref="BackboneErrorCode.NotSupported"/>.</summary>
    /// <param name="request">Pausa.</param>
    /// <param name="ct">Cancelación.</param>
    /// <returns>Una tarea que termina al aplicarse.</returns>
    Task SetPauseAsync(PauseRequest request, CancellationToken ct = default);

    // ---- NAS y credenciales ----

    /// <summary>Guarda (o sustituye) la credencial de un recurso, cifrada con DPAPI (RF-60, ADR-07).</summary>
    /// <remarks>Errores: <see cref="BackboneErrorCode.ValidationFailed"/> si el recurso no cumple <see cref="UncPaths.TryParse"/> o se superan los límites.</remarks>
    /// <param name="shareRoot">Recurso, <c>\\servidor\recurso</c> (el servicio lo normaliza con <see cref="UncPaths"/>).</param>
    /// <param name="credential">Usuario y contraseña. La contraseña nunca se registra ni se devuelve.</param>
    /// <param name="ct">Cancelación.</param>
    /// <returns>Una tarea que termina al guardarla.</returns>
    Task SaveCredentialAsync(string shareRoot, CredentialInput credential, CancellationToken ct = default);

    /// <summary>Credencial guardada de un recurso, sin contraseña (RF-62).</summary>
    /// <param name="shareRoot">Recurso, <c>\\servidor\recurso</c>.</param>
    /// <param name="ct">Cancelación.</param>
    /// <returns>La credencial, o <see langword="null"/> si no hay ninguna.</returns>
    Task<CredentialInfo?> GetCredentialInfoAsync(string shareRoot, CancellationToken ct = default);

    /// <summary>Borra la credencial de un recurso.</summary>
    /// <param name="shareRoot">Recurso, <c>\\servidor\recurso</c>.</param>
    /// <param name="ct">Cancelación.</param>
    /// <returns>Una tarea que termina al borrarla.</returns>
    Task DeleteCredentialAsync(string shareRoot, CancellationToken ct = default);

    /// <summary>"Probar conexión" (RF-61): conecta, lee, escribe y borra un archivo de prueba.</summary>
    /// <param name="destination">Destino UNC.</param>
    /// <param name="credential">
    /// Credencial que se prueba; <see langword="null"/> = la guardada para el recurso. Si no hay ninguna, el resultado es
    /// <see cref="BackboneErrorCode.CredentialRequired"/> sin intentar conectar (nunca se conecta con la cuenta del equipo).
    /// </param>
    /// <param name="ct">Cancelación.</param>
    /// <returns>Resultado; un fallo de conexión no es una excepción.</returns>
    Task<ConnectionTestResult> TestConnectionAsync(string destination, CredentialInput? credential, CancellationToken ct = default);

    /// <summary><b>F2</b> (RF-43). Espacio del recurso y tamaño del repositorio. El servicio puede devolver <see cref="BackboneErrorCode.NotSupported"/>.</summary>
    /// <param name="jobId">Trabajo.</param>
    /// <param name="ct">Cancelación.</param>
    /// <returns>Espacio.</returns>
    Task<StorageInfo> GetStorageInfoAsync(Guid jobId, CancellationToken ct = default);

    // ---- Historial ----

    /// <summary>Historial de ejecuciones filtrado y paginado (RF-53), del más reciente al más antiguo.</summary>
    /// <remarks>Errores: <see cref="BackboneErrorCode.ValidationFailed"/> si <c>Offset</c> &lt; 0, <c>Limit</c> fuera de 1–<see cref="HistoryQuery.MaxLimit"/> o <c>FromUtc</c> &gt; <c>ToUtc</c>.</remarks>
    /// <param name="query">Filtro.</param>
    /// <param name="ct">Cancelación.</param>
    /// <returns>Una página del historial con el total.</returns>
    Task<HistoryPage> GetHistoryAsync(HistoryQuery query, CancellationToken ct = default);

    /// <summary>Detalle de una ejecución con sus avisos y errores.</summary>
    /// <param name="runId">Ejecución.</param>
    /// <param name="ct">Cancelación.</param>
    /// <returns>Detalle.</returns>
    Task<RunDetail> GetRunDetailAsync(long runId, CancellationToken ct = default);

    // ---- Restauración ----

    /// <summary>
    /// Todos los snapshots del repositorio del trabajo, del más reciente al más antiguo (RF-30), incluidos los creados por
    /// instalaciones anteriores o con otro nombre de trabajo (repositorio adoptado, HU-06.2).
    /// </summary>
    /// <param name="jobId">Trabajo.</param>
    /// <param name="ct">Cancelación.</param>
    /// <returns>Snapshots.</returns>
    Task<IReadOnlyList<SnapshotInfo>> GetSnapshotsAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>Contenido directo de una carpeta de un snapshot, paginado (RF-31).</summary>
    /// <remarks>Errores: <see cref="BackboneErrorCode.SnapshotNotFound"/>; <see cref="BackboneErrorCode.ValidationFailed"/> si el identificador o la ruta no son válidos (<see cref="SnapshotPaths"/>) o la paginación está fuera de rango.</remarks>
    /// <param name="jobId">Trabajo.</param>
    /// <param name="snapshotId">Snapshot.</param>
    /// <param name="path">Carpeta del snapshot; cadena vacía = raíz (una carpeta por alias).</param>
    /// <param name="offset">Elementos que se saltan; ≥ 0.</param>
    /// <param name="limit">Máximo de elementos, 1–<see cref="ContractLimits.MaxPageSize"/>.</param>
    /// <param name="ct">Cancelación.</param>
    /// <returns>Una página de carpetas y archivos hijos con el total.</returns>
    Task<SnapshotItemPage> BrowseSnapshotAsync(Guid jobId, string snapshotId, string path, int offset, int limit, CancellationToken ct = default);

    /// <summary>Busca por nombre (sin distinguir mayúsculas) en todo el snapshot (RF-31).</summary>
    /// <param name="jobId">Trabajo.</param>
    /// <param name="snapshotId">Snapshot.</param>
    /// <param name="text">Texto contenido en el nombre (1–<see cref="ContractLimits.MaxSearchTextLength"/> caracteres; literal, sin comodines).</param>
    /// <param name="limit">Máximo de resultados, 1–<see cref="ContractLimits.MaxPageSize"/>.</param>
    /// <param name="ct">Cancelación.</param>
    /// <returns>Archivos y carpetas encontrados.</returns>
    Task<IReadOnlyList<SnapshotItem>> SearchSnapshotAsync(Guid jobId, string snapshotId, string text, int limit, CancellationToken ct = default);

    /// <summary><b>F2</b> (RF-35). Versiones de un archivo: snapshots en los que cambió. El servicio puede devolver <see cref="BackboneErrorCode.NotSupported"/>.</summary>
    /// <param name="jobId">Trabajo.</param>
    /// <param name="path">Ruta del snapshot del archivo.</param>
    /// <param name="ct">Cancelación.</param>
    /// <returns>Versiones, de la más reciente a la más antigua.</returns>
    Task<IReadOnlyList<FileVersion>> GetFileVersionsAsync(Guid jobId, string path, CancellationToken ct = default);

    /// <summary>Inicia una restauración (RF-32 a RF-34). El progreso llega con <see cref="RestoreProgress"/>.</summary>
    /// <remarks>
    /// <para>La restauración pertenece al SID que la inicia: solo ese usuario recibe sus eventos y puede resolver sus conflictos
    /// o cancelarla (§7.3). Para no perder eventos, el cliente puede proponer <see cref="RestoreRequest.RestoreId"/> y filtrar
    /// por él antes de recibir la respuesta.</para>
    /// <para>Antes de copiar nada, el servicio abre el destino suplantando al llamante (§8.6). Errores:
    /// <see cref="BackboneErrorCode.ValidationFailed"/> (rutas, límites, identificador en uso),
    /// <see cref="BackboneErrorCode.JobNotFound"/>, <see cref="BackboneErrorCode.SnapshotNotFound"/>,
    /// <see cref="BackboneErrorCode.RestoreTargetNotWritable"/> (sin permiso o destino protegido),
    /// <see cref="BackboneErrorCode.CredentialRequired"/>, <see cref="BackboneErrorCode.NasUnreachable"/>,
    /// <see cref="BackboneErrorCode.CredentialsRejected"/>, <see cref="BackboneErrorCode.RepositoryIncompatible"/>,
    /// <see cref="BackboneErrorCode.RepositoryLocked"/>.</para>
    /// </remarks>
    /// <param name="request">Petición.</param>
    /// <param name="ct">Cancelación.</param>
    /// <returns>Identificador de la restauración (el propuesto o uno nuevo).</returns>
    Task<Guid> StartRestoreAsync(RestoreRequest request, CancellationToken ct = default);

    /// <summary>Responde al conflicto pendiente de una restauración (<see cref="RestoreConflict"/>).</summary>
    /// <remarks>Errores: <see cref="BackboneErrorCode.NotAuthorized"/> si la restauración es de otro SID; <see cref="BackboneErrorCode.ValidationFailed"/> si no existe o no tiene conflicto pendiente.</remarks>
    /// <param name="restoreId">Restauración.</param>
    /// <param name="resolution">Respuesta.</param>
    /// <param name="applyToAll">Aplicar la misma respuesta al resto de conflictos de esta restauración.</param>
    /// <param name="ct">Cancelación.</param>
    /// <returns>Una tarea que termina al aceptarse la respuesta.</returns>
    Task ResolveRestoreConflictAsync(Guid restoreId, ConflictResolution resolution, bool applyToAll, CancellationToken ct = default);

    /// <summary>Cancela una restauración; lo ya restaurado se queda.</summary>
    /// <remarks>Idempotente si ya terminó. Errores: <see cref="BackboneErrorCode.NotAuthorized"/> si la restauración es de otro SID.</remarks>
    /// <param name="restoreId">Restauración.</param>
    /// <param name="ct">Cancelación.</param>
    /// <returns>Una tarea que termina al aceptarse la cancelación.</returns>
    Task CancelRestoreAsync(Guid restoreId, CancellationToken ct = default);

    // ---- Mantenimiento (F2) ----

    /// <summary><b>F2</b> (RF-40). Aplica la retención o la simula. El servicio puede devolver <see cref="BackboneErrorCode.NotSupported"/>.</summary>
    /// <param name="jobId">Trabajo.</param>
    /// <param name="dryRun"><see langword="true"/> = simular: no borra nada (HU-28.2).</param>
    /// <param name="ct">Cancelación.</param>
    /// <returns>Qué snapshots se conservan y cuáles se eliminan (o se eliminarían).</returns>
    Task<RetentionResult> ApplyRetentionAsync(Guid jobId, bool dryRun, CancellationToken ct = default);

    /// <summary><b>F2</b> (RF-41). Encola una limpieza (<see cref="RunKind.Prune"/>). El servicio puede devolver <see cref="BackboneErrorCode.NotSupported"/>.</summary>
    /// <param name="jobId">Trabajo.</param>
    /// <param name="ct">Cancelación.</param>
    /// <returns>Una tarea que termina al aceptarse la petición; el resultado llega con <see cref="RunCompleted"/>.</returns>
    Task StartPruneAsync(Guid jobId, CancellationToken ct = default);

    /// <summary><b>F2</b> (RF-42). Encola una verificación rápida o completa. El servicio puede devolver <see cref="BackboneErrorCode.NotSupported"/>.</summary>
    /// <param name="jobId">Trabajo.</param>
    /// <param name="full"><see langword="true"/> = completa (<see cref="RunKind.FullCheck"/>); si no, rápida.</param>
    /// <param name="ct">Cancelación.</param>
    /// <returns>Una tarea que termina al aceptarse la petición; el resultado llega con <see cref="RunCompleted"/>.</returns>
    Task StartCheckAsync(Guid jobId, bool full, CancellationToken ct = default);

    // ---- Ajustes y usuarios autorizados ----

    /// <summary>Ajustes globales del servicio (sin secretos).</summary>
    /// <param name="ct">Cancelación.</param>
    /// <returns>Ajustes actuales.</returns>
    Task<ServiceSettings> GetSettingsAsync(CancellationToken ct = default);

    /// <summary>Guarda los ajustes globales (escritura atómica de <c>settings.json</c>) y emite <see cref="StatusChanged"/>.</summary>
    /// <remarks>Errores: <see cref="BackboneErrorCode.ValidationFailed"/> con valores fuera de rango.</remarks>
    /// <param name="settings">Ajustes.</param>
    /// <param name="ct">Cancelación.</param>
    /// <returns>Una tarea que termina al guardarlos.</returns>
    Task SaveSettingsAsync(ServiceSettings settings, CancellationToken ct = default);

    /// <summary>Usuarios autorizados (§7.3), con el nombre resuelto de cada SID.</summary>
    /// <param name="ct">Cancelación.</param>
    /// <returns>Usuarios autorizados.</returns>
    Task<IReadOnlyList<AuthorizedUser>> GetAuthorizedUsersAsync(CancellationToken ct = default);

    /// <summary>Añade un usuario autorizado y actualiza la ACL del pipe. Exige cliente <b>elevado</b> (§7.3).</summary>
    /// <remarks>
    /// Solo se admiten cuentas de <b>usuario</b> que sean miembros del grupo local Administradores (modelo "solo cuentas
    /// administradoras", §7.3). Errores: <see cref="BackboneErrorCode.NotAuthorized"/> si el llamante no está elevado;
    /// <see cref="BackboneErrorCode.ValidationFailed"/> con <see cref="ValidationIssueCode.AccountNotFound"/> (no se resuelve),
    /// <see cref="ValidationIssueCode.AccountNotUser"/> (grupo, Everyone, SID bien conocido o nombre sin cualificar) o
    /// <see cref="ValidationIssueCode.AccountNotAdministrator"/>. Añadir un usuario que ya está no es un error.
    /// </remarks>
    /// <param name="account">Cuenta cualificada como <c>EQUIPO\usuario</c>, <c>DOMINIO\usuario</c> o SID de usuario.</param>
    /// <param name="ct">Cancelación.</param>
    /// <returns>El usuario añadido, con su SID y el nombre resuelto por el servicio.</returns>
    Task<AuthorizedUser> AddAuthorizedUserAsync(string account, CancellationToken ct = default);

    /// <summary>Quita un usuario autorizado y actualiza la ACL del pipe. Exige cliente <b>elevado</b> (§7.3).</summary>
    /// <remarks>Errores: <see cref="BackboneErrorCode.NotAuthorized"/> si el llamante no está elevado. Quitar un SID que no está no es un error.</remarks>
    /// <param name="sid">SID del usuario.</param>
    /// <param name="ct">Cancelación.</param>
    /// <returns>Una tarea que termina al quitarlo.</returns>
    Task RemoveAuthorizedUserAsync(string sid, CancellationToken ct = default);

    /// <summary>
    /// <b>F2</b> (RF-55). Crea un ZIP de diagnóstico en la ruta indicada, escribiéndolo <b>suplantando al llamante</b> (§8.7).
    /// El servicio puede devolver <see cref="BackboneErrorCode.NotSupported"/>.
    /// </summary>
    /// <remarks>
    /// Contenido: configuración sin credenciales, logs de 14 días, últimas 100 ejecuciones y versiones (HU-23).
    /// Errores: <see cref="BackboneErrorCode.ValidationFailed"/> (<see cref="ValidationIssueCode.TargetPathNotAllowed"/>) si la ruta no se admite;
    /// <see cref="BackboneErrorCode.AccessDenied"/> si el llamante no puede crear el archivo.
    /// </remarks>
    /// <param name="request">Petición.</param>
    /// <param name="ct">Cancelación.</param>
    /// <returns>Ruta y tamaño del ZIP creado.</returns>
    Task<DiagnosticsExportResult> ExportDiagnosticsAsync(DiagnosticsExportRequest request, CancellationToken ct = default);

    // ---- Eventos push (StreamJsonRpc los envía como notificaciones a todos los clientes) ----

    /// <summary>El estado global ha cambiado.</summary>
    event EventHandler<StatusChangedEvent> StatusChanged;

    /// <summary>Progreso de la ejecución en curso; como máximo <see cref="BackboneIpc.MaxRunProgressEventsPerSecond"/> por segundo.</summary>
    event EventHandler<RunProgressEvent> RunProgress;

    /// <summary>Una ejecución ha terminado.</summary>
    event EventHandler<RunCompletedEvent> RunCompleted;

    /// <summary>Progreso de una restauración.</summary>
    event EventHandler<RestoreProgressEvent> RestoreProgress;

    /// <summary>Conflicto en una restauración; espera <see cref="ResolveRestoreConflictAsync"/>.</summary>
    event EventHandler<RestoreConflictEvent> RestoreConflict;
}
