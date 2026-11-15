namespace Backbone.Contracts;

/// <summary>
/// Límites de las entradas del contrato (§7.5, RNF-08). El servicio rechaza lo que los supera con
/// <see cref="BackboneErrorCode.ValidationFailed"/> (<see cref="ValidationIssueCode.LimitExceeded"/>); la app puede usarlos
/// para limitar los campos de texto.
/// </summary>
public static class ContractLimits
{
    /// <summary>Longitud máxima del nombre de un trabajo.</summary>
    public const int MaxJobNameLength = 128;

    /// <summary>Longitud máxima de un alias de origen.</summary>
    public const int MaxAliasLength = 64;

    /// <summary>Número máximo de orígenes de un trabajo.</summary>
    public const int MaxSources = 32;

    /// <summary>Número máximo de patrones de exclusión de un trabajo.</summary>
    public const int MaxExclusions = 256;

    /// <summary>Longitud máxima de un patrón de exclusión.</summary>
    public const int MaxExclusionPatternLength = 512;

    /// <summary>Número máximo de horas en una programación diaria.</summary>
    public const int MaxTimesOfDay = 24;

    /// <summary>Longitud máxima de una ruta local (orígenes, destino de restauración, exportación), en caracteres.</summary>
    public const int MaxLocalPathLength = 32_767;

    /// <summary>Longitud máxima de un destino UNC.</summary>
    public const int MaxDestinationLength = 1_024;

    /// <summary>Longitud máxima de una ruta de snapshot.</summary>
    public const int MaxSnapshotPathLength = 32_767;

    /// <summary>Longitud máxima de un segmento (nombre de archivo o carpeta).</summary>
    public const int MaxSegmentLength = 255;

    /// <summary>Número máximo de rutas en <see cref="RestoreRequest.Paths"/>.</summary>
    public const int MaxRestorePaths = 1_000;

    /// <summary>Tamaño máximo de página (historial, exploración de snapshots, búsqueda).</summary>
    public const int MaxPageSize = 1_000;

    /// <summary>Longitud máxima del texto de búsqueda en un snapshot.</summary>
    public const int MaxSearchTextLength = 256;

    /// <summary>Longitud máxima de una cuenta (<c>DOMINIO\usuario</c> o SID) o de un usuario del NAS.</summary>
    public const int MaxAccountNameLength = 256;

    /// <summary>Longitud máxima de una contraseña del NAS.</summary>
    public const int MaxPasswordLength = 256;

    /// <summary>
    /// Profundidad máxima de anidamiento JSON. Es el valor por defecto de System.Text.Json (64), que aplica
    /// <c>SystemTextJsonFormatter</c> sin cambiar sus opciones (ADR-10).
    /// </summary>
    public const int MaxJsonDepth = 64;
}
