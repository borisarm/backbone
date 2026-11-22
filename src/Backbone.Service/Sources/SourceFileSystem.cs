using System.Runtime.CompilerServices;
using Backbone.Contracts;
using Backbone.Core.Engine;
using Backbone.Service.Os;
using Microsoft.Extensions.Logging;
using static Backbone.Service.Os.NativeMethods;
using R = Backbone.Service.Os.NativeMethods.Resolution;
using S = Backbone.Service.Os.NativeMethods.SourceIo;

namespace Backbone.Service.Sources;

/// <summary>
/// Lectura de los orígenes de una ejecución (<see cref="ISourceFileSystem"/> de Core; HU-08, §8.2 "Lectura de los
/// orígenes", ADR-18, ADR-19, ADR-21). Se crea <b>por ejecución</b> con sus orígenes (<see cref="ISourceFileSystemFactory"/>)
/// y se desecha al terminar.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>
/// <b>Apertura relativa</b> (A-1, §8.2 "Apertura relativa"): nunca se abre un elemento por su ruta completa. La raíz de
/// cada origen se abre desde la raíz de su volumen (o del dispositivo de la instantánea) componente a componente, cada uno
/// relativo al anterior, comprobando que es una carpeta normal o de la lista blanca y que el volumen es un disco local
/// (<c>FileFsDeviceInformation</c>); su handle queda fijo toda la ejecución. Cada elemento se abre relativo al handle de su
/// carpeta padre, que sale de una caché de como mucho 128 handles (<see cref="SourceDirectoryCache"/>); si el padre no
/// está, se abre desde el antepasado abierto más cercano, componente a componente y con las mismas reglas.
/// </description></item>
/// <item><description>
/// Sobre el handle de cada apertura, antes de leer nada: atributos y etiqueta (<c>FileAttributeTagInfo</c>) clasificados
/// con <see cref="SourceAttributes.Classify(FileAttributes, uint)"/>, estado del marcador de nube y, como defensa en
/// profundidad, ruta final (<c>GetFinalPathNameByHandle</c>, <c>VOLUME_NAME_NT</c>) igual a la de la raíz más la ruta
/// relativa, fuera de las exclusiones obligatorias.
/// </description></item>
/// <item><description>
/// La enumeración se hace desde el handle (<c>FileIdBothDirectoryInfo</c>) con la etiqueta de <c>EaSize</c>.
/// </description></item>
/// <item><description>
/// El contenido de lo normal se lee del primer handle; el de la lista blanca, de un segundo handle reabierto relativo al
/// primero sin <c>FILE_OPEN_REPARSE_POINT</c>, tras volver a clasificar el primero (B-1), y comprobado sobre él mismo
/// (§8.2 "Apertura para leer el contenido").
/// </description></item>
/// <item><description>
/// Plazo de 60 s por operación y presupuesto de plazos por origen (<see cref="SourceOperationRunner"/>,
/// <see cref="SourceBudget"/>). Una operación abandonada lanza <see cref="SourceOperationAbandonedException"/>, que el
/// <c>JobRunner</c> traduce a <c>Failed</c>/<c>Internal</c> (<see cref="IsAbandoned"/> lo indica también después).
/// </description></item>
/// </list>
/// </remarks>
internal sealed partial class SourceFileSystem : IRunSourceFileSystem
{
    private readonly SourceRoots _roots;
    private readonly ISourceFileApi _api;
    private readonly SourceOperationRunner _runner;
    private readonly ILogger _logger;
    private readonly SourceDirectoryCache _cache;
    private readonly Dictionary<SourceRoot, RootState> _states = new(ReferenceEqualityComparer.Instance);

