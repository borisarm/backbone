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
    public void Inheritance_with_only_inherited_SYSTEM_and_Administrators_allow_entries_is_compliant()
    {
        var inherited = InheritedOnly(
            new CommonAce(AceFlags.Inherited | AceFlags.ContainerInherit | AceFlags.ObjectInherit, AceQualifier.AccessAllowed, FullControlMask, System, false, null),
            new CommonAce(AceFlags.Inherited | AceFlags.ContainerInherit | AceFlags.ObjectInherit, AceQualifier.AccessAllowed, FullControlMask, Administrators, false, null));

        Assert.True(DataDirectoryGuard.IsChildCompliant(inherited));
        Assert.True(DataDirectoryGuard.IsChildCompliant(inherited, isDirectory: true));
    }

    [Fact]
    public void An_entry_forged_as_inherited_for_another_SID_or_a_deny_is_not_compliant()
    {
        // B-a: INHERITED_ACE lo puede poner cualquiera que escriba la DACL; "Users: FullControl" marcada como heredada.
        var forgedUsers = InheritedOnly(
            new CommonAce(AceFlags.Inherited | AceFlags.ContainerInherit | AceFlags.ObjectInherit, AceQualifier.AccessAllowed, FullControlMask, System, false, null),
            new CommonAce(AceFlags.Inherited | AceFlags.ContainerInherit | AceFlags.ObjectInherit, AceQualifier.AccessAllowed, FullControlMask, Users, false, null));
        var forgedDeny = InheritedOnly(
            new CommonAce(AceFlags.Inherited, AceQualifier.AccessDenied, (int)FileSystemRights.Delete, Administrators, false, null),
            new CommonAce(AceFlags.Inherited, AceQualifier.AccessAllowed, FullControlMask, System, false, null));
        var forgedInheritOnly = InheritedOnly(
            new CommonAce(AceFlags.Inherited, AceQualifier.AccessAllowed, FullControlMask, System, false, null),
            new CommonAce(AceFlags.Inherited | AceFlags.InheritOnly | AceFlags.ContainerInherit | AceFlags.ObjectInherit, AceQualifier.AccessAllowed, FullControlMask, Users, false, null));

        // Lo que se comprueba es la ACE falsificada, no la forma: la regla anterior (sin ACE explícitas) sí la aceptaba.
        Assert.Empty(forgedUsers.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier)));
        Assert.Contains(forgedUsers.GetAccessRules(false, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(),
            r => r.IsInherited && (SecurityIdentifier)r.IdentityReference == Users && r.FileSystemRights == FileSystemRights.FullControl);

        Assert.False(DataDirectoryGuard.IsChildCompliant(forgedUsers));
        Assert.False(DataDirectoryGuard.IsChildCompliant(forgedUsers, isDirectory: true));
        Assert.False(DataDirectoryGuard.IsChildCompliant(forgedDeny));
        Assert.False(DataDirectoryGuard.IsChildCompliant(forgedInheritOnly, isDirectory: true));
    }

    [Fact]
    [Trait("Story", "HU-04")]
    public void A_conditional_callback_ACE_marked_inherited_is_not_compliant()
    {
        // B-1 (revisión de HT-03): "XA" = ACCESS_ALLOWED_CALLBACK_ACE condicional que concede control total a Todos, marcada
        // como heredada (ID). La comprobación se hace sobre la DACL en bruto y solo admite ACE de tipo 0, así que se rechaza
        // por su tipo, con independencia de cómo la represente FileSystemSecurity.GetAccessRules (que la lista como una regla
        // de permiso sin la condición).
        var sddl = "O:BAD:(A;ID;FA;;;SY)(A;ID;FA;;;BA)(XA;ID;FA;;;WD;(Member_of {SID(BU)}))";
        var raw = new RawSecurityDescriptor(sddl);

        Assert.Equal(AceType.AccessAllowedCallback, raw.DiscretionaryAcl![2].AceType);
        Assert.Equal(AceFlags.Inherited, raw.DiscretionaryAcl[2].AceFlags);

        Assert.False(DataDirectoryGuard.IsChildCompliant(raw));
        Assert.False(DataDirectoryGuard.IsChildCompliant(raw, isDirectory: true));

        // Se rechaza por el tipo, aunque sea de un SID fiable.
        Assert.False(DataDirectoryGuard.IsChildCompliant(new RawSecurityDescriptor("O:BAD:(A;ID;FA;;;SY)(XA;ID;FA;;;BA;(Member_of {SID(BU)}))")));
        Assert.True(DataDirectoryGuard.IsChildCompliant(new RawSecurityDescriptor("O:BAD:(A;ID;FA;;;SY)(A;ID;FA;;;BA)"))); // control
    }

    [Theory]
    [Trait("Story", "HU-04")]
    [InlineData("(XA;;FA;;;WD;(Member_of {SID(BU)}))")] // de callback condicional
    [InlineData("(XD;;FA;;;WD;(Member_of {SID(BU)}))")] // de denegación con callback
    [InlineData("(OA;;CC;bf967aba-0de6-11d0-a285-00aa003049e2;;WD)")] // de objeto
    [InlineData("(A;;FA;;;WD)")] // de permiso normal para otro SID
    public void A_root_or_canonical_file_DACL_with_any_extra_ACE_type_is_not_compliant(string extraAce)
    {
        var root = new RawSecurityDescriptor("O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)" + extraAce);
        var file = new RawSecurityDescriptor("O:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)" + extraAce);

        Assert.False(DataDirectoryGuard.IsRootCompliant(root));
        Assert.False(DataDirectoryGuard.IsChildCompliant(file));
        Assert.False(DataDirectoryGuard.IsCanonicalTrustedFileDacl(file, FileTrustPolicy.Default));

        // Control: sin la ACE añadida, las dos son conformes.
        Assert.True(DataDirectoryGuard.IsRootCompliant(new RawSecurityDescriptor("O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)")));
        Assert.True(DataDirectoryGuard.IsChildCompliant(new RawSecurityDescriptor("O:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)")));
    }

    [Fact]
    [Trait("Story", "HU-04")]
    public void A_null_DACL_is_never_compliant()
    {
        var nullDacl = new RawSecurityDescriptor("O:BAD:NO_ACCESS_CONTROL");

        Assert.False(DataDirectoryGuard.IsRootCompliant(nullDacl));
        Assert.False(DataDirectoryGuard.IsChildCompliant(nullDacl));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void The_check_creates_the_missing_fixed_subdirectories_inheriting_from_the_root_when_elevated()
    {
        Assert.SkipUnless(WindowsTestEnvironment.IsElevated, "Requires an elevated process (Administrators as owner).");
        var root = _temp.Combine("Backbone");
        Assert.Equal(DataDirectoryCheckResult.Corrected, _guard.EnsureSecure(root).Result);
        Directory.Delete(Path.Combine(root, "logs"));

        Assert.Equal(DataDirectoryCheckResult.Secure, _guard.EnsureSecure(root).Result);

        Assert.Equal(["config", "credentials", "data", "logs"], Directory.GetDirectories(root).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(["config", "credentials", "data", "logs"], DataDirectoryGuard.FixedSubdirectories);
        foreach (var name in DataDirectoryGuard.FixedSubdirectories)
        {
            var security = new DirectoryInfo(Path.Combine(root, name)).GetAccessControl();
            Assert.False(security.AreAccessRulesProtected);
            Assert.Empty(security.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier)));
            Assert.True(DataDirectoryGuard.IsChildCompliant(security, isDirectory: true));
            Assert.Contains(security.GetOwner(typeof(SecurityIdentifier)), new IdentityReference[] { System, Administrators });
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void A_junction_with_a_fixed_subdirectory_name_is_refused_when_elevated()
    {
        // Sin elevar la raíz es del usuario y se aparta antes de llegar al contenido: solo tiene sentido elevado.
        Assert.SkipUnless(WindowsTestEnvironment.IsElevated, "Requires an elevated process (Administrators as owner).");
        foreach (var name in DataDirectoryGuard.FixedSubdirectories)
        {
            using var temp = new TempDirectory();
            var root = Directory.CreateDirectory(temp.Combine("Backbone")).FullName;
            var outside = Directory.CreateDirectory(temp.Combine("fuera")).FullName;
            CreateJunction(Path.Combine(root, name), outside);
            TrySecureRoot(root);

            var ex = Assert.Throws<DataDirectoryInsecureException>(() => _guard.EnsureSecure(root));

            Assert.Contains("reparse point", ex.Message, StringComparison.Ordinal);
            Assert.True(Directory.Exists(root)); // no se ha apartado: se rechaza
            Assert.Empty(Directory.EnumerateFileSystemEntries(outside)); // nada creado a través del enlace
            Assert.True(new DirectoryInfo(Path.Combine(root, name)).Attributes.HasFlag(FileAttributes.ReparsePoint));
        }
    }

    [Fact]
    public void Acl_propagation_with_SetSecurityInfo_does_not_follow_a_junction_into_its_target()
    {
        // B-b (comprobación sin elevar del supuesto en que se basa el orden de EnsureSecure): la corrección de la raíz con
        // SetSecurityInfo propaga las ACE heredables a los hijos, pero no entra en el destino de un junction hijo, así que no
        // hace falta validar el contenido antes de corregir la raíz. La prueba elevada con el guardián completo está en
        // Backbone.IntegrationTests (DataDirectoryGuardElevatedTests).
        var root = Directory.CreateDirectory(_temp.Combine("raiz")).FullName;
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        var outside = Directory.CreateDirectory(_temp.Combine("fuera")).FullName;
        Directory.CreateDirectory(Path.Combine(outside, "dentro"));
        CreateJunction(Path.Combine(root, "enlace"), outside);
        var outsideBefore = new DirectoryInfo(outside).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        var insideBefore = new DirectoryInfo(Path.Combine(outside, "dentro")).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);

        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { WindowsTestEnvironment.CurrentUserSid, Users })
        {
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        }

        var raw = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        var dacl = new byte[raw.DiscretionaryAcl!.BinaryLength];
        raw.DiscretionaryAcl.GetBinaryForm(dacl, 0);
        using (var handle = NativeMethods.CreateFile(
            root, NativeMethods.WriteDac | NativeMethods.ReadControl | NativeMethods.FileReadAttributes | NativeMethods.Synchronize,
            NativeMethods.FileShareRead | NativeMethods.FileShareWrite | NativeMethods.FileShareDelete, 0, NativeMethods.OpenExisting,
            NativeMethods.FileFlagBackupSemantics | NativeMethods.FileFlagOpenReparsePoint, 0))
        {
            Assert.False(handle.IsInvalid);
            Assert.Equal(0, NativeMethods.SetSecurityInfo(handle, NativeMethods.SeFileObject,
                NativeMethods.DaclSecurityInformation | NativeMethods.ProtectedDaclSecurityInformation, 0, 0, dacl, 0));
        }

        // Se propaga a los hijos normales…
        Assert.Contains(new DirectoryInfo(Path.Combine(root, "sub")).GetAccessControl().GetAccessRules(false, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(),
            r => (SecurityIdentifier)r.IdentityReference == Users);
        // …pero no al destino del junction ni a lo que hay dentro.
        Assert.Equal(outsideBefore, new DirectoryInfo(outside).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access));
        Assert.Equal(insideBefore, new DirectoryInfo(Path.Combine(outside, "dentro")).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access));
    }

    private const int FullControlMask = (int)FileSystemRights.FullControl;

    /// <summary>Descriptor sin protección, propietario Administradores, cuya DACL solo tiene las ACE indicadas (marcadas a mano).</summary>
    private static DirectorySecurity InheritedOnly(params CommonAce[] aces)
    {
        var dacl = new RawAcl(GenericAcl.AclRevision, aces.Length);
        for (var i = 0; i < aces.Length; i++)
        {
            dacl.InsertAce(i, aces[i]);
        }

        var raw = new RawSecurityDescriptor(
            ControlFlags.DiscretionaryAclPresent | ControlFlags.SelfRelative, Administrators, null, null, dacl);
        var bytes = new byte[raw.BinaryLength];
        raw.GetBinaryForm(bytes, 0);
        var security = new DirectorySecurity();
        security.SetSecurityDescriptorBinaryForm(bytes);
        return security;
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
        var file = Path.Combine(root, "config", "settings.json"); // config\ la ha creado la comprobación (§8.3)

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
            // Nueva y vacía salvo las subcarpetas fijas que crea la comprobación (§8.3).
            Assert.Equal(DataDirectoryGuard.FixedSubdirectories, Directory.EnumerateFileSystemEntries(root).Select(Path.GetFileName).Order(StringComparer.Ordinal));
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
