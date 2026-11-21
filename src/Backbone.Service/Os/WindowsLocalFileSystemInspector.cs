using System.Buffers.Binary;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Backbone.Contracts;
using Backbone.Service.Jobs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;
using static Backbone.Service.Os.NativeMethods;
using R = Backbone.Service.Os.NativeMethods.Resolution;

namespace Backbone.Service.Os;

/// <summary>
/// <see cref="ILocalFileSystemInspector"/> sobre la API de Windows con la <b>resolución controlada</b> de ADR-20 (§8.3,
/// revisión de seguridad de HU-07, ALTA-1): el servicio nunca deja que el sistema siga un punto de reanálisis al abrir la ruta
/// de un origen.
/// </summary>
/// <remarks>
/// <list type="number">
/// <item><b>Raíz</b>: <c>\??\X:\</c> (o <c>\??\Volume{guid}\</c>) con <c>NtCreateFile</c>; sobre ese handle,
/// <c>FileFsDeviceInformation</c> tiene que ser <c>FILE_DEVICE_DISK</c> sin <c>FILE_REMOTE_DEVICE</c> ni
/// <c>FILE_REMOVABLE_MEDIA</c>. Antes, para una letra, <c>GetDriveType</c> (que la resuelve con el mapa de dispositivos del
/// proceso, sin abrir nada) descarta una unidad de red sin llegar a abrirla.</item>
/// <item><b>Cada componente</b> se abre relativo al handle del padre (<c>OBJECT_ATTRIBUTES.RootDirectory</c>, un solo
/// componente, <c>OBJ_CASE_INSENSITIVE</c>) con <c>FILE_OPEN | FILE_OPEN_REPARSE_POINT | FILE_OPEN_FOR_BACKUP_INTENT</c>,
/// <c>FILE_READ_ATTRIBUTES | SYNCHRONIZE</c> y sin <c>FILE_SHARE_DELETE</c>. El padre sigue abierto hasta abrir el hijo.</item>
/// <item><b>Clasificación</b> con <c>FileAttributeTagInfo</c> sobre el handle: sin punto de reanálisis o de la lista blanca
/// (nube totalmente poblada, WOF, Dedup), se sigue; un archivo da <see cref="LocalFolderStatus.NotFound"/>; un sustituto de
/// nombre se lee con <c>FSCTL_GET_REPARSE_POINT</c> sobre ese handle y solo se continúa hacia <c>\??\X:\…</c> o
/// <c>\??\Volume{guid}\…</c> (relativos compuestos léxicamente); cualquier otro destino da
/// <see cref="LocalFolderStatus.NotLocal"/> <b>sin abrirlo</b>; solo en la nube, no poblado o etiqueta no legible da
/// <see cref="LocalFolderStatus.Unreadable"/>.</item>
/// <item>Como mucho <see cref="MaxReparseHops"/> saltos.</item>
/// <item><b>Defensa en profundidad</b>: <c>GetFinalPathNameByHandle</c> (<c>VOLUME_NAME_GUID</c>) del handle final tiene que
/// coincidir con la ruta compuesta, y la ruta <c>VOLUME_NAME_DOS</c> no puede ser UNC.</item>
/// </list>
/// Cada operación que puede llegar a un filtro pasa por <see cref="SourceIoGuard.Run{T}"/> (plazo de §8.2).
/// </remarks>
internal sealed partial class WindowsLocalFileSystemInspector(ILogger<WindowsLocalFileSystemInspector>? logger = null) : ILocalFileSystemInspector
{
    /// <summary>Tope de sustitutos de nombre por resolución, el mismo que impone el sistema a una ruta.</summary>
    internal const int MaxReparseHops = 63;

    private const string LongPrefix = @"\\?\";
    private const string LongUncPrefix = @"\\?\UNC\";
    private const int MaxPathPlusOne = 261;

    private readonly ILogger _logger = logger ?? NullLogger<WindowsLocalFileSystemInspector>.Instance;

    /// <summary>Tope de caracteres de la ruta compuesta durante la resolución (§8.3 paso 4, B-4).</summary>
    internal const int MaxComposedPathLength = 32_767;