    /// <param name="sources">Orígenes de la ejecución (<see cref="BackupSource.ReadPath"/> ya traducida a la instantánea si la hay).</param>
    /// <param name="api">API del sistema.</param>
    /// <param name="time">Reloj.</param>
    /// <param name="options">Plazos y límites.</param>
    /// <param name="logger">Log.</param>
    /// <param name="token">Token de los hilos de lectura (ADR-21); sin él, se lee con el del proceso.</param>
    /// <param name="abandonedThreads">Contador de hilos abandonados de todo el servicio (R12).</param>
    /// <param name="snapshotDevices">Dispositivos de las instantáneas creadas para esta ejecución (B-2).</param>
    /// <param name="unreadableSources">Posiciones en <paramref name="sources"/> de los orígenes sin resolver: su raíz no se abre (M-B).</param>
    /// <exception cref="ArgumentException">Un origen o una exclusión obligatoria no son válidos (B-2, I-4).</exception>
    public SourceFileSystem(
        IReadOnlyList<BackupSource> sources,
        ISourceFileApi api,
        TimeProvider time,
        SourceReadOptions options,
        ILogger<SourceFileSystem> logger,
        SourceReadToken? token = null,
        AbandonedSourceThreads? abandonedThreads = null,
        IReadOnlyCollection<string>? snapshotDevices = null,
        IReadOnlyCollection<int>? unreadableSources = null)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);
        _roots = new SourceRoots(sources, snapshotDevices, unreadableSources);
        _api = api;
        _logger = logger;
        _cache = new SourceDirectoryCache(options.DirectoryCacheCapacity);
        var index = 0;
        foreach (var root in _roots.Roots)
        {
            _states[root] = new RootState(root, index++, new SourceBudget(UntrustedText.Describe(root.Source.Alias), options, logger));
        }

        _runner = new SourceOperationRunner(time, options, logger, token, abandonedThreads);
    }

    /// <summary>
    /// Se ha abandonado una operación que no volvió tras cancelarla (R12): la ejecución tiene que terminar
    /// <c>Failed</c>/<c>Internal</c> aunque el motor no lo haya visto.
    /// </summary>
    public bool IsAbandoned => _runner.IsAbandoned;

    /// <summary>Handles de carpeta en la caché (para pruebas).</summary>
    internal int CachedDirectories => _cache.Count;

    /// <summary>Handles que la lectura mantiene abiertos a propósito durante la ejecución: raíces y caché (para pruebas).</summary>
    internal int HeldHandles => _states.Values.Count(s => Volatile.Read(ref s.Handle) is not null) + _cache.Count;

    /// <summary>Presupuesto de plazos del origen de <paramref name="readPath"/> (para pruebas).</summary>
    internal SourceBudget BudgetOf(string readPath) => State(_roots.Resolve(readPath).Root).Budget;

    public async IAsyncEnumerable<SourceEntry> EnumerateAsync(string directoryPath, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var resolved = _roots.Resolve(directoryPath);
        var state = State(resolved.Root);
        ThrowIfExcluded(resolved);

        ISourceHandle directory;
        SourceDirectoryCache.Lease? lease = null;
        var enumeratingRoot = false;
        if (resolved.IsRoot)
        {
            directory = await EnsureRootAsync(state, cancellationToken).ConfigureAwait(false);
            if (Interlocked.CompareExchange(ref state.Enumerating, 1, 0) != 0)
            {
                // La enumeración lleva su posición en el handle: dos a la vez sobre la raíz se mezclarían.
                throw new SourceAccessException(RunIssueCode.ReadError, "La raíz del origen ya se está enumerando.");
            }

            enumeratingRoot = true;
        }
        else
        {
            var opened = await OpenCheckedAsync(state, resolved, SourceOpenMode.Directory, cancellationToken).ConfigureAwait(false);
            if (!opened.Tag.IsDirectory)
            {
                opened.Dispose();
                throw new SourceAccessException(RunIssueCode.ReadError, "No es una carpeta.");
            }

            // Queda en la caché como padre de lo que hay debajo (§8.2 "Caché de carpetas").
            lease = _cache.Add(SourceDirectoryCache.Key(state.Index, resolved.Relative, resolved.Relative.Length), opened.Handle);
            directory = lease.Handle;
        }

        try
        {
            var batch = new List<RawDirectoryEntry>();
            var restart = true;
            while (true)
            {
                batch.Clear();
                var first = restart;
                var more = await RunAsync(
                    "enumeración",
                    directoryPath,
                    state.Budget,
                    directory,
                    signal => directory.ReadDirectory(batch, first, signal),
                    cancellationToken).ConfigureAwait(false);
                restart = false;
                if (!more)
                {
                    yield break;
                }

                foreach (var raw in batch)
                {
                    if (raw.Name is "." or ".." || raw.Name.Length == 0)
                    {
                        continue;
                    }

                    var isReparse = (raw.Attributes & FileAttributes.ReparsePoint) != 0;
                    var isDirectory = (raw.Attributes & FileAttributes.Directory) != 0;
                    yield return new SourceEntry(
                        raw.Name,
                        SourceRoots.ChildPath(resolved, raw.Name),
                        isDirectory,
                        isDirectory ? 0 : raw.Length,
                        raw.LastWriteTimeUtc,
                        raw.Attributes)
                    {
                        // §8.2: con FILE_ATTRIBUTE_REPARSE_POINT, EaSize de FILE_ID_BOTH_DIR_INFO es la etiqueta; si no, 0.
                        ReparseTag = isReparse ? raw.EaSize : 0,
                    };
                }
            }
        }
        finally
        {
            lease?.Dispose();
            if (enumeratingRoot)
            {
                Volatile.Write(ref state.Enumerating, 0);
            }
        }
    }

    public async Task<Stream> OpenReadAsync(string filePath, CancellationToken cancellationToken)
    {
        var resolved = _roots.Resolve(filePath);
        var state = State(resolved.Root);
        ThrowIfExcluded(resolved);
        if (resolved.IsRoot)
        {
            throw new SourceAccessException(RunIssueCode.ReadError, "Es una carpeta, no un archivo.");
        }

        var budget = state.Budget;
        var first = await OpenCheckedAsync(state, resolved, SourceOpenMode.Content, cancellationToken).ConfigureAwait(false);
        IDisposable? ownedFirst = first;
        ISourceHandle? ownedSecond = null;
        try
        {
            if (first.Tag.IsDirectory)
            {
                throw new SourceAccessException(RunIssueCode.ReadError, "Es una carpeta, no un archivo.");
            }

            if (first.Kind == SourceEntryKind.Normal)
            {
                // Sin punto de reanálisis la opción se ignora: el contenido se lee de este mismo handle (§8.2, punto 1).
                ownedFirst = null;
                return new SourceFileStream(_runner, filePath, first.Handle, null, budget);
            }

            // Lista blanca: segundo handle sin FILE_OPEN_REPARSE_POINT (§8.2, punto 2). El primero sigue abierto sin
            // FILE_SHARE_DELETE hasta el final de la lectura.
            var isCloud = ReparseTags.IsCloud(first.Tag.ReparseTag);
            var firstId = await RunAsync("identificador", filePath, budget, first.Handle, s => first.Handle.GetFileId(s), cancellationToken).ConfigureAwait(false);

            // B-1: justo antes de reabrir, el primer handle sigue en la misma clase de la lista blanca.
            var again = await RunAsync("atributos", filePath, budget, first.Handle, s => first.Handle.GetAttributeTag(s), cancellationToken).ConfigureAwait(false);
            var againKind = ClassifyOnHandle(again, _api);
            if (againKind != SourceEntryKind.Content || again.IsDirectory || !SameClass(first.Tag.ReparseTag, again.ReparseTag))
            {
                throw TagChanged(first.Tag, again);
            }

            ISourceHandle second;
            try
            {
                second = await _runner.RunAsync("segunda apertura", filePath, budget, first.Handle, s => first.Handle.ReOpenForContent(s), cancellationToken).ConfigureAwait(false);
            }
            catch (SourceFileApiException ex) when (ex.Error == S.ErrorCantAccessFile || ex.NtStatus == S.StatusIoReparseTagNotHandled)
            {
                // El filtro propietario no está en la pila (posible en VSS), §8.2 punto 3.
                if (!isCloud)
                {
                    throw new SourceAccessException(
                        RunIssueCode.ReadError,
                        $"No se puede leer el contenido de un archivo con la etiqueta {ReparseTags.Format(first.Tag.ReparseTag)} sin su filtro; se conserva la versión anterior si la había.",
                        ex);
                }

                await EnsureFullyAllocatedAsync(first.Handle, filePath, budget, cancellationToken).ConfigureAwait(false);
                ownedFirst = null;
                return new SourceFileStream(_runner, filePath, first.Handle, null, budget);
            }
            catch (SourceFileApiException ex)
            {
                throw MapError(ex);
            }

            ownedSecond = second;
            await CheckSecondHandleAsync(resolved, budget, first, firstId, second, cancellationToken).ConfigureAwait(false);
            if (isCloud)
            {
                await EnsureFullyAllocatedAsync(second, filePath, budget, cancellationToken).ConfigureAwait(false);
            }

            ownedSecond = null;
            ownedFirst = null;
            return new SourceFileStream(_runner, filePath, second, first.Handle, budget);
        }
        finally
        {
            ownedSecond?.Dispose();
            ownedFirst?.Dispose();
        }
    }

    public async Task<SourceEntry?> GetFileInfoAsync(string filePath, CancellationToken cancellationToken)
    {
        var resolved = _roots.Resolve(filePath);
        var state = State(resolved.Root);
        ThrowIfExcluded(resolved);
        try
        {
            if (resolved.IsRoot)
            {
                var root = await EnsureRootAsync(state, cancellationToken).ConfigureAwait(false);
                var rootTag = await RunAsync("atributos", filePath, state.Budget, root, s => root.GetAttributeTag(s), cancellationToken).ConfigureAwait(false);
                _ = ClassifyOnHandle(rootTag, _api);
                return await DescribeAsync(state, resolved, root, rootTag, cancellationToken).ConfigureAwait(false);
            }

            using var opened = await OpenCheckedAsync(state, resolved, SourceOpenMode.Metadata, cancellationToken).ConfigureAwait(false);
            return await DescribeAsync(state, resolved, opened.Handle, opened.Tag, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _runner.Dispose();
        _cache.Dispose();
        foreach (var state in _states.Values)
        {
            state.Dispose();
        }
    }

    /// <summary>
    /// Clasificación sobre el handle (ADR-19): enlace, solo en la nube (también por el estado del marcador) y no legible
    /// lanzan; devuelve la clase si es normal o de la lista blanca.
    /// </summary>
    internal static SourceEntryKind ClassifyOnHandle(SourceAttributeTag tag, ISourceFileApi api)
    {
        var kind = SourceAttributes.Classify(tag.Attributes, tag.ReparseTag);
        switch (kind)
        {
            case SourceEntryKind.Link:
                throw new SourceAccessException(
                    RunIssueCode.ReparsePointSkipped,
                    $"Es un enlace (punto de reanálisis {ReparseTags.Format(tag.ReparseTag)}) y no se sigue.");
            case SourceEntryKind.CloudOnly:
                throw CloudOnly();
            case SourceEntryKind.Unreadable:
                throw new SourceAccessException(
                    RunIssueCode.ReadError,
                    $"Tiene un punto de reanálisis con la etiqueta {ReparseTags.Format(tag.ReparseTag)}, que no se puede leer; se conserva la versión anterior si la había.");
        }

        if (kind == SourceEntryKind.Content && ReparseTags.IsCloud(tag.ReparseTag))
        {
            var state = api.GetPlaceholderState(tag.Attributes, tag.ReparseTag);
            if (state == R.CfPlaceholderStateInvalid || (state & (R.CfPlaceholderStatePartial | R.CfPlaceholderStatePartiallyOnDisk)) != 0)
            {
                throw CloudOnly();
            }
        }

        return kind;
    }

    internal static Exception MapError(SourceFileApiException ex)
    {
        switch (ex.NtStatus)
        {
            case SourceFileApiException.StatusFileIsADirectory:
                return new SourceAccessException(RunIssueCode.ReadError, "Es una carpeta, no un archivo.", ex);
            case SourceFileApiException.StatusNotADirectory:
                return new SourceAccessException(RunIssueCode.ReadError, "No es una carpeta.", ex);
        }

        return ex.Error switch
        {
            ErrorFileNotFound => new FileNotFoundException("El elemento ya no existe.", ex),
            ErrorPathNotFound => new DirectoryNotFoundException("La carpeta que lo contiene ya no existe.", ex),
            ErrorAccessDenied => new UnauthorizedAccessException("Sin permiso para leerlo.", ex),
            ErrorSharingViolation or S.ErrorLockViolation => new SourceAccessException(RunIssueCode.FileLocked, "Está bloqueado por otro proceso.", ex),
            S.ErrorInvalidName or S.ErrorBadPathName or S.ErrorFilenameExcedRange => new SourceAccessException(RunIssueCode.PathInvalid, "La ruta no es válida.", ex),
            _ => new SourceAccessException(RunIssueCode.ReadError, $"Error de Windows {ex.Error} ({ex.Operation}).", ex),
        };
    }

    private static SourceAccessException CloudOnly() => new(
        RunIssueCode.CloudOnlySkipped,
        "Está solo en la nube (no descargado); se omite sin descargarlo y se conserva la versión anterior si la había.");

    private static SourceAccessException TagChanged(SourceAttributeTag before, SourceAttributeTag after) => new(
        RunIssueCode.ReparsePointSkipped,
        $"La etiqueta ha cambiado ({ReparseTags.Format(before.ReparseTag)} → {ReparseTags.Format(after.IsReparsePoint ? after.ReparseTag : 0)}); no se lee.");

    private static bool SameClass(uint firstTag, uint secondTag) =>
        (ReparseTags.IsCloud(firstTag) && ReparseTags.IsCloud(secondTag)) || (firstTag == secondTag && ReparseTags.IsContentTag(firstTag));

    private static void ThrowIfExcluded(ResolvedSourcePath resolved)
    {
        if (!resolved.IsRoot && resolved.Root.IsExcluded(resolved.Relative))
        {
            // Defensa en profundidad: el motor nunca pide una exclusión obligatoria (ADR-16).
            throw new SourceAccessException(RunIssueCode.ReparsePointSkipped, "Está dentro de una carpeta protegida y no se lee.");
        }
    }

    private RootState State(SourceRoot root) => _states[root];

    private async Task<SourceEntry> DescribeAsync(RootState state, ResolvedSourcePath resolved, ISourceHandle handle, SourceAttributeTag tag, CancellationToken cancellationToken)
    {
        var times = await RunAsync("metadatos", resolved.Path, state.Budget, handle, s => handle.GetSizeAndTime(s), cancellationToken).ConfigureAwait(false);
        var isDirectory = tag.IsDirectory;
        return new SourceEntry(resolved.Name, resolved.Path, isDirectory, isDirectory ? 0 : times.Length, times.LastWriteTimeUtc, tag.Attributes)
        {
            ReparseTag = tag.IsReparsePoint ? tag.ReparseTag : 0,
        };
    }

    /// <summary>Ejecuta una operación síncrona con plazo y traduce los errores del sistema.</summary>
    private async Task<T> RunAsync<T>(string description, string path, SourceBudget budget, ISourceHandle? target, Func<CancellationToken, T> operation, CancellationToken cancellationToken)
    {
        try
        {
            return await _runner.RunAsync(description, path, budget, target, operation, cancellationToken).ConfigureAwait(false);
        }
        catch (SourceFileApiException ex)
        {
            throw MapError(ex);
        }
    }

    /// <summary>
    /// Handle fijo de la raíz del origen (§8.2 "Apertura relativa"): la primera vez se abre la raíz del volumen (o de la
    /// instantánea), se comprueba que es un disco local y se baja componente a componente, cada uno relativo al anterior,
    /// exigiendo carpetas normales o de la lista blanca; al final, la ruta final tiene que ser la esperada.
    /// </summary>
    private async Task<ISourceHandle> EnsureRootAsync(RootState state, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref state.Handle) is { } ready)
        {
            return ready;
        }

        await state.OpenLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (state.Handle is { } existing)
            {
                return existing;
            }

            var opened = await OpenRootAsync(state, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref state.Handle, opened);
            return opened;
        }
        finally
        {
            state.OpenLock.Release();
        }
    }

    private async Task<ISourceHandle> OpenRootAsync(RootState state, CancellationToken cancellationToken)
    {
        var root = state.Root;
        var path = root.ReadPath;
        if (root.Unreadable)
        {
            // M-B: el origen no se pudo resolver al empezar y su ruta es la configurada: no se abre nada, ni siquiera el volumen.
            throw new SourceAccessException(RunIssueCode.ReadError, "El origen no se pudo resolver al empezar la ejecución; no se lee.");
        }

        var budget = state.Budget;
        var current = await RunAsync("apertura del volumen", path, budget, null, s => _api.OpenVolumeRoot(root.VolumeRoot, s), cancellationToken).ConfigureAwait(false);
        try
        {
            var volume = current;
            var device = await RunAsync("dispositivo", path, budget, volume, s => volume.GetDeviceInfo(s), cancellationToken).ConfigureAwait(false);
            if (!device.IsLocalDisk)
            {
                LogRootNotLocal(_logger, UntrustedText.Describe(path), device.DeviceType, device.Characteristics);
                throw new SourceAccessException(RunIssueCode.ReadError, "La raíz del origen no está en un disco local.");
            }

            var tag = await RunAsync("atributos", path, budget, volume, s => volume.GetAttributeTag(s), cancellationToken).ConfigureAwait(false);
            foreach (var name in root.RootComponents)
            {
                var parent = current;
                var child = await RunAsync("apertura", path, budget, parent, s => _api.OpenChild(parent, name, SourceOpenMode.Directory, exactCase: false, s), cancellationToken).ConfigureAwait(false);
                current = child;
                parent.Dispose();
                tag = await RunAsync("atributos", path, budget, child, s => child.GetAttributeTag(s), cancellationToken).ConfigureAwait(false);
                _ = ClassifyOnHandle(tag, _api);
                if (!tag.IsDirectory)
                {
                    throw new SourceAccessException(RunIssueCode.ReadError, "Un componente de la raíz del origen no es una carpeta.");
                }
            }

            _ = ClassifyOnHandle(tag, _api);
            if (!tag.IsDirectory)
            {
                throw new SourceAccessException(RunIssueCode.ReadError, "La raíz del origen no es una carpeta.");
            }

            var handle = current;
            var finalNt = await RunAsync("ruta final", path, budget, handle, s => handle.GetFinalPath(FinalPathFormat.Nt, s), cancellationToken).ConfigureAwait(false);
            await CheckRootAsync(state, handle, finalNt, cancellationToken).ConfigureAwait(false);
            return handle;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Handle de la carpeta padre de <paramref name="resolved"/>: la raíz, el de la caché o, si no está, abierto desde el
    /// antepasado abierto más cercano componente a componente (§8.2 "Caché de carpetas"). Quien lo recibe lo suelta.
    /// </summary>
    private async Task<ParentHandle> AcquireParentAsync(RootState state, ResolvedSourcePath resolved, CancellationToken cancellationToken)
    {
        var depth = resolved.Relative.Length - 1;
        var root = await EnsureRootAsync(state, cancellationToken).ConfigureAwait(false);
        var start = 0;
        SourceDirectoryCache.Lease? current = null;
        for (var k = depth; k >= 1; k--)
        {
            current = _cache.TryAcquire(SourceDirectoryCache.Key(state.Index, resolved.Relative, k));
            if (current is not null)
            {
                start = k;
                break;
            }
        }

        try
        {
            for (var i = start; i < depth; i++)
            {
                var parent = current?.Handle ?? root;
                var name = resolved.Relative[i];
                ISourceHandle child;
                try
                {
                    child = await RunAsync("apertura", resolved.Path, state.Budget, parent, s => _api.OpenChild(parent, name, SourceOpenMode.Directory, exactCase: true, s), cancellationToken).ConfigureAwait(false);
                }
                catch (FileNotFoundException ex)
                {
                    // Lo que falta es una carpeta de la ruta, no el elemento pedido.
                    throw new DirectoryNotFoundException("La carpeta que lo contiene ya no existe.", ex);
                }

                try
                {
                    var tag = await RunAsync("atributos", resolved.Path, state.Budget, child, s => child.GetAttributeTag(s), cancellationToken).ConfigureAwait(false);
                    _ = ClassifyOnHandle(tag, _api);
                    if (!tag.IsDirectory)
                    {
                        throw new DirectoryNotFoundException("Una carpeta de la ruta ya no es una carpeta.");
                    }
                }
                catch
                {
                    child.Dispose();
                    throw;
                }

                current?.Dispose();
                current = null;
                current = _cache.Add(SourceDirectoryCache.Key(state.Index, resolved.Relative, i + 1), child);
            }

            var result = current is null ? new ParentHandle(root, null) : new ParentHandle(current.Handle, current);
            current = null;
            return result;
        }
        finally
        {
            current?.Dispose();
        }
    }

    /// <summary>
    /// Abre el elemento relativo a su padre, lo clasifica y comprueba su ruta final sobre el handle. Si algo no se cumple,
    /// cierra el handle y lanza sin leer nada.
    /// </summary>
    private async Task<CheckedHandle> OpenCheckedAsync(RootState state, ResolvedSourcePath resolved, SourceOpenMode mode, CancellationToken cancellationToken)
    {
        var path = resolved.Path;
        var name = resolved.Relative[^1];
        ISourceHandle handle;
        using (var parent = await AcquireParentAsync(state, resolved, cancellationToken).ConfigureAwait(false))
        {
            var parentHandle = parent.Handle;
            try
            {
                handle = await _runner.RunAsync("apertura", path, state.Budget, parentHandle, s => _api.OpenChild(parentHandle, name, mode, exactCase: true, s), cancellationToken).ConfigureAwait(false);
            }
            catch (SourceFileApiException ex) when (ex.NtStatus is SourceFileApiException.StatusFileIsADirectory or SourceFileApiException.StatusNotADirectory)
            {
                // FILE_NON_DIRECTORY_FILE / FILE_DIRECTORY_FILE: no es del tipo pedido. Si lo que ocupa el nombre es un enlace
                // (p. ej. un archivo sustituido por un junction), el aviso es el de un enlace (RF-06), no un error de lectura.
                await ThrowIfLinkAsync(state, path, parentHandle, name, cancellationToken).ConfigureAwait(false);
                if (ex.NtStatus == SourceFileApiException.StatusFileIsADirectory)
                {
                    // Decisión del coordinador (HU-08): se enumeró como archivo y ahora es una carpeta real. Cambió durante
                    // la lectura: aviso ReadError para el elemento (el motor conserva su versión anterior) y, como el motor ya
                    // lo tiene como archivo, no se desciende en esa carpeta en esta ejecución.
                    throw new SourceAccessException(
                        RunIssueCode.ReadError,
                        "Ha cambiado durante la lectura: se enumeró como archivo y ahora es una carpeta; se conserva la versión anterior y no se recorre en esta ejecución.",
                        ex);
                }

                throw MapError(ex);
            }
            catch (SourceFileApiException ex)
            {
                throw MapError(ex);
            }
        }

        try
        {
            var tag = await RunAsync("atributos", path, state.Budget, handle, s => handle.GetAttributeTag(s), cancellationToken).ConfigureAwait(false);
            var kind = ClassifyOnHandle(tag, _api);
            var finalNt = await RunAsync("ruta final", path, state.Budget, handle, s => handle.GetFinalPath(FinalPathFormat.Nt, s), cancellationToken).ConfigureAwait(false);
            CheckFinalPath(resolved.Root, resolved.Relative, finalNt);
            return new CheckedHandle(handle, tag, kind);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>Abre <paramref name="name"/> solo para metadatos y lanza la excepción de su clase si no es normal.</summary>
    private async Task ThrowIfLinkAsync(RootState state, string path, ISourceHandle parent, string name, CancellationToken cancellationToken)
    {
        using var probe = await RunAsync("apertura", path, state.Budget, parent, s => _api.OpenChild(parent, name, SourceOpenMode.Metadata, exactCase: true, s), cancellationToken).ConfigureAwait(false);
        var tag = await RunAsync("atributos", path, state.Budget, probe, s => probe.GetAttributeTag(s), cancellationToken).ConfigureAwait(false);
        _ = ClassifyOnHandle(tag, _api);
    }

    private async Task CheckRootAsync(RootState state, ISourceHandle handle, string finalNt, CancellationToken cancellationToken)
    {
        var root = state.Root;
        var final = root.RootFinalFormat == FinalPathFormat.Nt
            ? finalNt
            : await RunAsync("ruta final", root.ReadPath, state.Budget, handle, s => handle.GetFinalPath(root.RootFinalFormat, s), cancellationToken).ConfigureAwait(false);
        if (!SourceRoots.SegmentsEqual(final, root.ExpectedRootFinal) || !root.TrySetRootFinalNt(finalNt))
        {
            LogRootMismatch(_logger, UntrustedText.Describe(root.ReadPath), UntrustedText.Describe(final));
            throw new SourceAccessException(
                RunIssueCode.ReparsePointSkipped,
                "La ruta final de la raíz del origen no es la esperada (puede haberse sustituido por un enlace).");
        }
    }

    private void CheckFinalPath(SourceRoot root, string[] relative, string finalNt)
    {
        var rootFinal = root.RootFinalNt ?? throw new SourceAccessException(RunIssueCode.ReadError, "No se ha podido comprobar la raíz del origen.");
        if (!SourceRoots.FinalPathMatches(finalNt, rootFinal, relative))
        {
            LogFinalPathMismatch(_logger, UntrustedText.Describe(SourceRoots.Join(rootFinal, relative)), UntrustedText.Describe(finalNt));
            throw new SourceAccessException(
                RunIssueCode.ReparsePointSkipped,
                "Su ruta final no es la esperada (una carpeta de la ruta puede haberse movido o sustituido por un enlace); no se sigue.");
        }

        if (root.IsExcluded(relative))
        {
            throw new SourceAccessException(RunIssueCode.ReparsePointSkipped, "Está dentro de una carpeta protegida y no se lee.");
        }
    }

    /// <summary>§8.2 "Apertura para leer el contenido", punto 2: comprobaciones sobre el segundo handle antes de leer.</summary>
    private async Task CheckSecondHandleAsync(ResolvedSourcePath resolved, SourceBudget budget, CheckedHandle first, SourceFileId firstId, ISourceHandle second, CancellationToken cancellationToken)
    {
        var path = resolved.Path;
        var secondId = await RunAsync("identificador", path, budget, second, s => second.GetFileId(s), cancellationToken).ConfigureAwait(false);
        if (secondId != firstId)
        {
            throw new SourceAccessException(RunIssueCode.ReparsePointSkipped, "El segundo handle no es el mismo archivo; no se lee.");
        }

        var tag = await RunAsync("atributos", path, budget, second, s => second.GetAttributeTag(s), cancellationToken).ConfigureAwait(false);
        var kind = ClassifyOnHandle(tag, _api); // enlace → ReparsePointSkipped; solo en la nube → CloudOnlySkipped
        if (kind != SourceEntryKind.Content || tag.IsDirectory || !SameClass(first.Tag.ReparseTag, tag.ReparseTag))
        {
            throw TagChanged(first.Tag, tag);
        }

        var finalNt = await RunAsync("ruta final", path, budget, second, s => second.GetFinalPath(FinalPathFormat.Nt, s), cancellationToken).ConfigureAwait(false);
        CheckFinalPath(resolved.Root, resolved.Relative, finalNt);
    }

    /// <summary>Con etiqueta de nube, antes de leer: sin rangos sin asignar (§8.2, R4); si no, <c>CloudOnlySkipped</c>.</summary>
    private async Task EnsureFullyAllocatedAsync(ISourceHandle handle, string path, SourceBudget budget, CancellationToken cancellationToken)
    {
        var times = await RunAsync("metadatos", path, budget, handle, s => handle.GetSizeAndTime(s), cancellationToken).ConfigureAwait(false);
        var allocated = await RunAsync("rangos asignados", path, budget, handle, s => handle.IsFullyAllocated(times.Length, s), cancellationToken).ConfigureAwait(false);
        if (!allocated)
        {
            throw CloudOnly();
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "La ruta final de la raíz del origen {ReadPath} es {FinalPath}, que no es la esperada; no se lee (ADR-18).")]
    private static partial void LogRootMismatch(ILogger logger, string readPath, string finalPath);

    [LoggerMessage(Level = LogLevel.Warning, Message = "La ruta final de un elemento de origen es {FinalPath} y se esperaba {Expected}; no se sigue (ADR-18).")]
    private static partial void LogFinalPathMismatch(ILogger logger, string expected, string finalPath);

    [LoggerMessage(Level = LogLevel.Warning, Message = "La raíz del origen {ReadPath} no está en un disco local (tipo 0x{DeviceType:X}, características 0x{Characteristics:X}); no se lee.")]
    private static partial void LogRootNotLocal(ILogger logger, string readPath, uint deviceType, uint characteristics);

    /// <summary>Estado de un origen en la ejecución: handle fijo de su raíz y presupuesto de plazos.</summary>
    private sealed class RootState(SourceRoot root, int index, SourceBudget budget) : IDisposable
    {
        public ISourceHandle? Handle;
        public int Enumerating;

        public SourceRoot Root => root;

        public int Index => index;

        public SourceBudget Budget => budget;

        public SemaphoreSlim OpenLock { get; } = new(1, 1);

        public void Dispose()
        {
            Interlocked.Exchange(ref Handle, null)?.Dispose();
            OpenLock.Dispose();
        }
    }

    /// <summary>Handle de un padre: la raíz (fija, no se suelta) o uno de la caché.</summary>
    private sealed class ParentHandle(ISourceHandle handle, SourceDirectoryCache.Lease? lease) : IDisposable
    {
        public ISourceHandle Handle => handle;

        public void Dispose() => lease?.Dispose();
    }

    /// <summary>Handle abierto y ya comprobado, con lo que se leyó sobre él.</summary>
    private sealed class CheckedHandle(ISourceHandle handle, SourceAttributeTag tag, SourceEntryKind kind) : IDisposable
    {
        public ISourceHandle Handle => handle;

        public SourceAttributeTag Tag => tag;

        public SourceEntryKind Kind => kind;

        public void Dispose() => handle.Dispose();
    }
}
