namespace Backbone.Core.Storage;

/// <summary>
/// Acceso al sistema de archivos de un repositorio (§6). Todo el motor lee y escribe el
/// repositorio exclusivamente a través de esta interfaz, para poder simular el disco e
/// inyectar fallos en pruebas (§11).
/// </summary>
/// <remarks>
/// <para>
/// Las rutas son relativas a la raíz del repositorio, con <c>/</c> como separador
/// (p. ej. <c>objects/ab/ab12…</c>). <see cref="RepositoryPath.Validate"/> define las rutas
/// admitidas; las implementaciones rechazan cualquier otra con <see cref="ArgumentException"/>.
/// La cadena vacía designa la raíz y solo se admite en las operaciones de carpeta.
/// </para>
/// <para>
/// Las implementaciones deben ser seguras para uso concurrente. Las comparaciones de nombres
/// no distinguen mayúsculas (como NTFS y SMB).
/// </para>
/// <para>
/// Cualquier operación puede lanzar <see cref="Repository.RepositoryException"/> con
/// <see cref="Repository.RepositoryErrorKind.Corrupt"/> si encuentra un punto de reanálisis (<i>junction</i>, enlace
/// simbólico u otro) en la raíz, en una carpeta intermedia, en el propio elemento o, al enumerar, en un elemento de la
/// carpeta (§6.1): la operación falla sin tocar nada.
/// </para>
/// <para>
/// Los nombres que devuelve <see cref="EnumerateAsync"/> son los del almacenamiento y no confiables: pueden no cumplir
/// <see cref="RepositoryPath.IsValidSegment"/> (un NAS admite nombres que Backbone rechaza); el llamante los comprueba
/// antes de componer rutas con ellos.
/// </para>
/// </remarks>
public interface IRepositoryStorage
{
    /// <summary>Abre un archivo existente para lectura secuencial.</summary>
    /// <exception cref="FileNotFoundException">El archivo (o su carpeta) no existe.</exception>
    Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// Crea un archivo nuevo y lo abre para escritura. Falla si ya existe.
    /// Solo el motor lo usa, y siempre dentro de <c>tmp/</c> (§6.4.1).
    /// </summary>
    /// <remarks>
    /// Tras <see cref="Stream.FlushAsync(CancellationToken)"/> los datos escritos son duraderos
    /// (llegan al almacenamiento estable). El llamante debe vaciar el flujo antes de liberarlo
    /// si quiere que el contenido sobreviva a un corte.
    /// </remarks>
    /// <exception cref="IOException">El archivo ya existe.</exception>
    /// <exception cref="DirectoryNotFoundException">La carpeta que lo contiene no existe.</exception>
    Task<Stream> CreateNewAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// Renombra un archivo de forma atómica dentro del repositorio.
    /// </summary>
    /// <param name="sourcePath">Archivo de origen (existente).</param>
    /// <param name="destinationPath">Nombre final; su carpeta debe existir.</param>
    /// <param name="overwrite">
    /// Si es <see langword="true"/>, sustituye el destino si existe; si no, el destino no se toca.
    /// </param>
    /// <returns>
    /// <see langword="false"/> si <paramref name="overwrite"/> es <see langword="false"/> y el destino ya existía
    /// (el origen se queda donde estaba); <see langword="true"/> si se ha renombrado.
    /// </returns>
    /// <exception cref="FileNotFoundException">El origen no existe.</exception>
    /// <exception cref="DirectoryNotFoundException">La carpeta de destino no existe.</exception>
    Task<bool> TryMoveAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken);

    /// <summary>Borra un archivo. No falla si no existe.</summary>
    Task DeleteFileAsync(string path, CancellationToken cancellationToken);

    /// <summary>Crea una carpeta (y sus antecesoras). No falla si ya existe.</summary>
    Task CreateDirectoryAsync(string path, CancellationToken cancellationToken);

    /// <summary>Indica si existe la carpeta.</summary>
    Task<bool> DirectoryExistsAsync(string path, CancellationToken cancellationToken);

    /// <summary>Devuelve la información de un archivo o <see langword="null"/> si no existe.</summary>
    Task<StorageEntry?> GetFileInfoAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// Enumera, sin recursión, los archivos y carpetas contenidos directamente en <paramref name="directoryPath"/>.
    /// Si la carpeta no existe, la secuencia está vacía. El orden no está definido.
    /// </summary>
    IAsyncEnumerable<StorageEntry> EnumerateAsync(string directoryPath, CancellationToken cancellationToken);
}
