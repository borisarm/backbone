using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using Backbone.Service.Os;
using Backbone.Service.Tests.TestSupport;

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
        Assert.Equal([Administrators, System], rules.Select(r => (SecurityIdentifier)r.IdentityReference).OrderBy(s => s.Value));
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

        Assert.Throws<DataDirectoryInsecureException>(() => _guard.EnsureSecure(root));
    }

    [Fact]
    public void A_directory_created_by_this_user_is_corrected_when_elevated_and_refused_otherwise()
    {
        var root = Directory.CreateDirectory(_temp.Combine("Backbone")).FullName;

        if (WindowsTestEnvironment.IsElevated)
        {
            Assert.Equal(DataDirectoryCheckResult.Corrected, _guard.EnsureSecure(root));
            Assert.True(DataDirectoryGuard.IsRootCompliant(new DirectoryInfo(root).GetAccessControl()));
            Assert.Equal(DataDirectoryCheckResult.Secure, _guard.EnsureSecure(root));
        }
        else
        {
            // Sin elevar no se puede asignar Administradores como propietario: se rechaza en lugar de usarla tal cual.
            Assert.Throws<DataDirectoryInsecureException>(() => _guard.EnsureSecure(root));
        }
    }

    [Fact]
    public void A_non_empty_directory_owned_by_an_untrusted_user_is_refused()
    {
        var root = Directory.CreateDirectory(_temp.Combine("Backbone")).FullName;
        File.WriteAllText(Path.Combine(root, "plantado.json"), "{}");
        if (WindowsTestEnvironment.IsElevated)
        {
            // Elevado, lo creado es de Administradores: se fuerza un propietario de usuario.
            var security = new DirectoryInfo(root).GetAccessControl();
            security.SetOwner(WindowsTestEnvironment.CurrentUserSid);
            new DirectoryInfo(root).SetAccessControl(security);
        }

        var ex = Assert.Throws<DataDirectoryInsecureException>(() => _guard.EnsureSecure(root));

        Assert.Contains("untrusted", ex.Message, StringComparison.Ordinal);
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
