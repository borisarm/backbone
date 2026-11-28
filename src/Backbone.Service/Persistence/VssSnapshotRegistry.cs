using System.Text.Json;
using Backbone.Service.Os;
using Microsoft.Extensions.Logging;

namespace Backbone.Service.Persistence;

/// <summary>Un conjunto de instantáneas que Backbone ha creado y aún no ha eliminado.</summary>
internal sealed record VssSnapshotSetRecord
{
    /// <summary>Instancia del servicio (una por proceso) que lo creó: el arranque no toca nunca las de su propia instancia.</summary>
    public Guid Owner { get; init; }

    /// <summary>Identificador del conjunto en VSS.</summary>
    public Guid SetId { get; init; }

    /// <summary>Cuándo se registró (UTC), solo informativo.</summary>
    public DateTimeOffset CreatedUtc { get; init; }

    /// <summary>Instantáneas del conjunto.</summary>
    public IReadOnlyList<VssSnapshotRecord?> Snapshots { get; init; } = [];
}

/// <summary>Una instantánea registrada: su identificador y el volumen del que es (<c>\\?\Volume{guid}\</c>).</summary>
internal sealed record VssSnapshotRecord
{
    public Guid SnapshotId { get; init; }

    public string Volume { get; init; } = string.Empty;
}

/// <summary>
/// <c>data\vss-snapshots.json</c> (§8.2 <c>VssSnapshotter</c>, HU-15 criterio 3): las instantáneas que Backbone ha creado y
/// aún no ha eliminado. Es la <b>única</b> forma de reconocer "las instantáneas de Backbone": VSS no tiene una etiqueta de
/// propietario que un tercero no pueda copiar, así que el arranque solo elimina identificadores (GUID de VSS) que esta lista
/// contiene, y aun así los contrasta con el conjunto y el volumen registrados antes de borrar.
/// </summary>
/// <remarks>
/// Acceso por handle con <see cref="TrustedFile"/> y escritura atómica (ADR-14). Un archivo rechazado, dañado o ilegible se
/// trata como <b>vacío</b> (con aviso en el log): lo peor que ocurre es que no se eliminen huérfanas, nunca que se elimine algo
/// que no es de Backbone. Un archivo rechazado tampoco se reescribe: <see cref="AddAsync"/> lanza
/// <see cref="UntrustedFileException"/> y no se crea la instantánea (así nunca existe una sin registrar). No lleva secretos.
/// </remarks>
internal sealed partial class VssSnapshotRegistry(string path, TrustedFile files, TimeProvider time, ILogger<VssSnapshotRegistry> logger) : IDisposable
{
    /// <summary>Tamaño máximo que se lee del archivo.</summary>
    internal const int MaxFileBytes = 1024 * 1024;

    /// <summary>Versión del esquema del archivo.</summary>
    internal const int SchemaVersion = 1;

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public string FilePath { get; } = Path.GetFullPath(path);

    public void Dispose() => _gate.Dispose();

    /// <summary>Lee el archivo; vacío si no existe, está dañado o se rechaza.</summary>
    public async Task<IReadOnlyList<VssSnapshotSetRecord>> LoadAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await ReadAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Registra un conjunto <b>antes</b> de crearlo en VSS. Si no se puede escribir, lanza y no se crea nada.
    /// </summary>
    /// <exception cref="UntrustedFileException">El archivo o su carpeta se rechazan: no se escribe nada.</exception>
    /// <exception cref="IOException">La carpeta no existe o la escritura falla.</exception>
    public async Task AddAsync(Guid owner, Guid setId, IEnumerable<(Guid SnapshotId, string Volume)> snapshots, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = await ReadAsync(ct).ConfigureAwait(false);
            var record = new VssSnapshotSetRecord
            {
                Owner = owner,
                SetId = setId,
                CreatedUtc = time.GetUtcNow(),
                Snapshots = [.. snapshots.Select(s => new VssSnapshotRecord { SnapshotId = s.SnapshotId, Volume = s.Volume })],
            };
            await WriteAsync([.. current.Where(r => r.SetId != setId), record], ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Quita conjuntos (ya eliminados o que ya no existen) del registro. Un fallo de escritura se registra y no se propaga.</summary>
    public async Task RemoveAsync(IReadOnlyCollection<Guid> setIds, CancellationToken ct)
    {
        if (setIds.Count == 0)
        {
            return;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = await ReadAsync(ct).ConfigureAwait(false);
            var remaining = current.Where(r => !setIds.Contains(r.SetId)).ToList();
            if (remaining.Count != current.Count)
            {
                await WriteAsync(remaining, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UntrustedFileException)
        {
            // La entrada se queda: el siguiente arranque la encontrará, no encontrará la instantánea y la quitará.
            LogWriteFailed(logger, FilePath, ex.GetType().Name);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<VssSnapshotSetRecord>> ReadAsync(CancellationToken ct)
    {
        byte[]? bytes;
        try
        {
            bytes = await files.ReadAllBytesAsync(FilePath, MaxFileBytes, ct).ConfigureAwait(false);
        }
        catch (UntrustedFileException ex)
        {
            LogRejected(logger, ex.FilePath ?? FilePath, ex.Reason);
            return [];
        }
        catch (InvalidDataException)
        {
            LogCorrupt(logger, FilePath, nameof(InvalidDataException));
            return [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogUnreadable(logger, FilePath, ex.GetType().Name);
            return [];
        }

        if (bytes is null)
        {
            return [];
        }

        try
        {
            var document = JsonSerializer.Deserialize<VssSnapshotsDocument>(bytes, JsonOptions);
            return
            [
                .. (document?.Sets ?? [])
                    .OfType<VssSnapshotSetRecord>()
                    .Where(s => s.SetId != Guid.Empty)
                    .Select(s => s with { Snapshots = [.. (s.Snapshots ?? []).OfType<VssSnapshotRecord>().Where(x => x.SnapshotId != Guid.Empty)] }),
            ];
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
        {
            LogCorrupt(logger, FilePath, ex.GetType().Name);
            return [];
        }
    }

    private Task WriteAsync(IReadOnlyList<VssSnapshotSetRecord> sets, CancellationToken ct)
    {
        var document = new VssSnapshotsDocument { SchemaVersion = SchemaVersion, Sets = sets };
        return files.WriteAllBytesAsync(FilePath, JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions), ct);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Se rechaza {Path} ({Reason}): se trata como vacío; no se eliminarán instantáneas huérfanas hasta que se repare (§8.2).")]
    private static partial void LogRejected(ILogger logger, string path, UntrustedFileReason reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Path} no es válido ({ErrorType}): se trata como vacío.")]
    private static partial void LogCorrupt(ILogger logger, string path, string errorType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se ha podido leer {Path} ({ErrorType}): se trata como vacío.")]
    private static partial void LogUnreadable(ILogger logger, string path, string errorType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se ha podido actualizar {Path} ({ErrorType}); la entrada se limpiará en el siguiente arranque.")]
    private static partial void LogWriteFailed(ILogger logger, string path, string errorType);
}

/// <summary>Contenido de <c>vss-snapshots.json</c> (camelCase).</summary>
internal sealed record VssSnapshotsDocument
{
    /// <summary>Versión del esquema del archivo.</summary>
    public int SchemaVersion { get; init; } = VssSnapshotRegistry.SchemaVersion;

    /// <summary>Conjuntos de instantáneas creados y no eliminados.</summary>
    public IReadOnlyList<VssSnapshotSetRecord?> Sets { get; init; } = [];
}
