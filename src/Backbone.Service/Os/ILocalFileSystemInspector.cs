namespace Backbone.Service.Os;

/// <summary>Resultado de la resolución controlada de una carpeta de origen (ADR-20, §8.3; códigos de §7.5).</summary>
internal enum LocalFolderStatus
{
    /// <summary>Carpeta local resuelta; <see cref="LocalFolderInfo.FinalPath"/> y <see cref="LocalFolderInfo.FileSystem"/> son válidos.</summary>
    Ok = 0,

    /// <summary>Un componente no existe, o el último es un archivo y no una carpeta (<c>SourceNotFound</c>).</summary>
    NotFound = 1,

    /// <summary>La ruta recibida no está normalizada (§7.5 "Ruta de origen"); no se abre nada (<c>SourcePathInvalid</c>).</summary>
    InvalidPath = 3,

    /// <summary>
    /// No se puede comprobar (<c>SourceUnreadable</c>): acceso denegado, error de E/S, plazo vencido, más de 63 saltos, un
    /// punto de reanálisis que no se atraviesa (solo en la nube, no poblado, etiqueta no legible) o una ruta final que no
    /// coincide con la resuelta.
    /// </summary>
    Unreadable = 4,

    /// <summary>
    /// No es local (<c>SourceNotLocal</c>): volumen que no es un disco local fijo (red, extraíble), o un enlace cuyo destino
    /// no es un volumen local (no se abre).
    /// </summary>
    NotLocal = 5,
}

/// <summary>Carpeta local resuelta sin dejar que el sistema siga ningún punto de reanálisis (ADR-20).</summary>
/// <param name="Status">Resultado.</param>
/// <param name="FinalPath">
/// Ruta final (<c>GetFinalPathNameByHandle</c>, <c>FILE_NAME_NORMALIZED | VOLUME_NAME_DOS</c>) sin el prefijo <c>\\?\</c>
/// (<c>C:\Users\ana</c>). Vacía si <paramref name="Status"/> no es <see cref="LocalFolderStatus.Ok"/>.
/// </param>
/// <param name="FileSystem">Sistema de archivos del volumen de la carpeta final (<c>NTFS</c>, <c>ReFS</c>, <c>FAT32</c>…).</param>
/// <param name="Detail">
/// Motivo técnico para el log (código de estado, destino de un enlace rechazado). Puede contener texto de un enlace plantado
/// por un usuario: se registra con <c>UntrustedText.Describe</c> y nunca va al cliente.
/// </param>
internal sealed record LocalFolderInfo(
    LocalFolderStatus Status,
    string FinalPath = "",
    string FileSystem = "",
    string Detail = "");

/// <summary>Carpetas reales encontradas por <see cref="ILocalFileSystemInspector.FindRealDirectories"/>.</summary>
/// <param name="Names">Nombres tal como están en disco; como mucho el máximo pedido.</param>
/// <param name="LimitExceeded">
/// Hay más del máximo pedido: la búsqueda ha parado al encontrar la siguiente (MEDIA-1) y <paramref name="Names"/> no está
/// completa.
/// </param>
internal sealed record RealDirectoryScan(IReadOnlyList<string> Names, bool LimitExceeded);

/// <summary>
/// Consultas de solo lectura sobre carpetas locales para la validación de trabajos (RF-03, HU-07), el <c>JobRunner</c> al
/// empezar cada ejecución y el cálculo de las carpetas protegidas (§8.3). Implementación real:
/// <see cref="WindowsLocalFileSystemInspector"/>; las pruebas usan un doble.
/// </summary>
/// <remarks>
/// Los métodos son síncronos y bloqueantes: se llaman desde el hilo de trabajo de <see cref="SourceValidationRunner"/>, nunca
/// desde el hilo RPC, y cada operación que puede llegar a un filtro pasa por <see cref="SourceIoGuard.Run{T}"/> (plazo de
/// §8.2). Pueden lanzar <see cref="SourceIoTimeoutException"/> y <see cref="OperationCanceledException"/>.
/// </remarks>
internal interface ILocalFileSystemInspector
{
    /// <summary>
    /// Resolución controlada (ADR-20, §8.3, pasos 1 a 7) de <paramref name="normalizedPath"/>, que debe venir normalizada
    /// (<see cref="Jobs.SourcePath.TryNormalize"/>, <c>X:\…</c>). Recorre la ruta componente a componente relativo al handle
    /// del padre, sin dejar que el sistema siga ningún punto de reanálisis, y solo continúa por un enlace cuyo destino sea un
    /// volumen local; cualquier otro destino da <see cref="LocalFolderStatus.NotLocal"/> sin abrirlo.
    /// </summary>
    LocalFolderInfo ResolveFolder(string normalizedPath, SourceIoGuard guard);

    /// <summary>
    /// Resuelve <paramref name="directory"/> como <see cref="ResolveFolder"/>, la enumera por handle y devuelve las entradas
    /// cuyo nombre empieza por <paramref name="namePrefix"/> (ordinal, sin distinguir mayúsculas) y que son <b>carpetas
    /// reales</b>: cada una se abre relativo a la carpeta, sin seguir puntos de reanálisis, y un punto de reanálisis o un
    /// archivo se ignora (M-B). Una entrada que no se puede abrir se cuenta como carpeta (falla cerrado). Deja de abrir
    /// entradas en cuanto ha encontrado <paramref name="maxNames"/> + 1 (MEDIA-1).
    /// </summary>
    /// <exception cref="IOException">La carpeta no se puede resolver, abrir o enumerar.</exception>
    RealDirectoryScan FindRealDirectories(string directory, string namePrefix, int maxNames, SourceIoGuard guard);
}
