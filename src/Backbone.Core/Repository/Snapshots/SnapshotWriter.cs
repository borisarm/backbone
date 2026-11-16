using System.Buffers;
using System.Globalization;
using System.Text.Json;
using Backbone.Contracts;
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
/// <para>
/// Nunca publica un manifiesto que <see cref="SnapshotReader"/> rechace (§6.3 "Límites"): rechaza al empezar los datos de
/// cabecera fuera de límite, recorta los textos de los avisos y guarda como mucho los que caben (con un aviso resumen
/// <see cref="RunIssueCode.WarningsTruncated"/>), y falla con <see cref="RepositoryException"/>
/// (<see cref="RepositoryErrorKind.SnapshotTooLarge"/>), sin publicar, si se superan
/// <see cref="RepositoryLayout.MaxSnapshotEntries"/> entradas o <see cref="RepositoryLayout.MaxSnapshotManifestBytes"/> bytes.
/// </para>
/// </remarks>
public sealed class SnapshotWriter : IAsyncDisposable
{
    private const int StagingFlushThreshold = 256 * 1024;
    private const int CopyBufferSize = 1024 * 1024;

    /// <summary>Lo que sigue a la lista de avisos en la cabecera: <c>],"entries":[</c>.</summary>
    private const int HeaderSuffixBytes = 13;

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
    private long entryBytes;

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

    /// <summary>Máximo de entradas (las pruebas lo reducen para comprobar el corte sin escribir millones).</summary>
    internal long MaxEntries { get; set; } = RepositoryLayout.MaxSnapshotEntries;

    /// <summary>Tamaño máximo del manifiesto (las pruebas lo reducen).</summary>
    internal long MaxManifestBytes { get; set; } = RepositoryLayout.MaxSnapshotManifestBytes;

    /// <summary>Lo que cierra el manifiesto después de las entradas: salto de línea, <c>]</c> y <c>}</c>.</summary>
    private static ReadOnlySpan<byte> ManifestEnd => "\n]}"u8;

