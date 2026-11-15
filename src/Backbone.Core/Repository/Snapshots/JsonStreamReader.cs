using System.Buffers;
using System.Text.Json;

namespace Backbone.Core.Repository.Snapshots;

/// <summary>
/// Lectura incremental de un JSON grande con <see cref="Utf8JsonReader"/> (R3): el documento se procesa
/// en unidades pequeñas (una propiedad de la cabecera, una entrada) sobre un búfer que se rellena desde
/// el flujo según hace falta. La memoria es proporcional a la unidad más grande, no al documento.
/// </summary>
/// <remarks>
/// Cada analizador debe ser "todo o nada": devuelve <see langword="false"/> en cuanto
/// <see cref="Utf8JsonReader.Read"/> o <see cref="Utf8JsonReader.TrySkip"/> indican que faltan datos;
/// entonces se rellena el búfer y se repite la unidad desde su principio.
/// </remarks>
internal sealed class JsonStreamReader : IDisposable
{
    /// <summary>Tamaño inicial del búfer.</summary>
    public const int DefaultBufferSize = 64 * 1024;

    /// <summary>Tamaño máximo de una unidad; más grande se considera corrupción (y protege la memoria).</summary>
    public const int MaxBufferSize = 64 * 1024 * 1024;

    private readonly Stream stream;
    private readonly string description;
    private byte[] buffer;
    private int start;
    private int end;
    private bool isFinalBlock;
    private JsonReaderState state = new(new JsonReaderOptions { MaxDepth = 16 });

    public JsonStreamReader(Stream stream, string description, int initialBufferSize = DefaultBufferSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(initialBufferSize, 1);
        this.stream = stream;
        this.description = description;
        buffer = ArrayPool<byte>.Shared.Rent(initialBufferSize);
    }

    /// <summary>Analizador de una unidad.</summary>
    public delegate bool Parser<T>(ref Utf8JsonReader reader, out T value);

    /// <summary>Analiza la siguiente unidad, leyendo del flujo lo que haga falta.</summary>
    public async ValueTask<T> ReadAsync<T>(Parser<T> parser, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (TryParse(parser, out var value))
            {
                return value;
            }

            if (isFinalBlock)
            {
                throw Corrupt("está truncado.");
            }

            await FillAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Comprueba que no queda nada después del valor raíz.</summary>
    public async ValueTask ExpectEndAsync(CancellationToken cancellationToken)
    {
        while (!isFinalBlock)
        {
            await FillAsync(cancellationToken).ConfigureAwait(false);
        }

        var parsed = TryParse(static (ref Utf8JsonReader reader, out bool value) =>
        {
            value = !reader.Read();
            return true;
        }, out var atEnd);

        if (!parsed || !atEnd)
        {
            throw Corrupt("tiene contenido después del objeto principal.");
        }
    }

    public RepositoryException Corrupt(string detail, Exception? inner = null) =>
        new(RepositoryErrorKind.Corrupt, $"{description} {detail}", inner);

    public void Dispose()
    {
        var rented = buffer;
        buffer = [];
        if (rented.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private bool TryParse<T>(Parser<T> parser, out T value)
    {
        var reader = new Utf8JsonReader(buffer.AsSpan(start, end - start), isFinalBlock, state);
        bool complete;
        try
        {
            complete = parser(ref reader, out value);
        }
        catch (JsonException ex)
        {
            throw Corrupt("no es un JSON válido.", ex);
        }
        catch (InvalidOperationException ex)
        {
            // Tipo de token inesperado (p. ej. GetInt64 sobre una cadena).
            throw Corrupt("tiene un valor de tipo inesperado.", ex);
        }
        catch (FormatException ex)
        {
            throw Corrupt("tiene un valor con formato no válido.", ex);
        }

        if (!complete)
        {
            return false;
        }

        start += checked((int)reader.BytesConsumed);
        state = reader.CurrentState;
        return true;
    }

    private async ValueTask FillAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (start > 0)
        {
            Buffer.BlockCopy(buffer, start, buffer, 0, end - start);
            end -= start;
            start = 0;
        }

        if (end == buffer.Length)
        {
            if (buffer.Length >= MaxBufferSize)
            {
                throw Corrupt("contiene un elemento demasiado grande.");
            }

            var larger = ArrayPool<byte>.Shared.Rent(Math.Min(buffer.Length * 2, MaxBufferSize));
            Buffer.BlockCopy(buffer, 0, larger, 0, end);
            ArrayPool<byte>.Shared.Return(buffer);
            buffer = larger;
        }

        // Se llena el búfer entero (o hasta el final) para que repetir una unidad sea poco frecuente.
        while (end < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(end), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                isFinalBlock = true;
                break;
            }

            end += read;
        }
    }
}
