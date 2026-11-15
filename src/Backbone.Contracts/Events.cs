namespace Backbone.Contracts;

/// <summary>
/// Evento <see cref="IBackboneService.StatusChanged"/>: el estado global ha cambiado (inicio o fin de ejecución,
/// cola, pausa, configuración de trabajos). Lleva el estado completo.
/// </summary>
public sealed record StatusChangedEvent
{
    /// <summary>Estado actual.</summary>
    public ServiceStatus Status { get; init => field = value ?? new(); } = new();
}

/// <summary>
/// Evento <see cref="IBackboneService.RunProgress"/>: progreso de la ejecución en curso
/// (como máximo <see cref="BackboneIpc.MaxRunProgressEventsPerSecond"/> por segundo).
/// </summary>
public sealed record RunProgressEvent
{
    /// <summary>Ejecución.</summary>
    public long RunId { get; init; }

    /// <summary>Trabajo.</summary>
    public Guid JobId { get; init; }

    /// <summary>Tipo de ejecución.</summary>
    public RunKind Kind { get; init; }

    /// <summary>Qué la lanzó.</summary>
    public RunTrigger Trigger { get; init; }

    /// <summary>Fase actual.</summary>
    public RunPhase Phase { get; init; }

    /// <summary>Inicio de la ejecución (UTC).</summary>
    public DateTimeOffset StartedUtc { get; init; }

    /// <summary>Archivos procesados.</summary>
    public long FilesProcessed { get; init; }

    /// <summary>Total de archivos, si ya se conoce.</summary>
    public long? FilesTotal { get; init; }

    /// <summary>Bytes procesados.</summary>
    public long BytesProcessed { get; init; }

    /// <summary>Total de bytes, si ya se conoce.</summary>
    public long? BytesTotal { get; init; }

    /// <summary>Velocidad actual en bytes por segundo.</summary>
    public double BytesPerSecond { get; init; }

    /// <summary>Ruta del snapshot del archivo actual.</summary>
    public string? CurrentPath { get; init; }

    /// <summary>Tiempo restante estimado, si se puede estimar.</summary>
    public TimeSpan? EstimatedRemaining { get; init; }

    /// <summary>Instante de la medida (UTC).</summary>
    public DateTimeOffset TimestampUtc { get; init; }
}

/// <summary>
/// Evento <see cref="IBackboneService.RunCompleted"/>: una ejecución ha terminado (cualquier estado final).
/// </summary>
public sealed record RunCompletedEvent
{
    /// <summary>Ejecución terminada, tal como queda en el historial.</summary>
    public RunRecord Run { get; init => field = value ?? new(); } = new();
}

/// <summary>
/// Evento <see cref="IBackboneService.RestoreProgress"/>: progreso de una restauración.
/// </summary>
/// <remarks>
/// Se emite al menos una vez con un estado final (<see cref="RestoreState.Completed"/>, <see cref="RestoreState.CompletedWithErrors"/>,
/// <see cref="RestoreState.Cancelled"/> o <see cref="RestoreState.Failed"/>). Solo ese último lleva <see cref="Items"/> e <see cref="Issues"/>
/// (el resumen); en los demás van vacíos.
/// </remarks>
public sealed record RestoreProgressEvent
{
    /// <summary>Restauración (valor devuelto por <see cref="IBackboneService.StartRestoreAsync"/>).</summary>
    public Guid RestoreId { get; init; }

    /// <summary>Trabajo.</summary>
    public Guid JobId { get; init; }

    /// <summary>Snapshot.</summary>
    public string SnapshotId { get; init => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Estado.</summary>
    public RestoreState State { get; init; }

    /// <summary>Archivos restaurados.</summary>
    public long FilesRestored { get; init; }

    /// <summary>Archivos omitidos.</summary>
    public long FilesSkipped { get; init; }

    /// <summary>Archivos fallidos.</summary>
    public long FilesFailed { get; init; }

    /// <summary>Total de archivos seleccionados, si ya se conoce.</summary>
    public long? FilesTotal { get; init; }

    /// <summary>Bytes restaurados.</summary>
    public long BytesRestored { get; init; }

    /// <summary>Total de bytes, si ya se conoce.</summary>
    public long? BytesTotal { get; init; }

    /// <summary>Ruta del snapshot del archivo actual.</summary>
    public string? CurrentPath { get; init; }

    /// <summary>Código del error si <see cref="State"/> es <see cref="RestoreState.Failed"/> (p. ej. <see cref="BackboneErrorCode.RestoreTargetNotWritable"/>).</summary>
    public BackboneErrorCode? ErrorCode { get; init; }

    /// <summary>Solo en el estado final: resultado de cada ruta seleccionada.</summary>
    public IReadOnlyList<RestoreItemResult> Items { get; init => field = value ?? []; } = [];

    /// <summary>Solo en el estado final: archivos omitidos o fallidos (el servicio puede truncar la lista; ver <see cref="IssuesTruncated"/>).</summary>
    public IReadOnlyList<RestoreIssue> Issues { get; init => field = value ?? []; } = [];

    /// <summary><see cref="Issues"/> no contiene todos los archivos omitidos o fallidos.</summary>
    public bool IssuesTruncated { get; init; }

    /// <summary>Instante de la medida (UTC).</summary>
    public DateTimeOffset TimestampUtc { get; init; }
}

/// <summary>
/// Evento <see cref="IBackboneService.RestoreConflict"/>: un archivo ya existe en el destino y la política es
/// <see cref="RestoreConflictPolicy.Ask"/>. La restauración espera a <see cref="IBackboneService.ResolveRestoreConflictAsync"/>;
/// hay como mucho un conflicto pendiente por restauración.
/// </summary>
public sealed record RestoreConflictEvent
{
    /// <summary>Restauración.</summary>
    public Guid RestoreId { get; init; }

    /// <summary>Ruta del snapshot del archivo que se restaura.</summary>
    public string SnapshotPath { get; init => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Ruta local del archivo existente.</summary>
    public string TargetPath { get; init => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Tamaño del archivo existente.</summary>
    public long ExistingSize { get; init; }

    /// <summary>Última modificación del archivo existente (UTC).</summary>
    public DateTimeOffset ExistingModifiedUtc { get; init; }

    /// <summary>Tamaño de la versión del snapshot.</summary>
    public long IncomingSize { get; init; }

    /// <summary>Última modificación de la versión del snapshot (UTC).</summary>
    public DateTimeOffset IncomingModifiedUtc { get; init; }
}
