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
/// <b>Puntos de reanálisis</b> (RF-06): la implementación no los sigue; los devuelve con
/// <see cref="FileAttributes.ReparsePoint"/> en <see cref="SourceEntry.Attributes"/> y el motor los omite.
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
    /// (una raíz de origen o el <see cref="SourceEntry.FullPath"/> de una carpeta). El orden no está definido.
    /// </summary>
    /// <exception cref="IOException">La carpeta no existe o no se puede leer.</exception>
    /// <exception cref="UnauthorizedAccessException">Sin permiso para leer la carpeta.</exception>
    IAsyncEnumerable<SourceEntry> EnumerateAsync(string directoryPath, CancellationToken cancellationToken);

    /// <summary>Abre un archivo (<see cref="SourceEntry.FullPath"/>) para lectura secuencial.</summary>
    /// <exception cref="IOException">El archivo no existe, está bloqueado o no se puede leer.</exception>
    /// <exception cref="UnauthorizedAccessException">Sin permiso de lectura.</exception>
    Task<Stream> OpenReadAsync(string filePath, CancellationToken cancellationToken);

    /// <summary>
    /// Vuelve a leer los metadatos de un archivo (<see cref="SourceEntry.FullPath"/>), sin abrir su contenido;
    /// <see langword="null"/> si ya no existe. El motor lo usa después de copiarlo para saber si cambió durante la copia
    /// (§6.4.2).
    /// </summary>
    Task<SourceEntry?> GetFileInfoAsync(string filePath, CancellationToken cancellationToken);
}

/// <summary>Archivo o carpeta de origen.</summary>
/// <param name="Name">Nombre del elemento dentro de su carpeta (no confiable: el motor lo valida antes de usarlo).</param>
/// <param name="FullPath">Ruta opaca con la que se vuelve a pedir el elemento a <see cref="ISourceFileSystem"/>.</param>
/// <param name="IsDirectory">Si es una carpeta.</param>
/// <param name="Length">Tamaño en bytes; 0 en carpetas.</param>
/// <param name="LastWriteTimeUtc">Última modificación (UTC).</param>
/// <param name="Attributes">Atributos del archivo; <see cref="FileAttributes.ReparsePoint"/> si es un punto de reanálisis.</param>
public sealed record SourceEntry(
    string Name,
    string FullPath,
    bool IsDirectory,
    long Length,
    DateTime LastWriteTimeUtc,
    FileAttributes Attributes);

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
