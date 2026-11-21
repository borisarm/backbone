namespace Backbone.Service.Sources;

/// <summary>
/// La lectura de los orígenes no puede seguir y la ejecución debe terminar <c>Failed</c> con
/// <see cref="Backbone.Contracts.BackboneErrorCode.Internal"/> (§8.2, R12). <b>No</b> deriva de <see cref="IOException"/>:
/// el motor solo trata como error de un elemento suelto <see cref="IOException"/> y <see cref="UnauthorizedAccessException"/>,
/// así que esta excepción atraviesa <c>BackupEngine</c> y llega al <c>JobRunner</c>, que la traduce a <c>Internal</c>.
/// </summary>
public class SourceReadingFailedException : Exception
{
    public SourceReadingFailedException()
        : base("Source reading cannot continue.")
    {
    }

    public SourceReadingFailedException(string message)
        : base(message)
    {
    }

    public SourceReadingFailedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Una operación sobre un origen superó su plazo (60 s) y tampoco volvió en los 30 s siguientes a cancelarla: su hilo se
/// abandona y la ejecución termina <c>Failed</c>/<c>Internal</c> (§8.2 "Plazo de las lecturas de los orígenes", R12). Desde
/// ese momento, toda operación de ese <see cref="SourceFileSystem"/> lanza esta excepción.
/// </summary>
public sealed class SourceOperationAbandonedException : SourceReadingFailedException
{
    public SourceOperationAbandonedException()
        : base("A source operation did not return after being cancelled and was abandoned.")
    {
    }

    public SourceOperationAbandonedException(string message)
        : base(message)
    {
    }

    public SourceOperationAbandonedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// El servicio no pudo exponer los marcadores de la Cloud Files API al arrancar
/// (<c>RtlSetProcessPlaceholderCompatibilityMode</c> devolvió un valor negativo): las copias que leen orígenes fallan con
/// <c>Internal</c> en lugar de leer con los marcadores disfrazados de archivos normales (§8.2, R4).
/// </summary>
public sealed class SourceReadingUnavailableException : SourceReadingFailedException
{
    public SourceReadingUnavailableException()
        : base("Cloud placeholders could not be exposed at service start; sources cannot be read safely.")
    {
    }

    public SourceReadingUnavailableException(string message)
        : base(message)
    {
    }

    public SourceReadingUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
