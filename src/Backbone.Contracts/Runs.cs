namespace Backbone.Contracts;

/// <summary>Estado de una ejecución (§8.4).</summary>
public enum RunState
{
    /// <summary>En cola.</summary>
    Queued = 0,

    /// <summary>En ejecución.</summary>
    Running = 1,

    /// <summary>Terminada correctamente.</summary>
    Completed = 2,

    /// <summary>Terminada con avisos (archivos omitidos, copia sin VSS…).</summary>
    CompletedWithWarnings = 3,

    /// <summary>Fallida; <see cref="RunRecord.ErrorCode"/> indica el motivo.</summary>
    Failed = 4,

    /// <summary>Cancelada por el usuario o por la parada del servicio.</summary>
    Cancelled = 5,

    /// <summary>No se lanzó porque no se cumplían las condiciones; <see cref="RunRecord.SkipReason"/> indica el motivo.</summary>
    Skipped = 6,
}

/// <summary>Tipo de ejecución.</summary>
/// <remarks>Las ejecuciones de mantenimiento (F2) usan el mismo historial y los mismos eventos que las copias.</remarks>
public enum RunKind
{
    /// <summary>Copia de seguridad.</summary>
    Backup = 0,

    /// <summary>F2 (RF-40). Aplicación de la retención a petición.</summary>
    Retention = 1,

    /// <summary>F2 (RF-41). Limpieza de objetos no referenciados.</summary>
    Prune = 2,

    /// <summary>F2 (RF-42). Verificación rápida.</summary>
    QuickCheck = 3,

    /// <summary>F2 (RF-42). Verificación completa (relee y recalcula hashes).</summary>
    FullCheck = 4,
}

/// <summary>Qué lanzó una ejecución.</summary>
public enum RunTrigger
{
    /// <summary>"Copiar ahora" o petición desde la app.</summary>
    Manual = 0,

    /// <summary>Programación.</summary>
    Scheduled = 1,

    /// <summary>Recuperación de una ejecución perdida (RF-11).</summary>
    CatchUp = 2,
}

/// <summary>F2 (RF-13). Motivo por el que una ejecución quedó omitida.</summary>
public enum SkipReason
{
    /// <summary>Otro motivo.</summary>
    Other = 0,

    /// <summary>Batería por debajo del umbral sin cargador.</summary>
    LowBattery = 1,

    /// <summary>Conexión de uso medido.</summary>
    MeteredConnection = 2,

    /// <summary>NAS inaccesible.</summary>
    NasUnreachable = 3,
}

/// <summary>Fase de una ejecución en curso (§8.4).</summary>
public enum RunPhase
{
    /// <summary>Comprobando condiciones y preparando.</summary>
    Starting = 0,

    /// <summary>Conectando con el recurso e inicializando o validando el repositorio.</summary>
    Connecting = 1,

    /// <summary>Creando las instantáneas VSS.</summary>
    CreatingVolumeSnapshots = 2,

    /// <summary>Recorriendo orígenes y subiendo objetos.</summary>
    Copying = 3,

    /// <summary>Escribiendo el snapshot.</summary>
    WritingSnapshot = 4,

    /// <summary>F2. Aplicando la retención.</summary>
    ApplyingRetention = 5,

    /// <summary>F2. Limpiando objetos no referenciados.</summary>
    Pruning = 6,

    /// <summary>F2. Verificando el repositorio.</summary>
    Checking = 7,

    /// <summary>Liberando recursos (bloqueo, instantáneas, conexión).</summary>
    Finishing = 8,
}

/// <summary>
/// Estadísticas de una ejecución o snapshot (mismos campos que <c>stats</c> en §6.3).
/// </summary>
public sealed record RunStats
{
    /// <summary>Archivos en el snapshot.</summary>
    public long Files { get; init; }

    /// <summary>Bytes en el snapshot (suma de tamaños).</summary>
    public long Bytes { get; init; }

    /// <summary>Archivos nuevos respecto al snapshot anterior.</summary>
    public long NewFiles { get; init; }

    /// <summary>Archivos modificados respecto al snapshot anterior.</summary>
    public long ChangedFiles { get; init; }

    /// <summary>Objetos nuevos subidos al repositorio.</summary>
    public long NewObjects { get; init; }

    /// <summary>Bytes transferidos (tamaño de los objetos nuevos).</summary>
    public long NewBytes { get; init; }

    /// <summary>Archivos omitidos (ilegibles, puntos de reanálisis, solo en la nube…).</summary>
    public long Skipped { get; init; }
}

/// <summary>
/// Una ejecución del historial (RF-53).
/// </summary>
public sealed record RunRecord
{
    /// <summary>Identificador en el historial.</summary>
    public long RunId { get; init; }

    /// <summary>Trabajo.</summary>
    public Guid JobId { get; init; }

