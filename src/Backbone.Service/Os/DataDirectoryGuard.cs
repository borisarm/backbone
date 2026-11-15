using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using static Backbone.Service.Os.NativeMethods;

namespace Backbone.Service.Os;

/// <summary>
/// Comprobación de <c>%ProgramData%\Backbone</c> al arrancar (HT-03 I-1, RNF-07).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Todo se comprueba y se corrige sobre handles abiertos con <c>FILE_FLAG_BACKUP_SEMANTICS |
/// FILE_FLAG_OPEN_REPARSE_POINT</c> (nunca se sigue un enlace): atributos con <c>GetFileInformationByHandleEx</c>
/// (<c>FileAttributeTagInfo</c>) y seguridad con <c>GetSecurityInfo</c>/<c>SetSecurityInfo</c>. La raíz y cada carpeta en
/// curso se mantienen abiertas con acceso a datos y sin <c>FILE_SHARE_DELETE</c>: mientras se tratan sus hijos nadie puede
/// renombrarlas ni sustituirlas por un enlace.</item>
/// <item>Raíz inexistente: se crea de forma atómica con <c>CreateDirectoryW</c> y el descriptor de seguridad definitivo
/// (nunca existe con otra ACL). Si otro la crea entre medias (<c>ERROR_ALREADY_EXISTS</c>), se vuelve a evaluar, con un
/// número acotado de intentos.</item>
/// <item>Raíz que es punto de reanálisis (o no es una carpeta): se rechaza; nunca se toca el destino.</item>
/// <item>Raíz con propietario no fiable (ni SYSTEM ni Administradores): <b>nunca se corrige en el sitio</b>, porque quien la
/// creó puede conservar handles con <c>WRITE_DAC</c> o sustituirla entre la comprobación y la corrección. Se aparta
/// renombrándola por handle a <c>Backbone.untrusted-&lt;guid&gt;</c> (sin borrar nada) y se crea una nueva. Si no se puede
/// renombrar (p. ej. hay handles abiertos que lo impiden), se rechaza.</item>
/// <item>Raíz de propietario fiable con DACL distinta de la canónica (protegida, solo SYSTEM y Administradores con control
/// total heredable): se corrige en el sitio.</item>
/// <item>Contenido: ningún punto de reanálisis; todo propiedad de SYSTEM o Administradores (si no, se rechaza); o bien
/// herencia sin ACE explícitas, o bien (solo archivos) exactamente la DACL protegida canónica con que
/// <see cref="Persistence.TrustedFile"/> crea los archivos (<see cref="FileTrustPolicy.Default"/>: solo SYSTEM y
/// Administradores con control total, §8.3). La herencia solo cuenta si además todas las ACE son de permiso y solo para
/// SYSTEM o Administradores (una ACE marcada a mano como heredada no basta). Cualquier otra DACL se corrige restableciendo
/// la herencia.</item>
/// <item>Subcarpetas fijas (<see cref="FixedSubdirectories"/>): con la raíz todavía abierta se crean las que falten, por
/// handle (<c>NtCreateFile</c> relativo al handle de la raíz, <c>FILE_CREATE</c> y <c>FILE_DIRECTORY_FILE</c>: falla si ya
/// existe cualquier entrada con ese nombre, sin seguirla) y sin descriptor explícito (heredan la DACL ya corregida de la
/// raíz). Sobre el handle de creación se comprueba que es una carpeta, que no es un punto de reanálisis, que su
/// propietario es fiable y que su DACL es la heredada. Es el único sitio donde se crean carpetas: los almacenes no las
/// crean (§8.2).</item>
/// </list>
/// Crear la raíz con Administradores como propietario exige que el proceso pueda asignarlo (SYSTEM o un administrador
/// elevado). Si una corrección falla, se rechaza.
/// </remarks>
internal sealed class DataDirectoryGuard : IDataDirectoryGuard
{
    /// <summary>Sufijo del nombre con que se aparta una raíz de propietario no fiable.</summary>
    internal const string QuarantineSuffix = ".untrusted-";

