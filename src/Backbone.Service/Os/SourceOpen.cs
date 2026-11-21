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
/// <item><description>Con <c>RootDirectory</c>, el nombre es un solo componente (sin <c>\</c>) o vacío (reabre el propio
/// objeto). <c>OBJ_CASE_INSENSITIVE</c> y SQOS de identificación (<c>SecurityIdentification</c>, estático,
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

        if (!relative && name.Length == 0)
        {
            throw new InvalidOperationException("An absolute source open needs a name.");
        }
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
    public static unsafe int Open(string name, SafeHandle? parent, uint access, uint share, uint options, out SafeFileHandle handle)
    {
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
                    Attributes = R.ObjCaseInsensitive,
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
