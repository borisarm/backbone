using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using R = Backbone.Service.Os.NativeMethods.Resolution;
using S = Backbone.Service.Os.NativeMethods.SourceIo;

namespace Backbone.Service.Os;

/// <summary>
/// La única función de apertura de los hilos de lectura de orígenes (ADR-21, §8.2 "Token de lectura de los orígenes"): la
/// usan <c>SourceFileSystem</c> y la resolución controlada de ADR-20. Con <c>SeBackupPrivilege</c> activado en el token del
/// hilo, una apertura con <c>FILE_OPEN_FOR_BACKUP_INTENT</c> salta la DACL, así que aquí se comprueba, <b>antes</b> de llamar a
/// <c>NtCreateFile</c>, que solo se pide lectura.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>Acceso: subconjunto de <see cref="AllowedAccess"/> (bits literales: nunca <c>MAXIMUM_ALLOWED</c>,
/// <c>GENERIC_ALL</c>, <c>ACCESS_SYSTEM_SECURITY</c>, <c>DELETE</c>, <c>WRITE_DAC</c>, <c>WRITE_OWNER</c> ni escritura).</description></item>
/// <item><description>Disposición: siempre <c>FILE_OPEN</c>.</description></item>
/// <item><description>Opciones: nunca <c>FILE_DELETE_ON_CLOSE</c> ni <c>FILE_OPEN_BY_FILE_ID</c>; siempre
/// <c>FILE_OPEN_REPARSE_POINT</c> salvo en la reapertura de la lista blanca, y siempre <c>FILE_OPEN_FOR_BACKUP_INTENT</c> y
/// <c>FILE_COMPLETE_IF_OPLOCKED</c> (§8.2 "Oplocks").</description></item>
/// <item><description>Sin <c>RootDirectory</c>, solo la raíz de un volumen local o de una instantánea
/// (<see cref="IsAllowedAbsolutePath"/>).</description></item>
/// <item><description>Con <c>RootDirectory</c>, el nombre es un solo componente (sin <c>\</c>) o vacío (reabre el propio
/// objeto). <c>OBJ_CASE_INSENSITIVE</c> salvo en los componentes que salen de la enumeración (<c>exactCase</c>), y SQOS de identificación (<c>SecurityIdentification</c>, estático,
/// <c>EffectiveOnly</c>).</description></item>
/// </list>
/// Si algo no se cumple lanza <see cref="InvalidOperationException"/> (error de programación) sin abrir nada.
/// </remarks>
internal static class SourceOpen
{
    /// <summary><c>GENERIC_READ | FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES | SYNCHRONIZE</c>.</summary>
    public const uint AllowedAccess = NativeMethods.GenericRead | NativeMethods.FileListDirectory | NativeMethods.FileReadAttributes | NativeMethods.Synchronize;

    /// <summary>Opciones que siempre se añaden: <c>FILE_OPEN_FOR_BACKUP_INTENT | FILE_COMPLETE_IF_OPLOCKED</c>.</summary>
    public const uint RequiredOptions = R.FileOpenForBackupIntent | S.FileCompleteIfOplocked;

    /// <summary>Opciones prohibidas.</summary>
    public const uint ForbiddenOptions = S.FileDeleteOnClose | S.FileOpenByFileId;

    /// <summary>Comprueba los parámetros de una apertura; lanza <see cref="InvalidOperationException"/> si no son de solo lectura.</summary>
    public static void Validate(uint access, uint disposition, uint options, string name, bool relative)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (access == 0 || (access & ~AllowedAccess) != 0)
        {
            throw new InvalidOperationException($"A source open requested access 0x{access:X8}, outside 0x{AllowedAccess:X8}.");
        }

        if (disposition != R.FileOpen)
        {
            throw new InvalidOperationException($"A source open requested disposition {disposition}; only FILE_OPEN is allowed.");
        }

        if ((options & ForbiddenOptions) != 0)
        {
            throw new InvalidOperationException($"A source open requested forbidden options 0x{options & ForbiddenOptions:X8}.");
        }

        if (relative && name.Contains('\\', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A relative source open must name a single component.");
        }

        if (!relative && !IsAllowedAbsolutePath(name))
        {
            throw new InvalidOperationException("An absolute source open must name a local volume root or a shadow copy root.");
        }

        // Solo la reapertura del propio objeto (relativa, nombre vacío) puede seguir un punto de reanálisis: ya no se resuelve
        // ninguna ruta, el filtro de la etiqueta sirve el contenido del mismo archivo (§8.2, punto 2).
        if ((options & R.FileOpenReparsePoint) == 0 && !(relative && name.Length == 0))
        {
            throw new InvalidOperationException("A source open must use FILE_OPEN_REPARSE_POINT unless it reopens its own handle.");
        }
    }

    /// <summary>
    /// Las únicas rutas NT absolutas que se abren: la raíz de un volumen local <c>\??\X:\</c> o <c>\??\Volume{guid}\</c>, o
    /// la de un dispositivo <c>\Device\HarddiskVolume&lt;n&gt;\</c> / <c>\Device\HarddiskVolumeShadowCopy&lt;n&gt;\</c> (con
    /// las reglas de <see cref="IsAllowedDevice"/>). Nada debajo de la raíz: el resto se abre relativo al handle.
    /// </summary>
    public static bool IsAllowedAbsolutePath(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length < 2 || name[^1] != '\\')
        {
            return false;
        }

