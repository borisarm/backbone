namespace Backbone.Core.Engine;

/// <summary>
/// Vista previa de las exclusiones (RF-04, HU-08 criterio 5, F2): cuántos archivos y bytes excluye cada patrón de un
/// trabajo, recorriendo los orígenes sin abrir ningún archivo.
/// </summary>
/// <remarks>
/// <para>
/// Cada patrón se cuenta <b>por separado</b>, como si fuera el único: un archivo que excluyen dos patrones cuenta en los
/// dos (p. ej. un <c>.tmp</c> dentro de <c>node_modules</c>), y lo que hay dentro de una carpeta excluida cuenta para el
/// patrón que la excluye. Por eso, a diferencia de la copia, sí se recorren las carpetas excluidas. Los totales
/// (<see cref="ExclusionPreviewResult.ExcludedFiles"/>…) cuentan cada archivo una vez.
/// </para>
/// <para>
/// Como en la copia: las exclusiones obligatorias (ADR-16) no se recorren ni se cuentan, los puntos de reanálisis no se
/// siguen ni se cuentan (RF-06), y los archivos solo en la nube se cuentan con su tamaño sin abrirlos. Una carpeta que no se
/// puede leer (o cuyo listado no cabe en el presupuesto de memoria, §8.4) se cuenta en
/// <see cref="ExclusionPreviewResult.UnreadableDirectories"/> y el resultado es aproximado. La memoria se acota igual que
/// en el motor: solo se guardan las subcarpetas de las carpetas abiertas, con su estado.
/// </para>
/// </remarks>
public sealed class ExclusionPreview
{
    /// <summary>Bytes estimados por subcarpeta pendiente, aparte de sus textos y su estado.</summary>
    private const long OverheadBytes = 64;

    /// <summary>Presupuesto de los listados pendientes (las pruebas lo reducen); por defecto el del motor.</summary>
    internal long ListingBudgetBytes { get; set; } = BackupEngine.DefaultListingBudgetBytes;

    /// <summary>Calcula la vista previa.</summary>
    /// <param name="source">Acceso a los orígenes (en vivo; la vista previa no usa VSS).</param>
    /// <param name="sources">Orígenes del trabajo, con sus exclusiones obligatorias.</param>
    /// <param name="patterns">Patrones del trabajo.</param>
    /// <param name="cancellationToken">Se comprueba en cada carpeta y cada elemento.</param>
    /// <returns>Recuento por patrón y totales.</returns>
    /// <exception cref="ArgumentException">Un patrón o una exclusión obligatoria no es válido.</exception>
    /// <exception cref="OperationCanceledException">Cancelada.</exception>
    public async Task<ExclusionPreviewResult> ComputeAsync(
        ISourceFileSystem source,
        IReadOnlyList<BackupSource> sources,
        IReadOnlyList<string> patterns,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sources);
        foreach (var backupSource in sources)
        {
            ArgumentNullException.ThrowIfNull(backupSource);
            ArgumentException.ThrowIfNullOrEmpty(backupSource.ReadPath);
            ArgumentException.ThrowIfNullOrEmpty(backupSource.Alias);
        }

        var matcher = ExclusionMatcher.Create(patterns);
        var mandatory = MandatoryExclusionSet.Create(sources);
        var counts = new Counter(matcher.Patterns.Count);
        var walk = new Walk(this, source, matcher, mandatory, counts);
        foreach (var backupSource in sources)
        {
            await walk.RunAsync(backupSource, cancellationToken).ConfigureAwait(false);
        }

