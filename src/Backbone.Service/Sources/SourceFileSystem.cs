using System.Runtime.CompilerServices;
using Backbone.Contracts;
using Backbone.Core.Engine;
using Microsoft.Extensions.Logging;
using static Backbone.Service.Os.NativeMethods.SourceIo;

namespace Backbone.Service.Sources;

/// <summary>
/// Lectura de los orígenes de una ejecución (<see cref="ISourceFileSystem"/> de Core; HU-08, §8.2 "Lectura de los
/// orígenes", ADR-18, ADR-19). Se crea <b>por ejecución</b> con sus orígenes (<see cref="ISourceFileSystemFactory"/>) y se
/// desecha al terminar.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>
/// Todo se abre por handle con <c>FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS</c> y sin
/// <c>FILE_SHARE_DELETE</c>, y sobre ese handle, antes de leer nada: atributos y etiqueta (<c>FileAttributeTagInfo</c>)
/// clasificados con <see cref="SourceAttributes.Classify(FileAttributes, uint)"/>, estado del marcador de nube y ruta final
/// (<c>GetFinalPathNameByHandle</c>, <c>VOLUME_NAME_NT</c>) igual a la de la raíz más la ruta relativa, fuera de las
/// exclusiones obligatorias.
/// </description></item>
/// <item><description>
/// La raíz se comprueba la primera vez que se usa: su ruta final tiene que ser la propia <see cref="BackupSource.ReadPath"/>
/// (la ruta final que dio la resolución controlada, ADR-20 punto 6, o su traducción a la instantánea VSS).
/// </description></item>
/// <item><description>
/// La enumeración se hace desde el handle (<c>FileIdBothDirectoryInfo</c>) con la etiqueta de <c>EaSize</c>.
/// </description></item>
/// <item><description>
/// El contenido de lo normal se lee del primer handle; el de la lista blanca, de un segundo handle (<c>ReOpenFile</c> sin
/// <c>FILE_FLAG_OPEN_REPARSE_POINT</c>) comprobado sobre él mismo (§8.2 "Apertura para leer el contenido").
/// </description></item>
/// <item><description>
/// Plazo de 60 s por operación (<see cref="SourceOperationRunner"/>). Una operación abandonada lanza
/// <see cref="SourceOperationAbandonedException"/>, que el <c>JobRunner</c> traduce a <c>Failed</c>/<c>Internal</c>
/// (<see cref="IsAbandoned"/> lo indica también después).
/// </description></item>
/// </list>
/// </remarks>
internal sealed partial class SourceFileSystem : ISourceFileSystem, IDisposable
{
    private readonly SourceRoots _roots;
    private readonly ISourceFileApi _api;
    private readonly SourceOperationRunner _runner;
    private readonly ILogger _logger;

