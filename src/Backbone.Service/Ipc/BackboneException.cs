using Backbone.Contracts;

namespace Backbone.Service.Ipc;

/// <summary>
/// Error de negocio del servicio (§7.4), independiente del transporte. El adaptador IPC lo convierte en
/// <c>LocalRpcException</c> con <see cref="BackboneErrorCode"/> y <see cref="BackboneErrorData"/>.
/// </summary>
/// <remarks>
/// <see cref="Exception.Message"/> es un detalle técnico en inglés para el log: nunca secretos, rutas de usuario, trazas ni
/// contenido de archivos, porque viaja a la app en <see cref="BackboneErrorData.Message"/>.
/// </remarks>
internal sealed class BackboneException : Exception
{
    public BackboneException()
        : this(BackboneErrorCode.Internal, "Internal error.")
    {
    }

    public BackboneException(string message)
        : this(BackboneErrorCode.Internal, message)
    {
    }

    public BackboneException(string message, Exception innerException)
        : base(message, innerException)
    {
        Code = BackboneErrorCode.Internal;
    }

    public BackboneException(BackboneErrorCode code, string message, ValidationResult? validation = null)
        : base(message)
    {
        Code = code;
        Validation = validation;
    }

    public BackboneErrorCode Code { get; }

    public ValidationResult? Validation { get; }

    public static BackboneException NotSupported(string method) =>
        new(BackboneErrorCode.NotSupported, $"{method} is not implemented in this version of the service.");

    public static BackboneException NotAuthorized(string reason) =>
        new(BackboneErrorCode.NotAuthorized, reason);

    public static BackboneException ValidationFailed(ValidationResult validation) =>
        new(BackboneErrorCode.ValidationFailed, "The input is not valid.", validation);

    public static BackboneException ValidationFailed(string field, ValidationIssueCode code) =>
        ValidationFailed(new ValidationResult { Errors = [new ValidationIssue { Field = field, Code = code }] });
}
