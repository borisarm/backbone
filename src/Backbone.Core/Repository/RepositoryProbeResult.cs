namespace Backbone.Core.Repository;

/// <summary>Qué hay en una carpeta de destino (HU-06).</summary>
public enum RepositoryState
{
    /// <summary>La carpeta no existe.</summary>
    Missing,

    /// <summary>
    /// La carpeta está vacía, o solo contiene los restos de una inicialización interrumpida
    /// (carpetas del formato vacías y temporales): se puede inicializar.
    /// </summary>
    Empty,

    /// <summary>Hay otros archivos y no hay <c>repo.json</c>: no es un repositorio y no se debe escribir.</summary>
    NotARepository,

    /// <summary>Repositorio v1 válido.</summary>
    Compatible,

    /// <summary>Repositorio de un formato superior o con características desconocidas (RNF-14).</summary>
    Incompatible,

    /// <summary>Hay <c>repo.json</c> pero el repositorio no es válido.</summary>
    Corrupt,
}

/// <summary>Resultado de <see cref="BackupRepository.ProbeAsync"/>.</summary>
/// <param name="State">Estado.</param>
/// <param name="FormatVersion">Versión de formato declarada, si se pudo leer.</param>
/// <param name="Configuration">Configuración, si el repositorio es compatible.</param>
/// <param name="Detail">Explicación técnica (no localizada) para registros.</param>
public sealed record RepositoryProbeResult(
    RepositoryState State,
    int? FormatVersion = null,
    RepositoryConfiguration? Configuration = null,
    string? Detail = null);

/// <summary>Opciones de <see cref="BackupRepository.InitializeAsync"/>.</summary>
public sealed record RepositoryInitializationOptions
{
    /// <summary>Texto de <c>createdBy</c>, p. ej. "Backbone 1.0.0 en PC-SALON" (lo compone el servicio).</summary>
    public required string CreatedBy { get; init; }

    /// <summary>Identificador del repositorio; por defecto, uno nuevo.</summary>
    public Guid? RepositoryId { get; init; }
}
