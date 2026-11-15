using System.Diagnostics;
using Backbone.Contracts;
using Microsoft.Extensions.Logging;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;

namespace Backbone.Service.Ipc;

/// <summary>
/// <see cref="JsonRpc"/> del servidor: ninguna excepción sale con el <see cref="CommonErrorData"/> por defecto (que lleva tipo,
/// mensaje y traza). Los errores de negocio (<see cref="LocalRpcException"/> con un <see cref="BackboneErrorCode"/> y su
/// <see cref="BackboneErrorData"/>) viajan con su código; cualquier otra excepción se convierte en
/// <see cref="BackboneErrorCode.Internal"/> con un identificador de correlación que también va al log (§7.4, M-5).
/// </summary>
internal sealed partial class BackboneServerJsonRpc : JsonRpc
{
    private readonly ILogger _logger;

    public BackboneServerJsonRpc(IJsonRpcMessageHandler handler, ILogger logger)
        : base(handler)
    {
        _logger = logger;

        // Nunca ISerializable: no se serializan excepciones hacia el cliente.
        ExceptionStrategy = ExceptionProcessing.CommonErrorData;
        CancelLocallyInvokedMethodsWhenConnectionIsClosed = true;

        // Sin trazas de StreamJsonRpc: en niveles detallados vuelcan mensajes completos con sus parámetros.
        TraceSource = CreateSilentTraceSource();
    }

    internal static TraceSource CreateSilentTraceSource()
    {
        var source = new TraceSource("Backbone.Ipc.JsonRpc", SourceLevels.Off);
        source.Listeners.Clear();
        return source;
    }

    /// <summary>Error genérico con identificador de correlación.</summary>
    internal static BackboneErrorData InternalErrorData(Guid correlationId) => new()
    {
        Code = BackboneErrorCode.Internal,
        Message = $"Internal error. Correlation id: {correlationId}",
    };

    protected override JsonRpcError.ErrorDetail CreateErrorDetails(JsonRpcRequest request, Exception exception)
    {
        if (exception is OperationCanceledException)
        {
            return new JsonRpcError.ErrorDetail { Code = JsonRpcErrorCode.RequestCanceled, Message = "The request was canceled." };
        }

        // Error de negocio: ya lleva BackboneErrorData y un mensaje pensado para viajar (BackboneException). Se construye aquí
        // en lugar de delegar en base para no depender de lo que StreamJsonRpc añada por defecto (tipo, traza, excepción interna).
        if (exception is LocalRpcException business && BackboneErrors.TryGetErrorCode(business.ErrorCode, out var code)
            && business.ErrorData is BackboneErrorData businessData && businessData.Code == code)
        {
            return new JsonRpcError.ErrorDetail
            {
                Code = (JsonRpcErrorCode)business.ErrorCode,
                Message = businessData.Message ?? string.Empty,
                Data = businessData,
            };
        }

        var correlationId = Guid.NewGuid();
        LogUnhandled(_logger, exception, request.Method ?? string.Empty, correlationId);
        var data = InternalErrorData(correlationId);
        return new JsonRpcError.ErrorDetail
        {
            Code = (JsonRpcErrorCode)BackboneErrors.ToJsonRpcErrorCode(BackboneErrorCode.Internal),
            Message = data.Message ?? string.Empty,
            Data = data,
        };
    }

    /// <summary>
    /// Última barrera antes de escribir una respuesta de error (la invoca el <see cref="BoundedHeaderDelimitedMessageHandler"/>).
    /// Los errores que <c>JsonRpc</c> genera sin pasar por <see cref="CreateErrorDetails"/> (argumentos que no se pueden
    /// deserializar, método inexistente, petición mal formada) llevan en <c>data</c> el <see cref="CommonErrorData"/> con
    /// tipos, mensajes y trazas, y en <c>message</c> texto enviado por el cliente: se conservan solo el código y un mensaje fijo.
    /// </summary>
    internal static void SanitizeOutgoingError(JsonRpcError.ErrorDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        if (detail.Data is BackboneErrorData data
            && BackboneErrors.TryGetErrorCode((int)detail.Code, out var code) && data.Code == code)
        {
            detail.Message = data.Message ?? string.Empty;
            return;
        }

        detail.Data = null;
        detail.Message = detail.Code switch
        {
            JsonRpcErrorCode.ParseError => "Parse error.",
            JsonRpcErrorCode.InvalidRequest => "Invalid request.",
            JsonRpcErrorCode.MethodNotFound => "Method not found.",
            JsonRpcErrorCode.InvalidParams => "Invalid params.",
            JsonRpcErrorCode.RequestCanceled => "The request was canceled.",
            _ => "Error.",
        };
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Error interno en la llamada IPC {Method}. Id. de correlación {CorrelationId}.")]
    private static partial void LogUnhandled(ILogger logger, Exception exception, string method, Guid correlationId);
}
