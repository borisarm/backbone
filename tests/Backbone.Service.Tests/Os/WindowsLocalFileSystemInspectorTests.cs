using System.Diagnostics;
using Backbone.Contracts;
using Backbone.Service.Hosting;
using Backbone.Service.Jobs;
using Backbone.Service.Os;
using Backbone.Service.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backbone.Service.Tests.Os;

/// <summary>
/// <see cref="WindowsLocalFileSystemInspector"/> con la API real de Windows sobre carpetas temporales (sin elevar): ruta
/// final siguiendo <i>junctions</i>, sistema de archivos y tipo de volumen, y carpetas apartadas reales (M-B). Al final,
/// la validación de HU-07 criterio 6 de punta a punta con carpetas, <i>junctions</i> y archivos de verdad.
/// </summary>
[Trait("Story", "HU-07")]
public sealed class WindowsLocalFileSystemInspectorTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly List<string> _junctions = [];
    private readonly WindowsLocalFileSystemInspector _inspector = new();

    public void Dispose()
    {
        foreach (var junction in _junctions)
        {
            try
            {
                Directory.Delete(junction);
            }
            catch (IOException)
            {
            }
        }

        _temp.Dispose();
    }

    [Fact]
    public void A_local_folder_reports_its_final_path_NTFS_and_a_fixed_disk()
    {
        var folder = Directory.CreateDirectory(_temp.Combine("Documentos")).FullName;
        SkipUnlessNtfsFixed(folder);

        var info = _inspector.InspectFolder(folder + @"\.\");

        Assert.Equal(LocalFolderStatus.Ok, info.Status);
        Assert.Equal(folder, info.FinalPath, ignoreCase: true);
        Assert.Equal("NTFS", info.FileSystem);
        Assert.Equal(LocalVolumeKind.Fixed, info.Volume);
    }

    [Fact]
    public void A_missing_folder_or_a_file_are_reported_as_such()
    {
        var file = _temp.Combine("archivo.txt");
        File.WriteAllText(file, "x");

        Assert.Equal(LocalFolderStatus.NotFound, _inspector.InspectFolder(_temp.Combine("no", "existe")).Status);
        Assert.Equal(LocalFolderStatus.NotADirectory, _inspector.InspectFolder(file).Status);
    }

    [Theory]
    [InlineData(@"\\nas\fotos")]
    [InlineData(@"\\?\C:\Windows")]
    [InlineData(@"\\.\C:\Windows")]
    [InlineData(@"Documentos")]
    [InlineData(@"C:")]
    [InlineData(@"C:relativa")]
    [InlineData("")]
    [InlineData("C:\\a\0b")]
    public void Paths_that_are_not_absolute_local_drive_paths_are_invalid_and_not_opened(string path)
    {
        Assert.Equal(LocalFolderStatus.InvalidPath, _inspector.InspectFolder(path).Status);
    }

    [Fact]
    public void A_junction_is_followed_and_the_final_path_is_its_target()
    {
        var target = Directory.CreateDirectory(_temp.Combine("destino", "real")).FullName;
        var link = _temp.Combine("enlace");
        CreateJunction(link, target);

        var info = _inspector.InspectFolder(link);

        Assert.Equal(LocalFolderStatus.Ok, info.Status);
        Assert.Equal(target, info.FinalPath, ignoreCase: true);
        Assert.Equal(target, _inspector.GetFinalDirectoryPath(link), ignoreCase: true);
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

        var info = _inspector.InspectFolder(path);

        Assert.Equal(LocalFolderStatus.Ok, info.Status);
        Assert.Equal(path, info.FinalPath, ignoreCase: true);
    }

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
        CreateJunction(Path.Join(programData, "Backbone.untrusted-trampa"), victim);

        var scan = _inspector.FindRealDirectories(programData, "Backbone.untrusted-", 100);

        Assert.Equal(2, scan.TotalCount);
        Assert.Equal(["BACKBONE.UNTRUSTED-bbbb", "Backbone.untrusted-aaaa"], scan.Names.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_scan_counts_beyond_the_maximum_but_returns_at_most_the_maximum()
    {
        var programData = Directory.CreateDirectory(_temp.Combine("PD")).FullName;
        for (var i = 0; i < 5; i++)
        {
            Directory.CreateDirectory(Path.Join(programData, $"Backbone.untrusted-{i}"));
        }

        var scan = _inspector.FindRealDirectories(programData, "Backbone.untrusted-", 3);

        Assert.Equal(5, scan.TotalCount);
        Assert.Equal(3, scan.Names.Count);
    }

    [Fact]
    public void Enumerating_a_missing_folder_throws_IOException()
    {
        Assert.ThrowsAny<IOException>(() => _inspector.FindRealDirectories(_temp.Combine("no-existe"), "x", 1));
    }

    /// <summary>
    /// HU-07 criterio 6 con el sistema real: <c>PD</c> es una carpeta temporal con la carpeta de datos, una carpeta apartada
    /// real y un <i>junction</i> con el prefijo hacia una carpeta de usuario.
    /// </summary>
    [Fact]
    public void Criterion_6_end_to_end_with_real_folders_and_junctions()
    {
        var programData = Directory.CreateDirectory(_temp.Combine("PD")).FullName;
        var dataRoot = Directory.CreateDirectory(Path.Join(programData, "Backbone")).FullName;
        Directory.CreateDirectory(Path.Join(dataRoot, "credentials"));
        var quarantined = Directory.CreateDirectory(Path.Join(programData, "Backbone.untrusted-0123")).FullName;
        var victim = Directory.CreateDirectory(_temp.Combine("Usuarios", "alice", "Documentos")).FullName;
        CreateJunction(Path.Join(programData, "Backbone.untrusted-trampa"), victim);
        var toData = _temp.Combine("atajo-a-credenciales");
        CreateJunction(toData, Path.Join(dataRoot, "credentials"));
        SkipUnlessNtfsFixed(programData);

        var validator = new JobValidator(_inspector, new ProtectedFolderResolver(new ServicePaths(dataRoot), _inspector, NullLogger<ProtectedFolderResolver>.Instance));
        ValidationResult Validate(string path) => validator.Validate(
            new JobDefinition { Name = "Prueba", Sources = [new JobSource { Alias = "Origen", Path = path }], Destination = @"\\nas\backups" },
            []);

        // El origen es la carpeta de datos, está dentro, es una carpeta apartada real o un junction hacia su interior.
        foreach (var path in new[] { dataRoot, Path.Join(dataRoot, "credentials"), quarantined, toData })
        {
            var error = Assert.Single(Validate(path).Errors);
            Assert.Equal(("Sources[0].Path", ValidationIssueCode.SourceNotAllowed), (error.Field, error.Code));
        }

        // La carpeta a la que apunta el junction con prefijo no es una carpeta protegida (la ubicación manda).
        var victimResult = Validate(victim);
        Assert.True(victimResult.IsValid);
        Assert.Empty(victimResult.Warnings);

        // Un origen que contiene PD se guarda con aviso, y las exclusiones son la carpeta de datos y la apartada real.
        var containing = Validate(_temp.Path);
        Assert.True(containing.IsValid);
        Assert.Equal(ValidationIssueCode.SourceContainsDataFolder, Assert.Single(containing.Warnings).Code);
        var protection = new ProtectedFolderResolver(new ServicePaths(dataRoot), _inspector, NullLogger<ProtectedFolderResolver>.Instance)
            .Resolve()
            .Classify(_inspector.InspectFolder(_temp.Path).FinalPath);
        Assert.Equal([@"PD\Backbone", @"PD\Backbone.untrusted-0123"], protection.MandatoryExclusions);
    }

    private void SkipUnlessNtfsFixed(string path)
    {
        var info = _inspector.InspectFolder(path);
        Assert.SkipUnless(
            info.Status == LocalFolderStatus.Ok && info.FileSystem == "NTFS" && info.Volume == LocalVolumeKind.Fixed,
            "La carpeta temporal no está en un disco fijo NTFS.");
    }

    /// <summary>Crea un <i>junction</i> con <c>mklink /J</c> (no requiere privilegios).</summary>
    private void CreateJunction(string link, string target)
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
        _junctions.Add(link);
    }
}
