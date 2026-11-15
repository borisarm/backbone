namespace Backbone.Contracts;

/// <summary>
/// Información del servicio y del llamante (<see cref="IBackboneService.GetServiceInfoAsync"/>).
/// </summary>
public sealed record ServiceInfo
{
    /// <summary>Versión del servicio (versión del producto, p. ej. <c>1.0.0</c>).</summary>
    public string ServiceVersion { get; init => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Versión del contrato que implementa el servicio (<see cref="Contracts.ContractVersion.Value"/>).</summary>
    public string ContractVersion { get; init => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Nombre del equipo.</summary>
    public string MachineName { get; init => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Instante de arranque del servicio (UTC).</summary>
    public DateTimeOffset StartedUtc { get; init; }

    /// <summary>Identidad y permisos del llamante según el servidor.</summary>
    public CallerInfo Caller { get; init => field = value ?? new(); } = new();
}

/// <summary>
/// Identidad y autorizaciones del cliente conectado, tal como las ve el servicio (§7.3).
/// </summary>
public sealed record CallerInfo
{
    /// <summary>Cuenta en formato <c>EQUIPO\usuario</c> o <c>DOMINIO\usuario</c>.</summary>
    public string UserName { get; init => field = value ?? string.Empty; } = string.Empty;

    /// <summary>SID de la cuenta.</summary>
    public string UserSid { get; init => field = value ?? string.Empty; } = string.Empty;

    /// <summary>
    /// El token del cliente tiene el grupo Administradores habilitado (app elevada). Es la condición para gestionar los
    /// usuarios autorizados (§7.3); el servicio la calcula del token, nunca la toma del cliente.
    /// </summary>
    public bool IsElevated { get; init; }
}

/// <summary>Salud resumida, para el icono de la bandeja y el panel (RF-50).</summary>
public enum HealthState
{
    /// <summary>Todo correcto (verde).</summary>
    Ok = 0,

    /// <summary>Con avisos o desactualizado (amarillo).</summary>
    Warning = 1,

    /// <summary>Error (rojo).</summary>
    Error = 2,
}

/// <summary>Actividad actual de un trabajo.</summary>
public enum JobActivity
{
    /// <summary>Sin ejecución en curso ni en cola.</summary>
    Idle = 0,

    /// <summary>En cola (RF-15).</summary>
    Queued = 1,

    /// <summary>En ejecución.</summary>
    Running = 2,
}

/// <summary>
/// Estado global del servicio (<see cref="IBackboneService.GetStatusAsync"/> y <see cref="StatusChangedEvent"/>).
/// </summary>
public sealed record ServiceStatus
{
    /// <summary>Salud global calculada por el servicio (la peor de los trabajos activados).</summary>
    public HealthState Health { get; init; }

    /// <summary>F2 (RF-12). Pausa global activa; <see langword="null"/> si no hay pausa.</summary>
    public PauseState? Pause { get; init; }

    /// <summary>Último progreso conocido de la ejecución en curso; <see langword="null"/> si no hay ninguna.</summary>
    public RunProgressEvent? CurrentRun { get; init; }

    /// <summary>Ejecuciones en cola, en orden de llegada (RF-15). No incluye la ejecución en curso.</summary>
    public IReadOnlyList<QueuedRun> Queue { get; init => field = value ?? []; } = [];

    /// <summary>Estado de cada trabajo.</summary>
    public IReadOnlyList<JobStatus> Jobs { get; init => field = value ?? []; } = [];

    /// <summary>Instante en el que se calculó este estado (UTC).</summary>
    public DateTimeOffset TimestampUtc { get; init; }
}

/// <summary>
/// Estado de un trabajo para el panel de inicio (RF-51).
/// </summary>
public sealed record JobStatus
{
    /// <summary>Identificador del trabajo.</summary>
    public Guid JobId { get; init; }

    /// <summary>Nombre del trabajo.</summary>
    public string JobName { get; init => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Si el trabajo está activado.</summary>
    public bool Enabled { get; init; }

    /// <summary>Actividad actual.</summary>
    public JobActivity Activity { get; init; }

    /// <summary>Salud del trabajo (resultado de la última ejecución y antigüedad de la última copia correcta).</summary>
    public HealthState Health { get; init; }

    /// <summary>
    /// Sin copia correcta en más de <see cref="ServiceSettings.OutdatedAfterDays"/> días (por defecto 3) o nunca copiado.
    /// </summary>
    public bool IsOutdated { get; init; }

    /// <summary>Última ejecución terminada; <see langword="null"/> si nunca se ha ejecutado.</summary>
    public RunRecord? LastRun { get; init; }

    /// <summary>
    /// Fin de la última ejecución correcta (<see cref="RunState.Completed"/> o <see cref="RunState.CompletedWithWarnings"/>), UTC.
    /// </summary>
    public DateTimeOffset? LastSuccessUtc { get; init; }

    /// <summary>Próxima ejecución programada calculada por el servicio (UTC); <see langword="null"/> si es manual o está desactivado.</summary>
    public DateTimeOffset? NextRunUtc { get; init; }

    /// <summary>Estado del repositorio del destino según la última vez que se abrió (HU-06.4).</summary>
    public RepositoryState RepositoryState { get; init; }
}

/// <summary>Estado del repositorio de un trabajo.</summary>
public enum RepositoryState
{
    /// <summary>Aún no se ha abierto o no se pudo conectar.</summary>
    Unknown = 0,

    /// <summary>Formato que esta versión entiende: lectura y escritura.</summary>
    ReadWrite = 1,

    /// <summary>
    /// Formato superior (RNF-14): solo lectura. Las copias fallan con <see cref="BackboneErrorCode.RepositoryIncompatible"/>;
    /// la app muestra el aviso.
    /// </summary>
    ReadOnlyNewerFormat = 2,
}

/// <summary>Una ejecución en cola.</summary>
public sealed record QueuedRun
{
    /// <summary>Trabajo.</summary>
    public Guid JobId { get; init; }

    /// <summary>Tipo de ejecución.</summary>
    public RunKind Kind { get; init; }

    /// <summary>Origen de la ejecución.</summary>
    public RunTrigger Trigger { get; init; }

    /// <summary>Instante en que entró en la cola (UTC).</summary>
    public DateTimeOffset QueuedUtc { get; init; }
}

/// <summary>
/// F2 (RF-12). Pausa global activa.
/// </summary>
public sealed record PauseState
{
    /// <summary>Inicio de la pausa (UTC).</summary>
    public DateTimeOffset SinceUtc { get; init; }

    /// <summary>Fin de la pausa (UTC); <see langword="null"/> = indefinida.</summary>
    public DateTimeOffset? UntilUtc { get; init; }
}

/// <summary>
/// F2 (RF-12). Petición de pausa o reanudación (<see cref="IBackboneService.SetPauseAsync"/>).
/// </summary>
/// <remarks>
/// La app convierte "1 h", "4 h" y "hasta mañana" (mañana a las 06:00 en hora local del usuario) en <see cref="UntilUtc"/>;
/// el servicio rechaza con <see cref="BackboneErrorCode.ValidationFailed"/> un fin en el pasado.
/// </remarks>
public sealed record PauseRequest
{
    /// <summary><see langword="true"/> para pausar; <see langword="false"/> para reanudar (se ignora <see cref="UntilUtc"/>).</summary>
    public bool Paused { get; init; }

    /// <summary>Fin de la pausa (UTC); <see langword="null"/> = indefinida.</summary>
    public DateTimeOffset? UntilUtc { get; init; }
}