        return new ExclusionPreviewResult
        {
            Patterns = [.. matcher.Patterns.Select((p, i) => new ExclusionPatternCount(p.Text, counts.Files[i], counts.Bytes[i]))],
            ExcludedFiles = counts.ExcludedFiles,
            ExcludedBytes = counts.ExcludedBytes,
            IncludedFiles = counts.IncludedFiles,
            IncludedBytes = counts.IncludedBytes,
            UnreadableDirectories = counts.UnreadableDirectories,
        };
    }

    private sealed class Counter(int patternCount)
    {
        public long[] Files { get; } = new long[patternCount];

        public long[] Bytes { get; } = new long[patternCount];

        public long ExcludedFiles { get; set; }

        public long ExcludedBytes { get; set; }

        public long IncludedFiles { get; set; }

        public long IncludedBytes { get; set; }

        public long UnreadableDirectories { get; set; }
    }

    /// <summary>Carpeta en recorrido: su estado, los patrones que excluyen a ella o a una carpeta de encima, y sus subcarpetas.</summary>
    private sealed class Frame(string fullPath, string snapshotPath, ulong[] state, bool[] matched, bool anyMatched)
    {
        public string FullPath { get; } = fullPath;

        public string SnapshotPath { get; } = snapshotPath;

        public ulong[] State { get; } = state;

        public bool[] Matched { get; } = matched;

        public bool AnyMatched { get; } = anyMatched;

        public List<Frame>? Subdirectories { get; set; }

        public int Next { get; set; }

        public long Bytes { get; set; }

        public long Cost => (2L * ((long)FullPath.Length + SnapshotPath.Length)) + (8L * State.Length) + Matched.Length + OverheadBytes;
    }

    private sealed class Walk(ExclusionPreview owner, ISourceFileSystem source, ExclusionMatcher matcher, MandatoryExclusionSet mandatory, Counter counts)
    {
        private readonly ulong[] scratch = new ulong[matcher.StateWords];
        private long listingBytes;

        public async Task RunAsync(BackupSource backupSource, CancellationToken cancellationToken)
        {
            var stack = new Stack<Frame>();
            var root = new Frame(backupSource.ReadPath, backupSource.Alias, matcher.RootState(backupSource.Alias), new bool[matcher.Patterns.Count], anyMatched: false);
            stack.Push(root);
            while (stack.TryPeek(out var frame))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (frame.Subdirectories is null)
                {
                    frame.Subdirectories = await ListAsync(frame, cancellationToken).ConfigureAwait(false);
                    if (frame.Subdirectories is null)
                    {
                        stack.Pop();
                        continue;
                    }
                }

                if (frame.Next < frame.Subdirectories.Count)
                {
                    stack.Push(frame.Subdirectories[frame.Next++]);
                    continue;
                }

                stack.Pop();
                listingBytes -= frame.Bytes;
            }
        }

        /// <summary>Cuenta los archivos de la carpeta y devuelve sus subcarpetas; <see langword="null"/> si no se puede leer.</summary>
        private async Task<List<Frame>?> ListAsync(Frame frame, CancellationToken cancellationToken)
        {
            var subdirectories = new List<Frame>();
            var cost = 0L;
            var checkMandatory = mandatory.HasExclusionsIn(frame.SnapshotPath);
            try
            {
                await foreach (var child in source.EnumerateAsync(frame.FullPath, cancellationToken).ConfigureAwait(false))
                {
                    ArgumentNullException.ThrowIfNull(child);
                    cancellationToken.ThrowIfCancellationRequested();
                    var childPath = frame.SnapshotPath + "/" + child.Name;
                    // Como en la copia: un archivo solo en la nube se cuenta aunque sea punto de reanálisis; un enlace, no.
                    var isLink = child.Attributes.HasFlag(FileAttributes.ReparsePoint)
                        && (child.IsDirectory || !SourceAttributes.IsCloudOnly(child.Attributes));
                    if ((checkMandatory && mandatory.IsExcluded(childPath)) || isLink)
                    {
                        continue;
                    }

                    var state = child.IsDirectory ? new ulong[matcher.StateWords] : scratch;
                    _ = matcher.Step(frame.State, child.Name, child.IsDirectory, state);
                    var matched = child.IsDirectory ? (bool[])frame.Matched.Clone() : null;
                    var any = frame.AnyMatched;
                    for (var p = 0; p < matcher.Patterns.Count; p++)
                    {
                        var hit = frame.Matched[p] || matcher.IsMatch(state, p, child.IsDirectory);
                        any |= hit;
                        if (matched is not null)
                        {
                            matched[p] = hit;
                        }
                        else if (hit)
                        {
                            counts.Files[p]++;
                            counts.Bytes[p] += Math.Max(child.Length, 0);
                        }
                    }

                    if (matched is not null)
                    {
                        var sub = new Frame(child.FullPath, childPath, state, matched, any);
                        cost += sub.Cost;
                        if (listingBytes + cost > owner.ListingBudgetBytes)
                        {
                            // §8.4: no cabe; la carpeta no se cuenta entera (lo ya contado de sus archivos se queda).
                            counts.UnreadableDirectories++;
                            return null;
                        }

                        subdirectories.Add(sub);
                    }
                    else if (any)
                    {
                        counts.ExcludedFiles++;
                        counts.ExcludedBytes += Math.Max(child.Length, 0);
                    }
                    else
                    {
                        counts.IncludedFiles++;
                        counts.IncludedBytes += Math.Max(child.Length, 0);
                    }
                }
            }
            catch (Exception ex) when (BackupEngine.IsSourceError(ex))
            {
                counts.UnreadableDirectories++;
                return null;
            }

            listingBytes += cost;
            frame.Bytes = cost;
            subdirectories.Sort(static (a, b) => string.CompareOrdinal(a.SnapshotPath, b.SnapshotPath));
            return subdirectories;
        }
    }
}

/// <summary>Resultado de <see cref="ExclusionPreview.ComputeAsync"/>.</summary>
public sealed record ExclusionPreviewResult
{
    /// <summary>Recuento de cada patrón, en el orden de la lista (cada uno por separado).</summary>
    public required IReadOnlyList<ExclusionPatternCount> Patterns { get; init; }

    /// <summary>Archivos que excluye algún patrón (cada uno una vez).</summary>
    public long ExcludedFiles { get; init; }

    /// <summary>Bytes de <see cref="ExcludedFiles"/>.</summary>
    public long ExcludedBytes { get; init; }

    /// <summary>Archivos que no excluye ningún patrón.</summary>
    public long IncludedFiles { get; init; }

    /// <summary>Bytes de <see cref="IncludedFiles"/>.</summary>
    public long IncludedBytes { get; init; }

    /// <summary>Carpetas que no se han podido recorrer (sin permiso, error, demasiado grandes); si no es 0, los números son aproximados.</summary>
    public long UnreadableDirectories { get; init; }
}

/// <summary>Archivos y bytes que excluye un patrón.</summary>
/// <param name="Pattern">Texto del patrón.</param>
/// <param name="Files">Archivos que excluye.</param>
/// <param name="Bytes">Suma de sus tamaños.</param>
public sealed record ExclusionPatternCount(string Pattern, long Files, long Bytes);