    /// <summary>Tope de aperturas por resolución, contando raíces de volumen y componentes de todos los saltos (§8.3 paso 4).</summary>
    internal const int MaxOpensPerResolution = 2_048;

    public LocalFolderInfo ResolveFolder(string normalizedPath, SourceIoGuard guard)
    {
        ArgumentNullException.ThrowIfNull(guard);
        if (SourcePath.TryNormalize(normalizedPath, out var normalized) != SourcePathCheck.Ok || !string.Equals(normalized, normalizedPath, StringComparison.Ordinal))
        {
            return new LocalFolderInfo(LocalFolderStatus.InvalidPath);
        }

        guard.CurrentPath = normalized;
        guard.ResetSlowBudget();
        var outcome = Resolve(normalized, guard);
        using var resolved = outcome.Handle;
        return outcome.Failure ?? Describe(outcome, guard);
    }

    public RealDirectoryScan FindRealDirectories(string directory, string namePrefix, int maxNames, SourceIoGuard guard)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxNames);
        ArgumentNullException.ThrowIfNull(guard);
        if (SourcePath.TryNormalize(directory, out var normalized) != SourcePathCheck.Ok)
        {
            throw new IOException($"The folder '{directory}' is not a normalized local path.");
        }

        guard.CurrentPath = normalized;
        guard.ResetSlowBudget();
        var outcome = Resolve(normalized, guard);
        using var resolved = outcome.Handle;
        if (outcome.Failure is { } failure || resolved is null)
        {
            throw new IOException($"The folder '{normalized}' could not be resolved ({outcome.Failure?.Status}: {outcome.Failure?.Detail}).");
        }

        // Handle con acceso de enumeración sobre el mismo objeto, sin volver a resolver la ruta: apertura relativa al handle
        // resuelto con nombre vacío (abre el propio objeto de RootDirectory).
        var reopen = OpenNt(string.Empty, resolved, guard, out var listingHandle, R.FileListDirectory | R.Synchronize);
        using var listing = listingHandle;
        if (reopen < 0)
        {
            throw new IOException(string.Create(CultureInfo.InvariantCulture, $"The folder '{normalized}' could not be opened for listing (NTSTATUS 0x{reopen:X8})."));
        }

        var names = new List<string>();
        var found = 0;
        var buffer = new byte[64 * 1024];
        var infoClass = FileFullDirectoryRestartInfo;
        while (true)
        {
            var currentClass = infoClass;
            var (ok, code) = guard.Run(_ =>
            {
                var result = GetFileInformationByHandleExBuffer(listing, currentClass, buffer, (uint)buffer.Length);
                return (result, result ? 0 : Marshal.GetLastPInvokeError());
            });
            if (!ok)
            {
                if (code == ErrorNoMoreFiles)
                {
                    break;
                }

                throw new IOException($"The folder '{normalized}' could not be enumerated.", new Win32Exception(code));
            }

            infoClass = FileFullDirectoryInfo;
            foreach (var name in DirectoryListing.ReadNames(buffer))
            {
                if (name is "." or ".." || !name.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!IsRealDirectoryOrUnknown(listing, name, guard))
                {
                    continue;
                }

                found++;
                if (found > maxNames)
                {
                    // MEDIA-1: con una más que el máximo ya se sabe que hay demasiadas; no se abren más entradas.
                    return new RealDirectoryScan(names, LimitExceeded: true);
                }

                names.Add(name);
            }
        }

        return new RealDirectoryScan(names, LimitExceeded: false);
    }

    /// <summary>Sin el prefijo <c>\\?\</c> (y <c>\\?\UNC\</c> como <c>\\</c>).</summary>
    internal static string StripLongPrefix(string path)
    {
        if (path.StartsWith(LongUncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[LongUncPrefix.Length..];
        }

        return path.StartsWith(LongPrefix, StringComparison.Ordinal) ? path[LongPrefix.Length..] : path;
    }

    // ---- Resolución controlada (§8.3, pasos 1 a 4 y 7) ----

    private sealed record Outcome(SafeFileHandle? Handle, LocalFolderInfo? Failure, string RootGuidPath, IReadOnlyList<string> Components);

    private static Outcome Fail(LocalFolderStatus status, string detail) => new(null, new LocalFolderInfo(status, Detail: detail), string.Empty, []);

    private Outcome Resolve(string normalized, SourceIoGuard guard)
    {
        var volume = normalized[..2]; // "X:"
        var pending = new List<string>(SourcePath.Components(normalized));
        var hops = 0;
        var opens = new OpenCounter();
        while (true)
        {
            if (ComposedLength(volume, pending) > MaxComposedPathLength)
            {
                return Fail(LocalFolderStatus.Unreadable, $"the composed path exceeds {MaxComposedPathLength} characters");
            }

            Outcome step;
            (string Volume, List<string> Components)? link;
            try
            {
                step = Walk(volume, pending, guard, opens, out link);
            }
            catch (OpenLimitExceededException)
            {
                return Fail(LocalFolderStatus.Unreadable, $"more than {MaxOpensPerResolution} opens");
            }
            if (link is null)
            {
                return step;
            }

            // Un sustituto de nombre admitido: se sigue con su destino más los componentes que quedaban, desde la raíz del
            // volumen del destino (nunca se pasa el destino al sistema como ruta completa).
            hops++;
            if (hops > MaxReparseHops)
            {
                return Fail(LocalFolderStatus.Unreadable, $"more than {MaxReparseHops} reparse hops");
            }

            volume = link.Value.Volume;
            pending = link.Value.Components;
        }
    }

    /// <summary>
    /// Abre la raíz de <paramref name="volume"/> y recorre <paramref name="components"/> uno a uno. Si encuentra un sustituto
    /// de nombre admitido, devuelve en <paramref name="link"/> el volumen y los componentes con que seguir.
    /// </summary>
    private Outcome Walk(string volume, List<string> components, SourceIoGuard guard, OpenCounter opens, out (string Volume, List<string> Components)? link)
    {
        link = null;
        var rootOutcome = OpenVolumeRoot(volume, guard, opens, out var current);
        if (rootOutcome is not null)
        {
            return rootOutcome;
        }

        try
        {
            if (!TryGetFinalPath(current!, FileNameNormalizedVolumeNameGuid, guard, out var rootGuidPath))
            {
                return Fail(LocalFolderStatus.Unreadable, "the volume GUID path of the root could not be obtained");
            }

            // B-1 (revisión final de HU-07): tiene que ser exactamente la raíz de un volumen, \\?\Volume{guid}\, sin nada
            // detrás (una letra definida con subst o DefineDosDevice hacia una subcarpeta no lo es).
            if (!IsExactVolumeRoot(rootGuidPath))
            {
                return Fail(LocalFolderStatus.NotLocal, "the drive is not the root of a volume");
            }

            var walked = new List<string>(components.Count);
            for (var i = 0; i < components.Count; i++)
            {
                var name = components[i];
                var status = OpenRelative(current!, name, guard, out var child, opens);
                if (status < 0)
                {
                    return Fail(MapOpenStatus(status), string.Create(CultureInfo.InvariantCulture, $"NTSTATUS 0x{status:X8} opening a component"));
                }

                var keepChild = false;
                try
                {
                    var tag = ReadAttributeTag(child, guard, out var tagError);
                    if (tag is null)
                    {
                        return Fail(LocalFolderStatus.Unreadable, $"attributes could not be read (Win32 error {tagError})");
                    }

                    var (attributes, reparseTag) = tag.Value;
                    if ((attributes & FileAttributeReparsePoint) != 0 && (reparseTag & R.ReparseTagNameSurrogateBit) != 0)
                    {
                        var target = ReadLinkTarget(child, reparseTag, guard, out var readFailure);
                        if (target is null)
                        {
                            return readFailure!;
                        }

                        var next = ComposeTarget(target.Value, volume, walked);
                        if (next is null)
                        {
                            LogLinkRejected(_logger, UntrustedText.Describe(target.Value.Name));
                            return Fail(LocalFolderStatus.NotLocal, "link target is not a local volume: " + UntrustedText.Describe(target.Value.Name));
                        }

                        var (nextVolume, nextComponents) = next.Value;
                        nextComponents.AddRange(components.Skip(i + 1));
                        link = (nextVolume, nextComponents);
                        return new Outcome(null, null, string.Empty, []);
                    }

                    if (IsNotTraversable(attributes, reparseTag, out var reason))
                    {
                        return Fail(LocalFolderStatus.Unreadable, reason);
                    }

                    if ((attributes & FileAttributeDirectory) == 0)
                    {
                        return Fail(LocalFolderStatus.NotFound, "a component is a file");
                    }

                    walked.Add(name);
                    current!.Dispose();
                    current = child;
                    keepChild = true;
                }
                finally
                {
                    if (!keepChild)
                    {
                        child.Dispose();
                    }
                }
            }

            var final = current;
            current = null;
            return new Outcome(final, null, rootGuidPath, walked);
        }
        finally
        {
            current?.Dispose();
        }
    }

    private static Outcome? OpenVolumeRoot(string volume, SourceIoGuard guard, OpenCounter opens, out SafeFileHandle? root)
    {
        root = null;
        if (volume.Length == 2)
        {
            // Unidad de red o inexistente: se descarta sin abrir nada (GetDriveType usa el mapa de dispositivos del proceso).
            switch (guard.Run(_ => GetDriveType(volume + @"\")))
            {
                case DriveRemote:
                    return Fail(LocalFolderStatus.NotLocal, "network drive");
                case DriveNoRootDir:
                    return Fail(LocalFolderStatus.NotFound, "the drive does not exist");
            }
        }

        var status = OpenNt(@"\??\" + volume + @"\", null, guard, out var handle, opens: opens);
        if (status < 0)
        {
            return Fail(MapOpenStatus(status), string.Create(CultureInfo.InvariantCulture, $"NTSTATUS 0x{status:X8} opening the volume root"));
        }

        var device = guard.Run(_ =>
        {
            var s = R.NtQueryVolumeInformationFile(handle, out IoStatusBlock ioStatus, out var info, (uint)Marshal.SizeOf<R.FileFsDeviceInformation>(), R.FileFsDeviceInformationClass);
            return (Status: s, Info: info);
        });
        if (device.Status < 0)
        {
            handle.Dispose();
            return Fail(LocalFolderStatus.Unreadable, string.Create(CultureInfo.InvariantCulture, $"NTSTATUS 0x{device.Status:X8} querying the device"));
        }

        if (device.Info.DeviceType != R.FileDeviceDisk || (device.Info.Characteristics & (R.FileRemoteDevice | R.FileRemovableMedia)) != 0)
        {
            handle.Dispose();
            return Fail(LocalFolderStatus.NotLocal, string.Create(CultureInfo.InvariantCulture, $"device type 0x{device.Info.DeviceType:X}, characteristics 0x{device.Info.Characteristics:X}"));
        }

        root = handle;
        return null;
    }

    /// <summary>Pasos 5 y 6: ruta final sobre el handle (defensa en profundidad) y sistema de archivos.</summary>
    private static LocalFolderInfo Describe(Outcome outcome, SourceIoGuard guard)
    {
        var handle = outcome.Handle!;
        if (!TryGetFinalPath(handle, FileNameNormalizedVolumeNameDos, guard, out var dosPath))
        {
            // Volumen sin letra (solo montado por GUID): se usa la ruta de volumen.
            if (!TryGetFinalPath(handle, FileNameNormalizedVolumeNameGuid, guard, out dosPath))
            {
                return new LocalFolderInfo(LocalFolderStatus.Unreadable, Detail: "the final path could not be obtained");
            }
        }

        if (dosPath.StartsWith(LongUncPrefix, StringComparison.OrdinalIgnoreCase) || dosPath.StartsWith(@"\\", StringComparison.Ordinal) && !dosPath.StartsWith(LongPrefix, StringComparison.Ordinal))
        {
            return new LocalFolderInfo(LocalFolderStatus.NotLocal, Detail: "the final path is a network path");
        }

        if (!TryGetFinalPath(handle, FileNameNormalizedVolumeNameGuid, guard, out var guidPath)
            || !SameSegments(guidPath, outcome.RootGuidPath, outcome.Components))
        {
            return new LocalFolderInfo(LocalFolderStatus.Unreadable, Detail: "the final path does not match the resolved path");
        }

        var fileSystem = new char[MaxPathPlusOne];
        var fileSystemName = guard.Run(_ => GetVolumeInformationByHandle(handle, 0, 0, 0, 0, 0, fileSystem, (uint)fileSystem.Length))
            ? new string(fileSystem, 0, Array.IndexOf(fileSystem, '\0') is var end and >= 0 ? end : fileSystem.Length)
            : string.Empty;

        return new LocalFolderInfo(LocalFolderStatus.Ok, StripLongPrefix(dosPath), fileSystemName, GuidPath: guidPath);
    }

    private static bool SameSegments(string finalGuidPath, string rootGuidPath, IReadOnlyList<string> components)
    {
        var actual = StripLongPrefix(finalGuidPath).Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var root = StripLongPrefix(rootGuidPath).Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (actual.Length != root.Length + components.Count)
        {
            return false;
        }

        for (var i = 0; i < actual.Length; i++)
        {
            var expected = i < root.Length ? root[i] : components[i - root.Length];
            if (!string.Equals(actual[i], expected, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>§8.3 paso 7: no existe → <see cref="LocalFolderStatus.NotFound"/>; acceso denegado y el resto → <see cref="LocalFolderStatus.Unreadable"/>.</summary>
    private static LocalFolderStatus MapOpenStatus(int status) => status switch
    {
        R.StatusObjectNameNotFound or R.StatusObjectPathNotFound or R.StatusNoSuchFile or R.StatusNotADirectory => LocalFolderStatus.NotFound,
        _ => LocalFolderStatus.Unreadable,
    };

    /// <summary>
    /// Solo en la nube, carpeta de nube no poblada u otra etiqueta que no es de la lista blanca (ADR-19): no se atraviesa ni
    /// se abre nada debajo, para no despertar a un proveedor ajeno.
    /// </summary>
    private static bool IsNotTraversable(uint attributes, uint tag, out string reason)
    {
        reason = string.Empty;
        if ((attributes & (R.FileAttributeRecallOnOpen | R.FileAttributeRecallOnDataAccess)) != 0)
        {
            reason = "cloud-only (recall attributes)";
            return true;
        }

        if ((attributes & FileAttributeReparsePoint) == 0)
        {
            return false;
        }

        var isCloud = (tag & R.IoReparseTagCloudMask) == R.IoReparseTagCloud;
        if ((attributes & R.FileAttributeOffline) != 0 && (isCloud || tag is R.IoReparseTagHsm or R.IoReparseTagHsm2))
        {
            reason = "offline";
            return true;
        }

        if (isCloud)
        {
            uint state;
            try
            {
                state = R.CfGetPlaceholderStateFromAttributeTag(attributes, tag);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                reason = "cloud placeholder state unavailable";
                return true;
            }

            if (state == R.CfPlaceholderStateInvalid || (state & (R.CfPlaceholderStatePartial | R.CfPlaceholderStatePartiallyOnDisk)) != 0)
            {
                reason = "cloud folder not fully populated";
                return true;
            }

            return false;
        }

        if (tag is R.IoReparseTagWof or R.IoReparseTagDedup)
        {
            return false;
        }

        reason = string.Create(CultureInfo.InvariantCulture, $"reparse tag 0x{tag:X8} is not readable");
        return true;
    }

    // ---- Destinos de los enlaces ----

    /// <summary>Destino de un sustituto de nombre (<c>SubstituteName</c>) y si es relativo (<c>SYMLINK_FLAG_RELATIVE</c>).</summary>
    internal readonly record struct LinkTarget(string Name, bool Relative);

    private LinkTarget? ReadLinkTarget(SafeFileHandle handle, uint tag, SourceIoGuard guard, out Outcome? failure)
    {
        failure = null;
        if (tag is not (R.IoReparseTagMountPoint or R.IoReparseTagSymlink))
        {
            var detail = string.Create(CultureInfo.InvariantCulture, $"name surrogate with unknown tag 0x{tag:X8}");
            LogLinkRejected(_logger, detail);
            failure = Fail(LocalFolderStatus.NotLocal, detail);
            return null;
        }

        var buffer = new byte[R.MaximumReparseDataBufferSize];
        var (ok, returned, error) = guard.Run(_ =>
        {
            var result = R.DeviceIoControl(handle, R.FsctlGetReparsePoint, 0, 0, buffer, (uint)buffer.Length, out var bytes, 0);
            return (result, bytes, result ? 0 : Marshal.GetLastPInvokeError());
        });
        if (!ok)
        {
            failure = Fail(LocalFolderStatus.Unreadable, $"FSCTL_GET_REPARSE_POINT failed (Win32 error {error})");
            return null;
        }

        var target = ParseReparseBuffer(buffer.AsSpan(0, (int)Math.Min(returned, (uint)buffer.Length)), tag);
        if (target is null)
        {
            LogLinkRejected(_logger, "malformed reparse data");
            failure = Fail(LocalFolderStatus.NotLocal, "malformed reparse data");
        }

        return target;
    }

    /// <summary>
    /// <c>REPARSE_DATA_BUFFER</c>: etiqueta (0), <c>ReparseDataLength</c> (4) y, en los dos tipos, <c>SubstituteNameOffset</c>
    /// (8) y <c>SubstituteNameLength</c> (10). <c>PathBuffer</c> empieza en 16 (<i>junction</i>) o en 20 tras <c>Flags</c>
    /// (16, enlace simbólico).
    /// </summary>
    internal static LinkTarget? ParseReparseBuffer(ReadOnlySpan<byte> data, uint expectedTag)
    {
        if (data.Length < 16 || BinaryPrimitives.ReadUInt32LittleEndian(data) != expectedTag)
        {
            return null;
        }

        var isSymlink = expectedTag == R.IoReparseTagSymlink;
        var pathBuffer = isSymlink ? 20 : 16;
        if (data.Length < pathBuffer)
        {
            return null;
        }

        int offset = BinaryPrimitives.ReadUInt16LittleEndian(data[8..]);
        int length = BinaryPrimitives.ReadUInt16LittleEndian(data[10..]);
        var relative = isSymlink && (BinaryPrimitives.ReadUInt32LittleEndian(data[16..]) & R.SymlinkFlagRelative) != 0;
        if (offset % 2 != 0 || length % 2 != 0 || length == 0 || pathBuffer + offset + length > data.Length)
        {
            return null;
        }

        var name = new string(MemoryMarshal.Cast<byte, char>(data.Slice(pathBuffer + offset, length)));
        return new LinkTarget(name, relative);
    }

    /// <summary>
    /// §8.3 paso 3: volumen y componentes con que seguir, o <see langword="null"/> si el destino no es un volumen local
    /// (<c>\??\UNC\…</c>, <c>\Device\…</c>, <c>\RPC Control\…</c>, <c>\GLOBALROOT\…</c>, <c>\??\GLOBALROOT\…</c>,
    /// <c>\\?\…</c>, <c>\??\</c> seguido de otra cosa, <c>:</c> fuera de la letra, vacío o mal formado). Un relativo se
    /// compone léxicamente con la carpeta que contiene el enlace (<paramref name="walked"/>); si sube por encima de la raíz,
    /// se rechaza.
    /// </summary>
    internal static (string Volume, List<string> Components)? ComposeTarget(LinkTarget target, string currentVolume, IReadOnlyList<string> walked)
    {
        var name = target.Name;
        if (name.Length == 0 || name.Contains('\0', StringComparison.Ordinal))
        {
            return null;
        }

        if (target.Relative)
        {
            if (name.StartsWith(@"\??\", StringComparison.Ordinal) || name.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return null;
            }

            var components = new List<string>(walked);
            var segments = name.Split('\\');
            var start = 0;
            if (name.StartsWith('\\'))
            {
                // Relativo a la raíz del volumen del enlace.
                components.Clear();
                start = 1;
            }

            for (var i = start; i < segments.Length; i++)
            {
                var segment = segments[i];
                if (segment.Length == 0 && i == segments.Length - 1)
                {
                    continue; // Barra final.
                }

                switch (segment)
                {
                    case ".":
                        continue;
                    case "..":
                        if (components.Count == 0)
                        {
                            return null; // Sube por encima de la raíz del volumen.
                        }

                        components.RemoveAt(components.Count - 1);
                        continue;
                }

                if (!SnapshotPaths.IsValidSegment(segment))
                {
                    return null;
                }

                components.Add(segment);
            }

            return (currentVolume, components);
        }

        if (!name.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            return null;
        }

        var rest = name[4..];
        string volume;
        string tail;
        if (rest.Length >= 2 && char.IsAsciiLetter(rest[0]) && rest[1] == ':' && (rest.Length == 2 || rest[2] == '\\'))
        {
            volume = char.ToUpperInvariant(rest[0]) + ":";
            tail = rest.Length > 3 ? rest[3..] : string.Empty;
        }
        else if (TryParseVolumeGuid(rest, out volume))
        {
            tail = rest.Length > volume.Length + 1 ? rest[(volume.Length + 1)..] : string.Empty;
        }
        else
        {
            return null;
        }

        if (tail.EndsWith('\\'))
        {
            tail = tail[..^1];
        }

        var result = new List<string>();
        if (tail.Length > 0)
        {
            foreach (var segment in tail.Split('\\'))
            {
                if (!SnapshotPaths.IsValidSegment(segment))
                {
                    return null;
                }

                result.Add(segment);
            }
        }

        return (volume, result);
    }

    /// <summary><c>Volume{xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx}</c> (GUID de 36 caracteres entre llaves) seguido de <c>\</c> o del final.</summary>
    private static bool TryParseVolumeGuid(string rest, out string volume)
    {
        const string Prefix = "Volume{";
        const int Length = 7 + 36 + 1;
        volume = string.Empty;
        if (rest.Length < Length || !rest.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) || rest[Length - 1] != '}'
            || (rest.Length > Length && rest[Length] != '\\')
            || !Guid.TryParseExact(rest.AsSpan(Prefix.Length, 36), "D", out _))
        {
            return false;
        }

        volume = string.Concat("Volume", rest.AsSpan(6, Length - 6));
        return true;
    }

    // ---- Aperturas por handle ----

    private static int OpenRelative(SafeFileHandle parent, string name, SourceIoGuard guard, out SafeFileHandle handle, OpenCounter? opens = null) =>
        OpenNt(name, parent, guard, out handle, opens: opens);

    /// <summary>
    /// Apertura de solo lectura por <see cref="SourceOpen.Open"/> (<c>NtCreateFile</c> con <c>FILE_OPEN</c>,
    /// <c>FILE_OPEN_REPARSE_POINT | FILE_OPEN_FOR_BACKUP_INTENT | FILE_COMPLETE_IF_OPLOCKED</c>, SQOS de identificación): con
    /// <paramref name="parent"/>, <paramref name="name"/> es un solo componente relativo a esa carpeta ya abierta, o vacío para
    /// reabrir el propio objeto. Si vence el plazo, el handle que vuelva tarde se cierra.
    /// </summary>
    private static int OpenNt(string name, SafeFileHandle? parent, SourceIoGuard guard, out SafeFileHandle handle, uint access = R.FileReadAttributes | R.Synchronize, OpenCounter? opens = null)
    {
        if (opens is not null && ++opens.Count > MaxOpensPerResolution)
        {
            throw new OpenLimitExceededException();
        }

        using var opened = guard.Run(_ =>
        {
            var s = SourceOpen.Open(name, parent, access, R.FileShareRead | R.FileShareWrite, R.FileOpenReparsePoint | R.FileSynchronousIoNonAlert, out var h);
            return new OpenResult(s, h);
        });
        handle = opened.Take();
        return opened.Status;
    }

    private static bool IsExactVolumeRoot(string guidPath) =>
        guidPath.Length == 4 + 44 + 1
        && guidPath.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase)
        && guidPath.EndsWith("}\\", StringComparison.Ordinal)
        && Guid.TryParseExact(guidPath.AsSpan(11, 36), "D", out _);

    private static int ComposedLength(string volume, List<string> components)
    {
        var length = volume.Length + 1;
        foreach (var component in components)
        {
            length += component.Length + 1;
        }

        return length;
    }

    /// <summary>Aperturas hechas en una resolución (§8.3 paso 4).</summary>
    private sealed class OpenCounter
    {
        public int Count { get; set; }
    }

    /// <summary>Se han superado <see cref="MaxOpensPerResolution"/> aperturas.</summary>
    private sealed class OpenLimitExceededException : Exception
    {
        public OpenLimitExceededException()
            : base("Too many opens in one resolution.")
        {
        }
    }

    /// <summary>Resultado de una apertura que se libera si nadie lo recoge (p. ej. porque vuelve después del plazo).</summary>
    private sealed class OpenResult(int status, SafeFileHandle handle) : IDisposable
    {
        private SafeFileHandle? _handle = handle;

        public int Status => status;

        public SafeFileHandle Take()
        {
            var taken = _handle ?? throw new ObjectDisposedException(nameof(OpenResult));
            _handle = null;
            return taken;
        }

        public void Dispose() => _handle?.Dispose();
    }

    private static (uint Attributes, uint Tag)? ReadAttributeTag(SafeFileHandle handle, SourceIoGuard guard, out int error)
    {
        var (ok, info, code) = guard.Run(_ =>
        {
            var result = GetFileInformationByHandleEx(handle, FileAttributeTagInfo, out FileAttributeTagInformation tag, (uint)Marshal.SizeOf<FileAttributeTagInformation>());
            return (result, tag, result ? 0 : Marshal.GetLastPInvokeError());
        });
        error = code;
        return ok ? (info.FileAttributes, info.ReparseTag) : null;
    }

    /// <summary>
    /// Entrada de <paramref name="directory"/> abierta relativo a ella <b>sin seguir</b> puntos de reanálisis; sobre ese handle,
    /// ¿es una carpeta real (M-B)? Si no se puede abrir o consultar, se cuenta como carpeta (falla cerrado: como mucho se
    /// excluye algo de más). Si ha desaparecido entre la enumeración y la apertura, ya no está.
    /// </summary>
    private static bool IsRealDirectoryOrUnknown(SafeFileHandle directory, string name, SourceIoGuard guard)
    {
        var status = OpenRelative(directory, name, guard, out var handle);
        using (handle)
        {
            if (status < 0)
            {
                return MapOpenStatus(status) != LocalFolderStatus.NotFound;
            }

            var tag = ReadAttributeTag(handle, guard, out _);
            if (tag is null)
            {
                return true;
            }

            return (tag.Value.Attributes & FileAttributeReparsePoint) == 0 && (tag.Value.Attributes & FileAttributeDirectory) != 0;
        }
    }

    private static bool TryGetFinalPath(SafeFileHandle handle, uint flags, SourceIoGuard guard, out string path)
    {
        var buffer = new char[512];
        while (true)
        {
            var current = buffer;
            var length = guard.Run(_ => GetFinalPathNameByHandle(handle, current, (uint)current.Length, flags));
            if (length == 0)
            {
                path = string.Empty;
                return false;
            }

            if (length < buffer.Length)
            {
                path = new string(buffer, 0, (int)length);
                return true;
            }

            buffer = new char[length];
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Resolución de un origen: se rechaza un enlace cuyo destino no es un volumen local, sin abrirlo ({Target}).")]
    private static partial void LogLinkRejected(ILogger logger, string target);
}
