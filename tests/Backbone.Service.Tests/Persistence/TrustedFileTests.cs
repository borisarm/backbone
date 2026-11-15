using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using Backbone.Service.Os;
using Backbone.Service.Persistence;
using Backbone.Service.Tests.TestSupport;

namespace Backbone.Service.Tests.Persistence;

/// <summary>
/// §8.2 / ADR-14 (R9): los almacenes abren los archivos de la carpeta de datos por handle, sin seguir enlaces, y rechazan los
/// que no son de SYSTEM o Administradores. Lo que exige asignar Administradores como propietario está en
/// <c>Backbone.IntegrationTests/Persistence/TrustedFileElevatedTests</c>.
/// </summary>
[Trait("Story", "HT-03")]
public sealed class TrustedFileTests : IDisposable
{
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    private readonly TempDirectory _temp = new();
    private readonly TrustedFile _testFiles = TestFileTrust.CurrentUser;
    private readonly TrustedFile _productionFiles = new(FileTrustPolicy.Default);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void The_production_policy_trusts_only_SYSTEM_and_Administrators_and_creates_with_the_RNF07_descriptor()
    {
        var policy = FileTrustPolicy.Default;

        Assert.True(policy.IsTrustedOwner(LocalSystem));
        Assert.True(policy.IsTrustedOwner(Administrators));
        Assert.False(policy.IsTrustedOwner(new SecurityIdentifier(TestSids.Ana)));
        Assert.False(policy.IsTrustedOwner(new SecurityIdentifier(TestSids.Users)));
        Assert.False(policy.IsTrustedOwner(null));

        var security = new FileSecurity();
        security.SetSecurityDescriptorBinaryForm(policy.BuildCreateSecurityDescriptor());
        Assert.Equal(Administrators, security.GetOwner(typeof(SecurityIdentifier)));
        Assert.True(security.AreAccessRulesProtected);
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        Assert.True(rules.Select(r => (SecurityIdentifier)r.IdentityReference).ToHashSet().SetEquals([LocalSystem, Administrators]));
        Assert.Equal(2, rules.Count);
        Assert.All(rules, r =>
        {
            Assert.Equal(AccessControlType.Allow, r.AccessControlType);
            Assert.Equal(FileSystemRights.FullControl, r.FileSystemRights);
            Assert.False(r.IsInherited);
        });
    }

    [Fact]
    public async Task A_missing_file_reads_as_null()
    {
        Assert.Null(await _testFiles.ReadAllBytesAsync(_temp.Combine("no-existe.json"), 1024, Ct));
    }

