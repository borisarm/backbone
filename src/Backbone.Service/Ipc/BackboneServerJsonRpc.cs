using System.Diagnostics;
using Backbone.Contracts;
using Microsoft.Extensions.Logging;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;

namespace Backbone.Service.Ipc;

/// <summary>
/// <see cref="JsonRpc"/> del servidor: ninguna excepción sale con el <see cref="CommonErrorData"/> por defecto (que lleva tipo,
/// mensaje y traza). Toda excepción que no sea un error de negocio (<see cref="LocalRpcException"/>) se convierte en
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

    [LoggerMessage(Level = LogLevel.Error, Message = "Error interno en la llamada IPC {Method}. Id. de correlación {CorrelationId}.")]
    private static partial void LogUnhandled(ILogger logger, Exception exception, string method, Guid correlationId);
}
