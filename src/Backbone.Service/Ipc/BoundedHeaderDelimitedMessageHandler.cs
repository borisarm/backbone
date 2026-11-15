using System.Buffers;
using System.Buffers.Text;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Backbone.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;
using StreamJsonRpc.Reflection;

namespace Backbone.Service.Ipc;

/// <summary>
/// Marco de mensajes compatible con <see cref="HeaderDelimitedMessageHandler"/> (<c>Content-Length: N\r\n\r\n</c> + JSON en
/// UTF-8) que <b>aplica el límite de tamaño antes de reservar memoria</b> para el mensaje (§7.1, <see cref="Contracts.BackboneIpc.MaxMessageBytes"/>).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>La cabecera se lee hasta <see cref="MaxHeaderBytes"/>; si no termina antes, o declara un tamaño mayor que el
/// límite, se lanza <see cref="BadRpcHeaderException"/> sin leer el cuerpo y <c>JsonRpc</c> cierra la conexión.</item>
/// <item>Análisis estricto: exactamente un <c>Content-Length</c> decimal; <c>Content-Type</c> opcional y solo con UTF-8;
/// cualquier otra cabecera se rechaza.</item>
/// <item>Toda respuesta de error pasa por <see cref="BackboneServerJsonRpc.SanitizeOutgoingError"/> antes de serializarse
/// (§7.4, M-5): nunca sale un <c>CommonErrorData</c> ni texto del cliente.</item>
/// <item>Permite pedir el cierre de la conexión justo después de enviar la siguiente respuesta de error
/// (<see cref="CloseAfterNextError"/>), para que un cliente rechazado reciba <c>NotAuthorized</c> antes de cortarse.</item>
/// <item>Peticiones simultáneas (leídas y aún sin respuesta) limitadas: al superar el máximo se lanza
/// <see cref="TooManyConcurrentRequestsException"/> y <c>JsonRpc</c> cierra la conexión (B-2).</item>
/// <item>El cuerpo de cada mensaje recibido (que puede llevar una contraseña) se pone a cero en cuanto termina su
/// deserialización (<see cref="IJsonRpcMessageBufferManager"/>), igual que los bytes consumidos del búfer del lector (B-1).</item>
/// <item>Una respuesta que superaría el límite no se envía: se sustituye por <see cref="BackboneErrorCode.Internal"/> (el
/// cliente la cortaría de todos modos). Una notificación demasiado grande se descarta.</item>
/// </list>
/// </remarks>
internal sealed partial class BoundedHeaderDelimitedMessageHandler : PipeMessageHandler, IJsonRpcMessageBufferManager
{
    /// <summary>Tamaño máximo del bloque de cabeceras, incluido el separador final.</summary>
    internal const int MaxHeaderBytes = 1024;

    /// <summary>Máximo por defecto de peticiones simultáneas por conexión.</summary>
    internal const int DefaultMaxConcurrentRequests = 16;

    private static ReadOnlySpan<byte> HeaderTerminator => "\r\n\r\n"u8;

    private readonly int _maxContentLength;
    private readonly int _maxConcurrentRequests;
    private readonly ILogger _logger;
    private readonly ArrayBufferWriter<byte> _body = new();
    private Action? _closeCallback;
    private volatile bool _closeAfterNextError;
    private bool _closePending;
    private byte[]? _pendingBody;
    private JsonRpcMessage? _pendingMessage;
    private int _inFlightRequests;