    public SourceFileSystem(IReadOnlyList<BackupSource> sources, ISourceFileApi api, TimeProvider time, SourceReadOptions options, ILogger<SourceFileSystem> logger)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(logger);
        _roots = new SourceRoots(sources);
        _api = api;
        _logger = logger;
        _runner = new SourceOperationRunner(time, options, logger);
    }

    /// <summary>
    /// Se ha abandonado una operación que no volvió tras cancelarla (R12): la ejecución tiene que terminar
    /// <c>Failed</c>/<c>Internal</c> aunque el motor no lo haya visto.
    /// </summary>
    public bool IsAbandoned => _runner.IsAbandoned;

    public async IAsyncEnumerable<SourceEntry> EnumerateAsync(string directoryPath, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var resolved = _roots.Resolve(directoryPath);
        using var directory = await OpenCheckedAsync(resolved, SourceOpenMode.Directory, cancellationToken).ConfigureAwait(false);
        if (!directory.Tag.IsDirectory)
        {
            throw new SourceAccessException(RunIssueCode.ReadError, "No es una carpeta.");
        }

        var batch = new List<RawDirectoryEntry>();
        while (true)
        {
            batch.Clear();
            var more = await RunAsync(
                "enumeración",
                directoryPath,
                directory.Handle,
                signal => directory.Handle.ReadDirectory(batch, signal),
                cancellationToken).ConfigureAwait(false);
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

    public async Task<Stream> OpenReadAsync(string filePath, CancellationToken cancellationToken)
    {
        var resolved = _roots.Resolve(filePath);
        var first = await OpenCheckedAsync(resolved, SourceOpenMode.Content, cancellationToken).ConfigureAwait(false);
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
                // Sin punto de reanálisis la marca se ignora: el contenido se lee de este mismo handle (§8.2, punto 1).
                ownedFirst = null;
                return new SourceFileStream(_runner, filePath, first.Handle, null);
            }

            // Lista blanca: segundo handle sin FILE_FLAG_OPEN_REPARSE_POINT (§8.2, punto 2). El primero sigue abierto sin
            // FILE_SHARE_DELETE hasta el final de la lectura.
            var isCloud = ReparseTags.IsCloud(first.Tag.ReparseTag);
            var firstId = await RunAsync("identificador", filePath, first.Handle, s => first.Handle.GetFileId(s), cancellationToken).ConfigureAwait(false);
            ISourceHandle second;
            try
            {
                second = await _runner.RunAsync("segunda apertura", filePath, first.Handle, s => first.Handle.ReOpenForContent(s), cancellationToken).ConfigureAwait(false);
            }
            catch (SourceFileApiException ex) when (ex.Error == ErrorCantAccessFile)
            {
                // El filtro propietario no está en la pila (posible en VSS), §8.2 punto 3.
                if (!isCloud)
                {
                    throw new SourceAccessException(
                        RunIssueCode.ReadError,
                        $"No se puede leer el contenido de un archivo con la etiqueta {ReparseTags.Format(first.Tag.ReparseTag)} sin su filtro; se conserva la versión anterior si la había.",
                        ex);
                }

                await EnsureFullyAllocatedAsync(first.Handle, filePath, cancellationToken).ConfigureAwait(false);
                ownedFirst = null;
                return new SourceFileStream(_runner, filePath, first.Handle, null);
            }
            catch (SourceFileApiException ex)
            {
                throw MapError(ex);
            }

            ownedSecond = second;
            await CheckSecondHandleAsync(resolved, first, firstId, second, cancellationToken).ConfigureAwait(false);
            if (isCloud)
            {
                await EnsureFullyAllocatedAsync(second, filePath, cancellationToken).ConfigureAwait(false);
            }

            ownedSecond = null;
            ownedFirst = null;
            return new SourceFileStream(_runner, filePath, second, first.Handle);
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
        CheckedHandle opened;
        try
        {
            opened = await OpenCheckedAsync(resolved, SourceOpenMode.Metadata, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }

        using (opened)
        {
            var times = await RunAsync("metadatos", filePath, opened.Handle, s => opened.Handle.GetSizeAndTime(s), cancellationToken).ConfigureAwait(false);
            var isDirectory = opened.Tag.IsDirectory;
            return new SourceEntry(resolved.Name, filePath, isDirectory, isDirectory ? 0 : times.Length, times.LastWriteTimeUtc, opened.Tag.Attributes)
            {
                ReparseTag = opened.Tag.IsReparsePoint ? opened.Tag.ReparseTag : 0,
            };
        }
    }

    public void Dispose() => _runner.Dispose();

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
            if (state == CfPlaceholderStateInvalid || (state & (CfPlaceholderStatePartial | CfPlaceholderStatePartiallyOnDisk)) != 0)
            {
                throw CloudOnly();
            }
        }

        return kind;
    }

    internal static Exception MapError(SourceFileApiException ex) => ex.Error switch
    {
        ErrorFileNotFound => new FileNotFoundException("El elemento ya no existe.", ex),
        ErrorPathNotFound => new DirectoryNotFoundException("La carpeta que lo contiene ya no existe.", ex),
        ErrorAccessDenied => new UnauthorizedAccessException("Sin permiso para leerlo.", ex),
        ErrorSharingViolation or ErrorLockViolation => new SourceAccessException(RunIssueCode.FileLocked, "Está bloqueado por otro proceso.", ex),
        ErrorInvalidName or ErrorBadPathName or ErrorFilenameExcedRange => new SourceAccessException(RunIssueCode.PathInvalid, "La ruta no es válida.", ex),
        _ => new SourceAccessException(RunIssueCode.ReadError, $"Error de Windows {ex.Error} ({ex.Operation}).", ex),
    };

    private static SourceAccessException CloudOnly() => new(
        RunIssueCode.CloudOnlySkipped,
        "Está solo en la nube (no descargado); se omite sin descargarlo y se conserva la versión anterior si la había.");

    private static bool SameClass(uint firstTag, uint secondTag) =>
        (ReparseTags.IsCloud(firstTag) && ReparseTags.IsCloud(secondTag)) || (firstTag == secondTag && ReparseTags.IsContentTag(firstTag));

    /// <summary>Ejecuta una operación síncrona con plazo y traduce los errores del sistema.</summary>
    private async Task<T> RunAsync<T>(string description, string path, ISourceHandle? target, Func<CancellationToken, T> operation, CancellationToken cancellationToken)
    {
        try
        {
            return await _runner.RunAsync(description, path, target, operation, cancellationToken).ConfigureAwait(false);
        }
        catch (SourceFileApiException ex)
        {
            throw MapError(ex);
        }
    }

    /// <summary>
    /// Abre el elemento, lo clasifica y comprueba su ruta final sobre el handle (y la raíz del origen, si aún no se había
    /// comprobado). Si algo no se cumple, cierra el handle y lanza sin leer nada.
    /// </summary>
    private async Task<CheckedHandle> OpenCheckedAsync(ResolvedSourcePath resolved, SourceOpenMode mode, CancellationToken cancellationToken)
    {
        var root = resolved.Root;
        if (!resolved.IsRoot)
        {
            if (root.IsExcluded(resolved.Relative))
            {
                // Defensa en profundidad: el motor nunca pide una exclusión obligatoria (ADR-16).
                throw new SourceAccessException(RunIssueCode.ReparsePointSkipped, "Está dentro de una carpeta protegida y no se lee.");
            }

            if (root.RootFinalNt is null)
            {
                // La ruta final de la raíz se obtiene una vez, abriéndola por handle con las mismas marcas (§8.2).
                var rootCheck = await OpenCheckedAsync(new ResolvedSourcePath(root, [], root.ReadPath), SourceOpenMode.Directory, cancellationToken).ConfigureAwait(false);
                rootCheck.Dispose();
            }
        }

        var path = resolved.Path;
        var handle = await RunAsync("apertura", path, null, s => _api.Open(root.OpenPathFor(resolved.Relative), mode, s), cancellationToken).ConfigureAwait(false);
        try
        {
            var tag = await RunAsync("atributos", path, handle, s => handle.GetAttributeTag(s), cancellationToken).ConfigureAwait(false);
            var kind = ClassifyOnHandle(tag, _api);
            var finalNt = await RunAsync("ruta final", path, handle, s => handle.GetFinalPath(FinalPathFormat.Nt, s), cancellationToken).ConfigureAwait(false);
            if (resolved.IsRoot)
            {
                await CheckRootAsync(root, handle, finalNt, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                CheckFinalPath(root, resolved.Relative, finalNt);
            }

            return new CheckedHandle(handle, tag, kind);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private async Task CheckRootAsync(SourceRoot root, ISourceHandle handle, string finalNt, CancellationToken cancellationToken)
    {
        var final = root.RootFinalFormat == FinalPathFormat.Nt
            ? finalNt
            : await RunAsync("ruta final", root.ReadPath, handle, s => handle.GetFinalPath(root.RootFinalFormat, s), cancellationToken).ConfigureAwait(false);
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
        if (!SourceRoots.SegmentsEqual(finalNt, SourceRoots.Join(rootFinal, relative)))
        {
            LogFinalPathMismatch(_logger, UntrustedText.Describe(SourceRoots.Join(rootFinal, relative)), UntrustedText.Describe(finalNt));
            throw new SourceAccessException(
                RunIssueCode.ReparsePointSkipped,
                "Su ruta final no es la esperada (una carpeta de la ruta puede haberse sustituido por un enlace); no se sigue.");
        }

        if (root.IsExcluded(relative))
        {
            throw new SourceAccessException(RunIssueCode.ReparsePointSkipped, "Está dentro de una carpeta protegida y no se lee.");
        }
    }

    /// <summary>§8.2 "Apertura para leer el contenido", punto 2: comprobaciones sobre el segundo handle antes de leer.</summary>
    private async Task CheckSecondHandleAsync(ResolvedSourcePath resolved, CheckedHandle first, SourceFileId firstId, ISourceHandle second, CancellationToken cancellationToken)
    {
        var path = resolved.Path;
        var secondId = await RunAsync("identificador", path, second, s => second.GetFileId(s), cancellationToken).ConfigureAwait(false);
        if (secondId != firstId)
        {
            throw new SourceAccessException(RunIssueCode.ReparsePointSkipped, "El segundo handle no es el mismo archivo; no se lee.");
        }

        var tag = await RunAsync("atributos", path, second, s => second.GetAttributeTag(s), cancellationToken).ConfigureAwait(false);
        var kind = ClassifyOnHandle(tag, _api); // enlace → ReparsePointSkipped; solo en la nube → CloudOnlySkipped
        if (kind != SourceEntryKind.Content || tag.IsDirectory || !SameClass(first.Tag.ReparseTag, tag.ReparseTag))
        {
            throw new SourceAccessException(
                RunIssueCode.ReparsePointSkipped,
                $"La etiqueta ha cambiado ({ReparseTags.Format(first.Tag.ReparseTag)} → {ReparseTags.Format(tag.IsReparsePoint ? tag.ReparseTag : 0)}); no se lee.");
        }

        var finalNt = await RunAsync("ruta final", path, second, s => second.GetFinalPath(FinalPathFormat.Nt, s), cancellationToken).ConfigureAwait(false);
        CheckFinalPath(resolved.Root, resolved.Relative, finalNt);
    }

    /// <summary>Con etiqueta de nube, antes de leer: sin rangos sin asignar (§8.2, R4); si no, <c>CloudOnlySkipped</c>.</summary>
    private async Task EnsureFullyAllocatedAsync(ISourceHandle handle, string path, CancellationToken cancellationToken)
    {
        var times = await RunAsync("metadatos", path, handle, s => handle.GetSizeAndTime(s), cancellationToken).ConfigureAwait(false);
        var allocated = await RunAsync("rangos asignados", path, handle, s => handle.IsFullyAllocated(times.Length, s), cancellationToken).ConfigureAwait(false);
        if (!allocated)
        {
            throw CloudOnly();
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "La ruta final de la raíz del origen {ReadPath} es {FinalPath}, que no es la esperada; no se lee (ADR-18).")]
    private static partial void LogRootMismatch(ILogger logger, string readPath, string finalPath);

    [LoggerMessage(Level = LogLevel.Warning, Message = "La ruta final de un elemento de origen es {FinalPath} y se esperaba {Expected}; no se sigue (ADR-18).")]
    private static partial void LogFinalPathMismatch(ILogger logger, string expected, string finalPath);

    /// <summary>Handle abierto y ya comprobado, con lo que se leyó sobre él.</summary>
    private sealed class CheckedHandle(ISourceHandle handle, SourceAttributeTag tag, SourceEntryKind kind) : IDisposable
    {
        public ISourceHandle Handle => handle;

        public SourceAttributeTag Tag => tag;

        public SourceEntryKind Kind => kind;

        public void Dispose() => handle.Dispose();
    }
}
