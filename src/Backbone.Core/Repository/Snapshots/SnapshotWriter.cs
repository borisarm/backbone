using System.Buffers;
using System.Text.Json;
using Backbone.Core.Repository.Locking;
using Backbone.Core.Storage;

namespace Backbone.Core.Repository.Snapshots;

/// <summary>
/// Escritor en streaming de un snapshot (§6.3, R3).
/// </summary>
/// <remarks>
/// <para>
/// Las entradas se escriben, a medida que llegan, en un temporal de <c>tmp/</c>. Al completar se compone el
/// manifiesto definitivo en otro temporal (cabecera, con las estadísticas ya conocidas, seguida de las entradas
/// copiadas byte a byte), se confirma que el bloqueo sigue vigente y se renombra a <c>snapshots/&lt;id&gt;.json</c>.
/// Así la cabecera va antes que las entradas (se puede listar un snapshot sin leerlas) y la memoria no
/// depende del número de archivos, salvo el conjunto de resúmenes de ruta (<see cref="EntryPathSet"/>).
/// </para>
/// <para>
/// El llamante debe garantizar que todos los objetos referenciados existen antes de llamar a
/// <see cref="CompleteAsync"/> (§6.4.3). Si el escritor se libera sin completar, no se publica nada.
/// </para>
/// </remarks>
public sealed class SnapshotWriter : IAsyncDisposable
{
    private const int StagingFlushThreshold = 256 * 1024;
    private const int CopyBufferSize = 1024 * 1024;

    private readonly IRepositoryStorage storage;
    private readonly TimeProvider timeProvider;
    private readonly RepositoryLock repositoryLock;
    private readonly SnapshotStart start;
    private readonly HashSet<string> aliases;
    private readonly string stagingPath;
    private readonly ArrayBufferWriter<byte> pending = new(StagingFlushThreshold + 4096);
    private readonly Utf8JsonWriter entryWriter;
    private readonly EntryPathSet paths = new();
    private Stream? staging;
    private State state = State.Open;
    private long files;
    private long bytes;
    private long entries;

    private SnapshotWriter(
        IRepositoryStorage storage,
        TimeProvider timeProvider,
        RepositoryLock repositoryLock,
        SnapshotStart start,
        string id,
        string stagingPath,
        Stream staging)
    {
        this.storage = storage;
        this.timeProvider = timeProvider;
        this.repositoryLock = repositoryLock;
        this.start = start;
        this.stagingPath = stagingPath;
        this.staging = staging;
        Id = id;
        aliases = new HashSet<string>(start.Sources.Select(s => s.Alias), StringComparer.Ordinal);
        entryWriter = new Utf8JsonWriter(pending, JsonFormat.WriterOptions(indented: false));
    }

    private enum State
    {
        Open,
        Completed,
        Failed,
    }

    /// <summary>Identificador del snapshot que se está escribiendo.</summary>
    public string Id { get; }

    /// <summary>Número de entradas añadidas.</summary>
    public long EntryCount => entries;

