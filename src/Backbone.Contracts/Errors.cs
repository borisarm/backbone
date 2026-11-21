namespace Backbone.Contracts;

/// <summary>
/// Códigos de error de negocio del contrato (§7.4).
/// </summary>
/// <remarks>
/// Viajan en el campo <c>error.code</c> de JSON-RPC (<c>RemoteInvocationException.ErrorCode</c> en el cliente)
/// con el valor numérico de este enumerado, y en <see cref="BackboneErrorData.Code"/> dentro de <c>error.data</c>.
/// Los valores son explícitos y estables: no se reutilizan ni se reordenan. Están fuera del intervalo
/// [-32768, -32000] reservado por JSON-RPC.
/// </remarks>
public enum BackboneErrorCode
{
    /// <summary>La entrada no es válida; <see cref="BackboneErrorData.Validation"/> lleva el detalle.</summary>
    ValidationFailed = 1000,

    /// <summary>No existe el trabajo indicado.</summary>
    JobNotFound = 1001,

    /// <summary>El trabajo ya está en ejecución o en cola.</summary>
    JobAlreadyRunning = 1002,

    /// <summary>No se encuentra el NAS en la red.</summary>
    NasUnreachable = 1003,

    /// <summary>El NAS ha rechazado el usuario o la contraseña.</summary>
    CredentialsRejected = 1004,

    /// <summary>El recurso compartido no existe en el servidor.</summary>
    ShareNotFound = 1005,

    /// <summary>Acceso denegado en el recurso o la ruta (p. ej. sin permiso de escritura en el NAS).</summary>
    AccessDenied = 1006,

    /// <summary>El repositorio tiene un bloqueo incompatible vigente (§6.4.4).</summary>
    RepositoryLocked = 1007,

    /// <summary>El repositorio es de un formato superior al que entiende esta versión (RNF-14).</summary>
    RepositoryIncompatible = 1008,

    /// <summary>El repositorio está dañado (faltan objetos o su contenido no coincide).</summary>
    RepositoryCorrupt = 1009,

    /// <summary>No existe el snapshot indicado.</summary>
    SnapshotNotFound = 1010,

    /// <summary>El usuario que pide la restauración no puede escribir en el destino o el destino está protegido (§8.6).</summary>
    RestoreTargetNotWritable = 1011,

    /// <summary>La ejecución se interrumpió por suspensión o apagado del equipo.</summary>
    Interrupted = 1012,

    /// <summary>El llamante no tiene permiso para esta operación (p. ej. no está elevado, §7.3).</summary>
    NotAuthorized = 1013,

    /// <summary>El método pertenece a una fase aún no implementada en este servicio (F2/F3).</summary>
    NotSupported = 1014,

    /// <summary>
    /// Error interno no previsto. <see cref="BackboneErrorData.Message"/> es genérico y lleva un identificador de correlación
    /// que también aparece en el log del servicio; nunca la traza ni el texto de la excepción (§7.4).
    /// </summary>
    Internal = 1015,

    /// <summary>No hay credencial guardada para el recurso: el servicio nunca conecta al NAS sin credencial explícita.</summary>
    CredentialRequired = 1016,

    /// <summary>
    /// Contrato 1.2. La copia tiene demasiados archivos y carpetas para un solo snapshot: más de 5 000 000 de entradas o un
    /// manifiesto (el listado, no el contenido copiado) de más de 16 GiB (§6.3). No se publica snapshot; el anterior sigue
    /// siendo el último restaurable. Solución: dividir el trabajo en varios. Solo aparece en <see cref="RunRecord.ErrorCode"/>.
    /// </summary>
    SnapshotTooLarge = 1017,

    /// <summary>
    /// Contrato 1.3. El servicio no puede conectar ahora con ese servidor, y la respuesta no depende de si la credencial es
    /// correcta (revisión de seguridad de HU-05, M-1; §8.2 "Conexiones al NAS"). Hay dos casos: el servidor ya tiene una
    /// conexión del servicio hecha con otra credencial (Windows admite un solo usuario por servidor), o el usuario que llama ha
    /// acumulado demasiados <see cref="CredentialsRejected"/> seguidos. No se ha intentado conectar. Solución: reintentar
    /// cuando termine la otra operación o pasados unos minutos.
    /// </summary>
    NasBusy = 1018,
}

/// <summary>
/// Datos de un error de negocio: contenido del campo <c>error.data</c> de JSON-RPC.
/// </summary>
/// <remarks>
/// El servicio lanza <c>LocalRpcException</c> con <c>ErrorCode = (int)Code</c> y <c>ErrorData</c> = esta instancia.
/// El cliente lo recibe como <c>RemoteInvocationException</c>: <c>ErrorCode</c> identifica el error y
/// <c>DeserializedErrorData</c> contiene esta instancia si el cliente sobrescribe
/// <c>JsonRpc.GetErrorDetailsDataType</c> para devolver <see cref="BackboneErrorData"/> cuando
/// <see cref="BackboneErrors.TryGetErrorCode"/> reconoce el código (§7.4).
/// </remarks>
public sealed record BackboneErrorData
{
    /// <summary>Código de error; coincide con <c>error.code</c>.</summary>
    public BackboneErrorCode Code { get; init; } = BackboneErrorCode.Internal;

    /// <summary>
    /// Detalle técnico en inglés para el log, sin secretos, trazas ni contenido de archivos. La app <b>no</b>
    /// lo muestra: traduce <see cref="Code"/> a un mensaje localizado. Con <see cref="BackboneErrorCode.Internal"/> es un texto
    /// genérico con el identificador de correlación.
    /// </summary>
    public string? Message { get; init; }

    /// <summary>Resultado de la validación cuando <see cref="Code"/> es <see cref="BackboneErrorCode.ValidationFailed"/>.</summary>
    public ValidationResult? Validation { get; init; }
}

/// <summary>
/// Utilidades para traducir entre <see cref="BackboneErrorCode"/> y el <c>error.code</c> de JSON-RPC.
/// </summary>
public static class BackboneErrors
{
    /// <summary>Valor de <c>error.code</c> de JSON-RPC para un código de negocio.</summary>
    /// <param name="code">Código de negocio.</param>
    /// <returns>El valor numérico del código.</returns>
    public static int ToJsonRpcErrorCode(BackboneErrorCode code) => (int)code;

    /// <summary>Reconoce un <c>error.code</c> de JSON-RPC como código de negocio de Backbone.</summary>
    /// <param name="jsonRpcErrorCode">Valor recibido en <c>error.code</c>.</param>
    /// <param name="code">Código de negocio, si se reconoce.</param>
    /// <returns><see langword="true"/> si el valor corresponde a un <see cref="BackboneErrorCode"/> definido.</returns>
    public static bool TryGetErrorCode(int jsonRpcErrorCode, out BackboneErrorCode code)
    {
        code = (BackboneErrorCode)jsonRpcErrorCode;
        return Enum.IsDefined(code);
    }
}
