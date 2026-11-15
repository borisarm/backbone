using System.Text;

namespace Backbone.Service.Tests.TestSupport;

/// <summary>Lectura y escritura de mensajes <c>Content-Length</c> en bruto, para ver exactamente lo que viaja.</summary>
internal static class RawFraming
{
    public static async Task WriteAsync(Stream stream, string json, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(json);
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"), ct);
        await stream.WriteAsync(body, ct);
        await stream.FlushAsync(ct);
    }

    /// <summary>Lee un mensaje completo; <see langword="null"/> si el flujo se cierra antes.</summary>
    public static async Task<string?> ReadAsync(Stream stream, CancellationToken ct)
    {
        var header = new StringBuilder();
        var one = new byte[1];
        while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (await stream.ReadAsync(one, ct) == 0)
            {
                return null;
            }

            header.Append((char)one[0]);
        }

        var line = header.ToString().Split("\r\n")[0];
        var length = int.Parse(line["Content-Length:".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, ct);
        return Encoding.UTF8.GetString(body);
    }

    /// <summary>Espera a que el otro extremo cierre el flujo (lectura que devuelve 0 o falla).</summary>
    public static async Task<bool> WaitForEndOfStreamAsync(Stream stream, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var buffer = new byte[4096];
        try
        {
            while (await stream.ReadAsync(buffer, cts.Token) > 0)
            {
            }

            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }
}
