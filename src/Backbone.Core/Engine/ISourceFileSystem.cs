using Backbone.Contracts;

namespace Backbone.Core.Engine;

/// <summary>
/// Acceso de solo lectura a los archivos de origen de una copia (HU-13). El motor no conoce el sistema operativo: recorre
/// los orígenes y abre sus archivos exclusivamente a través de esta interfaz, para poder simular el disco, contar las
/// lecturas e inyectar fallos en pruebas (§11).
/// </summary>
/// <remarks>
/// <para>
/// La implementación real vive en <c>Backbone.Service</c>, junto con lo demás que depende de Windows: recibe rutas ya
/// resueltas (p. ej. traducidas a la instantánea VSS, RF-22) y decide cómo abrir los archivos (semántica de copia de
/// seguridad, compartición). Las rutas son opacas para el motor: solo usa <see cref="SourceEntry.FullPath"/> tal como lo
/// devuelve <see cref="EnumerateAsync"/>, sin componerlas ni interpretarlas, así que las rutas largas (RNF-09) dependen
/// solo de la implementación.
/// </para>
/// <para>
/// <b>Contrato de seguridad</b> (§8.2 "Lectura de los orígenes", revisión de seguridad de HU-13, MEDIA-2). El servicio lee
/// como SYSTEM con semántica de copia de seguridad, así que la implementación nunca debe llevar la lectura fuera del
/// origen siguiendo un punto de reanálisis (<i>junction</i>, enlace simbólico u otro), tampoco si un elemento se sustituye
/// por uno después de enumerarlo (p. ej. hacia <c>%ProgramData%\Backbone</c>, §8.3):
/// </para>
/// <list type="bullet">
/// <item><description>
/// <see cref="EnumerateAsync"/>, <see cref="OpenReadAsync"/> y <see cref="GetFileInfoAsync"/> abren el elemento por handle
/// <b>sin seguir puntos de reanálisis</b> (<c>CreateFile</c> con <c>FILE_FLAG_OPEN_REPARSE_POINT</c>, más
/// <c>FILE_FLAG_BACKUP_SEMANTICS</c> en carpetas y, para leer como SYSTEM, en archivos).
/// </description></item>
/// <item><description>
/// Sobre <b>ese mismo handle</b>, antes de leer nada, comprueban (<c>GetFileInformationByHandleEx</c>,
/// <c>FileAttributeTagInfo</c>) los atributos y la etiqueta de reanálisis, y que es carpeta o archivo según lo pedido, y
/// repiten la clasificación de ADR-19 (<see cref="SourceAttributes.Classify(FileAttributes, uint)"/>, §8.2 "Clasificación
/// por etiqueta"), porque el elemento puede haber cambiado desde que se enumeró: un <b>enlace</b> lanza
/// <see cref="SourceAccessException"/> con <see cref="RunIssueCode.ReparsePointSkipped"/> y no se sigue (RF-06); <b>solo
/// en la nube</b> (también por el estado <c>PARTIAL</c> del marcador, que la enumeración no muestra),
/// <see cref="RunIssueCode.CloudOnlySkipped"/>; <b>no legible</b> (etiqueta fuera de la lista blanca),
/// <see cref="RunIssueCode.ReadError"/> con la etiqueta en hexadecimal. Lo de la lista blanca se lee por su contenido
/// (§8.2 "Apertura para leer el contenido").
/// </description></item>
/// <item><description>
/// La enumeración se hace <b>desde ese handle</b> (<c>FileIdBothDirectoryInfo</c> o <c>FileFullDirectoryInfo</c>), sin
/// volver a abrir la ruta; el contenido se lee del handle de <see cref="OpenReadAsync"/> y los metadatos de
/// <see cref="GetFileInfoAsync"/>, del suyo.
/// </description></item>
/// <item><description>
/// Cada elemento enumerado lleva en <see cref="SourceEntry.Attributes"/> sus atributos tal como los da el sistema y en
/// <see cref="SourceEntry.ReparseTag"/> su etiqueta (de <c>EaSize</c> de <c>FILE_ID_BOTH_DIR_INFO</c> cuando hay
/// <c>FILE_ATTRIBUTE_REPARSE_POINT</c>; 0 si no). El motor clasifica cada uno con esos dos valores antes de abrirlo y no
/// abre ni enumera los enlaces, los que están solo en la nube ni los no legibles.
/// </description></item>
/// <item><description>
/// <b>Ruta final sobre el mismo handle</b> (ADR-18, revisión de seguridad final de HU-13, M-A).
/// <c>FILE_FLAG_OPEN_REPARSE_POINT</c> solo afecta al último componente: en una copia en vivo (sin VSS, RF-22) una carpeta
/// <b>intermedia</b> ya enumerada se puede sustituir por un <i>junction</i> y la ruta se resolvería a través de él. Por eso,
/// en las tres operaciones, tras abrir el elemento y antes de leer nada, la implementación obtiene sobre ese mismo handle su
/// ruta final (<c>GetFinalPathNameByHandle</c> con <c>FILE_NAME_NORMALIZED | VOLUME_NAME_NT</c>, que vale igual dentro de
/// una instantánea VSS sin letra de unidad) y comprueba (1) que es <b>exactamente</b> la ruta final de la raíz del origen
/// más la ruta relativa del elemento (ordinal sin distinguir mayúsculas, por segmentos completos; la ruta final de la raíz
/// se obtiene una vez al empezar abriendo <see cref="BackupSource.ReadPath"/> por handle con las mismas marcas) y (2) que la
/// ruta relativa no es ni empieza por ninguna exclusión obligatoria del origen (<see cref="BackupSource.MandatoryExclusions"/>,
/// §8.3; defensa en profundidad, el motor no las pide nunca). Si no se cumple cualquiera de las dos, lanza
/// <see cref="SourceAccessException"/> con <see cref="RunIssueCode.ReparsePointSkipped"/> y no lee nada del handle.
/// <b>Alternativa válida</b>: abrir cada elemento relativo al handle de su carpeta padre (<c>NtCreateFile</c> con
/// <c>OBJECT_ATTRIBUTES.RootDirectory</c> = handle del padre, un solo componente y <c>OBJ_DONT_REPARSE</c>), manteniendo
/// abiertos los handles de las carpetas en recorrido; la comprobación de la ruta final se mantiene igualmente como defensa
/// en profundidad.
/// </description></item>
/// <item><description>
/// <b>Rutas y raíces</b>: la firma no cambia y las rutas siguen siendo opacas para el motor. La implementación se crea
/// <b>por ejecución</b> con la lista de orígenes (<see cref="BackupSource"/>: <see cref="BackupSource.ReadPath"/> y
/// exclusiones obligatorias) y deduce la ruta relativa de cada ruta recibida quitando el prefijo <c>ReadPath\</c> de su
/// origen. Rechaza con <see cref="SourceAccessException"/> y <see cref="RunIssueCode.PathInvalid"/> una ruta que no sea la
/// raíz de un origen de la ejecución ni empiece por ella, o que tenga segmentos vacíos, <c>.</c> o <c>..</c>. Las rutas que
/// recibe son la raíz <see cref="BackupSource.ReadPath"/> de un origen o las que devolvió ella misma
/// (<see cref="SourceEntry.FullPath"/>: raíz, <c>\</c> y nombre enumerado), así que esto no rechaza nada legítimo.
/// </description></item>
/// </list>
/// <para>
/// <b>Archivos y carpetas solo en la nube</b> (R4, RF-06, HU-08): el motor no abre ni enumera lo que la clasificación da
/// por solo en la nube (<see cref="SourceEntryKind.CloudOnly"/>) y conserva su versión del snapshot padre (en una carpeta,
/// todo lo que tenía debajo). Si, al abrir un archivo, al leer sus metadatos o al enumerar una carpeta, la implementación ve
/// sobre el handle que ha pasado a estar solo en la nube, lanza <see cref="SourceAccessException"/> con
/// <see cref="RunIssueCode.CloudOnlySkipped"/> sin leer nada, y el motor lo omite igual, conservando lo del padre.
/// </para>
/// <para>
/// <b>Plazo</b> (§8.2 "Plazo de las lecturas de los orígenes"): si una apertura, una enumeración, una consulta de metadatos
/// o la lectura de un bloque supera su plazo, la implementación lanza <see cref="SourceAccessException"/> con
/// <see cref="RunIssueCode.ReadError"/>; el motor omite el elemento y conserva su versión del padre (RF-25), también si
/// ocurre al volver a leer los metadatos después de copiarlo.
/// </para>
/// <para>
/// Si lo que falla es la <b>raíz</b> de un origen (cualquier error, también <see cref="RunIssueCode.ReparsePointSkipped"/>),
/// el motor emite <see cref="RunIssueCode.SourceRootUnreadable"/> y conserva todo el origen del snapshot padre (§8.4, R11).
/// </para>
/// <para>
/// <b>Errores</b>: un error de un archivo o de una carpeta se lanza como <see cref="IOException"/> (o una derivada) o
/// <see cref="UnauthorizedAccessException"/>; el motor omite ese elemento con un aviso y sigue (RF-25). Para indicar el
/// motivo exacto (p. ej. <see cref="RunIssueCode.FileLocked"/> ante una violación de compartición) se lanza
/// <see cref="SourceAccessException"/>. La cancelación se señala con <see cref="OperationCanceledException"/>.
/// </para>
/// </remarks>
public interface ISourceFileSystem
{
    /// <summary>
    /// Enumera, sin recursión, los archivos y carpetas contenidos directamente en <paramref name="directoryPath"/>
    /// (la raíz <see cref="BackupSource.ReadPath"/> de un origen o el <see cref="SourceEntry.FullPath"/> de una carpeta).
    /// El orden no está definido.
    /// </summary>
    /// <remarks>
    /// Abre la carpeta por handle sin seguir puntos de reanálisis, comprueba sobre ese handle que no es un punto de
    /// reanálisis y que su ruta final es la esperada, y enumera desde ese handle (ver el contrato de la interfaz). El error
    /// puede llegar al pedir el primer elemento o en cualquier momento de la enumeración.
    /// </remarks>
    /// <exception cref="SourceAccessException">
    /// Con <see cref="RunIssueCode.ReparsePointSkipped"/> si la carpeta es (o ha pasado a ser) un punto de reanálisis, o si
    /// su ruta final no es la ruta final de la raíz del origen más su ruta relativa o cae en una exclusión obligatoria; con
    /// <see cref="RunIssueCode.PathInvalid"/> si la ruta no es de un origen de la ejecución o tiene segmentos vacíos,
    /// <c>.</c> o <c>..</c>.
    /// </exception>
    /// <exception cref="IOException">La carpeta no existe o no se puede leer.</exception>
    /// <exception cref="UnauthorizedAccessException">Sin permiso para leer la carpeta.</exception>
    IAsyncEnumerable<SourceEntry> EnumerateAsync(string directoryPath, CancellationToken cancellationToken);