    /// <summary>Añade una entrada.</summary>
    /// <exception cref="ArgumentException">
    /// La ruta no pertenece a ningún origen del snapshot o ya se había añadido.
    /// </exception>
    public async Task AddAsync(SnapshotEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ThrowIfNotOpen();
        cancellationToken.ThrowIfCancellationRequested();

        if (!aliases.Contains(entry.Alias))
        {
            throw new ArgumentException($"La entrada '{entry.Path}' no pertenece a ningún origen del snapshot.", nameof(entry));
        }

        if (!paths.Add(entry.Path))
        {
            throw new ArgumentException($"La ruta '{entry.Path}' ya está en el snapshot.", nameof(entry));
        }

        // Separador entre entradas: una por línea, legible y sin coste apreciable.
        pending.Write(entries == 0 ? "\n"u8 : ",\n"u8);
        entryWriter.Reset();
        entryWriter.WriteStartObject();
        entryWriter.WriteString("p", entry.Path);
        if (entry.IsDirectory)
        {
            entryWriter.WriteBoolean("d", true);
        }
        else
        {
            entryWriter.WriteString("h", entry.Hash.GetValueOrDefault().ToString());
            entryWriter.WriteNumber("s", entry.Size);
            JsonFormat.WriteUtc(entryWriter, "m", entry.ModifiedUtc, alwaysFraction: true);
            entryWriter.WriteNumber("a", (int)entry.Attributes);
            files++;
            bytes = checked(bytes + entry.Size);
        }

        entryWriter.WriteEndObject();
        entryWriter.Flush();
        entries++;

        if (pending.WrittenCount >= StagingFlushThreshold)
        {
            await FlushPendingAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Publica el snapshot. Antes de renombrar comprueba que el bloqueo sigue vigente.
    /// </summary>
    /// <returns>Cabecera del snapshot publicado.</returns>
    /// <exception cref="RepositoryException">
    /// <see cref="RepositoryErrorKind.LockLost"/> si el bloqueo se perdió;
    /// <see cref="RepositoryErrorKind.SnapshotIdConflict"/> si ya existía un snapshot con el mismo identificador.
    /// </exception>
    public async Task<SnapshotHeader> CompleteAsync(SnapshotCompletion completion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(completion);
        ThrowIfNotOpen();
        try
        {
            var header = BuildHeader(completion);

            await FlushPendingAsync(cancellationToken).ConfigureAwait(false);
            var stream = staging ?? throw new InvalidOperationException("Temporal de entradas cerrado.");
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            await stream.DisposeAsync().ConfigureAwait(false);
            staging = null;

            var published = await AtomicFile.WriteAsync(
                storage,
                RepositoryLayout.SnapshotPath(Id),
                overwrite: false,
                (output, ct) => WriteManifestAsync(output, header, ct),
                cancellationToken,
                beforeMove: repositoryLock.EnsureHeldAsync).ConfigureAwait(false);

            if (!published)
            {
                throw new RepositoryException(RepositoryErrorKind.SnapshotIdConflict, $"Ya existe un snapshot {Id}.");
            }

            state = State.Completed;
            await AtomicFile.TryDeleteAsync(storage, stagingPath).ConfigureAwait(false);
            return header;
        }
        catch
        {
            state = State.Failed;
            throw;
        }
    }

    /// <summary>Si no se completó, descarta los temporales (no se publica nada).</summary>
    public async ValueTask DisposeAsync()
    {
        await entryWriter.DisposeAsync().ConfigureAwait(false);
        if (staging is not null)
        {
            try
            {
                await staging.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // El temporal queda en tmp/ y lo recogerá la limpieza.
            }

            staging = null;
        }

        if (state != State.Completed)
        {
            state = State.Failed;
            await AtomicFile.TryDeleteAsync(storage, stagingPath).ConfigureAwait(false);
        }
    }

    /// <summary>Crea un escritor; valida los datos de inicio y elige un identificador libre.</summary>
    internal static async Task<SnapshotWriter> CreateAsync(
        IRepositoryStorage storage,
        TimeProvider timeProvider,
        RepositoryLock repositoryLock,
        SnapshotStart start,
        CancellationToken cancellationToken)
    {
        Validate(start);
        repositoryLock.ThrowIfLost();

        string id;
        var attempts = 0;
        do
        {
            if (++attempts > 16)
            {
                throw new RepositoryException(RepositoryErrorKind.SnapshotIdConflict, "No se encontró un identificador de snapshot libre.");
            }

            id = SnapshotId.Create(start.StartedUtc);
        }
        while (await storage.GetFileInfoAsync(RepositoryLayout.SnapshotPath(id), cancellationToken).ConfigureAwait(false) is not null);

        var stagingPath = RepositoryLayout.NewTempPath();
        var staging = await storage.CreateNewAsync(stagingPath, cancellationToken).ConfigureAwait(false);
        return new SnapshotWriter(storage, timeProvider, repositoryLock, start, id, stagingPath, staging);
    }

    private static void Validate(SnapshotStart start)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentException.ThrowIfNullOrWhiteSpace(start.JobId);
        ArgumentNullException.ThrowIfNull(start.JobName);
        ArgumentException.ThrowIfNullOrWhiteSpace(start.Host);
        JsonFormat.RequireUtc(start.StartedUtc, nameof(start));
        ArgumentNullException.ThrowIfNull(start.Sources);

        if (start.ParentId is not null && !SnapshotId.IsValid(start.ParentId))
        {
            throw new ArgumentException($"parentId no válido: '{start.ParentId}'.", nameof(start));
        }

        if (start.Sources.Count == 0)
        {
            throw new ArgumentException("Un snapshot necesita al menos un origen.", nameof(start));
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in start.Sources)
        {
            if (!EntryPath.IsValidAlias(source.Alias) || !seen.Add(source.Alias))
            {
                throw new ArgumentException($"Alias de origen no válido o repetido: '{source.Alias}'.", nameof(start));
            }

            ArgumentException.ThrowIfNullOrEmpty(source.Path);
            if (JsonFormat.HasLoneSurrogate(source.Path))
            {
                throw new ArgumentException($"La ruta del origen '{source.Alias}' no es UTF-16 válido.", nameof(start));
            }
        }

        foreach (var text in new[] { start.JobId, start.JobName, start.Host })
        {
            if (JsonFormat.HasLoneSurrogate(text))
            {
                throw new ArgumentException("Texto no válido en los datos del snapshot.", nameof(start));
            }
        }
    }

    private SnapshotHeader BuildHeader(SnapshotCompletion completion)
    {
        ArgumentNullException.ThrowIfNull(completion.Stats);
        ArgumentNullException.ThrowIfNull(completion.Warnings);
        var stats = completion.Stats with { Files = files, Bytes = bytes };
        if (stats.NewFiles < 0 || stats.ChangedFiles < 0 || stats.NewObjects < 0 || stats.NewBytes < 0 || stats.Skipped < 0)
        {
            throw new ArgumentException("Las estadísticas no pueden ser negativas.", nameof(completion));
        }

        return new SnapshotHeader
        {
            FormatVersion = RepositoryLayout.FormatVersion,
            Id = Id,
            JobId = start.JobId,
            JobName = start.JobName,
            Host = start.Host,
            StartedUtc = start.StartedUtc,
            CompletedUtc = timeProvider.GetUtcNow().UtcDateTime,
            ParentId = start.ParentId,
            Status = completion.Status,
            UsedVss = start.UsedVss,
            Sources = [.. start.Sources],
            Stats = stats,
            Warnings = [.. completion.Warnings.Select(w => new SnapshotWarning(
                JsonFormat.SanitizeText(w.Path),
                JsonFormat.SanitizeText(w.Code),
                JsonFormat.SanitizeText(w.Message)))],
        };
    }

    private async Task WriteManifestAsync(Stream output, SnapshotHeader header, CancellationToken cancellationToken)
    {
        var writer = new Utf8JsonWriter(output, JsonFormat.WriterOptions(indented: false));
        await using (writer.ConfigureAwait(false))
        {
            writer.WriteStartObject();
            writer.WriteNumber("formatVersion", header.FormatVersion);
            writer.WriteString("id", header.Id);
            writer.WriteString("jobId", header.JobId);
            writer.WriteString("jobName", header.JobName);
            writer.WriteString("host", header.Host);
            JsonFormat.WriteUtc(writer, "startedUtc", header.StartedUtc);
            JsonFormat.WriteUtc(writer, "completedUtc", header.CompletedUtc);
            if (header.ParentId is null)
            {
                writer.WriteNull("parentId");
            }
            else
            {
                writer.WriteString("parentId", header.ParentId);
            }

            writer.WriteString("status", header.Status == SnapshotStatus.CompletedWithWarnings ? "completedWithWarnings" : "completed");
            writer.WriteBoolean("usedVss", header.UsedVss);

            writer.WriteStartArray("sources");
            foreach (var source in header.Sources)
            {
                writer.WriteStartObject();
                writer.WriteString("alias", source.Alias);
                writer.WriteString("path", source.Path);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            writer.WriteStartObject("stats");
            writer.WriteNumber("files", header.Stats.Files);
            writer.WriteNumber("bytes", header.Stats.Bytes);
            writer.WriteNumber("newFiles", header.Stats.NewFiles);
            writer.WriteNumber("changedFiles", header.Stats.ChangedFiles);
            writer.WriteNumber("newObjects", header.Stats.NewObjects);
            writer.WriteNumber("newBytes", header.Stats.NewBytes);
            writer.WriteNumber("skipped", header.Stats.Skipped);
            writer.WriteEndObject();

            writer.WriteStartArray("warnings");
            foreach (var warning in header.Warnings)
            {
                writer.WriteStartObject();
                writer.WriteString("path", warning.Path);
                writer.WriteString("code", warning.Code);
                writer.WriteString("message", warning.Message);
                writer.WriteEndObject();

                if (writer.BytesPending >= StagingFlushThreshold)
                {
                    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            writer.WriteEndArray();

            // Las entradas ya están serializadas en el temporal: se copian tal cual dentro de la lista.
            writer.WriteStartArray("entries");
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);

            var entriesStream = await storage.OpenReadAsync(stagingPath, cancellationToken).ConfigureAwait(false);
            await using (entriesStream.ConfigureAwait(false))
            {
                await entriesStream.CopyToAsync(output, CopyBufferSize, cancellationToken).ConfigureAwait(false);
            }

            await output.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
            writer.WriteEndArray();
            writer.WriteEndObject();
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task FlushPendingAsync(CancellationToken cancellationToken)
    {
        if (pending.WrittenCount == 0)
        {
            return;
        }

        var stream = staging ?? throw new InvalidOperationException("Temporal de entradas cerrado.");
        try
        {
            await stream.WriteAsync(pending.WrittenMemory, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            state = State.Failed;
            throw;
        }

        pending.ResetWrittenCount();
    }

    private void ThrowIfNotOpen()
    {
        if (state != State.Open)
        {
            throw new InvalidOperationException(state == State.Completed
                ? "El snapshot ya se ha publicado."
                : "El escritor del snapshot falló y no se puede reutilizar.");
        }
    }
}
