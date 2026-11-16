using Backbone.Contracts;
using Backbone.Core.Repository;
using Backbone.Core.Repository.Locking;
using Backbone.Core.Repository.Snapshots;
using SnapshotStatus = Backbone.Core.Repository.Snapshots.SnapshotStatus;

namespace Backbone.Core.Engine;

/// <summary>
/// Copia incremental con versiones (HU-13, RF-23, RF-25, §6.4): recorre los orígenes, sube como objetos los archivos nuevos
/// o cambiados y publica un snapshot completo que reutiliza los hashes del snapshot anterior para lo que no ha cambiado.
/// </summary>
/// <remarks>
/// <para>
/// <b>Orden y atomicidad</b>: el identificador del snapshot se elige al empezar; cada objeto se escribe en <c>tmp/</c>
/// calculando su SHA-256 sobre los bytes que se escriben y se renombra (§6.4.1–2); el manifiesto se publica al final, cuando
/// todos sus objetos existen, renovando antes el bloqueo (§6.4.3). Si algo falla o se cancela, no se publica nada y el
/// snapshot anterior sigue siendo el último restaurable (RF-21, RNF-01).
/// </para>
/// <para>
/// <b>Detección incremental</b> (RF-23): un archivo cuyo tamaño, fecha de modificación y atributos guardados coinciden con
/// los del snapshot padre no se abre; su entrada reutiliza el hash del padre. El padre es el último snapshot del
/// repositorio con el mismo <see cref="BackupRequest.JobId"/>.
/// </para>
/// <para>
/// <b>Archivos ilegibles</b> (RF-25): un archivo o carpeta que no se puede leer se omite con un aviso y la ejecución termina
/// con avisos; si estaba en el snapshot padre, se conserva su versión anterior (en una carpeta ilegible, todo lo que tenía
/// debajo en el padre).
/// </para>
/// <para>
/// <b>Archivo que cambia durante la copia</b> (§6.4.2): se guarda el hash real de los bytes leídos, con su tamaño, y se
/// registra el aviso <see cref="RunIssueCode.FileChangedDuringCopy"/>. La entrada lleva la fecha de modificación vista
/// <i>antes</i> de copiar: así la siguiente copia ve una fecha distinta y vuelve a leer el archivo, en vez de dar por buena
/// una mezcla de dos versiones.
/// </para>
/// <para>
/// <b>Errores fatales</b>: los del repositorio (<see cref="IOException"/> del almacenamiento, <see cref="RepositoryException"/>,
/// incluido <see cref="RepositoryErrorKind.SnapshotTooLarge"/> y <see cref="RepositoryErrorKind.LockLost"/>) y la
/// cancelación se propagan; un snapshot padre corrupto también (el motor no adivina qué versión conservar).
/// </para>
/// <para>
/// <b>Memoria</b> (RNF-05): el manifiesto padre se lee en streaming a un índice compacto (<see cref="ParentIndex"/>), las
/// entradas nuevas van directas al escritor del snapshot, en memoria solo hay los hijos de las carpetas que se están
/// recorriendo y como mucho <see cref="SnapshotLimits.MaxWarnings"/> avisos.
/// </para>
/// </remarks>
public sealed class BackupEngine
{
    private readonly TimeProvider timeProvider;

    /// <summary>Crea el motor.</summary>
    public BackupEngine(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        this.timeProvider = timeProvider;
    }

    /// <summary>Máximo de entradas del snapshot (las pruebas lo reducen); por defecto el de §6.3.</summary>
    internal long MaxSnapshotEntries { get; set; } = RepositoryLayout.MaxSnapshotEntries;

