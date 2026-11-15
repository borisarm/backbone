namespace Backbone.Contracts;

/// <summary>
/// F2 (RF-40). Resultado de aplicar o simular la retención (<see cref="IBackboneService.ApplyRetentionAsync"/>).
/// </summary>
public sealed record RetentionResult
{
    /// <summary>Trabajo.</summary>
    public Guid JobId { get; init; }

    /// <summary>Era una simulación: no se ha borrado nada.</summary>
    public bool DryRun { get; init; }

    /// <summary>Decisión para cada snapshot del repositorio, del más reciente al más antiguo.</summary>
    public IReadOnlyList<RetentionDecision> Decisions { get; init => field = value ?? []; } = [];
}

/// <summary>Motivos por los que la retención conserva un snapshot (combinables).</summary>
[Flags]
public enum RetentionReasons
{
    /// <summary>Ningún motivo: se elimina.</summary>
    None = 0,

    /// <summary>Entre los últimos N.</summary>
    Last = 1,

    /// <summary>Diario.</summary>
    Daily = 2,

    /// <summary>Semanal.</summary>
    Weekly = 4,

    /// <summary>Mensual.</summary>
    Monthly = 8,

    /// <summary>Anual.</summary>
    Yearly = 16,

    /// <summary>Es el más reciente (nunca se elimina, HU-28.4).</summary>
    MostRecent = 32,

    /// <summary>F3 (RF-44). Protegido por el usuario.</summary>
    Protected = 64,
}

/// <summary>
/// F2. Decisión de retención para un snapshot.
/// </summary>
public sealed record RetentionDecision
{
    /// <summary>Snapshot.</summary>
    public string SnapshotId { get; init => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Fecha del snapshot (UTC).</summary>
    public DateTimeOffset SnapshotUtc { get; init; }

    /// <summary>Se conserva (<see cref="Reasons"/> distinto de <see cref="RetentionReasons.None"/>).</summary>
    public bool Keep { get; init; }

    /// <summary>Motivos por los que se conserva.</summary>
    public RetentionReasons Reasons { get; init; }
}
