using Backbone.Contracts;

namespace Backbone.Core.Repository.Snapshots;

/// <summary>
/// Límites de un manifiesto (§6.3 "Límites", M-2, ADR-11). El lector trata como corrupción lo que los supera. El escritor
/// nunca publica un manifiesto que el lector rechace: recorta los textos de los avisos y descarta los avisos que no caben
/// (con un aviso resumen <c>WarningsTruncated</c>), rechaza al empezar con <see cref="ArgumentException"/> los demás campos
/// de cabecera fuera de límite y falla la copia si se superan las entradas o el tamaño total.
/// </summary>
public static class SnapshotLimits
{
    /// <summary>
    /// Presupuesto de la cabecera: todo lo anterior a las entradas, desde la <c>{</c> inicial hasta <c>"entries":[</c>
    /// incluido, con las propiedades desconocidas (16 MiB).
    /// </summary>
    public const int MaxHeaderBytes = 16 * 1024 * 1024;

    /// <summary>
    /// Tamaño máximo de una unidad de la cabecera (256 KiB): el valor de una propiedad simple o desconocida, <c>stats</c>,
    /// un elemento de <c>sources</c> o de <c>warnings</c>, y cada propiedad posterior a <c>entries</c>. Un aviso con sus
    /// tres textos al máximo y todos los caracteres escapados ocupa unos 210 KB.
    /// </summary>
    public const int MaxHeaderUnitBytes = 256 * 1024;

    /// <summary>
    /// Tope de la cabecera mientras el escritor añade avisos cuando no caben todos (16 MiB − 256 KiB): deja sitio para el
    /// aviso resumen.
    /// </summary>
    public const int TruncatedWarningsHeaderBytes = MaxHeaderBytes - MaxHeaderUnitBytes;

    /// <summary>Tamaño máximo de una entrada serializada (una ruta de 32 767 caracteres ocupa hasta ~98 KB en UTF-8).</summary>
    public const int MaxEntryUnitBytes = 1024 * 1024;

    /// <summary>Número máximo de avisos (incluido el aviso resumen, si lo hay).</summary>
    public const int MaxWarnings = 10_000;

    /// <summary>Número máximo de orígenes.</summary>
    public const int MaxSources = ContractLimits.MaxSources;

    /// <summary>Longitud máxima de <c>jobId</c>.</summary>
    public const int MaxJobIdLength = 128;

    /// <summary>Longitud máxima de <c>jobName</c>.</summary>
    public const int MaxJobNameLength = ContractLimits.MaxJobNameLength;

    /// <summary>Longitud máxima de <c>host</c>.</summary>
    public const int MaxHostLength = ContractLimits.MaxSegmentLength;

    /// <summary>Longitud máxima de la ruta local de un origen.</summary>
    public const int MaxSourcePathLength = ContractLimits.MaxLocalPathLength;

    /// <summary>Longitud máxima de la ruta de un aviso.</summary>
    public const int MaxWarningPathLength = ContractLimits.MaxSnapshotPathLength;

    /// <summary>Longitud máxima del código de un aviso.</summary>
    public const int MaxWarningCodeLength = 64;

    /// <summary>Longitud máxima del mensaje de un aviso.</summary>
    public const int MaxWarningMessageLength = 2048;

    /// <summary>Longitud máxima de un texto corto de la cabecera (<c>id</c>, <c>parentId</c>, <c>status</c>).</summary>
    public const int MaxShortTextLength = 64;
}