    /// <summary>Abre un archivo (<see cref="SourceEntry.FullPath"/>) para lectura secuencial.</summary>
    /// <remarks>
    /// Abre por handle sin seguir puntos de reanálisis, comprueba sobre ese handle que no es un punto de reanálisis y que
    /// su ruta final es la esperada, y devuelve un flujo sobre ese handle (ver el contrato de la interfaz).
    /// </remarks>
    /// <exception cref="SourceAccessException">
    /// Con <see cref="RunIssueCode.ReparsePointSkipped"/> si el archivo es (o ha pasado a ser) un punto de reanálisis, o si
    /// su ruta final no es la esperada o cae en una exclusión obligatoria; con <see cref="RunIssueCode.PathInvalid"/> si la
    /// ruta no es de un origen de la ejecución o tiene segmentos vacíos, <c>.</c> o <c>..</c>; con
    /// <see cref="RunIssueCode.FileLocked"/> si está bloqueado.
    /// </exception>
    /// <exception cref="IOException">El archivo no existe, está bloqueado o no se puede leer.</exception>
    /// <exception cref="UnauthorizedAccessException">Sin permiso de lectura.</exception>
    Task<Stream> OpenReadAsync(string filePath, CancellationToken cancellationToken);

    /// <summary>
    /// Vuelve a leer los metadatos de un archivo (<see cref="SourceEntry.FullPath"/>), sin abrir su contenido;
    /// <see langword="null"/> si ya no existe. El motor lo usa después de copiarlo para saber si cambió durante la copia
    /// (§6.4.2).
    /// </summary>
    /// <remarks>
    /// Abre por handle sin seguir puntos de reanálisis, hace sobre ese handle las mismas comprobaciones (punto de
    /// reanálisis, ruta final) y lee sus metadatos, con la etiqueta en <see cref="SourceEntry.ReparseTag"/> (ver el
    /// contrato de la interfaz). Para el motor, <see cref="RunIssueCode.ReparsePointSkipped"/> hace que el archivo se omita
    /// sin conservar nada; <see cref="RunIssueCode.CloudOnlySkipped"/> y <see cref="RunIssueCode.ReadError"/> (p. ej. plazo
    /// vencido), que se omita conservando su versión anterior; cualquier otro error, que cambió durante la copia.
    /// </remarks>
    /// <exception cref="SourceAccessException">
    /// Con <see cref="RunIssueCode.ReparsePointSkipped"/> si el archivo ha pasado a ser un punto de reanálisis, o si su ruta
    /// final no es la esperada o cae en una exclusión obligatoria; con <see cref="RunIssueCode.PathInvalid"/> si la ruta no
    /// es de un origen de la ejecución o tiene segmentos vacíos, <c>.</c> o <c>..</c>.
    /// </exception>
    /// <exception cref="IOException">No se pueden leer los metadatos.</exception>
    /// <exception cref="UnauthorizedAccessException">Sin permiso.</exception>
    Task<SourceEntry?> GetFileInfoAsync(string filePath, CancellationToken cancellationToken);
}

