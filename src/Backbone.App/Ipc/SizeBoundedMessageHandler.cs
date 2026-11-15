using System.Buffers;
using System.Buffers.Text;
using System.Text;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;

namespace Backbone.App.Ipc;

/// <summary>
/// Marco de mensajes compatible con <see cref="HeaderDelimitedMessageHandler"/> (<c>Content-Length: N\r\n\r\n</c> + JSON UTF-8)
/// que <b>aplica el límite de tamaño antes de reservar memoria</b> (§7.1, <see cref="Contracts.BackboneIpc.MaxMessageBytes"/>):
/// un mensaje que declara más del límite corta la conexión sin leerse ni deserializarse.
/// </summary>
/// <remarks>Usa el mismo formato y las mismas reglas de cabecera que el manejador del servidor, para que ambos extremos corten igual.</remarks>
internal sealed class SizeBoundedMessageHandler : PipeMessageHandler
{
    /// <summary>Tamaño máximo del bloque de cabeceras, incluido el separador final.</summary>
    internal const int MaxHeaderBytes = 1024;

    private static ReadOnlySpan<byte> HeaderTerminator => "\r\n\r\n"u8;

    private readonly int _maxContentLength;
    private readonly ArrayBufferWriter<byte> _body = new();

    public SizeBoundedMessageHandler(Stream duplexStream, IJsonRpcMessageFormatter formatter, int maxContentLength)
        : base(duplexStream, duplexStream, formatter)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxContentLength);
        _maxContentLength = maxContentLength;
    }

    protected override async ValueTask<JsonRpcMessage?> ReadCoreAsync(CancellationToken cancellationToken)
    {
        int contentLength;
        while (true)
        {
            var result = await Reader!.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (result.IsCanceled)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            var buffer = result.Buffer;
            if (TryParseHeaders(buffer, _maxContentLength, out var headerLength, out contentLength))
            {
                Reader.AdvanceTo(buffer.GetPosition(headerLength));
                break;
            }

            if (result.IsCompleted)
            {
                Reader.AdvanceTo(buffer.End);
                if (buffer.IsEmpty)
                {
                    return null;
                }

                throw new EndOfStreamException("The stream ended in the middle of a message header.");
            }

            Reader.AdvanceTo(buffer.Start, buffer.End);
        }

        while (true)
        {
            var result = await Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (result.IsCanceled)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            var buffer = result.Buffer;
            if (buffer.Length >= contentLength)
            {
                var body = buffer.Slice(0, contentLength).ToArray();
                Reader.AdvanceTo(buffer.GetPosition(contentLength));
                // No se pone a cero: el formateador conserva referencias al búfer hasta deserializar el resultado (se comprobó:
                // con ZeroMemory el resultado llega corrupto). Los mensajes entrantes no llevan contraseñas (§7.2).
                return Formatter.Deserialize(new ReadOnlySequence<byte>(body));
            }

            if (result.IsCompleted)
            {
                Reader.AdvanceTo(buffer.End);
                throw new EndOfStreamException("The stream ended in the middle of a message.");
            }

            Reader.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    protected override void Write(JsonRpcMessage content, CancellationToken cancellationToken)
    {
        // Write nunca se llama en paralelo (contrato de PipeMessageHandler): el búfer del cuerpo se reutiliza.
        _body.Clear();
        try
        {
            Formatter.Serialize(_body, content);
        }
        catch
        {
            _body.Clear();
            throw;
        }

        if (_body.WrittenCount > _maxContentLength)
        {
            _body.Clear();
            // Nunca se envía algo que el servidor cortaría: se falla en local sin tocar la conexión.
            throw new InvalidOperationException("The message exceeds the maximum allowed size.");
        }

        Span<byte> header = stackalloc byte[64];
        "Content-Length: "u8.CopyTo(header);
        var length = "Content-Length: "u8.Length;
        Utf8Formatter.TryFormat(_body.WrittenCount, header[length..], out var digits);
        length += digits;
        HeaderTerminator.CopyTo(header[length..]);
        length += HeaderTerminator.Length;

        try
        {
            var writer = Writer!;
            writer.Write(header[..length]);
            writer.Write(_body.WrittenSpan);
        }
        finally
        {
            // El cuerpo puede contener contraseñas: no se deja serializado en memoria.
            _body.Clear();
        }
    }

    /// <summary>Busca el final de las cabeceras y las valida. Devuelve <see langword="false"/> si aún no han llegado completas.</summary>
    /// <exception cref="BadRpcHeaderException">Cabecera mal formada, demasiado grande o con un tamaño por encima del límite.</exception>
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
}
