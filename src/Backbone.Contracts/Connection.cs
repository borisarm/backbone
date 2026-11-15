using System.Text;

namespace Backbone.Contracts;

/// <summary>
/// Credencial guardada para un recurso, <b>sin contraseña</b> (RF-62).
/// </summary>
public sealed record CredentialInfo
{
    /// <summary>Raíz del recurso normalizada, <c>\\servidor\recurso</c>.</summary>
    public string ShareRoot { get; init => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Usuario del NAS.</summary>
    public string UserName { get; init => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Última vez que se guardó (UTC).</summary>
    public DateTimeOffset UpdatedUtc { get; init; }
}

/// <summary>
/// Credencial introducida por el usuario: para guardarla (<see cref="IBackboneService.SaveCredentialAsync"/>) o para probar
/// una conexión antes de guardarla (<see cref="IBackboneService.TestConnectionAsync"/>).
/// </summary>
/// <remarks>
/// Solo viaja de la app al servicio. <see cref="ToString"/> oculta la contraseña para que no acabe en un log; por eso la
/// contraseña nunca es un parámetro suelto de la interfaz.
/// </remarks>
public sealed record CredentialInput
{
    /// <summary>Usuario del NAS.</summary>
    public string UserName { get; init => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Contraseña. Nunca se registra ni se devuelve.</summary>
    public string Password { get; init => field = value ?? string.Empty; } = string.Empty;

    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("UserName = ").Append(UserName).Append(", Password = ***");
        return true;
    }
}

/// <summary>
/// Resultado de "Probar conexión" (RF-61).
/// </summary>
/// <remarks>
/// Un fallo de conexión es un resultado (<see cref="Success"/> = <see langword="false"/> y <see cref="ErrorCode"/>),
/// no una excepción. Solo una entrada no válida (destino que no es UNC) produce <see cref="BackboneErrorCode.ValidationFailed"/>.
/// </remarks>
public sealed record ConnectionTestResult
{
    /// <summary>Se pudo conectar, leer, escribir y borrar el archivo de prueba.</summary>
    public bool Success { get; init; }

    /// <summary>
    /// Motivo del fallo: <see cref="BackboneErrorCode.NasUnreachable"/>, <see cref="BackboneErrorCode.CredentialsRejected"/>,
    /// <see cref="BackboneErrorCode.ShareNotFound"/>, <see cref="BackboneErrorCode.AccessDenied"/> (sin permiso de escritura) o
    /// <see cref="BackboneErrorCode.CredentialRequired"/> (sin credencial indicada ni guardada: no se intenta conectar).
    /// </summary>
    public BackboneErrorCode? ErrorCode { get; init; }

    /// <summary>Raíz del recurso normalizada (<c>\\servidor\recurso</c>), con la que se guarda la credencial.</summary>
    public string ShareRoot { get; init => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Se pudo leer.</summary>
    public bool CanRead { get; init; }

    /// <summary>Se pudo escribir el archivo de prueba.</summary>
    public bool CanWrite { get; init; }

    /// <summary>Se pudo borrar el archivo de prueba.</summary>
    public bool CanDelete { get; init; }

    /// <summary>Espacio libre del recurso en bytes, si se pudo obtener.</summary>
    public long? FreeBytes { get; init; }

    /// <summary>Espacio total del recurso en bytes, si se pudo obtener.</summary>
    public long? TotalBytes { get; init; }

    /// <summary>Dialecto SMB negociado (p. ej. <c>3.1.1</c>), si se pudo obtener.</summary>
    public string? SmbDialect { get; init; }
}

/// <summary>
/// F2 (RF-43). Espacio del recurso y del repositorio de un trabajo.
/// </summary>
public sealed record StorageInfo
{
    /// <summary>Trabajo.</summary>
    public Guid JobId { get; init; }

    /// <summary>Raíz del recurso, <c>\\servidor\recurso</c>.</summary>
    public string ShareRoot { get; init => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Espacio total del recurso en bytes.</summary>
    public long TotalBytes { get; init; }

    /// <summary>Espacio libre del recurso en bytes.</summary>
    public long FreeBytes { get; init; }

    /// <summary>Tamaño del repositorio en bytes; <see langword="null"/> si aún no se ha calculado.</summary>
    public long? RepositoryBytes { get; init; }

    /// <summary>El espacio libre está por debajo del 10 % o de 20 GB (calculado por el servicio).</summary>
    public bool IsLowSpace { get; init; }

    /// <summary>Instante de la medida (UTC).</summary>
    public DateTimeOffset MeasuredUtc { get; init; }
}