        var body = name.AsSpan(0, name.Length - 1);
        if (body.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            var volume = body[4..];
            return (volume.Length == 2 && char.IsAsciiLetter(volume[0]) && volume[1] == ':') || IsVolumeGuid(volume);
        }

        const string DevicePrefix = @"\Device\";
        if (body.StartsWith(DevicePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var device = body[DevicePrefix.Length..];
            return device.IndexOf('\\') < 0 && IsAllowedDevice(device.ToString(), out _);
        }

        return false;
    }

    /// <summary><c>Volume{guid}</c> con el GUID en formato <c>D</c>.</summary>
    public static bool IsVolumeGuid(ReadOnlySpan<char> segment) =>
        segment.Length == 44
        && segment.StartsWith("Volume{", StringComparison.OrdinalIgnoreCase)
        && segment[^1] == '}'
        && Guid.TryParseExact(segment[7..^1], "D", out _);

    /// <summary>
    /// B-2: <c>HarddiskVolumeShadowCopy&lt;n&gt;</c> o <c>HarddiskVolume&lt;n&gt;</c>, con <c>n</c> solo dígitos ASCII decimales,
    /// sin signo ni ceros a la izquierda.
    /// </summary>
    public static bool IsAllowedDevice(string device, out bool isShadowCopy)
    {
        ArgumentNullException.ThrowIfNull(device);
        const string ShadowCopy = "HarddiskVolumeShadowCopy";
        const string Volume = "HarddiskVolume";
        isShadowCopy = device.StartsWith(ShadowCopy, StringComparison.OrdinalIgnoreCase);
        var number = isShadowCopy
            ? device.AsSpan(ShadowCopy.Length)
            : device.StartsWith(Volume, StringComparison.OrdinalIgnoreCase) ? device.AsSpan(Volume.Length) : [];
        if (number.IsEmpty || (number.Length > 1 && number[0] == '0') || number.Length > 10)
        {
            return false;
        }

        foreach (var c in number)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// <c>NtCreateFile</c> de solo lectura. Devuelve el <c>NTSTATUS</c>; con éxito (<c>NT_SUCCESS</c>, que incluye
    /// <c>STATUS_OPLOCK_BREAK_IN_PROGRESS</c>) <paramref name="handle"/> es válido, si no se ha cerrado ya.
    /// </summary>
    /// <param name="name">Un componente (relativo a <paramref name="parent"/>), vacío (reabre <paramref name="parent"/>) o, sin padre, una ruta NT absoluta.</param>
    /// <param name="parent">Handle de la carpeta padre, o del propio objeto para reabrirlo; <see langword="null"/> para una ruta absoluta.</param>
    /// <param name="access">Acceso pedido (subconjunto de <see cref="AllowedAccess"/>).</param>
    /// <param name="share">Compartición.</param>
    /// <param name="options">Opciones además de <see cref="RequiredOptions"/>.</param>
    /// <param name="handle">Handle abierto.</param>
    /// <param name="exactCase">
    /// Sin <c>OBJ_CASE_INSENSITIVE</c>: para un componente que sale de la enumeración (nombre tal como está en disco), de modo
    /// que en una carpeta que distingue mayúsculas con <c>a</c> y <c>A</c> se abra exactamente ese (§8.2). Solo con
    /// <paramref name="parent"/> y nombre no vacío.
    /// </param>
    public static unsafe int Open(string name, SafeHandle? parent, uint access, uint share, uint options, out SafeFileHandle handle, bool exactCase = false)
    {
        if (exactCase && (parent is null || name.Length == 0))
        {
            throw new InvalidOperationException("Only a relative open of one component can be case sensitive.");
        }

        var allOptions = options | RequiredOptions;
        Validate(access, R.FileOpen, allOptions, name, parent is not null);
        var addedRef = false;
        try
        {
            parent?.DangerousAddRef(ref addedRef);
            var qos = new S.SecurityQualityOfService
            {
                Length = (uint)sizeof(S.SecurityQualityOfService),
                ImpersonationLevel = S.SecurityIdentification,
                ContextTrackingMode = S.SecurityStaticTracking,
                EffectiveOnly = 1,
            };
            fixed (char* buffer = name)
            {
                var objectName = new NativeMethods.UnicodeString
                {
                    Length = checked((ushort)(name.Length * sizeof(char))),
                    MaximumLength = checked((ushort)(name.Length * sizeof(char))),
                    Buffer = (nint)buffer,
                };
                var attributes = new NativeMethods.ObjectAttributes
                {
                    Length = (uint)sizeof(NativeMethods.ObjectAttributes),
                    RootDirectory = parent?.DangerousGetHandle() ?? 0,
                    ObjectName = (nint)(&objectName),
                    Attributes = exactCase ? 0 : R.ObjCaseInsensitive,
                    SecurityDescriptor = 0,
                    SecurityQualityOfService = (nint)(&qos),
                };
                var status = NativeMethods.NtCreateFile(out handle, access, in attributes, out _, 0, 0, share, R.FileOpen, allOptions, 0, 0);
                if (status < 0)
                {
                    handle.Dispose();
                }

                return status;
            }
        }
        finally
        {
            if (addedRef)
            {
                parent?.DangerousRelease();
            }
        }
    }
}
