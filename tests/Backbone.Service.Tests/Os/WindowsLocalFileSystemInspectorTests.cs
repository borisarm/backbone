using System.Diagnostics;
using Backbone.Contracts;
using Backbone.Service.Hosting;
using Backbone.Service.Jobs;
using Backbone.Service.Os;
using Backbone.Service.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backbone.Service.Tests.Os;

/// <summary>
/// <see cref="WindowsLocalFileSystemInspector"/> con la API real de Windows sobre carpetas temporales (sin elevar): la
/// resolución controlada de ADR-20 (§8.3) con <i>junctions</i> y enlaces simbólicos reales, incluidos los destinos que un
/// usuario sin privilegios puede plantar (UNC, <c>\RPC Control</c>, <c>GLOBALROOT</c>, <c>\Device\Mup</c>), y las carpetas
/// apartadas reales (M-B). Al final, la validación de HU-07 criterio 6 de punta a punta.
/// </summary>
/// <remarks>
/// Los destinos de red usan <c>192.0.2.1</c> (TEST-NET-1, nunca responde): si la resolución abriera el destino, SMB tardaría
/// decenas de segundos en rendirse; se exige que termine en menos de <see cref="NoNetworkBudget"/> y con
/// <see cref="LocalFolderStatus.NotLocal"/> por el destino del enlace (no por la red). Es la prueba de que no hay ningún
/// intento de conexión.
/// </remarks>
[Trait("Story", "HU-07")]
public sealed class WindowsLocalFileSystemInspectorTests : IDisposable
{
    private const string UnreachableServer = "192.0.2.1";
    private static readonly TimeSpan NoNetworkBudget = TimeSpan.FromSeconds(2);

    private readonly TempDirectory _temp = new();
    private readonly List<string> _links = [];
    private readonly WindowsLocalFileSystemInspector _inspector = new();

