namespace Backbone.Core.Repository.Snapshots;

/// <summary>Estado de un snapshot completado (§6.3).</summary>
public enum SnapshotStatus
{
    /// <summary><c>completed</c>.</summary>
    Completed,

    /// <summary><c>completedWithWarnings</c>.</summary>
    CompletedWithWarnings,
}

/// <summary>Origen incluido en el snapshot.</summary>
/// <param name="Alias">Alias único dentro del trabajo; primer segmento de las rutas de sus entradas.</param>
/// <param name="Path">Ruta local del origen en el momento de la copia.</param>
public sealed record SnapshotSource(string Alias, string Path);

/// <summary>Estadísticas del snapshot.</summary>
public sealed record SnapshotStats
{
    /// <summary>Número de archivos (sin contar carpetas vacías). Lo calcula el escritor.</summary>
    public long Files { get; init; }

    /// <summary>Suma de tamaños de los archivos. Lo calcula el escritor.</summary>
    public long Bytes { get; init; }

    /// <summary>Archivos que no estaban en el snapshot padre.</summary>
    public long NewFiles { get; init; }

    /// <summary>Archivos que han cambiado respecto al padre.</summary>
    public long ChangedFiles { get; init; }

    /// <summary>Objetos escritos en esta ejecución.</summary>
    public long NewObjects { get; init; }

    /// <summary>Bytes de los objetos escritos en esta ejecución.</summary>
    public long NewBytes { get; init; }

    /// <summary>Archivos omitidos.</summary>
    public long Skipped { get; init; }
}

/// <summary>Aviso registrado durante la ejecución.</summary>
/// <param name="Path">Ruta afectada (formato de <see cref="SnapshotEntry.Path"/> si es posible).</param>
/// <param name="Code">Código del aviso (p. ej. <c>FileLocked</c>).</param>
/// <param name="Message">Descripción.</param>
public sealed record SnapshotWarning(string Path, string Code, string Message);

/// <summary>Datos conocidos al empezar a escribir un snapshot.</summary>
public sealed record SnapshotStart
{
    /// <summary>Identificador del trabajo.</summary>
    public required string JobId { get; init; }

    /// <summary>Nombre del trabajo.</summary>
    public required string JobName { get; init; }

    /// <summary>Equipo que hace la copia.</summary>
    public required string Host { get; init; }

    /// <summary>Inicio de la ejecución (UTC); determina el identificador.</summary>
    public required DateTime StartedUtc { get; init; }

    /// <summary>Snapshot anterior del trabajo, si lo hay.</summary>
    public string? ParentId { get; init; }

    /// <summary>Si se leyó desde una instantánea VSS.</summary>
    public required bool UsedVss { get; init; }

    /// <summary>Orígenes del trabajo (al menos uno).</summary>
    public required IReadOnlyList<SnapshotSource> Sources { get; init; }
}

/// <summary>Datos conocidos al terminar un snapshot.</summary>
public sealed record SnapshotCompletion
{
    /// <summary>Estado final.</summary>
    public required SnapshotStatus Status { get; init; }

    /// <summary>
    /// Estadísticas; <see cref="SnapshotStats.Files"/> y <see cref="SnapshotStats.Bytes"/> se ignoran
    /// porque el escritor las calcula a partir de las entradas.
    /// </summary>
    public SnapshotStats Stats { get; init; } = new();

    /// <summary>Avisos de la ejecución.</summary>
    public IReadOnlyList<SnapshotWarning> Warnings { get; init; } = [];

    /// <summary>
    /// Avisos de la ejecución que no se pasan en <see cref="Warnings"/> porque el llamante no los guarda en memoria (van
    /// detrás de ellos). Cuentan para el límite de avisos y para el aviso resumen <c>WarningsTruncated</c> (§6.3).
    /// </summary>
    public long OmittedWarnings { get; init; }
}

/// <summary>Cabecera de un snapshot: todo menos las entradas.</summary>
public sealed record SnapshotHeader
{
    /// <summary>Versión de formato del manifiesto.</summary>
    public required int FormatVersion { get; init; }

    /// <summary>Identificador (<c>yyyyMMddTHHmmssZ-xxxx</c>).</summary>
    public required string Id { get; init; }

    /// <inheritdoc cref="SnapshotStart.JobId"/>
    public required string JobId { get; init; }