    /// <summary>Hace una copia y publica su snapshot.</summary>
    /// <param name="repository">Repositorio abierto.</param>
    /// <param name="repositoryLock">Bloqueo vigente sobre <paramref name="repository"/> (normalmente compartido).</param>
    /// <param name="source">Acceso a los orígenes.</param>
    /// <param name="request">Datos del trabajo y orígenes.</param>
    /// <param name="progress">Progreso, después de cada archivo; opcional.</param>
    /// <param name="cancellationToken">Se comprueba entre archivos y en cada bloque de 1 MiB dentro de un archivo.</param>
    /// <returns>El snapshot publicado y los avisos.</returns>
    /// <exception cref="ArgumentException">Datos del trabajo no válidos (§6.3 "Límites").</exception>
    /// <exception cref="RepositoryException">
    /// Bloqueo perdido, snapshot padre ilegible o corrupto, conflicto de identificador o snapshot demasiado grande
    /// (<see cref="RepositoryErrorKind.SnapshotTooLarge"/>); no se publica nada.
    /// </exception>
    /// <exception cref="IOException">Error del almacenamiento del repositorio; no se publica nada.</exception>
    /// <exception cref="OperationCanceledException">Cancelada; no se publica nada.</exception>
    public async Task<BackupResult> RunAsync(
        BackupRepository repository,
        RepositoryLock repositoryLock,
        ISourceFileSystem source,
        BackupRequest request,
        IProgress<BackupProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(repositoryLock);
        ArgumentNullException.ThrowIfNull(source);
        Validate(request);

        repositoryLock.ThrowIfLost();
        var startedUtc = request.StartedUtc is { } started ? ToUtc(started) : timeProvider.GetUtcNow().UtcDateTime;

        // Con el bloqueo ya adquirido: ni la retención ni la limpieza pueden borrar el padre ni sus objetos (§6.4.4–5).
        var parentId = await FindParentAsync(repository, request.JobId, cancellationToken).ConfigureAwait(false);
        var parent = parentId is null
            ? ParentIndex.Empty
            : await ParentIndex.LoadAsync(repository, parentId, cancellationToken).ConfigureAwait(false);

        var start = new SnapshotStart
        {
            JobId = request.JobId,
            JobName = request.JobName,
            Host = request.Host,
            StartedUtc = startedUtc,
            ParentId = parentId,
            UsedVss = request.UsedVss,
            Sources = [.. request.Sources.Select(s => new SnapshotSource(s.Alias, s.Path))],
        };

        var writer = await repository.CreateSnapshotAsync(start, repositoryLock, cancellationToken).ConfigureAwait(false);
        await using (writer.ConfigureAwait(false))
        {
            writer.MaxEntries = MaxSnapshotEntries;
            var run = new Run(repository, repositoryLock, source, writer, parent, progress);
            foreach (var warning in request.AdditionalWarnings)
            {
                run.Warn(warning);
            }

            foreach (var backupSource in request.Sources)
            {
                await run.CopySourceAsync(backupSource, cancellationToken).ConfigureAwait(false);
            }

            if (parentId is not null)
            {
                await run.PreserveUnreadableDirectoriesAsync(parentId, cancellationToken).ConfigureAwait(false);
            }

            var status = run.WarningCount > 0 ? SnapshotStatus.CompletedWithWarnings : SnapshotStatus.Completed;
            var header = await writer.CompleteAsync(
                new SnapshotCompletion
                {
                    Status = status,
                    Stats = run.Stats,
                    Warnings = run.Warnings,
                    OmittedWarnings = run.WarningCount - run.Warnings.Count,
                },
                cancellationToken).ConfigureAwait(false);

            return new BackupResult
            {
                Snapshot = header,
                State = status == SnapshotStatus.CompletedWithWarnings ? RunState.CompletedWithWarnings : RunState.Completed,
                Warnings = run.Warnings,
                WarningCount = run.WarningCount,
            };
        }
    }

    /// <summary>
    /// Errores de un elemento de origen que permiten omitirlo y seguir (RF-25). La cancelación no lo es.
    /// </summary>
    internal static bool IsSourceError(Exception exception) =>
        exception is IOException or UnauthorizedAccessException;

    /// <summary>Motivo del aviso de un elemento de origen ilegible.</summary>
    internal static RunIssueCode IssueCode(Exception exception) => exception switch
    {
        SourceAccessException access => access.Code,
        UnauthorizedAccessException => RunIssueCode.AccessDenied,
        PathTooLongException => RunIssueCode.PathInvalid,
        _ => RunIssueCode.ReadError,
    };

    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    /// <summary>Último snapshot del repositorio del mismo trabajo, o <see langword="null"/> si no hay ninguno.</summary>
    private static async Task<string?> FindParentAsync(BackupRepository repository, string jobId, CancellationToken cancellationToken)
    {
        var ids = await repository.ListSnapshotIdsAsync(cancellationToken).ConfigureAwait(false);
        for (var i = ids.Count - 1; i >= 0; i--)
        {
            var header = await repository.ReadSnapshotHeaderAsync(ids[i], cancellationToken).ConfigureAwait(false);
            if (string.Equals(header.JobId, jobId, StringComparison.Ordinal))
            {
                return ids[i];
            }
        }

        return null;
    }