    [Fact]
    public async Task A_missing_parent_folder_fails_every_operation_and_is_not_created()
    {
        var missing = _temp.Combine("config");
        var path = Path.Combine(missing, "settings.json");

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => _testFiles.ReadAllBytesAsync(path, 1024, Ct));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => _testFiles.WriteAllBytesAsync(path, "{}"u8.ToArray(), Ct));
        Assert.Throws<DirectoryNotFoundException>(() => _testFiles.MoveReplacing(path, path + ".bad"));

        Assert.False(Directory.Exists(missing)); // los almacenes no crean carpetas (§8.2)
        Assert.Empty(Directory.EnumerateFileSystemEntries(_temp.Path));
    }

    [Fact]
    public async Task A_file_owned_by_a_user_is_rejected_without_reading_it()
    {
        var path = CreateUserOwnedFile("settings.json", "{}");

        var ex = await Assert.ThrowsAsync<UntrustedFileException>(() => _productionFiles.ReadAllBytesAsync(path, 1024, Ct));

        Assert.Equal(UntrustedFileReason.UntrustedOwner, ex.Reason);
        // Sin elevar, la carpeta temporal también es del usuario y se rechaza antes (como carpeta padre); elevado, la carpeta
        // es de Administradores y se rechaza el archivo.
        Assert.Equal(WindowsTestEnvironment.IsElevated ? path : Path.GetDirectoryName(path), ex.FilePath);
    }

    [Fact]
    public async Task A_parent_folder_owned_by_a_user_is_rejected_as_a_file()
    {
        Assert.SkipWhen(WindowsTestEnvironment.IsElevated, "Elevated, the temporary folder is owned by Administrators.");
        var path = CreateCurrentUserFile("settings.json", "{}");

        var read = await Assert.ThrowsAsync<UntrustedFileException>(() => _productionFiles.ReadAllBytesAsync(path, 1024, Ct));
        var write = await Assert.ThrowsAsync<UntrustedFileException>(() => _productionFiles.WriteAllBytesAsync(path, "{\"v\":2}"u8.ToArray(), Ct));
        var move = Assert.Throws<UntrustedFileException>(() => _productionFiles.MoveReplacing(path, path + ".bad"));

        Assert.All([read, write, move], ex =>
        {
            Assert.Equal(UntrustedFileReason.UntrustedOwner, ex.Reason);
            Assert.Equal(_temp.Path, ex.FilePath);
        });
        Assert.Equal("{}", await File.ReadAllTextAsync(path, Ct));
        Assert.Equal(["settings.json"], Directory.GetFiles(_temp.Path).Select(Path.GetFileName));
    }

    [Fact]
    public async Task A_junction_as_the_parent_folder_is_rejected_and_nothing_is_written_or_read_in_its_target()
    {
        // Revisión final de HT-03 (M-a): config\ creada de antemano como junction hacia otra carpeta.
        var target = Directory.CreateDirectory(_temp.Combine("destino")).FullName;
        File.WriteAllText(Path.Combine(target, "settings.json"), "original");
        var config = _temp.Combine("config");
        RunCmd("mklink", "/J", config, target);
        var path = Path.Combine(config, "settings.json");

        var write = await Assert.ThrowsAsync<UntrustedFileException>(() => _testFiles.WriteAllBytesAsync(path, "{\"v\":2}"u8.ToArray(), Ct));
        var read = await Assert.ThrowsAsync<UntrustedFileException>(() => _testFiles.ReadAllBytesAsync(path, 1024, Ct));
        var move = Assert.Throws<UntrustedFileException>(() => _testFiles.MoveReplacing(path, path + ".bad"));

        Assert.All([write, read, move], ex =>
        {
            Assert.Equal(UntrustedFileReason.ReparsePoint, ex.Reason);
            Assert.Equal(config, ex.FilePath);
        });
        Assert.Equal(["settings.json"], Directory.EnumerateFileSystemEntries(target).Select(Path.GetFileName));
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(target, "settings.json"), Ct));
        Assert.True(new DirectoryInfo(config).Attributes.HasFlag(FileAttributes.ReparsePoint));
    }

    [Fact]
    public void A_file_in_place_of_the_parent_folder_is_rejected()
    {
        var notAFolder = CreateCurrentUserFile("config", "x");

        var ex = Assert.Throws<UntrustedFileException>(() => _testFiles.MoveReplacing(Path.Combine(notAFolder, "a.json"), Path.Combine(notAFolder, "a.json.bad")));

        Assert.Equal(UntrustedFileReason.NotAFile, ex.Reason);
    }

    [Fact]
    public void While_the_parent_folder_is_held_open_neither_it_nor_any_folder_above_can_be_renamed()
    {
        // Lo que garantiza mantener abierta la carpeta padre (acceso a datos, sin FILE_SHARE_DELETE) durante la operación:
        // nadie puede renombrarla ni renombrar una carpeta superior para sustituirla por un junction a mitad.
        var config = Directory.CreateDirectory(_temp.Combine("config")).FullName;

        using (OpenParentLikeTrustedFile(config))
        {
            Assert.ThrowsAny<IOException>(() => Directory.Move(config, config + "-renombrada"));
            Assert.ThrowsAny<IOException>(() => Directory.Move(_temp.Path, _temp.Path + "-renombrada"));
        }

        Directory.Move(config, config + "-renombrada");
    }

    [Fact]
    public async Task A_junction_in_place_of_the_file_is_rejected_and_not_followed()
    {
        var target = Directory.CreateDirectory(_temp.Combine("destino")).FullName;
        File.WriteAllText(Path.Combine(target, "settings.json"), "{}");
        var link = _temp.Combine("settings.json");
        RunCmd("mklink", "/J", link, target);

        var ex = await Assert.ThrowsAsync<UntrustedFileException>(() => _testFiles.ReadAllBytesAsync(link, 1024, Ct));

        Assert.Equal(UntrustedFileReason.ReparsePoint, ex.Reason);
    }

    [Fact]
    public async Task A_symbolic_link_in_place_of_the_file_is_rejected_and_not_followed()
    {
        var target = CreateCurrentUserFile("real.json", "{}");
        var link = _temp.Combine("settings.json");
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (IOException)
        {
            Assert.Skip("Creating symbolic links requires SeCreateSymbolicLinkPrivilege or developer mode.");
        }
        catch (UnauthorizedAccessException)
        {
            Assert.Skip("Creating symbolic links requires SeCreateSymbolicLinkPrivilege or developer mode.");
        }

        var ex = await Assert.ThrowsAsync<UntrustedFileException>(() => _testFiles.ReadAllBytesAsync(link, 1024, Ct));

        Assert.Equal(UntrustedFileReason.ReparsePoint, ex.Reason);
    }

    [Fact]
    public async Task A_directory_in_place_of_the_file_is_rejected()
    {
        var path = Directory.CreateDirectory(_temp.Combine("settings.json")).FullName;

        var ex = await Assert.ThrowsAsync<UntrustedFileException>(() => _testFiles.ReadAllBytesAsync(path, 1024, Ct));

        Assert.Equal(UntrustedFileReason.NotAFile, ex.Reason);
    }

    [Fact]
    public async Task A_hard_linked_file_is_rejected()
    {
        var other = CreateCurrentUserFile("otro.json", "{}");
        var path = _temp.Combine("settings.json");
        RunCmd("mklink", "/H", path, other);

        var ex = await Assert.ThrowsAsync<UntrustedFileException>(() => _testFiles.ReadAllBytesAsync(path, 1024, Ct));

        Assert.Equal(UntrustedFileReason.HardLinked, ex.Reason);
    }

    [Fact]
    public async Task A_file_with_a_trusted_owner_is_read_from_the_same_handle()
    {
        var path = CreateCurrentUserFile("settings.json", "{\"a\":1}");

        var bytes = await _testFiles.ReadAllBytesAsync(path, 1024, Ct);

        Assert.Equal("{\"a\":1}", System.Text.Encoding.UTF8.GetString(bytes!));
    }

    [Fact]
    public async Task An_oversized_file_is_not_read()
    {
        var path = CreateCurrentUserFile("settings.json", new string('a', 2048));

        await Assert.ThrowsAsync<InvalidDataException>(() => _testFiles.ReadAllBytesAsync(path, 1024, Ct));
    }

    [Fact]
    public async Task The_atomic_write_creates_the_file_with_the_explicit_owner_and_protected_DACL_and_leaves_no_temporary()
    {
        Directory.CreateDirectory(_temp.Combine("config"));
        var path = _temp.Combine("config", "settings.json");
        var policy = _testFiles.Policy;

        await _testFiles.WriteAllBytesAsync(path, "{\"v\":1}"u8.ToArray(), Ct);
        await _testFiles.WriteAllBytesAsync(path, "{\"v\":2}"u8.ToArray(), Ct);

        Assert.Equal("{\"v\":2}", await File.ReadAllTextAsync(path, Ct));
        var security = new FileInfo(path).GetAccessControl();
        Assert.Equal(policy.CreateOwner, security.GetOwner(typeof(SecurityIdentifier)));
        Assert.True(security.AreAccessRulesProtected); // no hereda ACE de la carpeta
        var sids = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Select(r => (SecurityIdentifier)r.IdentityReference).ToHashSet();
        Assert.True(sids.SetEquals(policy.FullControlSids));
        Assert.Equal(["settings.json"], Directory.GetFiles(Path.GetDirectoryName(path)!).Select(Path.GetFileName));
    }

    [Fact]
    public async Task The_atomic_write_does_not_replace_a_junction_and_removes_its_temporary()
    {
        var target = Directory.CreateDirectory(_temp.Combine("destino")).FullName;
        var link = _temp.Combine("settings.json");
        RunCmd("mklink", "/J", link, target);

        var ex = await Assert.ThrowsAsync<UntrustedFileException>(() => _testFiles.WriteAllBytesAsync(link, "{}"u8.ToArray(), Ct));

        Assert.Equal(UntrustedFileReason.ReparsePoint, ex.Reason);
        Assert.True(new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint));
        Assert.Empty(Directory.EnumerateFileSystemEntries(target));
        Assert.Empty(Directory.GetFiles(_temp.Path, "*.tmp"));
    }

    [Fact]
    public async Task The_atomic_write_does_not_replace_a_hard_linked_destination()
    {
        var other = CreateCurrentUserFile("otro.json", "original");
        var path = _temp.Combine("settings.json");
        RunCmd("mklink", "/H", path, other);

        var ex = await Assert.ThrowsAsync<UntrustedFileException>(() => _testFiles.WriteAllBytesAsync(path, "{}"u8.ToArray(), Ct));

        Assert.Equal(UntrustedFileReason.HardLinked, ex.Reason);
        Assert.Equal("original", await File.ReadAllTextAsync(other, Ct));
        Assert.Empty(Directory.GetFiles(_temp.Path, "*.tmp"));
    }

    [Fact]
    public async Task Without_being_able_to_assign_Administrators_the_production_write_fails_and_leaves_nothing()
    {
        Assert.SkipWhen(WindowsTestEnvironment.IsElevated, "Elevated, Administrators can be assigned as owner (see the integration tests).");
        var path = _temp.Combine("settings.json");
        // Se confía en la carpeta temporal (del usuario) para llegar a la creación del temporal con Administradores como
        // propietario, que es lo que falla sin elevar.
        var policy = new FileTrustPolicy(Administrators, [LocalSystem, Administrators, WindowsTestEnvironment.CurrentUserSid], [LocalSystem, Administrators]);

        await Assert.ThrowsAnyAsync<IOException>(() => new TrustedFile(policy).WriteAllBytesAsync(path, "{}"u8.ToArray(), Ct));

        Assert.Empty(Directory.EnumerateFileSystemEntries(_temp.Path));
    }

    [Fact]
    public async Task MoveReplacing_renames_a_trusted_file_and_rejects_a_reparse_point()
    {
        var path = CreateCurrentUserFile("settings.json", "dañado");

        Assert.True(_testFiles.MoveReplacing(path, path + ".bad"));
        Assert.False(_testFiles.MoveReplacing(path, path + ".bad"));
        Assert.Equal("dañado", await File.ReadAllTextAsync(path + ".bad", Ct));
        Assert.False(File.Exists(path));

        var target = Directory.CreateDirectory(_temp.Combine("destino")).FullName;
        var link = _temp.Combine("enlace.json");
        RunCmd("mklink", "/J", link, target);
        Assert.Equal(UntrustedFileReason.ReparsePoint, Assert.Throws<UntrustedFileException>(() => _testFiles.MoveReplacing(link, link + ".bad")).Reason);
        Assert.True(Directory.Exists(link));
    }

    [Fact]
    public void MoveReplacing_only_renames_within_the_same_folder()
    {
        var path = CreateCurrentUserFile("settings.json", "dañado");
        var other = Directory.CreateDirectory(_temp.Combine("otra")).FullName;

        Assert.Throws<ArgumentException>(() => _testFiles.MoveReplacing(path, Path.Combine(other, "settings.json.bad")));

        Assert.True(File.Exists(path));
    }

    /// <summary>Abre una carpeta como <see cref="TrustedFile"/> abre la carpeta padre (acceso a datos, sin FILE_SHARE_DELETE).</summary>
    private static Microsoft.Win32.SafeHandles.SafeFileHandle OpenParentLikeTrustedFile(string path)
    {
        var handle = NativeMethods.CreateFile(
            path, NativeMethods.FileListDirectory | NativeMethods.ReadControl | NativeMethods.FileReadAttributes | NativeMethods.Synchronize,
            NativeMethods.FileShareRead | NativeMethods.FileShareWrite, 0, NativeMethods.OpenExisting,
            NativeMethods.FileFlagBackupSemantics | NativeMethods.FileFlagOpenReparsePoint, 0);
        Assert.False(handle.IsInvalid);
        return handle;
    }

    /// <summary>Archivo cuyo propietario es el usuario de las pruebas (lo creado elevado sería de Administradores).</summary>
    internal static string CreateUserOwnedFile(TempDirectory temp, string name, string content)
    {
        var path = temp.Combine(name);
        File.WriteAllText(path, content);
        var info = new FileInfo(path);
        var security = info.GetAccessControl();
        security.SetOwner(WindowsTestEnvironment.CurrentUserSid);
        info.SetAccessControl(security);
        Assert.Equal(WindowsTestEnvironment.CurrentUserSid, new FileInfo(path).GetAccessControl().GetOwner(typeof(SecurityIdentifier)));
        return path;
    }

    internal static void RunCmd(params string[] arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", .. arguments])
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("cmd.exe did not start.");
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    private string CreateUserOwnedFile(string name, string content) => CreateUserOwnedFile(_temp, name, content);

    private string CreateCurrentUserFile(string name, string content)
    {
        var path = _temp.Combine(name);
        File.WriteAllText(path, content);
        return path;
    }
}
