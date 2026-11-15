namespace Backbone.Contracts;

/// <summary>Estado de un snapshot (§6.3).</summary>
public enum SnapshotStatus
{
    /// <summary>Completado sin avisos.</summary>
    Completed = 0,

    /// <summary>Completado con avisos.</summary>
    CompletedWithWarnings = 1,
}

/// <summary>
/// Un snapshot de un trabajo (RF-30).
/// </summary>
public sealed record SnapshotInfo
{
    /// <summary>Identificador, <c>yyyyMMddTHHmmssZ-xxxx</c> (§6.3).</summary>
    public string Id { get; init => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Trabajo.</summary>
    public Guid JobId { get; init; }

    /// <summary>Nombre del trabajo guardado en el snapshot (puede venir de una instalación anterior, HU-06.2).</summary>
    public string JobName { get; init => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Equipo que lo creó.</summary>
    public string Host { get; init => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Inicio de la ejecución (UTC); es la fecha del snapshot.</summary>
    public DateTimeOffset StartedUtc { get; init; }

    /// <summary>Fin de la ejecución (UTC).</summary>
    public DateTimeOffset CompletedUtc { get; init; }

    /// <summary>Estado.</summary>
    public SnapshotStatus Status { get; init; }

    /// <summary>Estadísticas (número de archivos y tamaño en <see cref="RunStats.Files"/> y <see cref="RunStats.Bytes"/>).</summary>
    public RunStats Stats { get; init => field = value ?? new(); } = new();

    /// <summary>Se leyó desde instantáneas VSS.</summary>
    public bool UsedVss { get; init; }

    /// <summary>F3 (RF-44). Protegido frente a la retención.</summary>
    public bool IsProtected { get; init; }
}

/// <summary>
/// Un archivo o carpeta dentro de un snapshot (RF-31).
/// </summary>
/// <remarks>
/// Rutas del snapshot: separador <c>/</c>, prefijadas por el alias del origen, sin <c>/</c> inicial ni final
/// (p. ej. <c>Documentos/Facturas/enero.pdf</c>). La raíz es la cadena vacía y contiene una carpeta por alias.
/// </remarks>
public sealed record SnapshotItem
{
    /// <summary>Ruta completa dentro del snapshot.</summary>
    public string Path { get; init => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Nombre (último segmento de <see cref="Path"/>).</summary>
    public string Name { get; init => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Es una carpeta.</summary>
    public bool IsDirectory { get; init; }

    /// <summary>Tamaño en bytes (0 en carpetas).</summary>
    public long Size { get; init; }

    /// <summary>Última modificación guardada (UTC); <see langword="null"/> en carpetas.</summary>
    public DateTimeOffset? ModifiedUtc { get; init; }

    /// <summary>Atributos guardados (ReadOnly, Hidden, System, Archive; §6.3). Se serializa como número.</summary>
    public FileAttributes Attributes { get; init; }
}

/// <summary>
/// Página del contenido de una carpeta de un snapshot (<see cref="IBackboneService.BrowseSnapshotAsync"/>).
/// </summary>
public sealed record SnapshotItemPage
{
    /// <summary>Elementos de la página: primero carpetas y después archivos, por nombre (ordinal, sin distinguir mayúsculas).</summary>
    public IReadOnlyList<SnapshotItem> Items { get; init => field = value ?? []; } = [];

    /// <summary>Total de elementos de la carpeta.</summary>
    public long TotalCount { get; init; }
}

/// <summary>
/// F2 (RF-35). Una versión de un archivo: un snapshot en el que su contenido cambió.
/// </summary>
public sealed record FileVersion
{
    /// <summary>Primer snapshot que contiene esta versión.</summary>
    public string SnapshotId { get; init => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Fecha de ese snapshot (UTC).</summary>
    public DateTimeOffset SnapshotUtc { get; init; }

    /// <summary>Tamaño en bytes.</summary>
    public long Size { get; init; }

    /// <summary>Última modificación guardada (UTC).</summary>
    public DateTimeOffset ModifiedUtc { get; init; }

    /// <summary>Hash SHA-256 del contenido en hexadecimal minúsculo.</summary>
    public string Hash { get; init => field = value ?? string.Empty; } = string.Empty;
}

/// <summary>Política ante un archivo que ya existe en el destino (RF-32).</summary>
public enum RestoreConflictPolicy
{
    /// <summary>Preguntar con <see cref="IBackboneService.RestoreConflict"/>.</summary>
    Ask = 0,

    /// <summary>Sobrescribir.</summary>
    Overwrite = 1,

    /// <summary>Conservar ambos: <c>nombre (restaurado AAAA-MM-DD).ext</c>.</summary>
    KeepBoth = 2,

    /// <summary>Omitir.</summary>
    Skip = 3,
}

/// <summary>Respuesta del usuario a un conflicto (<see cref="IBackboneService.ResolveRestoreConflictAsync"/>).</summary>
public enum ConflictResolution
{
    /// <summary>Sobrescribir.</summary>
    Overwrite = 0,

    /// <summary>Conservar ambos.</summary>
    KeepBoth = 1,

    /// <summary>Omitir.</summary>
    Skip = 2,
}

/// <summary>
/// Petición de restauración (<see cref="IBackboneService.StartRestoreAsync"/>).
/// </summary>
public sealed record RestoreRequest
{
    /// <summary>
    /// Identificador propuesto por el cliente, para filtrar sus eventos desde antes de recibir la respuesta;
    /// <see cref="Guid.Empty"/> = lo genera el servicio. Uno en uso → <see cref="BackboneErrorCode.ValidationFailed"/>
    /// (<see cref="ValidationIssueCode.IdAlreadyInUse"/>).
    /// </summary>
    public Guid RestoreId { get; init; }

    /// <summary>Trabajo.</summary>
    public Guid JobId { get; init; }

    /// <summary>Snapshot del que se restaura.</summary>
    public string SnapshotId { get; init => field = value ?? string.Empty; } = string.Empty;

    /// <summary>
    /// Rutas del snapshot seleccionadas (archivos o carpetas; una carpeta incluye todo su contenido). Entre 1 y
    /// <see cref="ContractLimits.MaxRestorePaths"/>, cada una válida según <see cref="SnapshotPaths.IsValidPath"/>.
    /// </summary>
    public IReadOnlyList<string> Paths { get; init => field = value ?? []; } = [];

    /// <summary>
    /// Carpeta local de destino; <see langword="null"/> = ubicación original de cada origen. Con carpeta, cada ruta seleccionada
    /// se coloca directamente en ella con su subárbol, como al copiar y pegar en Windows
    /// (<c>Documentos/Fotos/2024</c> → <c>D:\Restaurado\2024\…</c>; RF-32).
    /// </summary>
    public string? TargetFolder { get; init; }

    /// <summary>Qué hacer si un archivo ya existe en el destino.</summary>
    public RestoreConflictPolicy ConflictPolicy { get; init; } = RestoreConflictPolicy.Ask;
}

/// <summary>Estado de una restauración.</summary>
public enum RestoreState
{
    /// <summary>Comprobando el destino (§8.6) y preparando.</summary>
    Preparing = 0,

    /// <summary>Restaurando.</summary>
    Running = 1,

    /// <summary>Esperando la respuesta a un <see cref="RestoreConflictEvent"/>.</summary>
    WaitingForConflictResolution = 2,

    /// <summary>Terminada sin fallos.</summary>
    Completed = 3,

    /// <summary>Terminada con archivos fallidos u omitidos.</summary>
    CompletedWithErrors = 4,

    /// <summary>Cancelada; los archivos ya restaurados se quedan.</summary>
    Cancelled = 5,

    /// <summary>Fallida; <see cref="RestoreProgressEvent.ErrorCode"/> indica el motivo.</summary>
    Failed = 6,
}

/// <summary>Resultado de restaurar un archivo o una ruta seleccionada.</summary>
public enum RestoreOutcome
{
    /// <summary>Restaurado (completo).</summary>
    Restored = 0,

    /// <summary>Restaurado en parte (carpeta con archivos fallidos, omitidos o sin procesar).</summary>
    PartiallyRestored = 1,

    /// <summary>Omitido por un conflicto.</summary>
    Skipped = 2,

    /// <summary>Fallido (p. ej. hash que no coincide, RF-33).</summary>
    Failed = 3,

    /// <summary>No procesado por cancelación.</summary>
    NotProcessed = 4,
}

/// <summary>
/// Resultado de una de las rutas seleccionadas en <see cref="RestoreRequest.Paths"/>.
/// </summary>
public sealed record RestoreItemResult
{
    /// <summary>Ruta seleccionada del snapshot.</summary>
    public string Path { get; init => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Ruta local donde se restauró (la carpeta o archivo de destino).</summary>
    public string? TargetPath { get; init; }

    /// <summary>Resultado global de esa ruta.</summary>
    public RestoreOutcome Outcome { get; init; }

    /// <summary>Archivos restaurados bajo esa ruta.</summary>
    public long FilesRestored { get; init; }
}

/// <summary>
/// Un archivo que no se restauró (omitido o fallido).
/// </summary>
public sealed record RestoreIssue
{
    /// <summary>Ruta del snapshot.</summary>
    public string Path { get; init => field = value ?? string.Empty; } = string.Empty;

    /// <summary><see cref="RestoreOutcome.Skipped"/> o <see cref="RestoreOutcome.Failed"/>.</summary>
    public RestoreOutcome Outcome { get; init; }

    /// <summary>Código del error, si falló.</summary>
    public BackboneErrorCode? ErrorCode { get; init; }

    /// <summary>Detalle técnico, sin secretos.</summary>
    public string? Message { get; init; }
}
