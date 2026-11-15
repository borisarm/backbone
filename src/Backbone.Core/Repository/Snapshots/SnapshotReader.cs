using System.Runtime.CompilerServices;
using System.Text.Json;
using Backbone.Core.Storage;

namespace Backbone.Core.Repository.Snapshots;

/// <summary>
/// Lector en streaming de <c>snapshots/&lt;id&gt;.json</c> (§6.3, R3). Al abrirse lee solo la cabecera
/// (todo lo anterior a <c>entries</c>); las entradas se recorren después una a una, sin cargarlas en memoria.
/// </summary>
/// <remarks>
/// El lector exige que los campos de cabecera precedan a <c>entries</c>, como los escribe
/// <see cref="SnapshotWriter"/>; los campos posteriores se ignoran. Toda entrada se valida (ruta, hash,
/// tamaño, unicidad) y al final se comprueba que coincide con <see cref="SnapshotStats.Files"/> y
/// <see cref="SnapshotStats.Bytes"/>; cualquier discrepancia es <see cref="RepositoryErrorKind.Corrupt"/>.
/// </remarks>
public sealed class SnapshotReader : IAsyncDisposable
{
    private readonly Stream stream;
    private readonly JsonStreamReader json;
    private readonly HashSet<string> aliases;
    private bool entriesStarted;

    private SnapshotReader(Stream stream, JsonStreamReader json, SnapshotHeader header)
    {
        this.stream = stream;
        this.json = json;
        Header = header;
        aliases = new HashSet<string>(header.Sources.Select(s => s.Alias), StringComparer.Ordinal);
    }

    /// <summary>Cabecera del snapshot.</summary>
    public SnapshotHeader Header { get; }

    /// <summary>
    /// Recorre las entradas. Solo se puede recorrer una vez; al terminar se valida el resto del documento.
    /// </summary>
    public async IAsyncEnumerable<SnapshotEntry> ReadEntriesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (entriesStarted)
        {
            throw new InvalidOperationException("Las entradas de un snapshot solo se pueden recorrer una vez.");
        }

        entriesStarted = true;
        var paths = new EntryPathSet();
        long files = 0;
        long bytes = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = await json.ReadAsync<SnapshotEntry?>(ParseEntry, cancellationToken).ConfigureAwait(false);
            if (entry is null)
            {
                break;
            }

            if (!aliases.Contains(entry.Alias))
            {
                throw json.Corrupt($"tiene una entrada de un origen que no figura en 'sources': '{entry.Path}'.");
            }

            if (!paths.Add(entry.Path))
            {
                throw json.Corrupt($"tiene una ruta repetida: '{entry.Path}'.");
            }

            if (!entry.IsDirectory)
            {
                files++;
                bytes = checked(bytes + entry.Size);
            }

