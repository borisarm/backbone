using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using Backbone.Service.Os;
using Backbone.Service.Tests.TestSupport;

namespace Backbone.IntegrationTests.Os;

/// <summary>
/// §8.3 con el guardián real y la política de producción: solo elevado se puede asignar Administradores como propietario.
/// Requiere PowerShell elevado.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Story", "HT-03")]
public sealed class DataDirectoryGuardElevatedTests : IDisposable
{
    private const string NeedsElevation = "Requires an elevated process (Administrators as owner).";

    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    private readonly TempDirectory _temp = new();
    private readonly DataDirectoryGuard _guard = new();

    public static bool IsElevated => WindowsTestEnvironment.IsElevated;

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// B-b (revisión final de HT-03): raíz de propietario fiable con una DACL no conforme (se corrige en el sitio con
    /// <c>SetSecurityInfo</c>, que propaga a los hijos) y un <i>junction</i> hijo hacia una carpeta externa. La comprobación
    /// rechaza el <i>junction</i> y la ACL de la carpeta externa (y de lo que contiene) no cambia: la propagación no entra en
    /// el destino de un punto de reanálisis.
    /// </summary>
    [Fact(Skip = NeedsElevation, SkipUnless = nameof(IsElevated))]
    public void Correcting_a_root_with_a_child_junction_does_not_change_the_security_of_the_junction_target()
    {
        var root = Directory.CreateDirectory(_temp.Combine("Backbone")).FullName;
        Assert.Equal(Administrators, new DirectoryInfo(root).GetAccessControl().GetOwner(typeof(SecurityIdentifier)));
        Assert.False(DataDirectoryGuard.IsRootCompliant(new DirectoryInfo(root).GetAccessControl())); // hereda de %TEMP%
        var outside = Directory.CreateDirectory(_temp.Combine("fuera")).FullName;
        var inside = Directory.CreateDirectory(Path.Combine(outside, "dentro")).FullName;
        File.WriteAllText(Path.Combine(inside, "archivo.txt"), "x");
        CreateJunction(Path.Combine(root, "enlace"), outside);
        var before = Snapshot(outside, inside, Path.Combine(inside, "archivo.txt"));

        var ex = Assert.Throws<DataDirectoryInsecureException>(() => _guard.EnsureSecure(root));

        Assert.Contains("reparse point", ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, Snapshot(outside, inside, Path.Combine(inside, "archivo.txt")));
        Assert.True(DataDirectoryGuard.IsRootCompliant(new DirectoryInfo(root).GetAccessControl())); // la raíz sí se corrigió
        Assert.Equal(["dentro"], Directory.EnumerateFileSystemEntries(outside).Select(Path.GetFileName));
    }

    [Fact(Skip = NeedsElevation, SkipUnless = nameof(IsElevated))]
    public void A_new_root_gets_the_fixed_subdirectories_and_the_next_check_is_secure()
    {
        var root = _temp.Combine("Backbone");

        Assert.Equal(DataDirectoryCheckResult.Corrected, _guard.EnsureSecure(root).Result);

        Assert.Equal(["config", "credentials", "data", "logs"], Directory.GetDirectories(root).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(DataDirectoryCheckResult.Secure, _guard.EnsureSecure(root).Result);
    }

    [Fact(Skip = NeedsElevation, SkipUnless = nameof(IsElevated))]
    public void A_fixed_subdirectory_owned_by_a_user_is_refused()
    {
        var root = _temp.Combine("Backbone");
        _guard.EnsureSecure(root);
        var config = new DirectoryInfo(Path.Combine(root, "config"));
        var security = config.GetAccessControl();
        security.SetOwner(WindowsTestEnvironment.CurrentUserSid);
        config.SetAccessControl(security);

        Assert.Throws<DataDirectoryInsecureException>(() => _guard.EnsureSecure(root));
    }

    private static string[] Snapshot(params string[] paths) =>
        [.. paths.Select(p => (File.Exists(p)
            ? (FileSystemSecurity)new FileInfo(p).GetAccessControl()
            : new DirectoryInfo(p).GetAccessControl()).GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access))];

    private static void CreateJunction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, target])
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("cmd.exe did not start.");
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }
}