/// <summary>Archivo o carpeta de origen.</summary>
/// <param name="Name">Nombre del elemento dentro de su carpeta (no confiable: el motor lo valida antes de usarlo).</param>
/// <param name="FullPath">Ruta opaca con la que se vuelve a pedir el elemento a <see cref="ISourceFileSystem"/>.</param>
/// <param name="IsDirectory">Si es una carpeta.</param>
/// <param name="Length">Tamaño en bytes; 0 en carpetas.</param>
/// <param name="LastWriteTimeUtc">Última modificación (UTC).</param>
/// <param name="Attributes">
/// Atributos del archivo tal como los da el sistema; <see cref="FileAttributes.ReparsePoint"/> si es un punto de reanálisis
/// (con su etiqueta en <see cref="ReparseTag"/>).
/// </param>
public sealed record SourceEntry(
    string Name,
    string FullPath,
    bool IsDirectory,
    long Length,
    DateTime LastWriteTimeUtc,
    FileAttributes Attributes)
{
    /// <summary>
    /// Etiqueta de reanálisis (<c>IO_REPARSE_TAG_*</c>) si <see cref="Attributes"/> tiene
    /// <see cref="FileAttributes.ReparsePoint"/>; 0 si no (sin punto de reanálisis), y entonces el motor la ignora.
    /// </summary>
    /// <remarks>
    /// Con la clasificación de ADR-19 (<see cref="SourceAttributes.Classify(SourceEntry)"/>, §8.2 "Clasificación por
    /// etiqueta") el motor decide, antes de abrir nada, si el elemento es un enlace, está solo en la nube, no es legible o se
    /// lee por su contenido. En la enumeración de Windows sale de <c>EaSize</c> de <c>FILE_ID_BOTH_DIR_INFO</c> cuando hay
    /// <c>FILE_ATTRIBUTE_REPARSE_POINT</c>, y en <see cref="ISourceFileSystem.GetFileInfoAsync"/> de
    /// <c>FileAttributeTagInfo</c>. Un punto de reanálisis con etiqueta 0 (implementación que no la rellena) se trata como
    /// no legible, nunca como normal. Se declara aquí y no como parámetro posicional para que el valor por defecto no
    /// obligue a cambiar quien construye el tipo.
    /// </remarks>
    public uint ReparseTag { get; init; }
}