            yield return entry;
        }

        // Resto del objeto principal: campos posteriores a "entries" (se ignoran).
        while (await json.ReadAsync<string?>(ParsePropertyNameOrEnd, cancellationToken).ConfigureAwait(false) is not null)
        {
            await json.ReadAsync<bool>(ParseSkip, cancellationToken).ConfigureAwait(false);
        }

        await json.ExpectEndAsync(cancellationToken).ConfigureAwait(false);

        if (files != Header.Stats.Files || bytes != Header.Stats.Bytes)
        {
            throw json.Corrupt($"no coincide con sus estadísticas: {files} archivos y {bytes} bytes en las entradas; {Header.Stats.Files} y {Header.Stats.Bytes} en 'stats'.");
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        json.Dispose();
        await stream.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Abre un snapshot y lee su cabecera.</summary>
    internal static async Task<SnapshotReader> OpenAsync(
        IRepositoryStorage storage,
        string snapshotId,
        int bufferSize,
        CancellationToken cancellationToken)
    {
        if (!SnapshotId.IsValid(snapshotId))
        {
            throw new ArgumentException($"Identificador de snapshot no válido: '{snapshotId}'.", nameof(snapshotId));
        }

        Stream stream;
        try
        {
            stream = await storage.OpenReadAsync(RepositoryLayout.SnapshotPath(snapshotId), cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException ex)
        {
            throw new RepositoryException(RepositoryErrorKind.SnapshotNotFound, $"No existe el snapshot {snapshotId}.", ex);
        }

        var json = new JsonStreamReader(stream, $"El snapshot {snapshotId}", bufferSize);
        try
        {
            var header = await ReadHeaderAsync(json, snapshotId, cancellationToken).ConfigureAwait(false);
            return new SnapshotReader(stream, json, header);
        }
        catch
        {
            json.Dispose();
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<SnapshotHeader> ReadHeaderAsync(JsonStreamReader json, string expectedId, CancellationToken cancellationToken)
    {
        await json.ReadAsync<bool>(ParseStartObject, cancellationToken).ConfigureAwait(false);

        int? formatVersion = null;
        string? id = null, jobId = null, jobName = null, host = null, parentId = null, status = null;
        DateTime? startedUtc = null, completedUtc = null;
        bool? usedVss = null;
        List<SnapshotSource>? sources = null;
        SnapshotStats? stats = null;
        List<SnapshotWarning>? warnings = null;

        while (true)
        {
            var name = await json.ReadAsync<string?>(ParsePropertyNameOrEnd, cancellationToken).ConfigureAwait(false)
                ?? throw json.Corrupt("no tiene 'entries'.");

            switch (name)
            {
                case "formatVersion":
                    formatVersion = await json.ReadAsync<int>(ParseInt32, cancellationToken).ConfigureAwait(false);
                    if (formatVersion > RepositoryLayout.FormatVersion)
                    {
                        throw new RepositoryException(
                            RepositoryErrorKind.Incompatible,
                            $"El snapshot {expectedId} usa la versión de formato {formatVersion}; esta versión de Backbone solo entiende hasta la {RepositoryLayout.FormatVersion}.");
                    }

                    break;
                case "id":
                    id = await json.ReadAsync<string?>(ParseString, cancellationToken).ConfigureAwait(false);
                    break;
                case "jobId":
                    jobId = await json.ReadAsync<string?>(ParseString, cancellationToken).ConfigureAwait(false);
                    break;
                case "jobName":
                    jobName = await json.ReadAsync<string?>(ParseString, cancellationToken).ConfigureAwait(false);
                    break;
                case "host":
                    host = await json.ReadAsync<string?>(ParseString, cancellationToken).ConfigureAwait(false);
                    break;
                case "parentId":
                    parentId = await json.ReadAsync<string?>(ParseNullableString, cancellationToken).ConfigureAwait(false);
                    break;
                case "status":
                    status = await json.ReadAsync<string?>(ParseString, cancellationToken).ConfigureAwait(false);
                    break;
                case "startedUtc":
                    startedUtc = await json.ReadAsync<DateTime>(ParseUtc, cancellationToken).ConfigureAwait(false);
                    break;
                case "completedUtc":
                    completedUtc = await json.ReadAsync<DateTime>(ParseUtc, cancellationToken).ConfigureAwait(false);
                    break;
                case "usedVss":
                    usedVss = await json.ReadAsync<bool>(ParseBoolean, cancellationToken).ConfigureAwait(false);
                    break;
                case "sources":
                    sources = await json.ReadAsync<List<SnapshotSource>>(ParseSources, cancellationToken).ConfigureAwait(false);
                    break;
                case "stats":
                    stats = await json.ReadAsync<SnapshotStats>(ParseStats, cancellationToken).ConfigureAwait(false);
                    break;
                case "warnings":
                    warnings = await json.ReadAsync<List<SnapshotWarning>>(ParseWarnings, cancellationToken).ConfigureAwait(false);
                    break;
                case "entries":
                    await json.ReadAsync<bool>(ParseStartArray, cancellationToken).ConfigureAwait(false);
                    return BuildHeader();
                default:
                    await json.ReadAsync<bool>(ParseSkip, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }

        SnapshotHeader BuildHeader()
        {
            if (formatVersion is null || formatVersion < 1)
            {
                throw json.Corrupt("no tiene una versión de formato válida.");
            }

            if (id != expectedId)
            {
                throw json.Corrupt($"declara el identificador '{id}', distinto del nombre del archivo.");
            }

            if (string.IsNullOrEmpty(jobId) || jobName is null || string.IsNullOrEmpty(host))
            {
                throw json.Corrupt("no identifica el trabajo o el equipo.");
            }

            if (parentId is not null && !SnapshotId.IsValid(parentId))
            {
                throw json.Corrupt("tiene un parentId no válido.");
            }

            if (startedUtc is null || completedUtc is null || usedVss is null || sources is null || stats is null)
            {
                throw json.Corrupt("tiene una cabecera incompleta.");
            }

            var parsedStatus = status switch
            {
                "completed" => SnapshotStatus.Completed,
                "completedWithWarnings" => SnapshotStatus.CompletedWithWarnings,
                _ => throw json.Corrupt($"tiene un estado desconocido: '{status}'."),
            };

            if (sources.Count == 0
                || sources.Any(s => !EntryPath.IsValidAlias(s.Alias))
                || sources.Select(s => s.Alias).Distinct(StringComparer.OrdinalIgnoreCase).Count() != sources.Count)
            {
                throw json.Corrupt("tiene orígenes no válidos.");
            }

            return new SnapshotHeader
            {
                FormatVersion = formatVersion.Value,
                Id = id,
                JobId = jobId,
                JobName = jobName,
                Host = host,
                StartedUtc = startedUtc.Value,
                CompletedUtc = completedUtc.Value,
                ParentId = parentId,
                Status = parsedStatus,
                UsedVss = usedVss.Value,
                Sources = sources,
                Stats = stats,
                Warnings = warnings ?? [],
            };
        }
    }

    // ---- Analizadores de unidades (todo o nada; véase JsonStreamReader) ----

    private static bool ParseStartObject(ref Utf8JsonReader reader, out bool value)
    {
        value = true;
        if (!reader.Read())
        {
            return false;
        }

        return reader.TokenType == JsonTokenType.StartObject ? true : throw new JsonException("Se esperaba un objeto.");
    }

    private static bool ParseStartArray(ref Utf8JsonReader reader, out bool value)
    {
        value = true;
        if (!reader.Read())
        {
            return false;
        }

        return reader.TokenType == JsonTokenType.StartArray ? true : throw new JsonException("Se esperaba una lista.");
    }

    private static bool ParsePropertyNameOrEnd(ref Utf8JsonReader reader, out string? value)
    {
        value = null;
        if (!reader.Read())
        {
            return false;
        }

        if (reader.TokenType == JsonTokenType.EndObject)
        {
            return true;
        }

        value = reader.GetString();
        return true;
    }

    private static bool ParseSkip(ref Utf8JsonReader reader, out bool value)
    {
        value = true;
        return reader.Read() && reader.TrySkip();
    }

    private static bool ParseString(ref Utf8JsonReader reader, out string? value)
    {
        value = null;
        if (!reader.Read())
        {
            return false;
        }

        value = reader.TokenType == JsonTokenType.String ? reader.GetString() : throw new JsonException("Se esperaba una cadena.");
        return true;
    }

    private static bool ParseNullableString(ref Utf8JsonReader reader, out string? value)
    {
        value = null;
        if (!reader.Read())
        {
            return false;
        }

        value = reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.String => reader.GetString(),
            _ => throw new JsonException("Se esperaba una cadena o null."),
        };
        return true;
    }

    private static bool ParseInt32(ref Utf8JsonReader reader, out int value)
    {
        value = 0;
        if (!reader.Read())
        {
            return false;
        }

        value = reader.GetInt32();
        return true;
    }

    private static bool ParseBoolean(ref Utf8JsonReader reader, out bool value)
    {
        value = false;
        if (!reader.Read())
        {
            return false;
        }

        value = reader.GetBoolean();
        return true;
    }

    private static bool ParseUtc(ref Utf8JsonReader reader, out DateTime value)
    {
        value = default;
        if (!reader.Read())
        {
            return false;
        }

        return JsonFormat.TryGetUtc(ref reader, out value) ? true : throw new JsonException("Se esperaba una fecha ISO 8601.");
    }

    private static bool ParseSources(ref Utf8JsonReader reader, out List<SnapshotSource> value)
    {
        value = [];
        if (!ParseStartArray(ref reader, out _))
        {
            return false;
        }

        while (true)
        {
            if (!reader.Read())
            {
                return false;
            }

            if (reader.TokenType == JsonTokenType.EndArray)
            {
                return true;
            }

            if (reader.TokenType != JsonTokenType.StartObject)
            {
                throw new JsonException("Se esperaba un origen.");
            }

            string? alias = null, path = null;
            while (true)
            {
                if (!reader.Read())
                {
                    return false;
                }

                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    break;
                }

                if (reader.ValueTextEquals("alias"u8))
                {
                    if (!ParseString(ref reader, out alias))
                    {
                        return false;
                    }
                }
                else if (reader.ValueTextEquals("path"u8))
                {
                    if (!ParseString(ref reader, out path))
                    {
                        return false;
                    }
                }
                else if (!ParseSkip(ref reader, out _))
                {
                    return false;
                }
            }

            value.Add(new SnapshotSource(
                alias ?? throw new JsonException("Origen sin alias."),
                path ?? throw new JsonException("Origen sin ruta.")));
        }
    }

    private static bool ParseStats(ref Utf8JsonReader reader, out SnapshotStats value)
    {
        value = new SnapshotStats();
        if (!ParseStartObjectToken(ref reader))
        {
            return false;
        }

        // files, bytes, newFiles, changedFiles, newObjects, newBytes, skipped
        Span<long> values = stackalloc long[7];
        while (true)
        {
            if (!reader.Read())
            {
                return false;
            }

            if (reader.TokenType == JsonTokenType.EndObject)
            {
                break;
            }

            var index = StatIndex(ref reader);
            if (index < 0)
            {
                if (!ParseSkip(ref reader, out _))
                {
                    return false;
                }

                continue;
            }

            if (!reader.Read())
            {
                return false;
            }

            var number = reader.GetInt64();
            values[index] = number >= 0 ? number : throw new JsonException("Estadística negativa.");
        }

        value = new SnapshotStats
        {
            Files = values[0],
            Bytes = values[1],
            NewFiles = values[2],
            ChangedFiles = values[3],
            NewObjects = values[4],
            NewBytes = values[5],
            Skipped = values[6],
        };
        return true;
    }

    private static int StatIndex(ref Utf8JsonReader reader) =>
        reader.ValueTextEquals("files"u8) ? 0
        : reader.ValueTextEquals("bytes"u8) ? 1
        : reader.ValueTextEquals("newFiles"u8) ? 2
        : reader.ValueTextEquals("changedFiles"u8) ? 3
        : reader.ValueTextEquals("newObjects"u8) ? 4
        : reader.ValueTextEquals("newBytes"u8) ? 5
        : reader.ValueTextEquals("skipped"u8) ? 6
        : -1;

    private static bool ParseWarnings(ref Utf8JsonReader reader, out List<SnapshotWarning> value)
    {
        value = [];
        if (!ParseStartArray(ref reader, out _))
        {
            return false;
        }

        while (true)
        {
            if (!reader.Read())
            {
                return false;
            }

            if (reader.TokenType == JsonTokenType.EndArray)
            {
                return true;
            }

            if (reader.TokenType != JsonTokenType.StartObject)
            {
                throw new JsonException("Se esperaba un aviso.");
            }

            string? path = null, code = null, message = null;
            while (true)
            {
                if (!reader.Read())
                {
                    return false;
                }

                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    break;
                }

                bool complete;
                if (reader.ValueTextEquals("path"u8))
                {
                    complete = ParseString(ref reader, out path);
                }
                else if (reader.ValueTextEquals("code"u8))
                {
                    complete = ParseString(ref reader, out code);
                }
                else if (reader.ValueTextEquals("message"u8))
                {
                    complete = ParseString(ref reader, out message);
                }
                else
                {
                    complete = ParseSkip(ref reader, out _);
                }

                if (!complete)
                {
                    return false;
                }
            }

            value.Add(new SnapshotWarning(path ?? string.Empty, code ?? string.Empty, message ?? string.Empty));
        }
    }

    /// <summary>Una entrada, o <see langword="null"/> al llegar al final de la lista.</summary>
    private static bool ParseEntry(ref Utf8JsonReader reader, out SnapshotEntry? value)
    {
        value = null;
        if (!reader.Read())
        {
            return false;
        }

        if (reader.TokenType == JsonTokenType.EndArray)
        {
            return true;
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Se esperaba una entrada.");
        }

        string? path = null, hash = null;
        long? size = null;
        DateTime? modified = null;
        int? attributes = null;
        var isDirectory = false;

        while (true)
        {
            if (!reader.Read())
            {
                return false;
            }

            if (reader.TokenType == JsonTokenType.EndObject)
            {
                break;
            }

            if (reader.ValueTextEquals("p"u8))
            {
                if (!ParseString(ref reader, out path))
                {
                    return false;
                }
            }
            else if (reader.ValueTextEquals("h"u8))
            {
                if (!ParseString(ref reader, out hash))
                {
                    return false;
                }
            }
            else if (reader.ValueTextEquals("s"u8))
            {
                if (!reader.Read())
                {
                    return false;
                }

                size = reader.GetInt64();
            }
            else if (reader.ValueTextEquals("m"u8))
            {
                if (!ParseUtc(ref reader, out var m))
                {
                    return false;
                }

                modified = m;
            }
            else if (reader.ValueTextEquals("a"u8))
            {
                if (!ParseInt32(ref reader, out var a))
                {
                    return false;
                }

                attributes = a;
            }
            else if (reader.ValueTextEquals("d"u8))
            {
                if (!ParseBoolean(ref reader, out isDirectory))
                {
                    return false;
                }
            }
            else if (!ParseSkip(ref reader, out _))
            {
                return false;
            }
        }

        value = isDirectory ? BuildDirectory(path) : BuildFile(path, hash, size, modified, attributes);
        return true;
    }

    private static SnapshotEntry BuildDirectory(string? path) =>
        EntryPath.IsValidDirectoryPath(path)
            ? SnapshotEntry.Directory(path)
            : throw new JsonException($"Ruta de carpeta no válida: '{path}'.");

    private static SnapshotEntry BuildFile(string? path, string? hash, long? size, DateTime? modified, int? attributes)
    {
        if (!EntryPath.IsValidFilePath(path))
        {
            throw new JsonException($"Ruta de archivo no válida: '{path}'.");
        }

        if (!ContentHash.TryParse(hash, out var contentHash))
        {
            throw new JsonException($"Hash no válido en '{path}'.");
        }

        if (size is null or < 0 || modified is null || attributes is null
            || ((FileAttributes)attributes.Value & ~SnapshotEntry.StoredAttributes) != 0)
        {
            throw new JsonException($"Entrada incompleta o no válida: '{path}'.");
        }

        return SnapshotEntry.File(path, contentHash, size.Value, modified.Value, (FileAttributes)attributes.Value);
    }

    private static bool ParseStartObjectToken(ref Utf8JsonReader reader)
    {
        if (!reader.Read())
        {
            return false;
        }

        return reader.TokenType == JsonTokenType.StartObject ? true : throw new JsonException("Se esperaba un objeto.");
    }
}