    public BoundedHeaderDelimitedMessageHandler(
        Stream duplexStream,
        IJsonRpcMessageFormatter formatter,
        int maxContentLength,
        int maxConcurrentRequests = DefaultMaxConcurrentRequests,
        ILogger? logger = null)
        : base(duplexStream, duplexStream, formatter)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxContentLength);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrentRequests);
        _maxContentLength = maxContentLength;
        _maxConcurrentRequests = maxConcurrentRequests;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Peticiones leídas a las que aún no se ha respondido.</summary>
    internal int InFlightRequests => Volatile.Read(ref _inFlightRequests);

    /// <summary>
    /// Tras escribir y volcar la siguiente respuesta de error, invoca <paramref name="onClosed"/> (en el grupo de subprocesos).
    /// </summary>
    public void CloseAfterNextError(Action onClosed)
    {
        _closeCallback = onClosed;
        _closeAfterNextError = true;
    }

    /// <remarks>
    /// Esta clase vuelve a implementar la interfaz, así que la implementación de <see cref="PipeMessageHandler"/> no se
    /// ejecuta. En StreamJsonRpc 2.26 esa implementación solo actúa sobre lo que reserva su propio
    /// <c>DeserializeMessageAsync</c> (que aquí no se usa): llama a <see cref="IJsonRpcMessageBufferManager.DeserializationComplete"/>
    /// del <b>mensaje</b> (los mensajes de <see cref="SystemTextJsonFormatter"/> liberan así su <c>JsonDocument</c> y los
    /// búferes alquilados) y avanza el lector. El avance ya lo hace <see cref="ReadBodyAsync"/>; la liberación del mensaje
    /// se hace aquí, en <see cref="ReleasePendingBody"/>, antes de poner el cuerpo a cero.
    /// </remarks>
    void IJsonRpcMessageBufferManager.DeserializationComplete(JsonRpcMessage message)
    {
        if (ReferenceEquals(Volatile.Read(ref _pendingMessage), message))
        {
            ReleasePendingBody();
        }
    }

    protected override void Dispose(bool disposing)
    {
        ReleasePendingBody();
        _body.Clear();
        base.Dispose(disposing);
    }

    protected override async ValueTask<JsonRpcMessage?> ReadCoreAsync(CancellationToken cancellationToken)
    {
        // Garantía de IJsonRpcMessageBufferManager: antes de la siguiente lectura el mensaje anterior está deserializado.
        ReleasePendingBody();

        int contentLength;
        while (true)
        {
            var result = await Reader!.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (result.IsCanceled)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            var buffer = result.Buffer;
            bool complete;
            int headerLength;
            try
            {
                complete = TryParseHeaders(buffer, _maxContentLength, out headerLength, out contentLength);
            }
            catch (BadRpcHeaderException)
            {
                ClearConsumed(buffer);
                Reader.AdvanceTo(buffer.End);
                throw;
            }

            if (complete)
            {
                Reader.AdvanceTo(buffer.GetPosition(headerLength));
                break;
            }

            if (result.IsCompleted)
            {
                ClearConsumed(buffer);
                Reader.AdvanceTo(buffer.End);
                if (buffer.IsEmpty)
                {
                    return null; // Fin limpio del flujo entre mensajes.
                }

                throw new EndOfStreamException("The stream ended in the middle of a message header.");
            }

            Reader.AdvanceTo(buffer.Start, buffer.End);
        }

        return await ReadBodyAsync(contentLength, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<JsonRpcMessage> ReadBodyAsync(int contentLength, CancellationToken cancellationToken)
    {
        while (true)
        {
            var result = await Reader!.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (result.IsCanceled)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            var buffer = result.Buffer;
            if (buffer.Length >= contentLength)
            {
                // Copia propia del cuerpo (como mucho el límite ya comprobado): el formateador conserva referencias al búfer
                // hasta terminar de deserializar los argumentos, y el del lector se recicla al avanzar. Los bytes consumidos
                // del lector se ponen a cero antes de devolverlos; la copia, al terminar la deserialización.
                var slice = buffer.Slice(0, contentLength);
                var body = slice.ToArray();
                ClearConsumed(slice);
                Reader.AdvanceTo(slice.End);
                return DeserializeAndTrack(body);
            }

            if (result.IsCompleted)
            {
                ClearConsumed(buffer);
                Reader.AdvanceTo(buffer.End);
                throw new EndOfStreamException("The stream ended in the middle of a message.");
            }

            Reader.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    private JsonRpcMessage DeserializeAndTrack(byte[] body)
    {
        JsonRpcMessage message;
        try
        {
            message = Formatter.Deserialize(new ReadOnlySequence<byte>(body));
        }
        catch
        {
            CryptographicOperations.ZeroMemory(body);
            throw;
        }

        Volatile.Write(ref _pendingBody, body);
        Volatile.Write(ref _pendingMessage, message);

        if (message is JsonRpcRequest { IsResponseExpected: true }
            && Interlocked.Increment(ref _inFlightRequests) > _maxConcurrentRequests)
        {
            ReleasePendingBody();
            LogTooManyRequests(_logger, _maxConcurrentRequests);
            throw new TooManyConcurrentRequestsException(_maxConcurrentRequests);
        }

        return message;
    }

    private void ReleasePendingBody()
    {
        // Primero el mensaje (una sola vez, aunque compitan la llamada de JsonRpc y la siguiente lectura o Dispose): suelta
        // sus referencias al cuerpo; después se pone el cuerpo a cero.
        if (Interlocked.Exchange(ref _pendingMessage, null) is IJsonRpcMessageBufferManager pooled and JsonRpcMessage message)
        {
            pooled.DeserializationComplete(message);
        }

        if (Interlocked.Exchange(ref _pendingBody, null) is { } body)
        {
            CryptographicOperations.ZeroMemory(body);
        }
    }

    /// <summary>Pone a cero bytes del búfer del lector que se van a dar por consumidos, antes de que vuelvan al grupo.</summary>
    private static void ClearConsumed(ReadOnlySequence<byte> consumed)
    {
        foreach (var segment in consumed)
        {
            MemoryMarshal.AsMemory(segment).Span.Clear();
        }
    }

    protected override void Write(JsonRpcMessage content, CancellationToken cancellationToken)
    {
        // Write nunca se llama en paralelo (contrato de PipeMessageHandler): el búfer del cuerpo se reutiliza.
        if (content is JsonRpcError { Error: { } detail })
        {
            BackboneServerJsonRpc.SanitizeOutgoingError(detail);
        }

        var isResponse = content is JsonRpcResult or JsonRpcError
                         && content is IJsonRpcMessageWithId { RequestId.IsEmpty: false };
        if (isResponse)
        {
            DecrementInFlight();
        }

        _body.Clear();
        Formatter.Serialize(_body, content);
        if (_body.WrittenCount > _maxContentLength)
        {
            var size = _body.WrittenCount;
            _body.Clear();
            if (!isResponse)
            {
                // Notificación demasiado grande: el cliente la cortaría y cerraría la conexión; se descarta.
                LogOversizedNotificationDropped(_logger, (content as JsonRpcRequest)?.Method ?? string.Empty, size);
                return;
            }

            var correlationId = Guid.NewGuid();
            LogOversizedResponse(_logger, size, correlationId);
            var data = BackboneServerJsonRpc.InternalErrorData(correlationId);
            content = new JsonRpcError
            {
                RequestId = ((IJsonRpcMessageWithId)content).RequestId,
                Error = new JsonRpcError.ErrorDetail
                {
                    Code = (JsonRpcErrorCode)BackboneErrors.ToJsonRpcErrorCode(BackboneErrorCode.Internal),
                    Message = data.Message ?? string.Empty,
                    Data = data,
                },
            };
            Formatter.Serialize(_body, content);
        }

        Span<byte> header = stackalloc byte[64];
        "Content-Length: "u8.CopyTo(header);
        var length = "Content-Length: "u8.Length;
        Utf8Formatter.TryFormat(_body.WrittenCount, header[length..], out var digits);
        length += digits;
        HeaderTerminator.CopyTo(header[length..]);
        length += HeaderTerminator.Length;

        var writer = Writer!;
        writer.Write(header[..length]);
        writer.Write(_body.WrittenSpan);
        _body.Clear();

        if (content is JsonRpcError && _closeAfterNextError)
        {
            _closePending = true;
        }
    }

    protected override async ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        await base.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (_closePending)
        {
            _closePending = false;
            _closeAfterNextError = false;
            var callback = Interlocked.Exchange(ref _closeCallback, null);
            if (callback is not null)
            {
                ThreadPool.QueueUserWorkItem(static cb => cb(), callback, preferLocal: false);
            }
        }
    }

    private void DecrementInFlight()
    {
        var current = Volatile.Read(ref _inFlightRequests);
        while (current > 0)
        {
            var seen = Interlocked.CompareExchange(ref _inFlightRequests, current - 1, current);
            if (seen == current)
            {
                return;
            }

            current = seen;
        }
    }

    /// <summary>
    /// Busca el final de las cabeceras y las valida. Devuelve <see langword="false"/> si aún no han llegado completas.
    /// </summary>
    internal static bool TryParseHeaders(ReadOnlySequence<byte> buffer, int maxContentLength, out int headerLength, out int contentLength)
    {
        headerLength = 0;
        contentLength = 0;

        var window = buffer.Length > MaxHeaderBytes ? buffer.Slice(0, MaxHeaderBytes) : buffer;
        Span<byte> bytes = stackalloc byte[MaxHeaderBytes];
        window.CopyTo(bytes);
        bytes = bytes[..(int)window.Length];

        var end = bytes.IndexOf(HeaderTerminator);
        if (end < 0)
        {
            if (buffer.Length >= MaxHeaderBytes)
            {
                throw new BadRpcHeaderException("The message header is too large.");
            }

            return false;
        }

        int? declared = null;
        var headers = bytes[..end];
        while (true)
        {
            var lineEnd = headers.IndexOf("\r\n"u8);
            var line = lineEnd < 0 ? headers : headers[..lineEnd];
            ParseHeaderLine(line, maxContentLength, ref declared);
            if (lineEnd < 0)
            {
                break;
            }

            headers = headers[(lineEnd + 2)..];
        }

        contentLength = declared ?? throw new BadRpcHeaderException("The message has no Content-Length header.");
        headerLength = end + HeaderTerminator.Length;
        return true;
    }

    private static void ParseHeaderLine(ReadOnlySpan<byte> line, int maxContentLength, ref int? contentLength)
    {
        var colon = line.IndexOf((byte)':');
        if (colon <= 0)
        {
            throw new BadRpcHeaderException("Malformed message header.");
        }

        foreach (var b in line)
        {
            if (b is < 0x20 or > 0x7E)
            {
                throw new BadRpcHeaderException("The message header contains non-ASCII or control characters.");
            }
        }

        var name = Encoding.ASCII.GetString(line[..colon]);
        var value = Encoding.ASCII.GetString(line[(colon + 1)..]).Trim(' ', '\t');

        if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
        {
            if (contentLength is not null)
            {
                throw new BadRpcHeaderException("Duplicate Content-Length header.");
            }

            if (value.Length is 0 or > 10 || !value.All(char.IsAsciiDigit)
                || !long.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var length))
            {
                throw new BadRpcHeaderException("Invalid Content-Length header.");
            }

            if (length <= 0 || length > maxContentLength)
            {
                throw new BadRpcHeaderException("The message exceeds the maximum allowed size.");
            }

            contentLength = (int)length;
        }
        else if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
        {
            // Solo se admite UTF-8 (el valor por defecto): otro juego de caracteres cambiaría la interpretación del cuerpo.
            var charset = value.Split(';').Skip(1)
                .Select(p => p.Trim())
                .FirstOrDefault(p => p.StartsWith("charset=", StringComparison.OrdinalIgnoreCase));
            if (charset is not null
                && !charset["charset=".Length..].Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase))
            {
                throw new BadRpcHeaderException("Unsupported message encoding.");
            }
        }
        else
        {
            throw new BadRpcHeaderException("Unknown message header.");
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Conexión IPC cerrada: más de {Max} peticiones simultáneas.")]
    private static partial void LogTooManyRequests(ILogger logger, int max);

    [LoggerMessage(Level = LogLevel.Error, Message = "Respuesta IPC de {Bytes} bytes por encima del límite: se envía un error interno. Id. de correlación {CorrelationId}.")]
    private static partial void LogOversizedResponse(ILogger logger, int bytes, Guid correlationId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Notificación IPC {Method} de {Bytes} bytes por encima del límite: se descarta.")]
    private static partial void LogOversizedNotificationDropped(ILogger logger, string method, int bytes);
}

/// <summary>El cliente tiene demasiadas peticiones sin responder en la misma conexión (B-2): se cierra la conexión.</summary>
internal sealed class TooManyConcurrentRequestsException : IOException
{
    public TooManyConcurrentRequestsException()
        : base("Too many concurrent requests on the connection.")
    {
    }

    public TooManyConcurrentRequestsException(int max)
        : base($"More than {max} concurrent requests on the connection.")
    {
    }

    public TooManyConcurrentRequestsException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