    /// <summary>Nombre del trabajo en el momento de la ejecución.</summary>
    public string JobName { get; init => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Tipo de ejecución.</summary>
    public RunKind Kind { get; init; }

    /// <summary>Qué la lanzó.</summary>
    public RunTrigger Trigger { get; init; }

    /// <summary>Estado.</summary>
    public RunState State { get; init; }

    /// <summary>Inicio (UTC).</summary>
    public DateTimeOffset StartedUtc { get; init; }

    /// <summary>Fin (UTC); <see langword="null"/> si no ha terminado. La duración es <c>CompletedUtc - StartedUtc</c>.</summary>
    public DateTimeOffset? CompletedUtc { get; init; }

    /// <summary>Estadísticas (ceros si no se llegó a copiar).</summary>
    public RunStats Stats { get; init => field = value ?? new(); } = new();

    /// <summary>Snapshot producido; <see langword="null"/> si no produjo ninguno.</summary>
    public string? SnapshotId { get; init; }

    /// <summary>Se leyó desde instantáneas VSS (RF-22).</summary>
    public bool UsedVss { get; init; }

    /// <summary>Número de avisos (el detalle está en <see cref="RunDetail.Issues"/>).</summary>
    public int WarningCount { get; init; }

    /// <summary>Número de errores.</summary>
    public int ErrorCount { get; init; }

    /// <summary>Código del error si <see cref="State"/> es <see cref="RunState.Failed"/>.</summary>
    public BackboneErrorCode? ErrorCode { get; init; }

    /// <summary>Detalle técnico del error, sin secretos; la app muestra el mensaje localizado de <see cref="ErrorCode"/>.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>F2. Motivo si <see cref="State"/> es <see cref="RunState.Skipped"/>.</summary>
    public SkipReason? SkipReason { get; init; }
}

/// <summary>
/// Detalle de una ejecución (<see cref="IBackboneService.GetRunDetailAsync"/>).
/// </summary>
public sealed record RunDetail
{
    /// <summary>Datos de la ejecución.</summary>
    public RunRecord Run { get; init => field = value ?? new(); } = new();

    /// <summary>Avisos y errores, con ruta y motivo.</summary>
    public IReadOnlyList<RunIssue> Issues { get; init => field = value ?? []; } = [];
}

/// <summary>Gravedad de una incidencia.</summary>
public enum IssueSeverity
{
    /// <summary>Aviso: la ejecución continúa.</summary>
    Warning = 0,

    /// <summary>Error.</summary>
    Error = 1,
}

/// <summary>Motivo de una incidencia de ejecución.</summary>
public enum RunIssueCode
{
    /// <summary>Otro motivo; ver <see cref="RunIssue.Message"/>.</summary>
    Other = 0,

    /// <summary>Archivo bloqueado por otro proceso (sin VSS).</summary>
    FileLocked = 1,

    /// <summary>Sin permiso de lectura.</summary>
    AccessDenied = 2,

    /// <summary>Ruta no válida.</summary>
    PathInvalid = 3,

    /// <summary>Punto de reanálisis (junction, enlace simbólico) no seguido (RF-06).</summary>
    ReparsePointSkipped = 4,

    /// <summary>Archivo de OneDrive solo en la nube, omitido sin descargar (RF-06).</summary>
    CloudOnlySkipped = 5,

    /// <summary>F2 (RF-05). Supera el tamaño máximo.</summary>
    FileTooLarge = 6,

    /// <summary>El archivo cambió durante la copia sin VSS; se usó el hash real (§6.4.2).</summary>
    FileChangedDuringCopy = 7,

    /// <summary>VSS no disponible; copia en vivo (RF-22).</summary>
    VssUnavailable = 8,

    /// <summary>Error de lectura del origen.</summary>
    ReadError = 9,

    /// <summary>F2 (RF-42). Objeto referenciado inexistente.</summary>
    ObjectMissing = 10,

    /// <summary>F2 (RF-42). Objeto con tamaño o hash que no coincide.</summary>
    ObjectCorrupt = 11,
}

/// <summary>
/// Un aviso o error de una ejecución.
/// </summary>
public sealed record RunIssue
{
    /// <summary>Gravedad.</summary>
    public IssueSeverity Severity { get; init; }

    /// <summary>Motivo.</summary>
    public RunIssueCode Code { get; init; }

    /// <summary>Ruta del snapshot afectada (con <c>/</c> y prefijo de alias); <see langword="null"/> si no afecta a un archivo.</summary>
    public string? Path { get; init; }

    /// <summary>F2. Snapshot afectado (verificación).</summary>
    public string? SnapshotId { get; init; }

    /// <summary>Detalle técnico, sin secretos ni contenido de archivos.</summary>
    public string? Message { get; init; }
}

/// <summary>
/// Página del historial (<see cref="IBackboneService.GetHistoryAsync"/>).
/// </summary>
public sealed record HistoryPage
{
    /// <summary>Ejecuciones de la página, de la más reciente a la más antigua.</summary>
    public IReadOnlyList<RunRecord> Items { get; init => field = value ?? []; } = [];

    /// <summary>Total de ejecuciones que cumplen el filtro (para la lista virtualizada, HU-21).</summary>
    public long TotalCount { get; init; }
}

/// <summary>
/// Filtro y paginación del historial (<see cref="IBackboneService.GetHistoryAsync"/>).
/// </summary>
/// <remarks>Resultados ordenados del más reciente al más antiguo.</remarks>
public sealed record HistoryQuery
{
    /// <summary>Solo este trabajo; <see langword="null"/> = todos.</summary>
    public Guid? JobId { get; init; }

    /// <summary>Solo estos estados; vacío = todos.</summary>
    public IReadOnlyList<RunState> States { get; init => field = value ?? []; } = [];

    /// <summary>Solo ejecuciones iniciadas desde este instante (inclusive; el servicio normaliza a UTC). Debe ser ≤ <see cref="ToUtc"/>.</summary>
    public DateTimeOffset? FromUtc { get; init; }

    /// <summary>Solo ejecuciones iniciadas antes de este instante (UTC, exclusivo).</summary>
    public DateTimeOffset? ToUtc { get; init; }

    /// <summary>Número de resultados que se saltan (paginación); ≥ 0.</summary>
    public int Offset { get; init; }

    /// <summary>Máximo de resultados (1–<see cref="MaxLimit"/>).</summary>
    public int Limit { get; init; } = DefaultLimit;

    /// <summary>Valor por defecto de <see cref="Limit"/>.</summary>
    public const int DefaultLimit = 100;

    /// <summary>Valor máximo de <see cref="Limit"/>.</summary>
    public const int MaxLimit = ContractLimits.MaxPageSize;
}
