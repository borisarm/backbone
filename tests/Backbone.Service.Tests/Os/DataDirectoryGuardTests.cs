using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using Backbone.Service.Hosting;
using Backbone.Service.Ipc;
using Backbone.Service.Os;
using Backbone.Service.Persistence;
using Backbone.Service.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;

namespace Backbone.Service.Tests.Os;

/// <summary>
/// HT-03 I-1 / RNF-07: comprobación de la carpeta de datos al arrancar. Las reglas se prueban sobre descriptores de
/// seguridad; el comportamiento real, sobre carpetas temporales (sin elevar, la corrección del propietario no es posible
/// y se comprueba el rechazo; elevado, se comprueba la corrección; ver también Backbone.IntegrationTests).
/// </summary>
[Trait("Story", "HT-03")]
public sealed class DataDirectoryGuardTests : IDisposable
{
    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);

    private readonly TempDirectory _temp = new();
    private readonly DataDirectoryGuard _guard = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void The_canonical_root_security_is_compliant()
    {
        Assert.True(DataDirectoryGuard.IsRootCompliant(DataDirectoryGuard.BuildRootSecurity()));
    }

    [Fact]
    public void The_canonical_root_security_has_only_SYSTEM_and_Administrators_with_inheritable_full_control()
    {
        var security = DataDirectoryGuard.BuildRootSecurity();
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();

        Assert.True(security.AreAccessRulesProtected);
        Assert.Equal(Administrators, security.GetOwner(typeof(SecurityIdentifier)));
        Assert.Equal([System, Administrators], rules.Select(r => (SecurityIdentifier)r.IdentityReference).OrderBy(s => s.Value, StringComparer.Ordinal));
        Assert.All(rules, r =>
        {
            Assert.Equal(AccessControlType.Allow, r.AccessControlType);
            Assert.Equal(FileSystemRights.FullControl, r.FileSystemRights);
            Assert.Equal(InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, r.InheritanceFlags);
        });
    }

    [Fact]
    public void A_root_with_any_other_entry_is_not_compliant()
    {
        var security = DataDirectoryGuard.BuildRootSecurity();
        security.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute, AccessControlType.Allow));

        Assert.False(DataDirectoryGuard.IsRootCompliant(security));
    }

    [Fact]
    public void A_root_with_inheritance_enabled_is_not_compliant()
    {
        var security = DataDirectoryGuard.BuildRootSecurity();
        security.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);

        Assert.False(DataDirectoryGuard.IsRootCompliant(security));
    }

    [Fact]
    public void A_root_owned_by_a_user_is_not_compliant()
    {
        var security = DataDirectoryGuard.BuildRootSecurity();
        security.SetOwner(new SecurityIdentifier(TestSids.Ana));

        Assert.False(DataDirectoryGuard.IsRootCompliant(security));
    }

    [Fact]
    public void A_root_with_a_deny_entry_or_partial_rights_is_not_compliant()
    {
        var deny = DataDirectoryGuard.BuildRootSecurity();
        deny.AddAccessRule(new FileSystemAccessRule(System, FileSystemRights.Delete, AccessControlType.Deny));
        var partial = new DirectorySecurity();
        partial.SetOwner(System);
        partial.SetAccessRuleProtection(true, false);
        partial.AddAccessRule(new FileSystemAccessRule(System, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        partial.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.Modify, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));

        Assert.False(DataDirectoryGuard.IsRootCompliant(deny));
        Assert.False(DataDirectoryGuard.IsRootCompliant(partial));
    }

    [Fact]
    public void A_child_with_explicit_entries_or_broken_inheritance_is_not_compliant()
    {
        var explicitEntry = new FileSecurity();
        explicitEntry.SetOwner(Administrators);
        explicitEntry.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.Read, AccessControlType.Allow));
        var protectedChild = new FileSecurity();
        protectedChild.SetOwner(Administrators);
        protectedChild.SetAccessRuleProtection(true, false);
        var userOwned = new FileSecurity();
        userOwned.SetOwner(new SecurityIdentifier(TestSids.Ana));

        Assert.False(DataDirectoryGuard.IsChildCompliant(explicitEntry));
        Assert.False(DataDirectoryGuard.IsChildCompliant(protectedChild));
        Assert.False(DataDirectoryGuard.IsChildCompliant(userOwned));
    }

    [Fact]
    public void A_file_with_the_canonical_TrustedFile_DACL_is_compliant_but_a_directory_with_it_is_not()
    {
        var canonical = CanonicalTrustedFileSecurity();

        Assert.True(DataDirectoryGuard.IsChildCompliant(canonical));
        Assert.False(DataDirectoryGuard.IsChildCompliant(canonical, isDirectory: true));
    }

    [Fact]
    public void A_file_with_any_other_protected_DACL_is_not_compliant()
    {
        var withUsers = CanonicalTrustedFileSecurity();
        withUsers.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.Read, AccessControlType.Allow));
        var onlySystem = new FileSecurity();
        onlySystem.SetOwner(Administrators);
        onlySystem.SetAccessRuleProtection(true, false);
        onlySystem.AddAccessRule(new FileSystemAccessRule(System, FileSystemRights.FullControl, AccessControlType.Allow));
        var partial = new FileSecurity();
        partial.SetOwner(Administrators);
        partial.SetAccessRuleProtection(true, false);
        partial.AddAccessRule(new FileSystemAccessRule(System, FileSystemRights.FullControl, AccessControlType.Allow));
        partial.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.Modify, AccessControlType.Allow));
        var withDeny = CanonicalTrustedFileSecurity();
        withDeny.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.Read, AccessControlType.Deny));
        var userOwned = CanonicalTrustedFileSecurity();
        userOwned.SetOwner(new SecurityIdentifier(TestSids.Ana));

        Assert.False(DataDirectoryGuard.IsChildCompliant(withUsers));
        Assert.False(DataDirectoryGuard.IsChildCompliant(onlySystem));
        Assert.False(DataDirectoryGuard.IsChildCompliant(partial));
        Assert.False(DataDirectoryGuard.IsChildCompliant(withDeny));
        Assert.False(DataDirectoryGuard.IsChildCompliant(userOwned)); // propietario no fiable: sigue siendo rechazo
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task After_a_TrustedFile_write_the_next_check_is_Secure_when_elevated()
    {
        Assert.SkipUnless(WindowsTestEnvironment.IsElevated, "Requires an elevated process (Administrators as owner).");
        var root = _temp.Combine("Backbone");
        Assert.Equal(DataDirectoryCheckResult.Corrected, _guard.EnsureSecure(root).Result);
        var file = Path.Combine(root, "config", "settings.json");

        await new TrustedFile(FileTrustPolicy.Default).WriteAllBytesAsync(file, "{}"u8.ToArray(), TestContext.Current.CancellationToken);

        Assert.True(new FileInfo(file).GetAccessControl().AreAccessRulesProtected);
        Assert.Equal(DataDirectoryCheckResult.Secure, _guard.EnsureSecure(root).Result);
        Assert.True(new FileInfo(file).GetAccessControl().AreAccessRulesProtected); // no se ha "corregido"
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void A_file_with_a_different_protected_DACL_is_corrected_when_elevated()
    {
        Assert.SkipUnless(WindowsTestEnvironment.IsElevated, "Requires an elevated process (Administrators as owner).");
        var root = _temp.Combine("Backbone");
        _guard.EnsureSecure(root);
        var file = Path.Combine(root, "planted.json");
        File.WriteAllText(file, "{}");
        var security = CanonicalTrustedFileSecurity();
        security.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.Read, AccessControlType.Allow));
        new FileInfo(file).SetAccessControl(security);

        Assert.Equal(DataDirectoryCheckResult.Corrected, _guard.EnsureSecure(root).Result);

        var corrected = new FileInfo(file).GetAccessControl();
        Assert.False(corrected.AreAccessRulesProtected);
        Assert.Empty(corrected.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier)));
        Assert.Equal(DataDirectoryCheckResult.Secure, _guard.EnsureSecure(root).Result);
    }

    /// <summary>El descriptor con que <see cref="TrustedFile"/> crea los archivos en producción.</summary>
    private static FileSecurity CanonicalTrustedFileSecurity()
    {
        var security = new FileSecurity();
        security.SetSecurityDescriptorBinaryForm(FileTrustPolicy.Default.BuildCreateSecurityDescriptor());
        return security;
    }

    [Fact]
    public void A_junction_is_refused()
    {
        var target = Directory.CreateDirectory(_temp.Combine("destino")).FullName;
        var link = _temp.Combine("Backbone");
        CreateJunction(link, target);

        var ex = Assert.Throws<DataDirectoryInsecureException>(() => _guard.EnsureSecure(link));

        Assert.Contains("reparse point", ex.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(target)); // nunca se borra ni se toca el destino del enlace
    }

    [Fact]
    public void A_junction_inside_the_data_directory_is_refused()
    {
        var root = Directory.CreateDirectory(_temp.Combine("Backbone")).FullName;
        CreateJunction(Path.Combine(root, "config"), Directory.CreateDirectory(_temp.Combine("otra")).FullName);
        TrySecureRoot(root);

        Assert.Throws<DataDirectoryInsecureException>(() => _guard.EnsureSecure(root).Result);
    }

    [Fact]
    public void A_directory_created_by_this_user_is_corrected_when_elevated_and_refused_otherwise()
    {
        var root = Directory.CreateDirectory(_temp.Combine("Backbone")).FullName;

        if (WindowsTestEnvironment.IsElevated)
        {
            Assert.Equal(DataDirectoryCheckResult.Corrected, _guard.EnsureSecure(root).Result);
            Assert.True(DataDirectoryGuard.IsRootCompliant(new DirectoryInfo(root).GetAccessControl()));
            Assert.Equal(DataDirectoryCheckResult.Secure, _guard.EnsureSecure(root).Result);
        }
        else
        {
            // Sin elevar no se puede asignar Administradores como propietario: se rechaza en lugar de usarla tal cual.
            Assert.Throws<DataDirectoryInsecureException>(() => _guard.EnsureSecure(root).Result);
        }
    }

    [Fact]
    public void A_missing_root_is_created_atomically_with_the_canonical_security_when_elevated_and_refused_otherwise()
    {
        var root = _temp.Combine("Backbone");

        if (WindowsTestEnvironment.IsElevated)
        {
            Assert.Equal(DataDirectoryCheckResult.Corrected, _guard.EnsureSecure(root).Result);
            Assert.True(DataDirectoryGuard.IsRootCompliant(new DirectoryInfo(root).GetAccessControl()));
        }
        else
        {
            // Sin elevar no se puede asignar Administradores como propietario: CreateDirectoryW falla y no queda nada creado
            // con otra ACL.
            var ex = Assert.Throws<DataDirectoryInsecureException>(() => _guard.EnsureSecure(root).Result);
            Assert.Contains("could not be created", ex.Message, StringComparison.Ordinal);
            Assert.False(Directory.Exists(root));
        }
    }

    [Fact]
    public void A_root_owned_by_an_untrusted_user_is_moved_aside_untouched_and_a_new_one_is_created()
    {
        var root = CreateUntrustedRoot();
        File.WriteAllText(Path.Combine(root, "plantado.json"), "{}");

        DataDirectoryCheck? check = null;
        if (WindowsTestEnvironment.IsElevated)
        {
            check = _guard.EnsureSecure(root);
            Assert.Equal(DataDirectoryCheckResult.Replaced, check.Result);
            Assert.True(DataDirectoryGuard.IsRootCompliant(new DirectoryInfo(root).GetAccessControl()));
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        else
        {
            // Se aparta igualmente; lo que no se puede sin elevar es crear la nueva con Administradores como propietario.
            Assert.Throws<DataDirectoryInsecureException>(() => _guard.EnsureSecure(root).Result);
            Assert.False(Directory.Exists(root));
        }

        // Lo apartado se conserva tal cual (no se corrige en el sitio ni se borra).
        var quarantined = Assert.Single(Directory.GetDirectories(_temp.Path, "Backbone" + DataDirectoryGuard.QuarantineSuffix + "*"));
        Assert.True(File.Exists(Path.Combine(quarantined, "plantado.json")));
        if (check is not null)
        {
            Assert.Equal(quarantined, Assert.Single(check.MovedAsidePaths)); // la ruta que va en el evento 1003
        }
        Assert.Equal(WindowsTestEnvironment.CurrentUserSid, new DirectoryInfo(quarantined).GetAccessControl().GetOwner(typeof(SecurityIdentifier)));
    }

    [Fact]
    public void An_untrusted_root_that_cannot_be_moved_aside_because_of_an_open_handle_is_refused()
    {
        var root = CreateUntrustedRoot();

        // Handle con acceso a datos y sin FILE_SHARE_DELETE (como el que conservaría quien la creó): impide renombrarla.
        using (OpenDirectoryWithoutShareDelete(root))
        {
            var ex = Assert.Throws<DataDirectoryInsecureException>(() => _guard.EnsureSecure(root).Result);

            Assert.Contains("could not be moved aside", ex.Message, StringComparison.Ordinal);
        }

        Assert.True(Directory.Exists(root));
        Assert.Empty(Directory.GetDirectories(_temp.Path, "Backbone" + DataDirectoryGuard.QuarantineSuffix + "*"));
        Assert.Equal(WindowsTestEnvironment.CurrentUserSid, new DirectoryInfo(root).GetAccessControl().GetOwner(typeof(SecurityIdentifier)));
    }

    [Fact]
    public async Task The_service_does_not_start_when_an_untrusted_root_cannot_be_moved_aside()
    {
        var root = CreateUntrustedRoot();
        var eventLog = new FakeEventLogWriter();
        using var settings = new SettingsStore(Path.Combine(root, "config", "settings.json"), TestFileTrust.CurrentUser, NullLogger<SettingsStore>.Instance);
        var startup = new StartupService(
            new ServicePaths(root), _guard, settings, eventLog, new ServiceRuntimeInfo(TimeProvider.System), NullLogger<StartupService>.Instance);

        using (OpenDirectoryWithoutShareDelete(root))
        {
            var ex = await Assert.ThrowsAsync<ServiceStartupException>(() => startup.StartingAsync(CancellationToken.None));
            Assert.IsType<DataDirectoryInsecureException>(ex.InnerException);
        }

        var entry = Assert.Single(eventLog.Entries);
        Assert.Equal(EventLogLevel.Error, entry.Level);
        Assert.Equal(BackboneEventIds.ServiceStartFailed, entry.EventId);
    }

    [Fact]
    public void A_junction_root_is_refused_even_when_its_target_looks_secure()
    {
        var target = Directory.CreateDirectory(_temp.Combine("destino")).FullName;
        TrySecureRoot(target);
        var link = _temp.Combine("Backbone");
        CreateJunction(link, target);

        var ex = Assert.Throws<DataDirectoryInsecureException>(() => _guard.EnsureSecure(link));

        Assert.Contains("reparse point", ex.Message, StringComparison.Ordinal);
        Assert.True(new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint)); // ni se aparta ni se sustituye
        Assert.Empty(Directory.GetDirectories(_temp.Path, "Backbone" + DataDirectoryGuard.QuarantineSuffix + "*"));
    }

    /// <summary>Raíz cuyo propietario es el usuario de las pruebas (no fiable).</summary>
    private string CreateUntrustedRoot()
    {
        var root = Directory.CreateDirectory(_temp.Combine("Backbone")).FullName;
        if (WindowsTestEnvironment.IsElevated)
        {
            // Elevado, lo creado es de Administradores: se fuerza un propietario de usuario.
            var security = new DirectoryInfo(root).GetAccessControl();
            security.SetOwner(WindowsTestEnvironment.CurrentUserSid);
            new DirectoryInfo(root).SetAccessControl(security);
        }

        Assert.Equal(WindowsTestEnvironment.CurrentUserSid, new DirectoryInfo(root).GetAccessControl().GetOwner(typeof(SecurityIdentifier)));
        return root;
    }

    private static SafeFileHandle OpenDirectoryWithoutShareDelete(string path)
    {
        const uint fileListDirectory = 0x0001;
        var handle = NativeMethods.CreateFile(
            path, fileListDirectory, NativeMethods.FileShareRead | NativeMethods.FileShareWrite, 0, NativeMethods.OpenExisting,
            NativeMethods.FileFlagBackupSemantics, 0);
        Assert.False(handle.IsInvalid);
        return handle;
    }

    private static void TrySecureRoot(string root)
    {
        if (WindowsTestEnvironment.IsElevated)
        {
            new DirectoryInfo(root).SetAccessControl(DataDirectoryGuard.BuildRootSecurity());
        }
    }

    private static void CreateJunction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, target])
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        Assert.True(new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint));
    }
}