    /// <inheritdoc cref="SnapshotStart.JobName"/>
    public required string JobName { get; init; }

    /// <inheritdoc cref="SnapshotStart.Host"/>
    public required string Host { get; init; }

    /// <inheritdoc cref="SnapshotStart.StartedUtc"/>
    public required DateTime StartedUtc { get; init; }

    /// <summary>Fin de la ejecución (UTC).</summary>
    public required DateTime CompletedUtc { get; init; }

    /// <inheritdoc cref="SnapshotStart.ParentId"/>
    public string? ParentId { get; init; }

    /// <inheritdoc cref="SnapshotCompletion.Status"/>
    public required SnapshotStatus Status { get; init; }

    /// <inheritdoc cref="SnapshotStart.UsedVss"/>
    public required bool UsedVss { get; init; }

    /// <inheritdoc cref="SnapshotStart.Sources"/>
    public required IReadOnlyList<SnapshotSource> Sources { get; init; }

    /// <summary>Estadísticas.</summary>
    public required SnapshotStats Stats { get; init; }

    /// <summary>Avisos.</summary>
    public required IReadOnlyList<SnapshotWarning> Warnings { get; init; }
}

/// <summary>
/// Entrada del manifiesto: un archivo o una carpeta vacía (§6.3).
/// </summary>
public sealed record SnapshotEntry
{
    /// <summary>Atributos que se guardan; el resto se descartan al crear la entrada.</summary>
    public const FileAttributes StoredAttributes =
        FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive;

    private SnapshotEntry(string path, bool isDirectory, ContentHash? hash, long size, DateTime modifiedUtc, FileAttributes attributes)
    {
        Path = path;
        IsDirectory = isDirectory;
        Hash = hash;
        Size = size;
        ModifiedUtc = modifiedUtc;
        Attributes = attributes;
    }

    /// <summary>
    /// Ruta relativa con <c>/</c>, prefijada por el alias del origen. Las carpetas vacías terminan en <c>/</c>.
    /// </summary>
    public string Path { get; }

    /// <summary>Si es una carpeta vacía (<c>"d": true</c>).</summary>
    public bool IsDirectory { get; }

    /// <summary>Hash del contenido; <see langword="null"/> en carpetas.</summary>
    public ContentHash? Hash { get; }

    /// <summary>Tamaño en bytes; 0 en carpetas.</summary>
    public long Size { get; }

    /// <summary>Última modificación (UTC, 100 ns); <see cref="DateTime.MinValue"/> en carpetas.</summary>
    public DateTime ModifiedUtc { get; }

    /// <summary>Atributos guardados (<see cref="StoredAttributes"/>); <see cref="FileAttributes.None"/> en carpetas.</summary>
    public FileAttributes Attributes { get; }

    /// <summary>Crea la entrada de un archivo. Valida la ruta y descarta los atributos que no se guardan.</summary>
    /// <exception cref="ArgumentException">Ruta, tamaño o fecha no válidos.</exception>
    public static SnapshotEntry File(string path, ContentHash hash, long size, DateTime modifiedUtc, FileAttributes attributes)
    {
        if (!EntryPath.IsValidFilePath(path))
        {
            throw new ArgumentException($"Ruta de entrada no válida: '{Untrusted.Describe(path)}'.", nameof(path));
        }

        if (hash == default)
        {
            throw new ArgumentException("El hash no está inicializado.", nameof(hash));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(size);
        JsonFormat.RequireUtc(modifiedUtc, nameof(modifiedUtc));
        return new SnapshotEntry(path, isDirectory: false, hash, size, modifiedUtc, attributes & StoredAttributes);
    }

    /// <summary>Crea la entrada de una carpeta vacía; la ruta termina en <c>/</c>.</summary>
    /// <exception cref="ArgumentException">Ruta no válida.</exception>
    public static SnapshotEntry Directory(string path)
    {
        if (!EntryPath.IsValidDirectoryPath(path))
        {
            throw new ArgumentException($"Ruta de carpeta no válida: '{Untrusted.Describe(path)}'.", nameof(path));
        }

        return new SnapshotEntry(path, isDirectory: true, hash: null, 0, DateTime.MinValue, FileAttributes.None);
    }

    /// <summary>Alias del origen: primer segmento de la ruta.</summary>
    public string Alias => Path[..Path.IndexOf('/', StringComparison.Ordinal)];
}
