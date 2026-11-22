using Backbone.Contracts;
using Backbone.Core.Engine;
using Backbone.Service.Os;

namespace Backbone.Service.Sources;

/// <summary>Forma de <see cref="BackupSource.ReadPath"/> y con qué ruta final se compara la raíz (ADR-18, ADR-20 punto 6).</summary>
internal enum SourceRootKind
{
    /// <summary><c>C:\…</c> o <c>\\?\C:\…</c> (copia en vivo): se compara con la ruta final <c>VOLUME_NAME_DOS</c>.</summary>
    DriveLetter = 0,

    /// <summary><c>\\?\GLOBALROOT\Device\…</c> (instantánea VSS): se compara con la ruta final <c>VOLUME_NAME_NT</c>.</summary>
    Device = 1,

    /// <summary><c>\\?\Volume{guid}\…</c>: se compara con la ruta final <c>VOLUME_NAME_GUID</c>.</summary>
    VolumeGuid = 2,
}

/// <summary>Un origen de la ejecución con sus rutas ya preparadas (§8.2 "Rutas y raíces").</summary>
internal sealed class SourceRoot
{
    private readonly Lock _gate = new();
    private string? _rootFinalNt;

    public SourceRoot(
        BackupSource source,
        string readPath,
        string childBase,
        string volumeRoot,
        string[] rootComponents,
        SourceRootKind kind,
        string expectedRootFinal,
        IReadOnlyList<string[]> exclusions)
    {
        Source = source;
        ReadPath = readPath;
        ChildBase = childBase;
        VolumeRoot = volumeRoot;
        RootComponents = rootComponents;
        Kind = kind;
        ExpectedRootFinal = expectedRootFinal;
        Exclusions = exclusions;
    }

    public BackupSource Source { get; }

    /// <summary>La ruta tal como la pasa el motor (<see cref="BackupSource.ReadPath"/>).</summary>
    public string ReadPath { get; }

    /// <summary><see cref="ReadPath"/> sin la barra final: la de los hijos es <c>ChildBase\nombre</c>.</summary>
    public string ChildBase { get; }

    /// <summary>
    /// Ruta NT de la raíz del volumen o de la instantánea, la única que se abre por ruta (<c>\??\C:\</c>,
    /// <c>\??\Volume{guid}\</c>, <c>\Device\HarddiskVolumeShadowCopy&lt;n&gt;\</c>); desde ella se abre cada componente de
    /// <see cref="RootComponents"/> relativo al anterior (§8.2 "Apertura relativa").
    /// </summary>
    public string VolumeRoot { get; }

    /// <summary>Componentes de la raíz del origen debajo de <see cref="VolumeRoot"/> (vacío si el origen es un volumen entero).</summary>
    public IReadOnlyList<string> RootComponents { get; }

    public SourceRootKind Kind { get; }

    /// <summary>Ruta final que tiene que dar la raíz en el formato de <see cref="Kind"/>.</summary>
    public string ExpectedRootFinal { get; }

    /// <summary>Exclusiones obligatorias (ADR-16) por segmentos.</summary>
    public IReadOnlyList<string[]> Exclusions { get; }

    /// <summary>Ruta final <c>VOLUME_NAME_NT</c> de la raíz, una vez abierta y comprobada.</summary>
    public string? RootFinalNt
    {
        get
        {
            lock (_gate)
            {
                return _rootFinalNt;
            }
        }
    }

    public FinalPathFormat RootFinalFormat => Kind switch
    {
        SourceRootKind.DriveLetter => FinalPathFormat.Dos,
        SourceRootKind.VolumeGuid => FinalPathFormat.Guid,
        _ => FinalPathFormat.Nt,
    };

    /// <summary>Guarda la ruta final NT de la raíz; si ya había una, tiene que ser la misma (si no, la raíz ha cambiado).</summary>
    public bool TrySetRootFinalNt(string finalNt)
    {
        lock (_gate)
        {
            if (_rootFinalNt is null)
            {
                _rootFinalNt = finalNt;
                return true;
            }

            return SourceRoots.SegmentsEqual(_rootFinalNt, finalNt);
        }
    }

