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
/// <remarks>
/// <see cref="MandatoryExclusions"/> son las exclusiones obligatorias de ADR-16 (§8.3 "Carpetas protegidas"): rutas
/// relativas a la raíz del origen (p. ej. <c>ProgramData\Backbone</c> o <c>ProgramData\Backbone.untrusted-…</c>) que el
/// servicio calcula a partir de las rutas finales. No son patrones <i>glob</i> de RF-04: el motor no entra nunca en ellas
/// (no las enumera ni abre nada debajo), no genera avisos por ellas y no aparecen en el snapshot, tampoco lo que hubiera
/// debajo en el snapshot padre. Como son relativas al origen, valen igual si <see cref="ReadPath"/> apunta a una
/// instantánea VSS. Se comparan sin distinguir mayúsculas (ordinal), por segmentos completos; admiten <c>\</c> o
/// <c>/</c> como separador.
/// </remarks>
public sealed record BackupSource(string Alias, string Path, string ReadPath)
{
    /// <summary>
    /// Exclusiones obligatorias del origen (ADR-16): rutas relativas a su raíz, no vacías, sin segmentos vacíos, <c>.</c>,
    /// <c>..</c> ni <c>:</c>, y con cada segmento válido en un snapshot (<c>SnapshotPaths.IsValidSegment</c>). Una ruta no
    /// válida hace que la copia se rechace con <see cref="ArgumentException"/> antes de leer nada.
    /// </summary>
    public IReadOnlyList<string> MandatoryExclusions { get; init; } = [];
}

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

    /// <summary>
    /// Patrones <i>glob</i> de exclusión del trabajo (RF-04, HU-08; sintaxis en <see cref="ExclusionPattern"/>), como mucho
    /// <see cref="ContractLimits.MaxExclusions"/>. Lo excluido no se recorre, no se abre, no genera avisos y no aparece en el
    /// snapshot. Es independiente de <see cref="BackupSource.MandatoryExclusions"/>, que se aplican siempre, también con
    /// esta lista vacía. Un patrón no válido hace que la copia se rechace con <see cref="ArgumentException"/> antes de leer
    /// nada.
    /// </summary>
    public IReadOnlyList<string> Exclusions { get; init; } = [];

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
    /// Avisos de la ejecución, en orden, como mucho <see cref="SnapshotLimits.MaxWarnings"/> y mientras la suma de sus
    /// textos en UTF-8 no pase de <see cref="SnapshotLimits.TruncatedWarningsHeaderBytes"/> (§8.4 "Memoria del motor"); los
    /// demás solo se cuentan en <see cref="WarningCount"/>, para que la memoria no crezca con el número de archivos omitidos
    /// ni con la longitud de sus rutas.
    /// </summary>
    public required IReadOnlyList<SnapshotWarning> Warnings { get; init; }

    /// <summary>Número total de avisos de la ejecución.</summary>
    public required long WarningCount { get; init; }

    /// <summary>
    /// Snapshots del trabajo descartados como padre por tener fecha futura (ADR-17, §6.3 "Snapshot padre"), como mucho
    /// <see cref="BackupEngine.MaxReportedIgnoredParents"/>; el total está en <see cref="IgnoredParentCount"/>. El motor
    /// no tiene registro propio: el servicio los registra en el log (los identificadores tienen formato válido, pero se
    /// sanean igual, §6.1).
    /// </summary>
    public IReadOnlyList<IgnoredParentCandidate> IgnoredParents { get; init; } = [];

    /// <summary>Número total de snapshots descartados como padre por tener fecha futura.</summary>
    public long IgnoredParentCount { get; init; }
}

/// <summary>Snapshot descartado como padre por tener fecha futura (ADR-17).</summary>
/// <param name="SnapshotId">Identificador del snapshot.</param>
/// <param name="DateUtc">Fecha que lo descarta: la de su identificador o su <c>startedUtc</c>.</param>
/// <param name="LimitUtc">Inicio de la copia más la tolerancia de reloj (15 min).</param>
/// <param name="HeaderRead">Si se leyó su cabecera (<see langword="false"/>: descartado solo por el identificador).</param>
public sealed record IgnoredParentCandidate(string SnapshotId, DateTime DateUtc, DateTime LimitUtc, bool HeaderRead);
