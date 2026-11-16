using Backbone.Contracts;
using Backbone.Core.Repository.Snapshots;

namespace Backbone.Core.Engine;

/// <summary>Origen de una copia.</summary>
/// <param name="Alias">Alias del origen; primer segmento de las rutas del snapshot (§6.3).</param>
/// <param name="Path">Ruta local del origen tal como la configuró el usuario; se guarda en <c>sources[].path</c>.</param>
/// <param name="ReadPath">
/// Ruta desde la que se lee, ya resuelta por el servicio (p. ej. dentro de la instantánea VSS); se pasa tal cual a
/// <see cref="ISourceFileSystem.EnumerateAsync"/>.
/// </param>
public sealed record BackupSource(string Alias, string Path, string ReadPath);

/// <summary>Lo que necesita el motor para hacer una copia.</summary>
public sealed record BackupRequest
{
    /// <summary>Identificador del trabajo (GUID en formato <c>D</c>).</summary>
    public required string JobId { get; init; }

    /// <summary>Nombre del trabajo.</summary>
    public required string JobName { get; init; }

    /// <summary>Nombre del equipo (segmento válido).</summary>
    public required string Host { get; init; }

    /// <summary>Orígenes (al menos uno; alias únicos sin distinguir mayúsculas).</summary>
    public required IReadOnlyList<BackupSource> Sources { get; init; }

    /// <summary>Si los orígenes se leen desde instantáneas VSS (RF-22).</summary>
    public bool UsedVss { get; init; }

    /// <summary>Inicio de la ejecución (UTC); si no se indica, el instante en que empieza el motor.</summary>
    public DateTime? StartedUtc { get; init; }

    /// <summary>
    /// Avisos que el servicio ya conoce antes de copiar (p. ej. <see cref="RunIssueCode.VssUnavailable"/>); se guardan los
    /// primeros, delante de los del motor, y hacen que la ejecución termine con avisos.
    /// </summary>
    public IReadOnlyList<SnapshotWarning> AdditionalWarnings { get; init; } = [];
}

/// <summary>Progreso de una copia; se informa después de cada archivo.</summary>
/// <param name="FilesProcessed">Archivos tratados (copiados, sin cambios u omitidos).</param>
/// <param name="BytesProcessed">Suma de los tamaños de los archivos tratados.</param>
/// <param name="CurrentPath">Ruta del snapshot del último archivo tratado.</param>
public sealed record BackupProgress(long FilesProcessed, long BytesProcessed, string CurrentPath);

/// <summary>Resultado de una copia que ha publicado su snapshot.</summary>
public sealed record BackupResult
{
    /// <summary>Cabecera del snapshot publicado (identificador, padre, estado, estadísticas y avisos guardados).</summary>
    public required SnapshotHeader Snapshot { get; init; }

    /// <summary>
    /// <see cref="RunState.Completed"/> o <see cref="RunState.CompletedWithWarnings"/> (si hay al menos un aviso).
    /// </summary>
    public required RunState State { get; init; }

    /// <summary>
    /// Avisos de la ejecución, en orden, como mucho <see cref="SnapshotLimits.MaxWarnings"/> (los demás solo se cuentan en
    /// <see cref="WarningCount"/>, para que la memoria no crezca con el número de archivos omitidos).
    /// </summary>
    public required IReadOnlyList<SnapshotWarning> Warnings { get; init; }

    /// <summary>Número total de avisos de la ejecución.</summary>
    public required long WarningCount { get; init; }
}