    /// <summary>Subcarpetas fijas de la carpeta de datos (§8.3).</summary>
    internal static readonly IReadOnlyList<string> FixedSubdirectories = ["config", "credentials", "data", "logs"];

    private const int MaxEntries = 100_000;
    private const int MaxAttempts = 3;

    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    public DataDirectoryCheck EnsureSecure(string root)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        try
        {
            return EnsureSecureCore(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PrivilegeNotHeldException
                                       or InvalidOperationException or IdentityNotMappedException or Win32Exception
                                       or ArgumentException)
        {
            throw new DataDirectoryInsecureException($"The data directory could not be checked or corrected ({ex.GetType().Name}).", ex);
        }
    }

    private static DataDirectoryCheck EnsureSecureCore(string root)
    {
        var result = DataDirectoryCheckResult.Secure;
        var movedAside = new List<string>();
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            // Acceso a datos + compartición sin FILE_SHARE_DELETE: mientras esté abierto, nadie puede renombrar la raíz.
            var handle = TryOpen(root, FileListDirectory | ReadControl | FileReadAttributes | Synchronize, FileShareRead | FileShareWrite, out var error);
            if (handle is null)
            {
                switch (error)
                {
                    case ErrorFileNotFound or ErrorPathNotFound:
                        if (TryCreateSecureDirectory(root))
                        {
                            result = Max(result, DataDirectoryCheckResult.Corrected);
                        }

                        continue; // Se vuelve a abrir y a comprobar lo creado (o lo que otro haya creado antes).
                    case ErrorAccessDenied:
                        // Ni siquiera se puede leer: su DACL no es la nuestra y nadie de confianza la ha dejado así.
                        movedAside.Add(MoveAside(root));
                        result = DataDirectoryCheckResult.Replaced;
                        continue;
                    default:
                        throw new DataDirectoryInsecureException("The data directory could not be opened.", new Win32Exception(error));
                }
            }

            using (handle)
            {
                EnsureDirectoryNotReparse(handle, "The data directory");
                var security = ReadSecurity(handle);
                if (!IsTrustedOwner(security))
                {
                    // Nunca se corrige en el sitio una raíz de propietario no fiable: se aparta y se crea otra.
                    handle.Dispose();
                    movedAside.Add(MoveAside(root));
                    result = DataDirectoryCheckResult.Replaced;
                    continue;
                }

                if (!IsRootCompliant(security))
                {
                    ProtectRoot(root);
                    if (!IsRootCompliant(ReadSecurity(handle)))
                    {
                        throw new DataDirectoryInsecureException("The data directory security could not be corrected.");
                    }

                    result = Max(result, DataDirectoryCheckResult.Corrected);
                }

                // Antes de tratar los hijos: sigue siendo la misma carpeta (el handle impide renombrarla) y no es un enlace.
                EnsureDirectoryNotReparse(handle, "The data directory");
                var entries = 0;
                if (CheckChildren(root, ref entries))
                {
                    result = Max(result, DataDirectoryCheckResult.Corrected);
                }

                // Con la raíz todavía abierta (no se puede renombrar ni sustituir): las subcarpetas fijas que falten.
                EnsureDirectoryNotReparse(handle, "The data directory");
                CreateFixedSubdirectories(handle, root);

                return new DataDirectoryCheck(result, movedAside);
            }
        }