    /// <summary>Añade una entrada.</summary>
    /// <exception cref="ArgumentException">
    /// La ruta no pertenece a ningún origen del snapshot o ya se había añadido.
    /// </exception>
    /// <exception cref="RepositoryException">
    /// <see cref="RepositoryErrorKind.SnapshotTooLarge"/>: se supera <see cref="RepositoryLayout.MaxSnapshotEntries"/> o el
    /// tamaño máximo del manifiesto; el escritor queda fallido y no publicará nada (§6.3).
    /// </exception>
    public async Task AddAsync(SnapshotEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ThrowIfNotOpen();
        cancellationToken.ThrowIfCancellationRequested();

        if (entries >= MaxEntries)
        {
            throw Fail($"La copia supera el máximo de {MaxEntries} entradas (archivos y carpetas vacías) de un snapshot; no se publica el snapshot.");
        }

        if (!aliases.Contains(entry.Alias))
        {
            throw new ArgumentException($"La entrada '{Untrusted.Describe(entry.Path)}' no pertenece a ningún origen del snapshot.", nameof(entry));
        }

        if (!paths.Add(entry.Path))
        {
            throw new ArgumentException($"La ruta '{Untrusted.Describe(entry.Path)}' ya está en el snapshot.", nameof(entry));
        }

        // Separador entre entradas: una por línea, legible y sin coste apreciable.
        var before = pending.WrittenCount;
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
        entryBytes += pending.WrittenCount - before;

        // Aun sin cabecera, las entradas ya no caben: no tiene sentido seguir copiando.
        if (entryBytes + ManifestEnd.Length > MaxManifestBytes)
        {
            throw Fail(ManifestTooLargeMessage());
        }

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
    /// <see cref="RepositoryErrorKind.SnapshotIdConflict"/> si ya existía un snapshot con el mismo identificador;
    /// <see cref="RepositoryErrorKind.SnapshotTooLarge"/> si el manifiesto superaría su tamaño máximo.
    /// </exception>
    public async Task<SnapshotHeader> CompleteAsync(SnapshotCompletion completion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(completion);
        ThrowIfNotOpen();
        try
        {
            var (header, headerBytes) = BuildHeader(completion);
            if (headerBytes.Length + entryBytes + ManifestEnd.Length > MaxManifestBytes)
            {
                throw new RepositoryException(RepositoryErrorKind.SnapshotTooLarge, ManifestTooLargeMessage());
            }

            await FlushPendingAsync(cancellationToken).ConfigureAwait(false);
            var stream = staging ?? throw new InvalidOperationException("Temporal de entradas cerrado.");
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            await stream.DisposeAsync().ConfigureAwait(false);
            staging = null;

            var published = await AtomicFile.WriteAsync(
                storage,
                RepositoryLayout.SnapshotPath(Id),
                overwrite: false,
                (output, ct) => WriteManifestAsync(output, headerBytes, ct),
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

    /// <summary>
    /// Datos de cabecera que no pueden venir de un trabajo válido (§7.5): se rechazan al empezar la copia, porque el lector
    /// los consideraría corrupción (§6.3 "Límites").
    /// </summary>
    private static void Validate(SnapshotStart start)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentException.ThrowIfNullOrWhiteSpace(start.JobId);
        ArgumentNullException.ThrowIfNull(start.JobName);
        ArgumentException.ThrowIfNullOrWhiteSpace(start.Host);
        JsonFormat.RequireUtc(start.StartedUtc, nameof(start));
        ArgumentNullException.ThrowIfNull(start.Sources);

        if (start.JobId.Length > SnapshotLimits.MaxJobIdLength)
        {
            throw new ArgumentException($"El identificador del trabajo supera {SnapshotLimits.MaxJobIdLength} caracteres.", nameof(start));
        }

        if (start.JobName.Length > SnapshotLimits.MaxJobNameLength)
        {
            throw new ArgumentException($"El nombre del trabajo supera {SnapshotLimits.MaxJobNameLength} caracteres.", nameof(start));
        }

        if (!RepositoryPath.IsValidSegment(start.Host))
        {
            throw new ArgumentException($"Nombre de equipo no válido para un snapshot: '{Untrusted.Describe(start.Host)}'.", nameof(start));
        }

        if (start.Sources.Count > SnapshotLimits.MaxSources)
        {
            throw new ArgumentException($"Un snapshot admite como mucho {SnapshotLimits.MaxSources} orígenes.", nameof(start));
        }

        if (start.ParentId is not null && !SnapshotId.IsValid(start.ParentId))
        {
            throw new ArgumentException($"parentId no válido: '{Untrusted.Describe(start.ParentId)}'.", nameof(start));
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
                throw new ArgumentException($"Alias de origen no válido o repetido: '{Untrusted.Describe(source.Alias)}'.", nameof(start));
            }

            ArgumentException.ThrowIfNullOrEmpty(source.Path);
            if (source.Path.Length > SnapshotLimits.MaxSourcePathLength)
            {
                throw new ArgumentException($"La ruta del origen '{Untrusted.Describe(source.Alias)}' supera {SnapshotLimits.MaxSourcePathLength} caracteres.", nameof(start));
            }

            if (JsonFormat.HasLoneSurrogate(source.Path))
            {
                throw new ArgumentException($"La ruta del origen '{Untrusted.Describe(source.Alias)}' no es UTF-16 válido.", nameof(start));
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

    /// <summary>Recorta un texto a <paramref name="maxLength"/> caracteres sin partir un par suplente.</summary>
    internal static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        var cut = maxLength;
        if (char.IsHighSurrogate(value[cut - 1]) && char.IsLowSurrogate(value[cut]))
        {
            cut--;
        }

        return value[..cut];
    }

    /// <summary>Aviso tal como se guarda: UTF-16 válido (U+FFFD en las suplentes sueltas) y textos recortados a su límite.</summary>
    internal static SnapshotWarning NormalizeWarning(SnapshotWarning warning)
    {
        ArgumentNullException.ThrowIfNull(warning);
        return new SnapshotWarning(
            Truncate(JsonFormat.SanitizeText(warning.Path), SnapshotLimits.MaxWarningPathLength),
            Truncate(JsonFormat.SanitizeText(warning.Code), SnapshotLimits.MaxWarningCodeLength),
            Truncate(JsonFormat.SanitizeText(warning.Message), SnapshotLimits.MaxWarningMessageLength));
    }

    /// <summary>Aviso resumen de los descartados (§6.3).</summary>
    internal static SnapshotWarning TruncationSummary(long discarded) =>
        new(
            string.Empty,
            nameof(RunIssueCode.WarningsTruncated),
            $"{discarded.ToString(CultureInfo.InvariantCulture)} avisos más no se han guardado en el snapshot.");

    /// <summary>
    /// Elige los avisos que se guardan (§6.3): todos si son como mucho <see cref="SnapshotLimits.MaxWarnings"/> y la cabecera
    /// no pasa de <see cref="SnapshotLimits.MaxHeaderBytes"/>; si no, los primeros que quepan, como mucho
    /// <see cref="SnapshotLimits.MaxWarnings"/> − 1 y con la cabecera dentro de
    /// <see cref="SnapshotLimits.TruncatedWarningsHeaderBytes"/>, seguidos del aviso resumen.
    /// </summary>
    /// <param name="warnings">Avisos de la ejecución, en orden.</param>
    /// <param name="fixedHeaderBytes">Bytes de la cabecera sin ningún aviso (incluido <c>],"entries":[</c>).</param>
    /// <param name="omitted">
    /// Avisos de la ejecución que el llamante ya no conserva en memoria (van detrás de <paramref name="warnings"/>); cuentan
    /// para el límite y para el aviso resumen.
    /// </param>
    internal static List<SnapshotWarning> SelectWarnings(IReadOnlyList<SnapshotWarning> warnings, long fixedHeaderBytes, long omitted = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(omitted);
        var measure = new ArrayBufferWriter<byte>(4096);
        using var measureWriter = new Utf8JsonWriter(measure, JsonFormat.WriterOptions(indented: false));

        // Como mucho se examinan MaxWarnings avisos: con más ya se sabe que no caben todos.
        var limit = Math.Min(warnings.Count, SnapshotLimits.MaxWarnings);
        var normalized = new List<SnapshotWarning>(limit);
        var total = warnings.Count + omitted;
        var allFit = total <= SnapshotLimits.MaxWarnings;
        var kept = 0;
        var keptStopped = false;
        var headerBytes = fixedHeaderBytes;
        for (var i = 0; i < limit && (allFit || !keptStopped); i++)
        {
            var warning = NormalizeWarning(warnings[i]);
            measure.ResetWrittenCount();
            measureWriter.Reset();
            WriteWarning(measureWriter, warning);
            measureWriter.Flush();

            // Detrás del primero, cada aviso lleva una coma delante.
            headerBytes += measure.WrittenCount + (i > 0 ? 1 : 0);
            allFit &= headerBytes <= SnapshotLimits.MaxHeaderBytes;
            if (!keptStopped && i < SnapshotLimits.MaxWarnings - 1 && headerBytes <= SnapshotLimits.TruncatedWarningsHeaderBytes)
            {
                kept = i + 1;
            }
            else
            {
                keptStopped = true;
            }

            normalized.Add(warning);
        }

        if (allFit)
        {
            return normalized;
        }

        normalized.RemoveRange(kept, normalized.Count - kept);
        normalized.Add(TruncationSummary(total - kept));
        return normalized;
    }

    private static void WriteWarning(Utf8JsonWriter writer, SnapshotWarning warning)
    {
        writer.WriteStartObject();
        writer.WriteString("path", warning.Path);
        writer.WriteString("code", warning.Code);
        writer.WriteString("message", warning.Message);
        writer.WriteEndObject();
    }

    /// <summary>Cabecera hasta <c>"warnings":[</c> incluido.</summary>
    private static void WriteHeaderStart(Utf8JsonWriter writer, SnapshotHeader header)
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
    }

    private (SnapshotHeader Header, ReadOnlyMemory<byte> Bytes) BuildHeader(SnapshotCompletion completion)
    {
        ArgumentNullException.ThrowIfNull(completion.Stats);
        ArgumentNullException.ThrowIfNull(completion.Warnings);
        if (completion.OmittedWarnings < 0)
        {
            throw new ArgumentException("El número de avisos omitidos no puede ser negativo.", nameof(completion));
        }

        var stats = completion.Stats with { Files = files, Bytes = bytes };
        if (stats.NewFiles < 0 || stats.ChangedFiles < 0 || stats.NewObjects < 0 || stats.NewBytes < 0 || stats.Skipped < 0)
        {
            throw new ArgumentException("Las estadísticas no pueden ser negativas.", nameof(completion));
        }

        var header = new SnapshotHeader
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
            Warnings = [],
        };

        // La cabecera se compone en memoria (como mucho 16 MiB) para conocer su tamaño exacto al elegir los avisos.
        var buffer = new ArrayBufferWriter<byte>(64 * 1024);
        using var writer = new Utf8JsonWriter(buffer, JsonFormat.WriterOptions(indented: false));
        WriteHeaderStart(writer, header);
        writer.Flush();

        var warnings = SelectWarnings(completion.Warnings, buffer.WrittenCount + HeaderSuffixBytes, completion.OmittedWarnings);
        foreach (var warning in warnings)
        {
            WriteWarning(writer, warning);
        }

        writer.WriteEndArray();
        writer.WriteStartArray("entries");
        writer.Flush();

        // Defensa: con los límites de inicio y la selección de avisos no puede pasar, y el lector lo rechazaría.
        if (buffer.WrittenCount > SnapshotLimits.MaxHeaderBytes)
        {
            throw new RepositoryException(RepositoryErrorKind.SnapshotTooLarge, $"La cabecera del snapshot supera {SnapshotLimits.MaxHeaderBytes} bytes; no se publica el snapshot.");
        }

        return (header with { Warnings = warnings }, buffer.WrittenMemory);
    }

    private async Task WriteManifestAsync(Stream output, ReadOnlyMemory<byte> header, CancellationToken cancellationToken)
    {
        await output.WriteAsync(header, cancellationToken).ConfigureAwait(false);

        // Las entradas ya están serializadas en el temporal: se copian tal cual dentro de la lista.
        var entriesStream = await storage.OpenReadAsync(stagingPath, cancellationToken).ConfigureAwait(false);
        await using (entriesStream.ConfigureAwait(false))
        {
            await entriesStream.CopyToAsync(output, CopyBufferSize, cancellationToken).ConfigureAwait(false);
        }

        await output.WriteAsync(ManifestEnd.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    private string ManifestTooLargeMessage() =>
        $"El manifiesto del snapshot superaría el tamaño máximo de {MaxManifestBytes} bytes; no se publica el snapshot.";

    /// <summary>Deja el escritor fallido (no publicará nada) y devuelve la excepción que se debe lanzar.</summary>
    private RepositoryException Fail(string message)
    {
        state = State.Failed;
        return new RepositoryException(RepositoryErrorKind.SnapshotTooLarge, message);
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