    public void Dispose()
    {
        foreach (var link in Enumerable.Reverse(_links))
        {
            try
            {
                Directory.Delete(link);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        _temp.Dispose();
    }

    // ---- Carpetas reales ----

    [Fact]
    public void A_local_folder_reports_its_final_path_and_NTFS()
    {
        var folder = Directory.CreateDirectory(_temp.Combine("Documentos")).FullName;
        SkipUnlessNtfsFixed(folder);

        var info = Resolve(folder);

        Assert.Equal(LocalFolderStatus.Ok, info.Status);
        Assert.Equal(folder, info.FinalPath, ignoreCase: true);
        Assert.Equal("NTFS", info.FileSystem);
    }

    [Fact]
    public void A_drive_root_resolves_to_itself()
    {
        var root = Path.GetPathRoot(_temp.Path) ?? @"C:\";
        var info = Resolve(root);

        Assert.Equal(LocalFolderStatus.Ok, info.Status);
        Assert.Equal(root, info.FinalPath, ignoreCase: true);
    }

    [Fact]
    public void A_missing_folder_a_missing_drive_or_a_file_are_NotFound()
    {
        var file = _temp.Combine("archivo.txt");
        File.WriteAllText(file, "x");

        Assert.Equal(LocalFolderStatus.NotFound, Resolve(_temp.Combine("no", "existe")).Status);
        Assert.Equal(LocalFolderStatus.NotFound, Resolve(file).Status);
        Assert.Equal(LocalFolderStatus.NotFound, Resolve(Path.Join(file, "debajo")).Status);
        var missingDrive = Enumerable.Range('D', 23).Select(c => $"{(char)c}:\\").FirstOrDefault(d => !Directory.Exists(d));
        if (missingDrive is not null)
        {
            Assert.Equal(LocalFolderStatus.NotFound, Resolve(missingDrive).Status);
        }
    }

    [Theory]
    [InlineData(@"\\nas\fotos")]
    [InlineData(@"\\?\C:\Windows")]
    [InlineData(@"C:")]
    [InlineData(@"C:relativa")]
    [InlineData("")]
    [InlineData(@"c:\Windows")]
    [InlineData(@"C:\Windows\")]
    [InlineData(@"C:\Windows\.\System32")]
    [InlineData(@"C:\ProgramData::$INDEX_ALLOCATION")]
    [InlineData(@"C:\Windows.")]
    public void Paths_that_are_not_normalized_are_invalid_and_not_opened(string path)
    {
        using var guard = TestIo.Guard();

        Assert.Equal(LocalFolderStatus.InvalidPath, _inspector.ResolveFolder(path, guard).Status);
        Assert.Equal(0, guard.Operations);
    }

    [Fact]
    public void Paths_longer_than_260_characters_are_supported()
    {
        var path = _temp.Path;
        while (path.Length < 300)
        {
            path = Path.Join(path, new string('d', 40));
        }

        Directory.CreateDirectory(path);
        SkipUnlessNtfsFixed(_temp.Path);

        var info = Resolve(path);

        Assert.Equal(LocalFolderStatus.Ok, info.Status);
        Assert.Equal(path, info.FinalPath, ignoreCase: true);
    }

    // ---- Enlaces locales legítimos ----

    [Fact]
    public void A_local_junction_to_another_NTFS_folder_is_followed_and_the_final_path_is_its_target()
    {
        var target = Directory.CreateDirectory(_temp.Combine("destino", "real")).FullName;
        var link = Junction(_temp.Combine("enlace"), target);

        var info = Resolve(link);

        Assert.Equal(LocalFolderStatus.Ok, info.Status);
        Assert.Equal(target, info.FinalPath, ignoreCase: true);
    }

    [Fact]
    public void A_junction_in_an_intermediate_component_is_followed_component_by_component()
    {
        var target = Directory.CreateDirectory(_temp.Combine("destino", "real", "sub", "hoja")).FullName;
        var link = Junction(_temp.Combine("enlace"), _temp.Combine("destino", "real"));

        var info = Resolve(Path.Join(link, "sub", "hoja"));

        Assert.Equal(LocalFolderStatus.Ok, info.Status);
        Assert.Equal(target, info.FinalPath, ignoreCase: true);
    }

    [Fact]
    public void A_junction_to_a_volume_guid_path_is_followed()
    {
        var target = Directory.CreateDirectory(_temp.Combine("por-guid")).FullName;
        var guidRoot = VolumeGuidRoot(target);
        Assert.SkipWhen(guidRoot is null, "No se ha podido obtener la ruta de volumen.");
        var link = MountPoint(_temp.Combine("enlace-guid"), @"\??\" + guidRoot + target[3..]);

        var info = Resolve(link);

        Assert.Equal(LocalFolderStatus.Ok, info.Status);
        Assert.Equal(target, info.FinalPath, ignoreCase: true);
    }

    [Fact]
    public void A_relative_directory_symlink_inside_the_volume_is_composed_lexically()
    {
        var target = Directory.CreateDirectory(_temp.Combine("a", "destino")).FullName;
        Directory.CreateDirectory(_temp.Combine("b"));
        var link = Symlink(_temp.Combine("b", "rel"), @"..\a\destino");

        var info = Resolve(link);

        Assert.Equal(LocalFolderStatus.Ok, info.Status);
        Assert.Equal(target, info.FinalPath, ignoreCase: true);
    }

    // ---- Destinos que no son un volumen local: SourceNotLocal sin abrirlos (ALTA-1) ----

    [Fact]
    public void A_directory_symlink_to_a_UNC_path_is_NotLocal_without_any_connection_attempt()
    {
        var link = Symlink(_temp.Combine("red"), $@"\\{UnreachableServer}\r");

        AssertNotLocalWithoutNetwork(link);
    }

    [Fact]
    public void A_directory_symlink_to_a_UNC_path_in_an_intermediate_component_is_NotLocal_without_any_connection_attempt()
    {
        var link = Symlink(_temp.Combine("red"), $@"\\{UnreachableServer}\r");

        AssertNotLocalWithoutNetwork(Path.Join(link, "sub", "carpeta"));
    }

    [Theory]
    [InlineData(@"\??\UNC\192.0.2.1\r")]
    [InlineData(@"\RPC Control\x")]
    [InlineData(@"\??\GLOBALROOT\Device\Mup\192.0.2.1\r")]
    [InlineData(@"\GLOBALROOT\Device\Mup\192.0.2.1\r")]
    [InlineData(@"\Device\Mup\192.0.2.1\r")]
    [InlineData(@"\Device\HarddiskVolume1\Windows")]
    [InlineData(@"\??\C:\a:b")]
    [InlineData(@"\??\Volume{no-es-un-guid}\x")]
    [InlineData(@"\\?\C:\Windows")]
    public void A_junction_to_a_target_that_is_not_a_local_volume_is_NotLocal_without_opening_it(string substituteName)
    {
        var link = MountPoint(_temp.Combine("trampa"), substituteName);

        AssertNotLocalWithoutNetwork(link);
        AssertNotLocalWithoutNetwork(Path.Join(link, "debajo"));
    }

    [Fact]
    public void A_relative_symlink_that_climbs_above_the_volume_root_is_NotLocal()
    {
        var depth = _temp.Path.Count(c => c == '\\');
        var link = Symlink(_temp.Combine("fuera"), string.Concat(Enumerable.Repeat(@"..\", depth + 2)) + "Windows");

        Assert.Equal(LocalFolderStatus.NotLocal, Resolve(link).Status);
    }

    [Fact]
    public void A_loop_of_junctions_is_Unreadable()
    {
        var a = _temp.Combine("bucle-a");
        var b = _temp.Combine("bucle-b");
        MountPoint(a, @"\??\" + b);
        MountPoint(b, @"\??\" + a);

        var info = Resolve(a);

        Assert.Equal(LocalFolderStatus.Unreadable, info.Status);
        Assert.Contains("63", info.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Up_to_63_hops_are_followed_and_the_64th_is_Unreadable()
    {
        var target = Directory.CreateDirectory(_temp.Combine("final")).FullName;
        var chain = new List<string>();
        var next = target;
        for (var i = 64; i >= 1; i--)
        {
            var link = _temp.Combine($"salto-{i:D2}");
            MountPoint(link, @"\??\" + next);
            chain.Insert(0, link);
            next = link;
        }

        // chain[0] necesita 64 saltos; chain[1], 63.
        Assert.Equal(LocalFolderStatus.Ok, Resolve(chain[1]).Status);
        Assert.Equal(target, Resolve(chain[1]).FinalPath, ignoreCase: true);
        Assert.Equal(LocalFolderStatus.Unreadable, Resolve(chain[0]).Status);
    }

    [Fact]
    public void The_resolution_never_touches_a_network_path_even_when_the_rejected_link_is_retried_many_times()
    {
        var link = MountPoint(_temp.Combine("trampa"), $@"\??\UNC\{UnreachableServer}\r");
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 20; i++)
        {
            Assert.Equal(LocalFolderStatus.NotLocal, Resolve(link).Status);
        }

        Assert.True(watch.Elapsed < NoNetworkBudget, $"20 resoluciones han tardado {watch.Elapsed}.");
    }

    // ---- Carpetas apartadas (M-B, MEDIA-1) ----

    [Fact]
    public void Only_real_folders_with_the_prefix_count_as_quarantined_and_junctions_or_files_are_ignored()
    {
        var programData = Directory.CreateDirectory(_temp.Combine("PD")).FullName;
        Directory.CreateDirectory(Path.Join(programData, "Backbone.untrusted-aaaa"));
        Directory.CreateDirectory(Path.Join(programData, "BACKBONE.UNTRUSTED-bbbb"));
        Directory.CreateDirectory(Path.Join(programData, "Backbone.untrustedX"));
        Directory.CreateDirectory(Path.Join(programData, "Otra"));
        File.WriteAllText(Path.Join(programData, "Backbone.untrusted-archivo"), "x");
        var victim = Directory.CreateDirectory(_temp.Combine("Usuarios", "alice", "Documentos")).FullName;
        Junction(Path.Join(programData, "Backbone.untrusted-trampa"), victim);
        MountPoint(Path.Join(programData, "Backbone.untrusted-red"), $@"\??\UNC\{UnreachableServer}\r");

        using var guard = TestIo.Guard();
        var watch = Stopwatch.StartNew();
        var scan = _inspector.FindRealDirectories(programData, "Backbone.untrusted-", 100, guard);

        Assert.True(watch.Elapsed < NoNetworkBudget);
        Assert.False(scan.LimitExceeded);
        Assert.Equal(["BACKBONE.UNTRUSTED-bbbb", "Backbone.untrusted-aaaa"], scan.Names.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_scan_stops_after_finding_one_more_than_the_maximum()
    {
        var programData = Directory.CreateDirectory(_temp.Combine("PD")).FullName;
        for (var i = 0; i < 10; i++)
        {
            Directory.CreateDirectory(Path.Join(programData, $"Backbone.untrusted-{i}"));
        }

        using var guard = TestIo.Guard();
        var scan = _inspector.FindRealDirectories(programData, "Backbone.untrusted-", 3, guard);
        var operationsAtLimit = guard.Operations;
        using var fullGuard = TestIo.Guard();
        var full = _inspector.FindRealDirectories(programData, "Backbone.untrusted-", 100, fullGuard);

        Assert.True(scan.LimitExceeded);
        Assert.Equal(3, scan.Names.Count);
        Assert.False(full.LimitExceeded);
        Assert.Equal(10, full.Names.Count);
        Assert.True(operationsAtLimit < fullGuard.Operations, "Debe dejar de abrir entradas al encontrar la cuarta.");
    }

    [Fact]
    public void Enumerating_a_missing_folder_or_a_link_to_the_network_throws_IOException()
    {
        var link = MountPoint(_temp.Combine("red"), $@"\??\UNC\{UnreachableServer}\r");
        using var guard = TestIo.Guard();

        Assert.ThrowsAny<IOException>(() => _inspector.FindRealDirectories(_temp.Combine("no-existe"), "x", 1, guard));
        Assert.ThrowsAny<IOException>(() => _inspector.FindRealDirectories(link, "x", 1, guard));
    }

    // ---- Análisis del destino de un enlace (puro) ----

    [Theory]
    [InlineData(@"\??\C:\Users\ana", false, "C:", @"Users\ana")]
    [InlineData(@"\??\d:\", false, "D:", "")]
    [InlineData(@"\??\Volume{01234567-89ab-cdef-0123-456789abcdef}\x\y", false, "Volume{01234567-89ab-cdef-0123-456789abcdef}", @"x\y")]
    [InlineData(@"..\otra", true, "C:", @"base\otra")]
    [InlineData(@".\sub\.\x", true, "C:", @"base\enlace-padre\sub\x")]
    [InlineData(@"\raiz", true, "C:", "raiz")]
    public void Admitted_link_targets_are_composed(string target, bool relative, string volume, string components)
    {
        var next = WindowsLocalFileSystemInspector.ComposeTarget(new(target, relative), "C:", ["base", "enlace-padre"]);

        Assert.NotNull(next);
        Assert.Equal(volume, next.Value.Volume);
        Assert.Equal(components, string.Join('\\', next.Value.Components));
    }

    [Theory]
    [InlineData(@"\??\UNC\host\r", false)]
    [InlineData(@"\??\GLOBALROOT\Device\Mup\host\r", false)]
    [InlineData(@"\Device\Mup\host\r", false)]
    [InlineData(@"\RPC Control\x", false)]
    [InlineData(@"\\?\C:\x", false)]
    [InlineData(@"\??\C:x", false)]
    [InlineData(@"\??\C:\a:b", false)]
    [InlineData(@"\??\C:\a\\b", false)]
    [InlineData(@"\??\C:\a\..\b", false)]
    [InlineData(@"\??\Volume{0123}\x", false)]
    [InlineData("", false)]
    [InlineData(@"..\..\..\x", true)]
    [InlineData(@"\??\C:\x", true)]
    [InlineData(@"\\host\r", true)]
    [InlineData(@"a:b", true)]
    public void Other_link_targets_are_rejected(string target, bool relative)
    {
        Assert.Null(WindowsLocalFileSystemInspector.ComposeTarget(new(target, relative), "C:", ["base", "enlace-padre"]));
    }

    [Fact]
    public void A_truncated_or_inconsistent_reparse_buffer_is_rejected()
    {
        Assert.Null(WindowsLocalFileSystemInspector.ParseReparseBuffer(new byte[10], 0xA000_0003));
        var buffer = new byte[24];
        BitConverter.TryWriteBytes(buffer.AsSpan(0), 0xA000_0003u);
        BitConverter.TryWriteBytes(buffer.AsSpan(10), (ushort)400);
        Assert.Null(WindowsLocalFileSystemInspector.ParseReparseBuffer(buffer, 0xA000_0003));
        Assert.Null(WindowsLocalFileSystemInspector.ParseReparseBuffer(buffer, 0xA000_000C));
    }

    // ---- Criterio 6 de punta a punta ----

    /// <summary>
    /// HU-07 criterio 6 con el sistema real: <c>PD</c> es una carpeta temporal con la carpeta de datos, una carpeta apartada
    /// real y un <i>junction</i> con el prefijo hacia una carpeta de usuario.
    /// </summary>
    [Fact]
    public async Task Criterion_6_end_to_end_with_real_folders_and_junctions()
    {
        var programData = Directory.CreateDirectory(_temp.Combine("PD")).FullName;
        var dataRoot = Directory.CreateDirectory(Path.Join(programData, "Backbone")).FullName;
        Directory.CreateDirectory(Path.Join(dataRoot, "credentials"));
        var quarantined = Directory.CreateDirectory(Path.Join(programData, "Backbone.untrusted-0123")).FullName;
        var victim = Directory.CreateDirectory(_temp.Combine("Usuarios", "alice", "Documentos")).FullName;
        Junction(Path.Join(programData, "Backbone.untrusted-trampa"), victim);
        var toData = Junction(_temp.Combine("atajo-a-credenciales"), Path.Join(dataRoot, "credentials"));
        SkipUnlessNtfsFixed(programData);

        var validator = TestJobValidators.Create(_inspector, dataRoot);
        Task<ValidationResult> ValidateAsync(string path) => validator.CheckAsync(
            new JobDefinition { Name = "Prueba", Sources = [new JobSource { Alias = "Origen", Path = path }], Destination = @"\\nas\backups" },
            []);

        // El origen es la carpeta de datos, está dentro, es una carpeta apartada real o un junction hacia su interior.
        foreach (var path in new[] { dataRoot, Path.Join(dataRoot, "credentials"), quarantined, toData })
        {
            var error = Assert.Single((await ValidateAsync(path)).Errors);
            Assert.Equal(("Sources[0].Path", ValidationIssueCode.SourceNotAllowed), (error.Field, error.Code));
        }

        // La carpeta a la que apunta el junction con prefijo no es una carpeta protegida (la ubicación manda).
        var victimResult = await ValidateAsync(victim);
        Assert.True(victimResult.IsValid);
        Assert.Empty(victimResult.Warnings);

        // Un origen que contiene PD se guarda con aviso, y las exclusiones son la carpeta de datos y la apartada real.
        var containing = await ValidateAsync(_temp.Path);
        Assert.True(containing.IsValid);
        Assert.Equal(ValidationIssueCode.SourceContainsDataFolder, Assert.Single(containing.Warnings).Code);
        using var guard = TestIo.Guard();
        var protection = new ProtectedFolderResolver(new ServicePaths(dataRoot), _inspector, NullLogger<ProtectedFolderResolver>.Instance)
            .Resolve(guard)
            .Classify(Resolve(_temp.Path).GuidPath);
        Assert.Equal([@"PD\Backbone", @"PD\Backbone.untrusted-0123"], protection.MandatoryExclusions);
    }

    [Fact]
    public async Task A_network_link_source_is_SourceNotLocal_end_to_end_and_fast()
    {
        var link = MountPoint(_temp.Combine("red"), $@"\??\UNC\{UnreachableServer}\r");
        var validator = TestJobValidators.Create(_inspector, _temp.Combine("PD", "Backbone"));
        var watch = Stopwatch.StartNew();

        var result = await validator.CheckAsync(
            new JobDefinition { Name = "Prueba", Sources = [new JobSource { Alias = "Red", Path = Path.Join(link, "x") }], Destination = @"\\nas\backups" },
            []);

        Assert.True(watch.Elapsed < NoNetworkBudget);
        var error = Assert.Single(result.Errors);
        Assert.Equal(("Sources[0].Path", ValidationIssueCode.SourceNotLocal), (error.Field, error.Code));
    }

    // ---- Utilidades ----

    private LocalFolderInfo Resolve(string path)
    {
        Assert.Equal(SourcePathCheck.Ok, SourcePath.TryNormalize(path, out var normalized));
        using var guard = TestIo.Guard();
        return _inspector.ResolveFolder(normalized, guard);
    }

    private void AssertNotLocalWithoutNetwork(string path)
    {
        var watch = Stopwatch.StartNew();
        var info = Resolve(path);
        watch.Stop();

        Assert.Equal(LocalFolderStatus.NotLocal, info.Status);
        Assert.Contains("link target", info.Detail, StringComparison.Ordinal);
        Assert.True(watch.Elapsed < NoNetworkBudget, $"La resolución ha tardado {watch.Elapsed}: ¿ha intentado conectar?");
    }

    private void SkipUnlessNtfsFixed(string path)
    {
        var info = Resolve(path);
        Assert.SkipUnless(info.Status == LocalFolderStatus.Ok && info.FileSystem == "NTFS", "La carpeta temporal no está en un disco fijo NTFS.");
    }

    private string MountPoint(string link, string substituteName)
    {
        ReparseLinks.CreateMountPoint(link, substituteName);
        _links.Add(link);
        return link;
    }

    private string Symlink(string link, string target)
    {
        Assert.SkipUnless(ReparseLinks.TryCreateDirectorySymlink(link, target), "Sin elevar, los enlaces simbólicos requieren el modo de desarrollador.");
        _links.Add(link);
        return link;
    }

    private static string? VolumeGuidRoot(string path) => ReparseLinks.VolumeGuidRoot(path);

    /// <summary>Crea un <i>junction</i> con <c>mklink /J</c> (no requiere privilegios).</summary>
    private string Junction(string link, string target)
    {
        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(link);
        startInfo.ArgumentList.Add(target);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("No se pudo ejecutar mklink.");
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"mklink /J falló: {output}");
        _links.Add(link);
        return link;
    }
}
