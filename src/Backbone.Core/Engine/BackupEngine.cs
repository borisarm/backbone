using System.Text;
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
/// los del snapshot padre no se abre; su entrada reutiliza el hash del padre. El padre (ADR-17, §6.3 "Snapshot padre") es
/// el último snapshot del repositorio con el mismo <see cref="BackupRequest.JobId"/> cuya fecha (la de su identificador y
/// su <c>startedUtc</c>) no pasa del inicio de la copia más la tolerancia de reloj
/// (<see cref="RepositoryLockPolicy.StaleAfter"/>); los de fecha futura se descartan y se devuelven en
/// <see cref="BackupResult.IgnoredParents"/> para que el servicio los registre. Una cabecera ilegible, o un padre cuyo
/// manifiesto es corrupto, hacen fallar la copia con <see cref="RepositoryErrorKind.Corrupt"/>; un error de E/S al leerlos
/// se propaga como tal.
/// </para>
/// <para>
/// <b>Exclusiones obligatorias</b> (ADR-16, §8.3): las rutas de <see cref="BackupSource.MandatoryExclusions"/> no se
/// recorren, no generan avisos y no aparecen en el snapshot (tampoco lo que hubiera debajo en el padre).
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
/// <b>Memoria</b> (RNF-05, §8.4 "Memoria del motor"): el manifiesto padre se lee en streaming a un índice compacto
/// (<see cref="ParentIndex"/>) y las entradas nuevas van directas al escritor del snapshot. Lo demás se acota por bytes:
/// los avisos retenidos (como mucho <see cref="SnapshotLimits.MaxWarnings"/> y
/// <see cref="SnapshotLimits.TruncatedWarningsHeaderBytes"/> en UTF-8; los demás solo se cuentan) y los listados de las
/// carpetas abiertas (<see cref="DefaultListingBudgetBytes"/>; una carpeta que no cabe se trata como ilegible con aviso
/// <see cref="RunIssueCode.ReadError"/> y conserva lo que tenía en el padre).
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

    /// <summary>
    /// Presupuesto de los listados de carpetas abiertas a la vez (§8.4 "Memoria del motor"): 128 MiB, estimados con
    /// 2 bytes por carácter de nombre y ruta más 64 bytes por elemento.
    /// </summary>
    public const long DefaultListingBudgetBytes = 128L * 1024 * 1024;

    /// <summary>Máximo de snapshots descartados por fecha futura que se devuelven en <see cref="BackupResult.IgnoredParents"/>.</summary>
    public const int MaxReportedIgnoredParents = 100;

    /// <summary>Bytes estimados por elemento de un listado, aparte de sus textos (§8.4).</summary>
    private const long ListingOverheadBytes = 64;

    /// <summary>Máximo de entradas del snapshot (las pruebas lo reducen); por defecto el de §6.3.</summary>
    internal long MaxSnapshotEntries { get; set; } = RepositoryLayout.MaxSnapshotEntries;

    /// <summary>Presupuesto de los listados abiertos (las pruebas lo reducen); por defecto <see cref="DefaultListingBudgetBytes"/>.</summary>
    internal long ListingBudgetBytes { get; set; } = DefaultListingBudgetBytes;

    /// <summary>Máximo de avisos retenidos en memoria (las pruebas lo reducen); por defecto <see cref="SnapshotLimits.MaxWarnings"/>.</summary>
    internal int MaxRetainedWarnings { get; set; } = SnapshotLimits.MaxWarnings;

    /// <summary>
    /// Máximo de la suma de los textos de los avisos retenidos en UTF-8 (las pruebas lo reducen); por defecto
    /// <see cref="SnapshotLimits.TruncatedWarningsHeaderBytes"/>.
    /// </summary>
    internal long MaxRetainedWarningBytes { get; set; } = SnapshotLimits.TruncatedWarningsHeaderBytes;

    /// <summary>Máximo de la estimación de los listados abiertos alcanzado en las ejecuciones de esta instancia (pruebas).</summary>
    internal long PeakListingBytes { get; private set; }

    /// <summary>
    /// Bytes que se cuentan por un elemento de un listado (§8.4): 2 por carácter de <see cref="SourceEntry.Name"/> y
    /// <see cref="SourceEntry.FullPath"/> más 64.
    /// </summary>
    internal static long EstimateListingBytes(SourceEntry entry) =>
        (2L * ((long)entry.Name.Length + entry.FullPath.Length)) + ListingOverheadBytes;

    /// <summary>Bytes que se cuentan por una carpeta de la pila (sus rutas), con la misma estimación.</summary>
    private static long EstimateFrameBytes(DirectoryFrame frame) =>
        (2L * ((long)frame.FullPath.Length + frame.SnapshotPath.Length)) + ListingOverheadBytes;

    /// <summary>Hace una copia y publica su snapshot.</summary>
    /// <param name="repository">Repositorio abierto.</param>
    /// <param name="repositoryLock">Bloqueo vigente sobre <paramref name="repository"/> (normalmente compartido).</param>
    /// <param name="source">Acceso a los orígenes.</param>
    /// <param name="request">Datos del trabajo y orígenes.</param>
    /// <param name="progress">Progreso, después de cada archivo; opcional.</param>
    /// <param name="cancellationToken">Se comprueba entre archivos y en cada bloque de 1 MiB dentro de un archivo.</param>
    /// <returns>El snapshot publicado y los avisos.</returns>
    /// <exception cref="ArgumentException">
    /// Datos del trabajo no válidos (§6.3 "Límites") o exclusión obligatoria que no es una ruta relativa válida.
    /// </exception>
    /// <exception cref="RepositoryException">
    /// Bloqueo perdido, cabecera de snapshot o snapshot padre ilegibles o corruptos, objeto existente que no coincide en
    /// tamaño (<see cref="RepositoryErrorKind.Corrupt"/>), conflicto de identificador o snapshot demasiado grande
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
        var exclusions = MandatoryExclusionSet.Create(request.Sources);

        repositoryLock.ThrowIfLost();
        var startedUtc = request.StartedUtc is { } started ? ToUtc(started) : timeProvider.GetUtcNow().UtcDateTime;

        // Con el bloqueo ya adquirido: ni la retención ni la limpieza pueden borrar el padre ni sus objetos (§6.4.4–5).
        var search = await FindParentAsync(repository, request.JobId, startedUtc, cancellationToken).ConfigureAwait(false);
        var parentId = search.ParentId;
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
            var run = new Run(this, repository, repositoryLock, source, writer, parent, exclusions, progress);
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
                IgnoredParents = search.Ignored,
                IgnoredParentCount = search.IgnoredCount,
            };
        }
    }

    /// <summary>
    /// Errores de un elemento de origen que permiten omitirlo y seguir (RF-25). La cancelación no lo es.
    /// </summary>
    internal static bool IsSourceError(Exception exception) =>
        exception is IOException or UnauthorizedAccessException;

    /// <summary>
    /// El elemento es (o se ha convertido en) un punto de reanálisis (§8.2): se omite como los que devuelve la enumeración
    /// (RF-06), sin conservar su versión anterior.
    /// </summary>
    internal static bool IsReparsePointError(Exception? exception) =>
        exception is SourceAccessException { Code: RunIssueCode.ReparsePointSkipped };

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

    /// <summary>
    /// Snapshot padre (ADR-17, §6.3 "Snapshot padre"): el más reciente del mismo trabajo cuya fecha no pasa de
    /// <paramref name="startedUtc"/> más <see cref="RepositoryLockPolicy.StaleAfter"/>. Los de fecha posterior se descartan
    /// y se sigue hacia atrás; si ya la fecha del identificador la supera, sin leer su cabecera.
    /// </summary>
    /// <exception cref="RepositoryException">
    /// Una cabecera que hay que leer no es válida (<see cref="RepositoryErrorKind.Corrupt"/>) o es de un formato posterior.
    /// No se salta: no se puede descartar que sea del trabajo.
    /// </exception>
    /// <exception cref="IOException">Error del almacenamiento al leer una cabecera (no se convierte en corrupción).</exception>
    private static async Task<ParentSearch> FindParentAsync(BackupRepository repository, string jobId, DateTime startedUtc, CancellationToken cancellationToken)
    {
        var limitUtc = startedUtc + RepositoryLockPolicy.StaleAfter;
        var ignored = new List<IgnoredParentCandidate>();
        long ignoredCount = 0;

        void Ignore(string id, DateTime dateUtc, bool headerRead)
        {
            ignoredCount++;
            if (ignored.Count < MaxReportedIgnoredParents)
            {
                ignored.Add(new IgnoredParentCandidate(id, dateUtc, limitUtc, headerRead));
            }
        }

        var ids = await repository.ListSnapshotIdsAsync(cancellationToken).ConfigureAwait(false);
        for (var i = ids.Count - 1; i >= 0; i--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = ids[i];
            if (SnapshotId.TryGetTimestamp(id, out var idDateUtc) && idDateUtc > limitUtc)
            {
                Ignore(id, idDateUtc, headerRead: false);
                continue;
            }

            var header = await repository.ReadSnapshotHeaderAsync(id, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(header.JobId, jobId, StringComparison.Ordinal))
            {
                continue;
            }

            var headerStartedUtc = ToUtc(header.StartedUtc);
            if (headerStartedUtc > limitUtc)
            {
                Ignore(id, headerStartedUtc, headerRead: true);
                continue;
            }

            return new ParentSearch(id, ignored, ignoredCount);
        }

        return new ParentSearch(null, ignored, ignoredCount);
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
            ArgumentNullException.ThrowIfNull(source.MandatoryExclusions);
        }

        // El resto (alias, rutas, textos de cabecera) lo valida el escritor del snapshot al empezar.
    }

    /// <summary>Resultado de la búsqueda del snapshot padre.</summary>
    private sealed record ParentSearch(string? ParentId, IReadOnlyList<IgnoredParentCandidate> Ignored, long IgnoredCount);

    /// <summary>Estado de una ejecución.</summary>
    private sealed class Run(
        BackupEngine engine,
        BackupRepository repository,
        RepositoryLock repositoryLock,
        ISourceFileSystem source,
        SnapshotWriter writer,
        ParentIndex parent,
        MandatoryExclusionSet exclusions,
        IProgress<BackupProgress>? progress)
    {
        private readonly List<SnapshotWarning> warnings = [];
        private readonly int maxRetainedWarnings = engine.MaxRetainedWarnings;
        private readonly long maxRetainedWarningBytes = engine.MaxRetainedWarningBytes;
        private readonly long listingBudgetBytes = engine.ListingBudgetBytes;

        /// <summary>Suma de los textos de los avisos retenidos, en UTF-8.</summary>
        private long warningBytes;

        /// <summary>Un aviso ya no cupo: los siguientes solo se cuentan (para no desordenarlos respecto al resumen).</summary>
        private bool warningsFull;

        /// <summary>Estimación de lo que ocupan los listados y las carpetas de la pila (§8.4).</summary>
        private long listingBytes;

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

        /// <summary>
        /// Registra un aviso (§8.4 "Memoria del motor"). Se retiene ya normalizado (textos recortados a sus límites de §6.3)
        /// mientras haya menos de <see cref="MaxRetainedWarnings"/> y la suma de sus textos en UTF-8 no pase de
        /// <see cref="MaxRetainedWarningBytes"/>; desde el primero que no cabe, los demás solo se cuentan. El escritor
        /// serializa con escapes, que solo agrandan el texto, así que nunca habría guardado uno que aquí se descarta.
        /// </summary>
        public void Warn(SnapshotWarning warning)
        {
            ArgumentNullException.ThrowIfNull(warning);
            WarningCount++;
            if (warningsFull)
            {
                return;
            }

            if (warnings.Count >= maxRetainedWarnings)
            {
                warningsFull = true;
                return;
            }

            var normalized = SnapshotWriter.NormalizeWarning(warning);
            var bytes = (long)Encoding.UTF8.GetByteCount(normalized.Path)
                + Encoding.UTF8.GetByteCount(normalized.Code)
                + Encoding.UTF8.GetByteCount(normalized.Message);
            if (warningBytes + bytes > maxRetainedWarningBytes)
            {
                warningsFull = true;
                return;
            }

            warnings.Add(normalized);
            warningBytes += bytes;
        }

        /// <summary>
        /// Recorre un origen en profundidad, con una pila explícita (sin recursión: la profundidad solo la limita la
        /// longitud de las rutas). Los hijos de cada carpeta se tratan en orden ordinal, para que el manifiesto sea
        /// determinista. Una carpeta sin ninguna entrada debajo se guarda como carpeta vacía (<c>"d": true</c>).
        /// </summary>
        public async Task CopySourceAsync(BackupSource backupSource, CancellationToken cancellationToken)
        {
            var stack = new Stack<DirectoryFrame>();
            stack.Push(NewFrame(backupSource.ReadPath, backupSource.Alias));
            while (stack.TryPeek(out var frame))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (frame.Children is null)
                {
                    frame.Children = await ListAsync(frame, cancellationToken).ConfigureAwait(false);
                    if (frame.Children is null)
                    {
                        // Ilegible (o demasiado grande, §8.4): lo que tuviera en el padre se conserva después; no se guarda
                        // como carpeta vacía. Su listado no ha llegado a contar. Si resultó ser un punto de reanálisis, se
                        // omite como los que devuelve la enumeración (RF-06), sin conservar nada.
                        stack.Pop();
                        if (frame.KeptFromParent && stack.TryPeek(out var owner))
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
                    if (frame.HasExclusions && exclusions.IsExcluded(childPath))
                    {
                        // ADR-16: exclusión obligatoria. Ni se recorre, ni se avisa, ni cuenta como omitido.
                    }
                    else if (child.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        // RF-06: no se siguen (el tratamiento completo, con exclusiones, es de HU-08). Si un elemento se
                        // convierte en enlace después de enumerarlo, la implementación lo detecta sobre el handle y lanza
                        // SourceAccessException(ReparsePointSkipped) (§8.2): se omite con ese aviso.
                        Skip(childPath, RunIssueCode.ReparsePointSkipped, "Punto de reanálisis (junction o enlace simbólico) no seguido.");
                    }
                    else if (child.IsDirectory)
                    {
                        if (IsValidName(child.Name) && EntryPath.IsValidDirectoryPath(childPath + "/"))
                        {
                            stack.Push(NewFrame(child.FullPath, childPath));
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
                listingBytes -= frame.ListingBytes;
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
        /// segunda lectura en streaming del padre, solo si hubo alguna. Lo que cae bajo una exclusión obligatoria no se
        /// conserva (ADR-16): un snapshot anterior a la exclusión no la puede reintroducir.
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
                    if (IsUnder(entry.Path, lookup) && !exclusions.CoversEntry(entry.Path))
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

        private DirectoryFrame NewFrame(string fullPath, string snapshotPath) =>
            new(fullPath, snapshotPath, exclusions.HasExclusionsIn(snapshotPath));

        /// <summary>
        /// Hijos de una carpeta, ordenados; <see langword="null"/> si no se puede leer o si su listado no cabe en el
        /// presupuesto de §8.4 (en los dos casos, con aviso y conservando después lo que tenía en el padre).
        /// </summary>
        private async Task<List<SourceEntry>?> ListAsync(DirectoryFrame frame, CancellationToken cancellationToken)
        {
            var children = new List<SourceEntry>();
            var cost = EstimateFrameBytes(frame);
            var tooLarge = listingBytes + cost > listingBudgetBytes;
            try
            {
                if (!tooLarge)
                {
                    await foreach (var child in source.EnumerateAsync(frame.FullPath, cancellationToken).ConfigureAwait(false))
                    {
                        ArgumentNullException.ThrowIfNull(child);
                        var childCost = EstimateListingBytes(child);
                        if (listingBytes + cost + childCost > listingBudgetBytes)
                        {
                            // Se deja de enumerar (el enumerador se libera al salir) y se descarta la lista.
                            tooLarge = true;
                            break;
                        }

                        cost += childCost;
                        children.Add(child);
                    }
                }
            }
            catch (Exception ex) when (IsReparsePointError(ex))
            {
                // §8.2: la carpeta se ha convertido en un punto de reanálisis después de enumerar a su padre; no se sigue.
                Skip(frame.SnapshotPath, RunIssueCode.ReparsePointSkipped, "La carpeta es un punto de reanálisis (junction o enlace simbólico); no se sigue.");
                return null;
            }
            catch (Exception ex) when (IsSourceError(ex))
            {
                MarkUnreadable(
                    frame,
                    IssueCode(ex),
                    $"No se pudo leer la carpeta; se conserva lo que tenía en el snapshot anterior: {Untrusted.Describe(ex.Message)}");
                return null;
            }

            if (tooLarge)
            {
                MarkUnreadable(
                    frame,
                    RunIssueCode.ReadError,
                    "La carpeta tiene demasiados elementos (o rutas demasiado largas) para recorrerla con la memoria reservada a la copia; se conserva lo que tenía en el snapshot anterior.");
                return null;
            }

            listingBytes += cost;
            frame.ListingBytes = cost;
            engine.PeakListingBytes = Math.Max(engine.PeakListingBytes, listingBytes);

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

        /// <summary>RF-25: carpeta que no se recorre; se conserva después lo que tenía debajo en el padre.</summary>
        private void MarkUnreadable(DirectoryFrame frame, RunIssueCode code, string message)
        {
            frame.KeptFromParent = true;
            unreadableDirectories.Add(frame.SnapshotPath + "/");
            skipped++;
            Warn(new SnapshotWarning(frame.SnapshotPath, code.ToString(), message));
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
            if (written is null && IsReparsePointError(sourceError))
            {
                // §8.2: se convirtió en un punto de reanálisis después de enumerarlo; se omite como en la enumeración (RF-06).
                Skip(path, RunIssueCode.ReparsePointSkipped, "El archivo es un punto de reanálisis (junction o enlace simbólico); no se sigue.");
                Report(file.Length, path);
                return false;
            }

            if (written is null)
            {
                return await SkipUnreadableAsync(path, sourceError ?? new SourceAccessException(), inParent ? previous : null, file.Length, cancellationToken).ConfigureAwait(false);
            }

            if (written.Stored)
            {
                newObjects++;
                newBytes += written.Length;
            }

            var check = await ChangedDuringCopyAsync(file, modifiedUtc, written.Length, cancellationToken).ConfigureAwait(false);
            if (check == AfterCopy.BecameReparsePoint)
            {
                // Se leyó del handle abierto antes del cambio, pero ahora es un enlace: se omite (RF-06, §8.2). El objeto
                // subido queda sin referencia hasta la limpieza.
                Skip(path, RunIssueCode.ReparsePointSkipped, "El archivo se ha convertido en un punto de reanálisis (junction o enlace simbólico) durante la copia; no se guarda.");
                Report(written.Length, path);
                return false;
            }

            if (check == AfterCopy.Changed)
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
        private async Task<AfterCopy> ChangedDuringCopyAsync(SourceEntry file, DateTime modifiedUtc, long length, CancellationToken cancellationToken)
        {
            SourceEntry? after;
            try
            {
                after = await source.GetFileInfoAsync(file.FullPath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsReparsePointError(ex))
            {
                return AfterCopy.BecameReparsePoint;
            }
            catch (Exception ex) when (IsSourceError(ex))
            {
                return AfterCopy.Changed;
            }

            if (after is not null && after.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return AfterCopy.BecameReparsePoint;
            }

            return length != file.Length || after is null || after.Length != length || ToUtc(after.LastWriteTimeUtc) != modifiedUtc
                ? AfterCopy.Changed
                : AfterCopy.Unchanged;
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

    /// <summary>Resultado de volver a mirar un archivo después de copiarlo (§6.4.2).</summary>
    private enum AfterCopy
    {
        Unchanged,
        Changed,
        BecameReparsePoint,
    }

    /// <summary>Carpeta en recorrido.</summary>
    private sealed class DirectoryFrame(string fullPath, string snapshotPath, bool hasExclusions)
    {
        /// <summary>Si contiene directamente alguna exclusión obligatoria (ADR-16).</summary>
        public bool HasExclusions { get; } = hasExclusions;

        /// <summary>Bytes estimados de esta carpeta y su listado que cuentan en el presupuesto (§8.4).</summary>
        public long ListingBytes { get; set; }

        /// <summary>No se pudo recorrer y lo que tenía debajo se conserva del snapshot padre (RF-25).</summary>
        public bool KeptFromParent { get; set; }

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
