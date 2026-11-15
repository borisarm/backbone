using Backbone.Contracts;
using StreamJsonRpc;

namespace Backbone.App.Ipc;

/// <summary>Causa de un intento de conexión fallido, ya clasificada.</summary>
public enum ConnectionFailure
{
    /// <summary>El pipe no existe, no responde o el servidor no es el auténtico.</summary>
    ServiceUnavailable = 0,

    /// <summary>Acceso denegado por la ACL del pipe o <c>NotAuthorized</c> del servicio.</summary>
    Unauthorized = 1,

    /// <summary>Otra versión mayor del contrato.</summary>
    IncompatibleContract = 2,
}

/// <summary>Clasificación de errores de conexión y de llamadas RPC.</summary>
public static class ConnectionFailures
{
    /// <summary>Clasifica la excepción de un intento de conexión (incluida la primera llamada, <c>GetServiceInfoAsync</c>).</summary>
    public static ConnectionFailure Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            // La ACL del pipe no concede acceso a este usuario (§7.3): CreateFile devuelve ACCESS_DENIED.
            UnauthorizedAccessException => ConnectionFailure.Unauthorized,
            ContractIncompatibleException => ConnectionFailure.IncompatibleContract,
            RemoteInvocationException rpc when TryGetErrorCode(rpc, out var code, out _) && code == BackboneErrorCode.NotAuthorized
                => ConnectionFailure.Unauthorized,
            // Cualquier otro (pipe ausente, tiempo agotado, servidor falso, conexión cortada, mensaje inválido):
            // desde el punto de vista del usuario "el servicio no está disponible".
            _ => ConnectionFailure.ServiceUnavailable,
        };
    }

    /// <summary>Estado de conexión que corresponde a un fallo de clasificación <paramref name="failure"/>.</summary>
    /// <param name="failure">Causa del fallo.</param>
    /// <param name="hadSession">Hubo una conexión operativa antes de este fallo.</param>
    /// <param name="consecutiveFailures">Fallos seguidos, incluida la pérdida de la conexión (1 tras perderla).</param>
    /// <param name="reconnectingAttempts">Tantos fallos seguidos, tras una conexión operativa, siguen mostrándose como "reconectando".</param>
    public static ConnectionState ToState(ConnectionFailure failure, bool hadSession, int consecutiveFailures, int reconnectingAttempts) =>
        failure switch
        {
            ConnectionFailure.Unauthorized => ConnectionState.Unauthorized,
            ConnectionFailure.IncompatibleContract => ConnectionState.IncompatibleContract,
            _ => hadSession && consecutiveFailures < reconnectingAttempts ? ConnectionState.Reconnecting : ConnectionState.ServiceStopped,
        };

    /// <summary>
    /// Extrae el código de negocio de un error remoto (§7.4): <c>error.code</c> reconocido por
    /// <see cref="BackboneErrors.TryGetErrorCode"/> y, si llegó, <see cref="BackboneErrorData"/> (con <c>Validation</c>).
    /// </summary>
    public static bool TryGetErrorCode(RemoteInvocationException exception, out BackboneErrorCode code, out BackboneErrorData? data)
    {
        ArgumentNullException.ThrowIfNull(exception);
        data = exception.DeserializedErrorData as BackboneErrorData;
        code = default;
        if (exception.ErrorCode is int raw && BackboneErrors.TryGetErrorCode(raw, out code))
        {
            return true;
        }

        if (data is not null)
        {
            code = data.Code;
            return true;
        }

        return false;
    }
}
