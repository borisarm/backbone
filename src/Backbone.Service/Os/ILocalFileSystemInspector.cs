namespace Backbone.Service.Os;

/// <summary>Resultado de abrir una carpeta local de origen (<see cref="ILocalFileSystemInspector.InspectFolder"/>).</summary>
internal enum LocalFolderStatus
{
    /// <summary>Existe y es una carpeta; <see cref="LocalFolderInfo.FinalPath"/> y el volumen son válidos.</summary>
    Ok = 0,

    /// <summary>No existe (ni la carpeta ni alguna de las de encima).</summary>
    NotFound = 1,

    /// <summary>Existe pero es un archivo.</summary>
    NotADirectory = 2,

    /// <summary>La ruta no es local absoluta con letra de unidad (<c>X:\…</c>) o tiene caracteres no válidos.</summary>
    InvalidPath = 3,

    /// <summary>Existe pero no se puede abrir o consultar (acceso denegado, error de E/S).</summary>
    Unreadable = 4,

    /// <summary>Está en una unidad de red: no se abre (no se accede a la red con la cuenta del equipo).</summary>
    Remote = 5,
}

/// <summary>Tipo del volumen en que está la carpeta (<c>GetDriveType</c>).</summary>
internal enum LocalVolumeKind
{
    /// <summary>Desconocido, CD-ROM, disco RAM o sin volumen.</summary>
    Other = 0,

    /// <summary>Disco fijo.</summary>
    Fixed = 1,

    /// <summary>Unidad de red.</summary>
    Remote = 2,

    /// <summary>Unidad extraíble (memoria USB, tarjeta).</summary>
    Removable = 3,
}

/// <summary>Carpeta local abierta siguiendo los puntos de reanálisis.</summary>
/// <param name="Status">Resultado.</param>
/// <param name="FinalPath">
/// Ruta final (<c>GetFinalPathNameByHandle</c>, <c>FILE_NAME_NORMALIZED</c>) sin el prefijo <c>\\?\</c>: <c>C:\Users\ana</c>,
/// <c>\\servidor\recurso\…</c> si el enlace lleva a la red. Vacía si <paramref name="Status"/> no es
/// <see cref="LocalFolderStatus.Ok"/>.
/// </param>
/// <param name="FileSystem">Nombre del sistema de archivos del volumen de la ruta final (<c>NTFS</c>, <c>ReFS</c>, <c>FAT32</c>…).</param>
/// <param name="Volume">Tipo del volumen de la ruta final.</param>
internal sealed record LocalFolderInfo(
    LocalFolderStatus Status,
    string FinalPath = "",
    string FileSystem = "",
    LocalVolumeKind Volume = LocalVolumeKind.Other);

/// <summary>Carpetas reales encontradas por <see cref="ILocalFileSystemInspector.FindRealDirectories"/>.</summary>
/// <param name="Names">Nombres tal como están en disco; como mucho el máximo pedido.</param>
/// <param name="TotalCount">Cuántas cumplen las condiciones, aunque superen el máximo.</param>
internal sealed record RealDirectoryScan(IReadOnlyList<string> Names, int TotalCount);

/// <summary>
/// Consultas de solo lectura sobre carpetas locales que necesitan la validación de trabajos (RF-03, HU-07) y el cálculo de
/// las carpetas protegidas (§8.3). Implementación real: <see cref="WindowsLocalFileSystemInspector"/>; las pruebas usan un
/// doble.
/// </summary>
internal interface ILocalFileSystemInspector
{
    /// <summary>
    /// Abre <paramref name="path"/> por handle <b>siguiendo</b> los puntos de reanálisis y obtiene, sobre ese handle, su ruta
    /// final, el sistema de archivos y el tipo de volumen (§8.3, punto 1). Una unidad de red no se abre.
    /// </summary>
    LocalFolderInfo InspectFolder(string path);

    /// <summary>Ruta final (sin <c>\\?\</c>) de una carpeta, siguiendo enlaces (<c>%ProgramData%</c>, §8.3).</summary>
    /// <exception cref="IOException">No existe o no se puede abrir.</exception>
    string GetFinalDirectoryPath(string path);

    /// <summary>
    /// Enumera <paramref name="directory"/> por handle y devuelve las entradas cuyo nombre empieza por
    /// <paramref name="namePrefix"/> (ordinal, sin distinguir mayúsculas) y que son <b>carpetas reales</b>: cada una se abre
    /// con <c>FILE_FLAG_OPEN_REPARSE_POINT</c> y, sobre ese handle, un punto de reanálisis o un archivo se ignora (M-B). Una
    /// entrada que no se puede abrir se cuenta como carpeta (falla cerrado).
    /// </summary>
    /// <exception cref="IOException">La carpeta no se puede abrir o enumerar.</exception>
    RealDirectoryScan FindRealDirectories(string directory, string namePrefix, int maxNames);
}
