using System.Security.AccessControl;
using System.Security.Principal;

namespace Backbone.Service.Os;

/// <summary>
/// Comprobación de <c>%ProgramData%\Backbone</c> al arrancar (HT-03 I-1, RNF-07).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Raíz: no es punto de reanálisis; propietario SYSTEM o Administradores; DACL protegida (sin herencia) con solo
/// SYSTEM y Administradores en control total heredable. Si el propietario no es de confianza solo se corrige con la carpeta
/// vacía (alguien la creó antes de instalar, pero no ha dejado nada); si tiene contenido, se rechaza.</item>
/// <item>Contenido: ningún punto de reanálisis; todo propiedad de SYSTEM o Administradores (si no, su contenido no es de
/// fiar y se rechaza); sin ACE explícitas ni herencia cortada (si las hay, se restablece la herencia).</item>
/// </list>
/// Corregir el propietario exige que el proceso pueda asignar el grupo Administradores como propietario (SYSTEM o un
/// administrador elevado). Si una corrección falla, se rechaza.
/// </remarks>
internal sealed class DataDirectoryGuard : IDataDirectoryGuard
{
    private const int MaxEntries = 100_000;

    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    public DataDirectoryCheckResult EnsureSecure(string root)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        try
        {
            return EnsureSecureCore(new DirectoryInfo(root));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PrivilegeNotHeldException
                                       or InvalidOperationException or IdentityNotMappedException)
        {
            throw new DataDirectoryInsecureException($"The data directory could not be checked or corrected ({ex.GetType().Name}).", ex);
        }
    }

    private static DataDirectoryCheckResult EnsureSecureCore(DirectoryInfo root)
    {
        var corrected = false;
        if (!root.Exists)
        {
            root.Create(BuildRootSecurity());
            corrected = true;
        }

        root.Refresh();
        if (root.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new DataDirectoryInsecureException("The data directory is a reparse point.");
        }

        var security = root.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        if (!IsTrustedOwner(security) && root.EnumerateFileSystemInfos().Any())
        {
            throw new DataDirectoryInsecureException("The data directory is owned by an untrusted account and is not empty.");
        }

        if (!IsRootCompliant(security))
        {
            root.SetAccessControl(BuildRootSecurity());
            corrected = true;
            if (!IsRootCompliant(root.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)))
            {
                throw new DataDirectoryInsecureException("The data directory security could not be corrected.");
            }
        }

        var entries = 0;
        corrected |= CheckChildren(root, ref entries);
        return corrected ? DataDirectoryCheckResult.Corrected : DataDirectoryCheckResult.Secure;
    }

    private static bool CheckChildren(DirectoryInfo directory, ref int entries)
    {
        var corrected = false;
        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            if (++entries > MaxEntries)
            {
                throw new DataDirectoryInsecureException("The data directory contains too many entries.");
            }

            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new DataDirectoryInsecureException("The data directory contains a reparse point.");
            }

            FileSystemSecurity security = entry is DirectoryInfo d
                ? d.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)
                : ((FileInfo)entry).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);

            if (!IsTrustedOwner(security))
            {
                throw new DataDirectoryInsecureException("The data directory contains an entry owned by an untrusted account.");
            }

            if (!IsChildCompliant(security))
            {
                ResetToInherited(entry, security);
                corrected = true;
            }

            if (entry is DirectoryInfo subdirectory)
            {
                corrected |= CheckChildren(subdirectory, ref entries);
            }
        }

        return corrected;
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

    internal static bool IsChildCompliant(FileSystemSecurity security) =>
        IsTrustedOwner(security)
        && !security.AreAccessRulesProtected
        && security.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier)).Count == 0;

    private static bool HasFullControlInheritable(List<FileSystemAccessRule> rules, SecurityIdentifier sid) =>
        rules.Any(r => (SecurityIdentifier)r.IdentityReference == sid
                       && (r.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl
                       && r.InheritanceFlags == (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit)
                       && r.PropagationFlags == PropagationFlags.None);

    private static bool IsTrustedOwner(FileSystemSecurity security) =>
        security.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner
        && (owner == LocalSystem || owner == Administrators);

    private static void ResetToInherited(FileSystemInfo entry, FileSystemSecurity security)
    {
        // Se parte de la DACL actual (nunca de una nula, que daría acceso a todos), se quitan las ACE explícitas y se
        // reactiva la herencia: el sistema vuelve a aplicar las ACE heredables de la raíz.
        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier)))
        {
            security.RemoveAccessRuleSpecific(rule);
        }

        security.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
        switch (entry)
        {
            case DirectoryInfo d:
                d.SetAccessControl((DirectorySecurity)security);
                break;
            case FileInfo f:
                f.SetAccessControl((FileSecurity)security);
                break;
        }
    }
}
