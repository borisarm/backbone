namespace Backbone.Core.Repository;

/// <summary>Motivo de un <see cref="RepositoryException"/>.</summary>
/// <remarks>
/// Backbone.Service traduce estos valores a <c>BackboneErrorCode</c> (§7.4):
/// <see cref="Incompatible"/> → <c>RepositoryIncompatible</c>; <see cref="Locked"/> y <see cref="LockLost"/> →
/// <c>RepositoryLocked</c>; <see cref="Corrupt"/> y <see cref="ObjectNotFound"/> → <c>RepositoryCorrupt</c>;
/// <see cref="SnapshotNotFound"/> → <c>SnapshotNotFound</c>; <see cref="NotARepository"/>, <see cref="NotEmpty"/>
/// y <see cref="AlreadyInitialized"/> → <c>ValidationFailed</c>. La traducción de <see cref="SnapshotTooLarge"/> la decide el
/// servicio en la historia del <c>JobRunner</c> (HU-13).
/// </remarks>
public enum RepositoryErrorKind
{
    /// <summary>La carpeta no contiene un repositorio (no hay <c>repo.json</c>).</summary>
    NotARepository,

    /// <summary>Se intentó inicializar un repositorio en una carpeta con otros archivos.</summary>
    NotEmpty,

    /// <summary>Se intentó inicializar un repositorio donde ya hay uno.</summary>
    AlreadyInitialized,

    /// <summary>Formato superior o características desconocidas (RNF-14). Solo lectura imposible.</summary>
    Incompatible,

    /// <summary>Estructura o contenido no válido según §6.</summary>
    Corrupt,

    /// <summary>Otro proceso tiene un bloqueo incompatible (§6.4.4).</summary>
    Locked,

    /// <summary>El bloqueo propio ha caducado o ha desaparecido; no es seguro seguir escribiendo.</summary>
    LockLost,

    /// <summary>No existe el snapshot pedido.</summary>
    SnapshotNotFound,

    /// <summary>Ya existe un snapshot con el mismo identificador.</summary>
    SnapshotIdConflict,

    /// <summary>No existe el objeto pedido.</summary>
    ObjectNotFound,

    /// <summary>
    /// La copia supera el máximo de entradas (<see cref="RepositoryLayout.MaxSnapshotEntries"/>) o el manifiesto superaría
    /// su tamaño máximo (<see cref="RepositoryLayout.MaxSnapshotManifestBytes"/>) (§6.3): la copia falla y no se publica
    /// ningún snapshot; el anterior sigue siendo el último restaurable.
    /// </summary>
    SnapshotTooLarge,
}

/// <summary>Error del repositorio con un motivo tipado.</summary>
public sealed class RepositoryException : Exception
{
    /// <summary>Crea la excepción.</summary>
    public RepositoryException(RepositoryErrorKind kind, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    /// <summary>Crea una excepción <see cref="RepositoryErrorKind.Corrupt"/>.</summary>
    public RepositoryException()
        : this(RepositoryErrorKind.Corrupt, "Repositorio no válido.")
    {
    }

    /// <summary>Crea una excepción <see cref="RepositoryErrorKind.Corrupt"/>.</summary>
    public RepositoryException(string message)
        : this(RepositoryErrorKind.Corrupt, message)
    {
    }

    /// <summary>Crea una excepción <see cref="RepositoryErrorKind.Corrupt"/>.</summary>
    public RepositoryException(string message, Exception innerException)
        : this(RepositoryErrorKind.Corrupt, message, innerException)
    {
    }

    /// <summary>Motivo del error.</summary>
    public RepositoryErrorKind Kind { get; }
}