    public bool IsExcluded(IReadOnlyList<string> relative)
    {
        foreach (var exclusion in Exclusions)
        {
            if (relative.Count < exclusion.Length)
            {
                continue;
            }

            var match = true;
            for (var i = 0; i < exclusion.Length && match; i++)
            {
                match = string.Equals(relative[i], exclusion[i], StringComparison.OrdinalIgnoreCase);
            }

            if (match)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>Una ruta recibida, resuelta a su origen y su ruta relativa.</summary>
internal readonly record struct ResolvedSourcePath(SourceRoot Root, string[] Relative, string Path)
{
    public bool IsRoot => Relative.Length == 0;

    public string Name => IsRoot ? SourceRoots.LastSegment(Root.ChildBase) : Relative[^1];
}

/// <summary>
/// Los orígenes de una ejecución y la deducción de la ruta relativa de cada ruta recibida (§8.2 "Rutas y raíces", ADR-18):
/// quita el prefijo <c>ReadPath\</c> de su origen y rechaza con <see cref="RunIssueCode.PathInvalid"/> lo que no empiece
/// por la raíz de un origen o tenga segmentos vacíos, <c>.</c> o <c>..</c> (y caracteres que ningún nombre enumerado puede
/// tener, como <c>:</c>, que abriría un flujo alternativo).
/// </summary>
/// <remarks>
/// Al crearla se rechaza con <see cref="ArgumentException"/> (error de programación del servicio: la ejecución termina
/// <c>Failed</c>/<c>Internal</c>):
/// <list type="bullet">
/// <item><description>B-2: una raíz <c>\\?\GLOBALROOT\Device\&lt;dispositivo&gt;\…</c> cuyo dispositivo no sea
/// <c>HarddiskVolumeShadowCopy&lt;n&gt;</c> ni <c>HarddiskVolume&lt;n&gt;</c> (<c>n</c> solo dígitos ASCII, sin ceros a la
/// izquierda), o una instantánea que no esté entre las creadas para esta ejecución.</description></item>
/// <item><description>I-4: una exclusión obligatoria vacía o con algún segmento vacío, <c>.</c>, <c>..</c> o no
/// válido; nunca se descarta en silencio.</description></item>
/// </list>
/// </remarks>
internal sealed class SourceRoots
{
    private const string Win32Prefix = @"\\?\";

    private static readonly System.Buffers.SearchValues<char> InvalidSegmentChars = System.Buffers.SearchValues.Create(":/\0*?\"<>|");

    private readonly SourceRoot[] _roots;

    /// <param name="sources">Orígenes de la ejecución.</param>
    /// <param name="snapshotDevices">
    /// Dispositivos de las instantáneas VSS creadas para esta ejecución (<c>HarddiskVolumeShadowCopy&lt;n&gt;</c>); ninguno si
    /// la copia es en vivo.
    /// </param>
    public SourceRoots(IReadOnlyList<BackupSource> sources, IReadOnlyCollection<string>? snapshotDevices = null)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var allowed = new HashSet<string>(snapshotDevices ?? [], StringComparer.OrdinalIgnoreCase);
        var roots = new List<SourceRoot>(sources.Count);
        foreach (var source in sources)
        {
            ArgumentNullException.ThrowIfNull(source, nameof(sources));
            roots.Add(Create(source, allowed));
        }

        // La más larga primero, por si un origen está dentro de otro.
        _roots = [.. roots.OrderByDescending(r => r.ChildBase.Length)];
    }

    public IReadOnlyList<SourceRoot> Roots => _roots;

    public ResolvedSourcePath Resolve(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        foreach (var root in _roots)
        {
            if (string.Equals(path, root.ReadPath, StringComparison.OrdinalIgnoreCase)
                || string.Equals(path, root.ChildBase, StringComparison.OrdinalIgnoreCase))
            {
                return new ResolvedSourcePath(root, [], path);
            }

            if (path.Length > root.ChildBase.Length + 1
                && path.StartsWith(root.ChildBase, StringComparison.OrdinalIgnoreCase)
                && path[root.ChildBase.Length] == '\\')
            {
                var relative = path[(root.ChildBase.Length + 1)..].Split('\\');
                foreach (var segment in relative)
                {
                    if (!IsValidSegment(segment))
                    {
                        throw InvalidPath();
                    }
                }

                return new ResolvedSourcePath(root, relative, path);
            }
        }

        throw InvalidPath();
    }

    /// <summary>Ruta de un hijo enumerado, tal como se devuelve al motor en <see cref="SourceEntry.FullPath"/>.</summary>
    public static string ChildPath(ResolvedSourcePath directory, string name) =>
        directory.IsRoot ? directory.Root.ChildBase + "\\" + name : directory.Path + "\\" + name;

    public static bool IsValidSegment(string segment) =>
        segment.Length > 0 && segment is not "." and not ".." && segment.AsSpan().IndexOfAny(InvalidSegmentChars) < 0;

    /// <summary>Comparación de rutas por segmentos completos, ordinal sin distinguir mayúsculas (§8.2).</summary>
    public static bool SegmentsEqual(string left, string right)
    {
        var a = TrimTrailingSeparators(left).Split('\\');
        var b = TrimTrailingSeparators(right).Split('\\');
        if (a.Length != b.Length)
        {
            return false;
        }

        for (var i = 0; i < a.Length; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// ¿Es <paramref name="final"/> la ruta <paramref name="rootFinal"/> seguida de <paramref name="relative"/>? La raíz se compara
    /// sin distinguir mayúsculas (como <see cref="SegmentsEqual"/>); la parte relativa, <b>ordinal distinguiendo mayúsculas</b>:
    /// sus nombres salen de la enumeración tal como están en disco y, en una carpeta que distingue mayúsculas, <c>A</c> y
    /// <c>a</c> son elementos distintos (§8.2).
    /// </summary>
    public static bool FinalPathMatches(string final, string rootFinal, IReadOnlyList<string> relative)
    {
        ArgumentNullException.ThrowIfNull(relative);
        var actual = TrimTrailingSeparators(final).Split('\\');
        var root = TrimTrailingSeparators(rootFinal).Split('\\');
        if (actual.Length != root.Length + relative.Count)
        {
            return false;
        }

        for (var i = 0; i < root.Length; i++)
        {
            if (!string.Equals(actual[i], root[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        for (var i = 0; i < relative.Count; i++)
        {
            if (!string.Equals(actual[root.Length + i], relative[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    public static string Join(string basePath, IReadOnlyList<string> relative) =>
        TrimTrailingSeparators(basePath) + "\\" + string.Join('\\', relative);

    public static string LastSegment(string path)
    {
        var trimmed = TrimTrailingSeparators(path);
        var index = trimmed.LastIndexOf('\\');
        return index < 0 ? trimmed : trimmed[(index + 1)..];
    }

    internal static string TrimTrailingSeparators(string path) => path.TrimEnd('\\');

    private static SourceAccessException InvalidPath() =>
        new(RunIssueCode.PathInvalid, "La ruta no pertenece a ningún origen de la copia o tiene segmentos no válidos.");

    /// <summary>B-2: mismas reglas que <see cref="SourceOpen.IsAllowedDevice"/>.</summary>
    internal static bool IsAllowedDevice(string device, out bool isShadowCopy) => SourceOpen.IsAllowedDevice(device, out isShadowCopy);

    private static SourceRoot Create(BackupSource source, HashSet<string> snapshotDevices)
    {
        var readPath = source.ReadPath;
        ArgumentException.ThrowIfNullOrEmpty(readPath, nameof(source));
        var childBase = TrimTrailingSeparators(readPath);
        string body; // la ruta sin el prefijo \\?\ ni \\.\
        SourceRootKind kind;
        string expected;
        string[] fixedSegments; // segmentos que no son nombres (letra, GLOBALROOT\Device\X, Volume{…})

        if (readPath.StartsWith(Win32Prefix, StringComparison.Ordinal) || readPath.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            body = readPath[4..];
        }
        else if (readPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new ArgumentException($"The read path of source '{source.Alias}' is not local.", nameof(source));
        }
        else
        {
            body = readPath;
        }

        var segments = TrimTrailingSeparators(body).Split('\\');
        if (segments.Length >= 1 && segments[0].Length == 2 && char.IsAsciiLetter(segments[0][0]) && segments[0][1] == ':')
        {
            kind = SourceRootKind.DriveLetter;
            fixedSegments = segments[..1];
            expected = Win32Prefix + JoinVolume(segments);
        }
        else if (segments.Length >= 1 && segments[0].Equals("GLOBALROOT", StringComparison.OrdinalIgnoreCase))
        {
            if (segments.Length < 3 || !segments[1].Equals("Device", StringComparison.OrdinalIgnoreCase)
                || !IsAllowedDevice(segments[2], out var isShadowCopy))
            {
                throw new ArgumentException($"The read path of source '{source.Alias}' names a device that is not a volume or a shadow copy.", nameof(source));
            }

            if (isShadowCopy && !snapshotDevices.Contains(segments[2]))
            {
                throw new ArgumentException($"The read path of source '{source.Alias}' names a shadow copy that was not created for this run.", nameof(source));
            }

            kind = SourceRootKind.Device;
            fixedSegments = segments[..3];
            expected = "\\" + JoinVolume(segments[1..]);
        }
        else if (segments.Length >= 1 && IsVolumeGuid(segments[0]))
        {
            kind = SourceRootKind.VolumeGuid;
            fixedSegments = segments[..1];
            expected = Win32Prefix + JoinVolume(segments);
        }
        else
        {
            throw new ArgumentException($"The read path of source '{source.Alias}' is not a local volume path.", nameof(source));
        }

        foreach (var segment in segments[fixedSegments.Length..])
        {
            if (!IsValidSegment(segment))
            {
                throw new ArgumentException($"The read path of source '{source.Alias}' has invalid segments.", nameof(source));
            }
        }

        // La raíz del volumen se abre con la barra final (\??\C:\); sin ella se abriría el dispositivo del volumen.
        var volumeRoot = kind == SourceRootKind.Device
            ? "\\" + string.Join('\\', fixedSegments[1..]) + "\\"
            : "\\??\\" + string.Join('\\', fixedSegments) + "\\";
        var rootComponents = segments[fixedSegments.Length..];

        ArgumentNullException.ThrowIfNull(source.MandatoryExclusions, nameof(source));
        var exclusions = new List<string[]>(source.MandatoryExclusions.Count);
        foreach (var exclusion in source.MandatoryExclusions)
        {
            exclusions.Add(ParseExclusion(source, exclusion));
        }

        return new SourceRoot(source, readPath, childBase, volumeRoot, rootComponents, kind, expected, exclusions);
    }

    /// <summary>
    /// I-4: segmentos de una exclusión obligatoria (separador <c>\</c>, o <c>/</c> como el motor). Vacía, o con un segmento
    /// vacío, <c>.</c>, <c>..</c> o no válido, es un error de programación: una exclusión perdida dejaría leer la carpeta de
    /// datos.
    /// </summary>
    private static string[] ParseExclusion(BackupSource source, string? exclusion)
    {
        if (string.IsNullOrEmpty(exclusion))
        {
            throw new ArgumentException($"A mandatory exclusion of source '{source.Alias}' is empty.", nameof(source));
        }

        var parts = exclusion.Split(['\\', '/']);
        foreach (var part in parts)
        {
            if (!IsValidSegment(part))
            {
                throw new ArgumentException($"A mandatory exclusion of source '{source.Alias}' has an empty or invalid segment.", nameof(source));
            }
        }

        return parts;
    }

    /// <summary>Une los segmentos; si solo está el volumen, con la barra final (como da <c>GetFinalPathNameByHandle</c>).</summary>
    private static string JoinVolume(string[] segments) =>
        segments.Length == 1 ? segments[0] + "\\" : string.Join('\\', segments);

    private static bool IsVolumeGuid(string segment) => SourceOpen.IsVolumeGuid(segment);
}