/// <summary>
/// Error de lectura de un elemento de origen con su motivo (<see cref="RunIssueCode"/>), para que el aviso de la ejecución
/// diga por qué se omitió (p. ej. <see cref="RunIssueCode.FileLocked"/>). La implementación de
/// <see cref="ISourceFileSystem"/> traduce a esta excepción los errores del sistema operativo que sabe distinguir.
/// </summary>
public sealed class SourceAccessException : IOException
{
    /// <summary>Crea la excepción con un motivo.</summary>
    public SourceAccessException(RunIssueCode code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    /// <summary>Crea la excepción con el motivo <see cref="RunIssueCode.ReadError"/>.</summary>
    public SourceAccessException()
        : this(RunIssueCode.ReadError, "No se pudo leer el elemento de origen.")
    {
    }

    /// <summary>Crea la excepción con el motivo <see cref="RunIssueCode.ReadError"/>.</summary>
    public SourceAccessException(string message)
        : this(RunIssueCode.ReadError, message)
    {
    }

    /// <summary>Crea la excepción con el motivo <see cref="RunIssueCode.ReadError"/>.</summary>
    public SourceAccessException(string message, Exception innerException)
        : this(RunIssueCode.ReadError, message, innerException)
    {
    }

    /// <summary>Motivo del error.</summary>
    public RunIssueCode Code { get; }
}
