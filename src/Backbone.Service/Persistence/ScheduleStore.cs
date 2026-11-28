using System.Text.Json;
using Backbone.Service.Os;
using Microsoft.Extensions.Logging;

namespace Backbone.Service.Persistence;

/// <summary>
/// <c>data\schedule.json</c> (§8.2 "Programación", HU-09): el último punto de programación consumido de cada trabajo, para
/// distinguir tras un reinicio "ya se hizo" de "se perdió" (RF-11).
/// </summary>
/// <remarks>
/// Acceso por handle con <see cref="TrustedFile"/> y escritura atómica (ADR-14), como los demás almacenes. A diferencia de
/// <c>settings.json</c> y <c>jobs.json</c>, <b>un archivo rechazado o dañado se trata como vacío</b> (con aviso en el log) y no
/// impide arrancar: lo peor que ocurre es que no se recuperen ejecuciones perdidas. Un archivo rechazado tampoco se reescribe
/// (la escritura lanza <see cref="UntrustedFileException"/>, que el scheduler registra). No lleva secretos.
/// </remarks>
internal sealed partial class ScheduleStore(string path, TrustedFile files, ILogger<ScheduleStore> logger)
{
    /// <summary>Tamaño máximo que se lee de <c>schedule.json</c>.</summary>
    internal const int MaxFileBytes = 4 * 1024 * 1024;

    /// <summary>Versión del esquema del archivo.</summary>
    internal const int SchemaVersion = 1;

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public string FilePath { get; } = Path.GetFullPath(path);

    /// <summary>Lee el archivo; vacío si no existe, está dañado o se rechaza.</summary>
    public async Task<Dictionary<Guid, DateTimeOffset>> LoadAsync(CancellationToken ct)
    {
        var result = new Dictionary<Guid, DateTimeOffset>();
        byte[]? bytes;
        try
        {
            bytes = await files.ReadAllBytesAsync(FilePath, MaxFileBytes, ct).ConfigureAwait(false);
        }
        catch (UntrustedFileException ex)
        {
            LogRejected(logger, ex.FilePath ?? FilePath, ex.Reason);
            return result;
        }
        catch (InvalidDataException)
        {
            LogCorrupt(logger, FilePath, nameof(InvalidDataException));
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogUnreadable(logger, FilePath, ex.GetType().Name);
            return result;
        }

        if (bytes is null)
        {
            return result;
        }

        try
        {
            var document = JsonSerializer.Deserialize<ScheduleDocument>(bytes, JsonOptions);
            foreach (var entry in document?.Jobs ?? [])
            {
                if (entry is not null && entry.JobId != Guid.Empty)
                {
                    result[entry.JobId] = entry.LastConsumedUtc.ToUniversalTime();
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
        {
            LogCorrupt(logger, FilePath, ex.GetType().Name);
            result.Clear();
        }

        return result;
    }

    /// <summary>Escribe el archivo de forma atómica.</summary>
    /// <exception cref="UntrustedFileException">El archivo existente o su carpeta se rechazan: no se escribe nada.</exception>
    /// <exception cref="IOException">La carpeta no existe o la escritura falla.</exception>
    public Task SaveAsync(IReadOnlyDictionary<Guid, DateTimeOffset> entries, CancellationToken ct)
    {
        var document = new ScheduleDocument
        {
            SchemaVersion = SchemaVersion,
            Jobs = [.. entries.OrderBy(e => e.Key).Select(e => new ScheduleEntry { JobId = e.Key, LastConsumedUtc = e.Value })],
        };
        return files.WriteAllBytesAsync(FilePath, JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions), ct);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Se rechaza {Path} ({Reason}): se trata como vacío y no se reemplaza; no se recuperarán ejecuciones perdidas hasta que se repare (§8.2).")]
    private static partial void LogRejected(ILogger logger, string path, UntrustedFileReason reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Path} no es válido ({ErrorType}): se trata como vacío.")]
    private static partial void LogCorrupt(ILogger logger, string path, string errorType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se ha podido leer {Path} ({ErrorType}): se trata como vacío.")]
    private static partial void LogUnreadable(ILogger logger, string path, string errorType);
}

/// <summary>Contenido de <c>schedule.json</c> (camelCase).</summary>
internal sealed record ScheduleDocument
{
    /// <summary>Versión del esquema del archivo.</summary>
    public int SchemaVersion { get; init; } = ScheduleStore.SchemaVersion;

    /// <summary>Último punto consumido de cada trabajo.</summary>
    public IReadOnlyList<ScheduleEntry?> Jobs { get; init; } = [];
}

/// <summary>Último punto de programación consumido de un trabajo.</summary>
internal sealed record ScheduleEntry
{
    /// <summary>Identificador del trabajo.</summary>
    public Guid JobId { get; init; }

    /// <summary>Último punto consumido (UTC).</summary>
    public DateTimeOffset LastConsumedUtc { get; init; }
}