        throw new DataDirectoryInsecureException("The data directory could not be created securely (it keeps being replaced).");
    }

    private static DataDirectoryCheckResult Max(DataDirectoryCheckResult a, DataDirectoryCheckResult b) => a > b ? a : b;

    private static bool CheckChildren(string directory, ref int entries)
    {
        var corrected = false;
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            if (++entries > MaxEntries)
            {
                throw new DataDirectoryInsecureException("The data directory contains too many entries.");
            }

            // Las carpetas quedan abiertas sin FILE_SHARE_DELETE mientras se recorren (no se pueden sustituir).
            using var handle = TryOpen(path, FileListDirectory | ReadControl | FileReadAttributes | Synchronize, FileShareRead | FileShareWrite, out var error)
                ?? throw new DataDirectoryInsecureException("An entry of the data directory could not be opened.", new Win32Exception(error));
            var info = GetAttributeTag(handle);
            if ((info.FileAttributes & FileAttributeReparsePoint) != 0)
            {
                throw new DataDirectoryInsecureException("The data directory contains a reparse point.");
            }

            var security = ReadSecurity(handle);
            if (!IsTrustedOwner(security))
            {
                throw new DataDirectoryInsecureException("The data directory contains an entry owned by an untrusted account.");
            }

            var isDirectory = (info.FileAttributes & FileAttributeDirectory) != 0;
            if (!IsChildCompliant(security, isDirectory))
            {
                ResetToInherited(path);
                if (!IsChildCompliant(ReadSecurity(handle), isDirectory))
                {
                    throw new DataDirectoryInsecureException("The security of an entry of the data directory could not be corrected.");
                }

                corrected = true;
            }

            if (isDirectory)
            {
                corrected |= CheckChildren(path, ref entries);
            }
        }

        return corrected;
    }

    /// <summary>
    /// Crea, relativas al handle de la raíz, las subcarpetas fijas que falten (§8.3). Un nombre ya ocupado se ha comprobado
    /// como contenido; si lo ocupa algo creado entre medias, se comprueba igual.
    /// </summary>
    private static void CreateFixedSubdirectories(SafeFileHandle root, string rootPath)
    {
        foreach (var name in FixedSubdirectories)
        {
            var status = CreateChildDirectory(root, name, out var created);
            if (status == StatusObjectNameCollision)
            {
                // Ya existía (tratada como contenido) o alguien la ha creado después del recorrido: se vuelve a comprobar.
                var path = Path.Combine(rootPath, name);
                using var existing = TryOpen(path, FileListDirectory | ReadControl | FileReadAttributes | Synchronize, FileShareRead | FileShareWrite, out var error)
                    ?? throw new DataDirectoryInsecureException($"The data subdirectory '{name}' could not be opened.", new Win32Exception(error));
                EnsureFixedSubdirectory(existing, name);
                continue;
            }

            if (status < 0)
            {
                throw new DataDirectoryInsecureException(
                    $"The data subdirectory '{name}' could not be created.", new Win32Exception(RtlNtStatusToDosError(status)));
            }

            using (created)
            {
                EnsureFixedSubdirectory(created, name);
            }
        }
    }

    /// <summary>Carpeta (no punto de reanálisis), de propietario fiable y con la DACL heredada de la raíz.</summary>
    private static void EnsureFixedSubdirectory(SafeFileHandle handle, string name)
    {
        EnsureDirectoryNotReparse(handle, $"The data subdirectory '{name}'");
        var security = ReadSecurity(handle);
        if (!IsTrustedOwner(security))
        {
            throw new DataDirectoryInsecureException($"The data subdirectory '{name}' is owned by an untrusted account.");
        }

        if (!IsChildCompliant(security, isDirectory: true))
        {
            throw new DataDirectoryInsecureException($"The data subdirectory '{name}' does not inherit the security of the data directory.");
        }
    }

    /// <summary>
    /// <c>NtCreateFile</c> relativo a <paramref name="parent"/>: carpeta nueva (<c>FILE_CREATE</c>, nunca abre ni sigue lo
    /// que exista), sin descriptor explícito (hereda), abierta sin <c>FILE_SHARE_DELETE</c>.
    /// </summary>
    /// <returns>El <c>NTSTATUS</c>; <paramref name="handle"/> solo es válido si es de éxito.</returns>
    private static unsafe int CreateChildDirectory(SafeFileHandle parent, string name, out SafeFileHandle handle)
    {
        var addedRef = false;
        try
        {
            parent.DangerousAddRef(ref addedRef);
            fixed (char* buffer = name)
            {
                var objectName = new UnicodeString
                {
                    Length = checked((ushort)(name.Length * sizeof(char))),
                    MaximumLength = checked((ushort)(name.Length * sizeof(char))),
                    Buffer = (nint)buffer,
                };
                var attributes = new ObjectAttributes
                {
                    Length = (uint)sizeof(ObjectAttributes),
                    RootDirectory = parent.DangerousGetHandle(),
                    ObjectName = (nint)(&objectName),
                    Attributes = ObjCaseInsensitive,
                    SecurityDescriptor = 0,
                    SecurityQualityOfService = 0,
                };
                var status = NtCreateFile(
                    out handle,
                    FileListDirectory | ReadControl | FileReadAttributes | Synchronize,
                    in attributes,
                    out _,
                    0,
                    0,
                    FileShareRead | FileShareWrite,
                    NtFileCreate,
                    NtFileDirectoryFile | NtFileSynchronousIoNonAlert | NtFileOpenReparsePoint,
                    0,
                    0);
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
                parent.DangerousRelease();
            }
        }
    }

    internal static DirectorySecurity BuildRootSecurity()
    {
        var security = new DirectorySecurity();
        security.SetOwner(Administrators);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { LocalSystem, Administrators })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                sid,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        return security;
    }

    internal static bool IsRootCompliant(FileSystemSecurity security)
    {
        if (!IsTrustedOwner(security) || !security.AreAccessRulesProtected)
        {
            return false;
        }

        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToList();
        if (rules.Any(r => r.AccessControlType != AccessControlType.Allow
                           || r.IsInherited
                           || (r.IdentityReference is not SecurityIdentifier sid)
                           || (sid != LocalSystem && sid != Administrators)))
        {
            return false;
        }

        return HasFullControlInheritable(rules, LocalSystem) && HasFullControlInheritable(rules, Administrators);
    }

    /// <summary>
    /// Contenido conforme (§8.3): propietario fiable y, o bien herencia sin ACE explícitas, o bien (solo archivos) la DACL
    /// protegida canónica de <see cref="FileTrustPolicy.Default"/>.
    /// </summary>
    internal static bool IsChildCompliant(FileSystemSecurity security, bool isDirectory = false) =>
        IsTrustedOwner(security)
        && (IsInheritedOnly(security) || (!isDirectory && IsCanonicalTrustedFileDacl(security, FileTrustPolicy.Default)));

    /// <summary>
    /// Herencia sin ACE explícitas y, además, todas las ACE (marcadas como heredadas) de permiso y solo para SYSTEM o
    /// Administradores: el indicador <c>INHERITED_ACE</c> lo puede poner cualquiera que escriba la DACL, así que no basta
    /// (B-a). Se comprueban todas, también las solo heredables: en el contenido legítimo, heredado de la raíz canónica, no hay
    /// ninguna otra.
    /// </summary>
    private static bool IsInheritedOnly(FileSystemSecurity security) =>
        !security.AreAccessRulesProtected
        && security.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier)).Count == 0
        && security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .All(r => r.AccessControlType == AccessControlType.Allow
                      && r.IdentityReference is SecurityIdentifier sid
                      && (sid == LocalSystem || sid == Administrators));

    /// <summary>
    /// DACL protegida con exactamente una ACE explícita de permiso por cada SID de <see cref="FileTrustPolicy.FullControlSids"/>,
    /// con control total y sin herencia; ninguna otra ACE.
    /// </summary>
    internal static bool IsCanonicalTrustedFileDacl(FileSystemSecurity security, FileTrustPolicy policy)
    {
        if (!security.AreAccessRulesProtected)
        {
            return false;
        }

        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToList();
        if (rules.Count != policy.FullControlSids.Length)
        {
            return false;
        }

        var seen = new HashSet<SecurityIdentifier>();
        foreach (var rule in rules)
        {
            if (rule.IsInherited
                || rule.AccessControlType != AccessControlType.Allow
                || rule.IdentityReference is not SecurityIdentifier sid
                || !policy.FullControlSids.Contains(sid)
                || !seen.Add(sid)
                || rule.FileSystemRights != FileSystemRights.FullControl
                || rule.InheritanceFlags != InheritanceFlags.None
                || rule.PropagationFlags != PropagationFlags.None)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Nombre con que se aparta <paramref name="root"/>: <c>Backbone.untrusted-&lt;guid&gt;</c>, en la misma carpeta.</summary>
    internal static string QuarantinePath(string root) =>
        Path.Combine(Path.GetDirectoryName(root) ?? throw new ArgumentException("The data directory has no parent.", nameof(root)),
            Path.GetFileName(root) + QuarantineSuffix + Guid.NewGuid().ToString("N"));

    private static bool HasFullControlInheritable(List<FileSystemAccessRule> rules, SecurityIdentifier sid) =>
        rules.Any(r => (SecurityIdentifier)r.IdentityReference == sid
                       && (r.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl
                       && r.InheritanceFlags == (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit)
                       && r.PropagationFlags == PropagationFlags.None);

    private static bool IsTrustedOwner(FileSystemSecurity security) =>
        security.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner
        && (owner == LocalSystem || owner == Administrators);

    /// <summary>
    /// Crea la raíz con el descriptor definitivo. <see langword="false"/> si ya existe (otro la ha creado entre medias: se
    /// vuelve a evaluar lo que haya).
    /// </summary>
    private static bool TryCreateSecureDirectory(string root)
    {
        var descriptor = BuildRootSecurity().GetSecurityDescriptorBinaryForm();
        var buffer = Marshal.AllocHGlobal(descriptor.Length);
        try
        {
            Marshal.Copy(descriptor, 0, buffer, descriptor.Length);
            var attributes = new SecurityAttributes
            {
                Length = (uint)Marshal.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = buffer,
                InheritHandle = 0,
            };
            if (CreateDirectory(root, in attributes))
            {
                return true;
            }

            var error = Marshal.GetLastPInvokeError();
            return error == ErrorAlreadyExists
                ? false
                : throw new DataDirectoryInsecureException("The data directory could not be created with the required security.", new Win32Exception(error));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Aparta la raíz renombrándola por handle (se renombra el objeto abierto, aunque alguien cambie la ruta entre medias).
    /// Abrirla con <c>DELETE</c> falla si hay handles abiertos sin <c>FILE_SHARE_DELETE</c>: entonces se rechaza.
    /// </summary>
    /// <returns>Ruta completa con que ha quedado apartada.</returns>
    private static string MoveAside(string root)
    {
        using var handle = TryOpen(root, Delete | FileReadAttributes | Synchronize, FileShareRead | FileShareWrite, out var error);
        if (handle is null)
        {
            throw new DataDirectoryInsecureException(
                "The data directory is not trustworthy and could not be moved aside (it may be in use).",
                new Win32Exception(error));
        }

        EnsureDirectoryNotReparse(handle, "The data directory");

        var target = QuarantinePath(root);
        var rootOffset = IntPtr.Size; // union { BOOLEAN ReplaceIfExists; DWORD Flags; } alineada al tamaño de un puntero.
        var lengthOffset = rootOffset + IntPtr.Size;
        var nameOffset = lengthOffset + sizeof(uint);
        var nameBytes = target.Length * sizeof(char);
        var info = new byte[nameOffset + nameBytes + sizeof(char)];
        // ReplaceIfExists = FALSE, RootDirectory = NULL (ya a cero).
        BitConverter.TryWriteBytes(info.AsSpan(lengthOffset), (uint)nameBytes);
        MemoryMarshal.AsBytes(target.AsSpan()).CopyTo(info.AsSpan(nameOffset));

        if (!SetFileInformationByHandle(handle, FileRenameInfo, info, (uint)info.Length))
        {
            throw new DataDirectoryInsecureException(
                "The data directory is not trustworthy and could not be moved aside.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        return target;
    }

    /// <summary>Aplica la DACL canónica (protegida) a la raíz, de propietario ya fiable, sobre un handle comprobado.</summary>
    private static void ProtectRoot(string root)
    {
        using var handle = TryOpen(root, WriteDac | ReadControl | FileReadAttributes | Synchronize, FileShareRead | FileShareWrite | FileShareDelete, out var error)
            ?? throw new DataDirectoryInsecureException("The data directory could not be opened to correct it.", new Win32Exception(error));
        EnsureDirectoryNotReparse(handle, "The data directory");
        if (!IsTrustedOwner(ReadSecurity(handle)))
        {
            throw new DataDirectoryInsecureException("The data directory changed while it was being corrected.");
        }

        var canonical = new RawSecurityDescriptor(BuildRootSecurity().GetSecurityDescriptorBinaryForm(), 0).DiscretionaryAcl
            ?? throw new InvalidOperationException("The canonical security descriptor has no DACL.");
        var dacl = new byte[canonical.BinaryLength];
        canonical.GetBinaryForm(dacl, 0);
        ThrowIfFailed(SetSecurityInfo(handle, SeFileObject, DaclSecurityInformation | ProtectedDaclSecurityInformation, 0, 0, dacl, 0));
    }

    /// <summary>
    /// Quita las ACE explícitas y reactiva la herencia (DACL vacía, nunca nula, sin protección: el sistema aplica las ACE
    /// heredables de la carpeta padre).
    /// </summary>
    private static void ResetToInherited(string path)
    {
        using var handle = TryOpen(path, WriteDac | ReadControl | FileReadAttributes | Synchronize, FileShareRead | FileShareWrite | FileShareDelete, out var error)
            ?? throw new DataDirectoryInsecureException("An entry of the data directory could not be opened to correct it.", new Win32Exception(error));
        if ((GetAttributeTag(handle).FileAttributes & FileAttributeReparsePoint) != 0)
        {
            throw new DataDirectoryInsecureException("The data directory contains a reparse point.");
        }

        if (!IsTrustedOwner(ReadSecurity(handle)))
        {
            throw new DataDirectoryInsecureException("The data directory contains an entry owned by an untrusted account.");
        }

        var empty = new RawAcl(GenericAcl.AclRevision, 0);
        var dacl = new byte[empty.BinaryLength];
        empty.GetBinaryForm(dacl, 0);
        ThrowIfFailed(SetSecurityInfo(handle, SeFileObject, DaclSecurityInformation | UnprotectedDaclSecurityInformation, 0, 0, dacl, 0));
    }

    private static SafeFileHandle? TryOpen(string path, uint access, uint share, out int error)
    {
        var handle = CreateFile(path, access, share, 0, OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, 0);
        if (handle.IsInvalid)
        {
            error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            return null;
        }

        error = ErrorSuccess;
        return handle;
    }

    private static FileAttributeTagInformation GetAttributeTag(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandleEx(handle, FileAttributeTagInfo, out FileAttributeTagInformation info, (uint)Marshal.SizeOf<FileAttributeTagInformation>()))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        return info;
    }

    private static void EnsureDirectoryNotReparse(SafeFileHandle handle, string what)
    {
        var info = GetAttributeTag(handle);
        if ((info.FileAttributes & FileAttributeReparsePoint) != 0)
        {
            throw new DataDirectoryInsecureException($"{what} is a reparse point.");
        }

        if ((info.FileAttributes & FileAttributeDirectory) == 0)
        {
            throw new DataDirectoryInsecureException($"{what} is not a directory.");
        }
    }

    private static DirectorySecurity ReadSecurity(SafeFileHandle handle)
    {
        ThrowIfFailed(GetSecurityInfo(handle, SeFileObject, OwnerSecurityInformation | DaclSecurityInformation,
            out _, out _, out _, out _, out var descriptor));
        try
        {
            var bytes = new byte[GetSecurityDescriptorLength(descriptor)];
            Marshal.Copy(descriptor, bytes, 0, bytes.Length);
            var security = new DirectorySecurity();
            security.SetSecurityDescriptorBinaryForm(bytes, AccessControlSections.Owner | AccessControlSections.Access);
            return security;
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    private static void ThrowIfFailed(int error)
    {
        if (error != ErrorSuccess)
        {
            throw new Win32Exception(error);
        }
    }
}