    private static void Validate(BackupRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Sources);
        ArgumentNullException.ThrowIfNull(request.AdditionalWarnings);
        foreach (var source in request.Sources)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentException.ThrowIfNullOrEmpty(source.ReadPath);
        }

        // El resto (alias, rutas, textos de cabecera) lo valida el escritor del snapshot al empezar.
    }

    /// <summary>Estado de una ejecución.</summary>
    private sealed class Run(
        BackupRepository repository,
        RepositoryLock repositoryLock,
        ISourceFileSystem source,
        SnapshotWriter writer,
        ParentIndex parent,
        IProgress<BackupProgress>? progress)
    {
        private readonly List<SnapshotWarning> warnings = [];

        /// <summary>Prefijos (<c>alias/…/</c>) de las carpetas que no se han podido leer.</summary>
        private readonly HashSet<string> unreadableDirectories = new(StringComparer.Ordinal);

        private long newFiles;
        private long changedFiles;
        private long newObjects;
        private long newBytes;
        private long skipped;
        private long filesProcessed;
        private long bytesProcessed;

        public List<SnapshotWarning> Warnings => warnings;

        public long WarningCount { get; private set; }

        public SnapshotStats Stats => new()
        {
            NewFiles = newFiles,
            ChangedFiles = changedFiles,
            NewObjects = newObjects,
            NewBytes = newBytes,
            Skipped = skipped,
        };

        /// <summary>Registra un aviso; en memoria se guardan como mucho <see cref="SnapshotLimits.MaxWarnings"/>.</summary>
        public void Warn(SnapshotWarning warning)
        {
            ArgumentNullException.ThrowIfNull(warning);
            WarningCount++;
            if (warnings.Count < SnapshotLimits.MaxWarnings)
            {
                warnings.Add(SnapshotWriter.NormalizeWarning(warning));
            }
        }

        /// <summary>
        /// Recorre un origen en profundidad, con una pila explícita (sin recursión: la profundidad solo la limita la
        /// longitud de las rutas). Los hijos de cada carpeta se tratan en orden ordinal, para que el manifiesto sea
        /// determinista. Una carpeta sin ninguna entrada debajo se guarda como carpeta vacía (<c>"d": true</c>).
        /// </summary>
        public async Task CopySourceAsync(BackupSource backupSource, CancellationToken cancellationToken)
        {
            var stack = new Stack<DirectoryFrame>();
            stack.Push(new DirectoryFrame(backupSource.ReadPath, backupSource.Alias));
            while (stack.TryPeek(out var frame))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (frame.Children is null)
                {
                    frame.Children = await ListAsync(frame, cancellationToken).ConfigureAwait(false);
                    if (frame.Children is null)
                    {
                        // Ilegible: lo que tuviera en el padre se conserva después; no se guarda como carpeta vacía.
                        stack.Pop();
                        if (stack.TryPeek(out var owner))
                        {
                            owner.Added++;
                        }

                        continue;
                    }
                }

                if (frame.Next < frame.Children.Count)
                {
                    var child = frame.Children[frame.Next++];
                    var childPath = frame.SnapshotPath + "/" + child.Name;
                    if (child.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        // RF-06: no se siguen (el tratamiento completo, con exclusiones, es de HU-08).
                        Skip(childPath, RunIssueCode.ReparsePointSkipped, "Punto de reanálisis (junction o enlace simbólico) no seguido.");
                    }
                    else if (child.IsDirectory)
                    {
                        if (IsValidName(child.Name) && EntryPath.IsValidDirectoryPath(childPath + "/"))
                        {
                            stack.Push(new DirectoryFrame(child.FullPath, childPath));
                        }
                        else
                        {
                            Skip(childPath, RunIssueCode.PathInvalid, "El nombre de la carpeta no es válido en un snapshot (§7.5); no se copia.");
                        }
                    }
                    else if (!IsValidName(child.Name) || !EntryPath.IsValidFilePath(childPath))
                    {
                        Skip(childPath, RunIssueCode.PathInvalid, "El nombre del archivo no es válido en un snapshot (§7.5); no se copia.");
                        Report(child.Length, childPath);
                    }
                    else if (await CopyFileAsync(child, childPath, cancellationToken).ConfigureAwait(false))
                    {
                        frame.Added++;
                    }

                    continue;
                }

                stack.Pop();
                if (frame.Added == 0)
                {
                    await writer.AddAsync(SnapshotEntry.Directory(frame.SnapshotPath + "/"), cancellationToken).ConfigureAwait(false);
                }

                if (stack.TryPeek(out var container))
                {
                    container.Added++;
                }
            }
        }

        /// <summary>
        /// Conserva del snapshot padre todo lo que había debajo de las carpetas que no se han podido leer (RF-25). Es una
        /// segunda lectura en streaming del padre, solo si hubo alguna.
        /// </summary>
        public async Task PreserveUnreadableDirectoriesAsync(string parentId, CancellationToken cancellationToken)
        {
            if (unreadableDirectories.Count == 0)
            {
                return;
            }

            var lookup = unreadableDirectories.GetAlternateLookup<ReadOnlySpan<char>>();
            var reader = await repository.OpenSnapshotAsync(parentId, cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                await foreach (var entry in reader.ReadEntriesAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (IsUnder(entry.Path, lookup))
                    {
                        await writer.AddAsync(entry, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }

        /// <summary>
        /// El nombre es un único segmento válido. <see cref="EntryPath"/> valida la ruta compuesta, pero un nombre con
        /// <c>/</c> (que ningún sistema de archivos de Windows devuelve, pero sí una implementación defectuosa de
        /// <see cref="ISourceFileSystem"/>) daría una ruta válida con segmentos de más, que podría coincidir con otra.
        /// </summary>
        private static bool IsValidName(string name) => SnapshotPaths.IsValidSegment(name);

        private static bool IsUnder(string path, HashSet<string>.AlternateLookup<ReadOnlySpan<char>> prefixes)
        {
            for (var slash = path.IndexOf('/', StringComparison.Ordinal); slash >= 0; slash = path.IndexOf('/', slash + 1))
            {
                if (prefixes.Contains(path.AsSpan(0, slash + 1)))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Hijos de una carpeta, ordenados; <see langword="null"/> si no se puede leer (con aviso).</summary>
        private async Task<List<SourceEntry>?> ListAsync(DirectoryFrame frame, CancellationToken cancellationToken)
        {
            var children = new List<SourceEntry>();
            try
            {
                await foreach (var child in source.EnumerateAsync(frame.FullPath, cancellationToken).ConfigureAwait(false))
                {
                    ArgumentNullException.ThrowIfNull(child);
                    children.Add(child);
                }
            }
            catch (Exception ex) when (IsSourceError(ex))
            {
                unreadableDirectories.Add(frame.SnapshotPath + "/");
                skipped++;
                Warn(new SnapshotWarning(
                    frame.SnapshotPath,
                    IssueCode(ex).ToString(),
                    $"No se pudo leer la carpeta; se conserva lo que tenía en el snapshot anterior: {Untrusted.Describe(ex.Message)}"));
                return null;
            }

            children.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));

            // Un nombre repetido no puede venir de un sistema de archivos real; se descarta para no repetir la ruta.
            for (var i = children.Count - 1; i > 0; i--)
            {
                if (string.Equals(children[i].Name, children[i - 1].Name, StringComparison.Ordinal))
                {
                    children.RemoveAt(i);
                }
            }

            return children;
        }

        /// <summary>Copia (o reutiliza) un archivo. Devuelve si ha añadido una entrada al snapshot.</summary>
        private async Task<bool> CopyFileAsync(SourceEntry file, string path, CancellationToken cancellationToken)
        {
            repositoryLock.ThrowIfLost();
            var modifiedUtc = ToUtc(file.LastWriteTimeUtc);
            var attributes = file.Attributes & SnapshotEntry.StoredAttributes;
            var inParent = parent.TryGet(EntryPathSet.Digest(path), out var previous);

            // RF-23: mismos tamaño, fecha y atributos que en el padre → no se lee.
            if (inParent && previous.Matches(file.Length, modifiedUtc, attributes))
            {
                await writer.AddAsync(SnapshotEntry.File(path, previous.Hash, previous.Size, previous.ModifiedUtc, previous.Attributes), cancellationToken).ConfigureAwait(false);
                Report(file.Length, path);
                return true;
            }

            var (written, sourceError) = await WriteObjectAsync(file, cancellationToken).ConfigureAwait(false);
            if (written is null)
            {
                return await SkipUnreadableAsync(path, sourceError ?? new SourceAccessException(), inParent ? previous : null, file.Length, cancellationToken).ConfigureAwait(false);
            }

            if (written.Stored)
            {
                newObjects++;
                newBytes += written.Length;
            }

            if (await ChangedDuringCopyAsync(file, modifiedUtc, written.Length, cancellationToken).ConfigureAwait(false))
            {
                Warn(new SnapshotWarning(
                    path,
                    nameof(RunIssueCode.FileChangedDuringCopy),
                    "El archivo cambió durante la copia; se guarda el contenido leído con su hash real (§6.4.2)."));
            }

            if (!inParent)
            {
                newFiles++;
            }
            else if (previous.Hash != written.Hash)
            {
                changedFiles++;
            }

            // Tamaño real (el del objeto) y fecha vista antes de copiar (ver las notas de la clase).
            await writer.AddAsync(SnapshotEntry.File(path, written.Hash, written.Length, modifiedUtc, attributes), cancellationToken).ConfigureAwait(false);
            Report(written.Length, path);
            return true;
        }

        /// <summary>
        /// Abre el archivo y lo copia como objeto. Devuelve el resultado, o <see langword="null"/> y el error si el origen no se
        /// pudo abrir o leer. Los errores del repositorio (también <see cref="IOException"/>) se propagan: por eso la apertura
        /// y la lectura del origen se distinguen aparte (<see cref="SourceReadStream"/>) y no por el tipo de excepción.
        /// </summary>
        private async Task<(ObjectWriteResult? Result, Exception? SourceError)> WriteObjectAsync(SourceEntry file, CancellationToken cancellationToken)
        {
            Stream stream;
            try
            {
                stream = await source.OpenReadAsync(file.FullPath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsSourceError(ex))
            {
                return (null, ex);
            }

            try
            {
                var result = await repository.WriteObjectAsync(new SourceReadStream(stream), expectedHash: null, repositoryLock, cancellationToken).ConfigureAwait(false);
                return (result, null);
            }
            catch (SourceReadException ex)
            {
                // El temporal del objeto ya lo ha borrado WriteObjectAsync; no queda nada a medias en objects/.
                return (null, ex.Original);
            }
            finally
            {
                try
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (IsSourceError(ex))
                {
                    // Cerrar un archivo de origen no afecta a lo ya escrito.
                }
            }
        }

        /// <summary>
        /// §6.4.2: el archivo cambió durante la copia si se leyó un número de bytes distinto del tamaño visto al recorrer, o si
        /// después de copiarlo su tamaño o su fecha ya no son los de entonces (o no se pueden leer).
        /// </summary>
        private async Task<bool> ChangedDuringCopyAsync(SourceEntry file, DateTime modifiedUtc, long length, CancellationToken cancellationToken)
        {
            if (length != file.Length)
            {
                return true;
            }

            try
            {
                var after = await source.GetFileInfoAsync(file.FullPath, cancellationToken).ConfigureAwait(false);
                return after is null || after.Length != length || ToUtc(after.LastWriteTimeUtc) != modifiedUtc;
            }
            catch (Exception ex) when (IsSourceError(ex))
            {
                return true;
            }
        }

        /// <summary>RF-25: omite un archivo ilegible con aviso y conserva su versión anterior si la había.</summary>
        private async Task<bool> SkipUnreadableAsync(string path, Exception error, ParentFile? previous, long length, CancellationToken cancellationToken)
        {
            var preserved = previous.HasValue;
            Skip(
                path,
                IssueCode(error),
                (preserved ? "No se pudo leer el archivo; se conserva su versión anterior: " : "No se pudo leer el archivo; no se copia: ")
                    + Untrusted.Describe(error.Message));
            Report(length, path);
            if (previous is { } kept)
            {
                await writer.AddAsync(SnapshotEntry.File(path, kept.Hash, kept.Size, kept.ModifiedUtc, kept.Attributes), cancellationToken).ConfigureAwait(false);
            }

            return preserved;
        }

        private void Skip(string path, RunIssueCode code, string message)
        {
            skipped++;
            Warn(new SnapshotWarning(path, code.ToString(), message));
        }

        private void Report(long length, string path)
        {
            filesProcessed++;
            bytesProcessed += Math.Max(length, 0);
            progress?.Report(new BackupProgress(filesProcessed, bytesProcessed, path));
        }
    }

    /// <summary>Carpeta en recorrido.</summary>
    private sealed class DirectoryFrame(string fullPath, string snapshotPath)
    {
        /// <summary>Ruta opaca en <see cref="ISourceFileSystem"/>.</summary>
        public string FullPath { get; } = fullPath;

        /// <summary>Ruta en el snapshot, sin barra final (<c>alias</c> o <c>alias/…</c>).</summary>
        public string SnapshotPath { get; } = snapshotPath;

        /// <summary>Hijos ordenados; <see langword="null"/> hasta enumerarla.</summary>
        public List<SourceEntry>? Children { get; set; }

        /// <summary>Siguiente hijo que tratar.</summary>
        public int Next { get; set; }

        /// <summary>Entradas (o subcarpetas con entradas) añadidas debajo.</summary>
        public long Added { get; set; }
    }
}
