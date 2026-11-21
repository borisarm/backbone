using System.Diagnostics;
using Backbone.Contracts;
using Backbone.Service.Hosting;
using Backbone.Service.Jobs;
using Backbone.Service.Os;
using Backbone.Service.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backbone.Service.Tests.Jobs;

/// <summary>
/// HU-07 criterios 2, 3 y 6: casos límite añadidos por QA sobre las pruebas del implementador (alias sin distinguir
/// mayúsculas con Unicode, <i>junctions</i> a la carpeta de datos y a las apartadas, mayúsculas, 100 carpetas apartadas
/// exactas frente a 101, rutas de más de 260 caracteres, y la misma regla con carpetas, <i>junctions</i> y archivos reales).
/// </summary>
[Trait("Story", "HU-07")]
public sealed class JobValidatorCriteriaGapTests : IDisposable
{
    private const string Documents = @"C:\Users\ana\Documentos";
    private const string Quarantined = @"C:\ProgramData\Backbone.untrusted-0123456789abcdef0123456789abcdef";

    private readonly FakeLocalFileSystemInspector _fileSystem = new FakeLocalFileSystemInspector()
        .AddFolder(Documents)
        .AddFolder(@"C:\")
        .AddFolder(@"C:\ProgramData")
        .AddFolder(@"C:\ProgramData\Backbone")
        .AddFolder(@"C:\ProgramData\Backbone\credentials")
        .AddFolder(Quarantined);

    private readonly TempDirectory _temp = new();
    private readonly List<string> _junctions = [];

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

    private JobValidator Validator => TestJobValidators.Create(_fileSystem, @"C:\ProgramData\Backbone");

    private static JobDefinition Job(params JobSource[] sources) =>
        JobDefaults.NewJob() with { Name = "Prueba", Sources = [.. sources], Destination = @"\\nas\backups\pc" };

    private static JobSource Source(string path, string alias = "Origen") => new() { Alias = alias, Path = path };

    // ---- Criterio 2: alias repetidos sin distinguir mayúsculas ----

    [Fact]
    public async Task Aliases_equal_ignoring_case_with_unicode_letters_are_duplicates()
    {
        var result = await Validator.CheckAsync(Job(Source(Documents, "Álbum"), Source(Documents, "ÁLBUM"), Source(Documents, "ñandú"), Source(Documents, "ÑANDÚ")), []);

        Assert.Equal(
            [("Sources[1].Alias", ValidationIssueCode.AliasDuplicate), ("Sources[3].Alias", ValidationIssueCode.AliasDuplicate)],
            result.Errors.Select(e => (e.Field, e.Code)));
    }

    [Fact]
    public async Task Three_equal_aliases_mark_every_repetition_but_not_the_first()
    {
        var result = await Validator.CheckAsync(Job(Source(Documents, "Docs"), Source(Documents, "docs"), Source(Documents, "DOCS")), []);

        Assert.Equal(["Sources[1].Alias", "Sources[2].Alias"], result.Errors.Select(e => e.Field));
        Assert.All(result.Errors, e => Assert.Equal(ValidationIssueCode.AliasDuplicate, e.Code));
    }

    [Fact]
    public async Task Different_aliases_with_the_same_path_are_allowed()
    {
        Assert.True((await Validator.CheckAsync(Job(Source(Documents, "A"), Source(Documents, "B")), [])).IsValid);
    }

    // ---- Criterio 6: la carpeta de datos, mayúsculas, formas de la ruta y junctions ----

    [Theory]
    [InlineData(@"c:\programdata\backbone")]
    [InlineData(@"C:\PROGRAMDATA\BACKBONE\CREDENTIALS")]
    [InlineData(@"C:\ProgramData\BACKBONE.UNTRUSTED-0123456789ABCDEF0123456789ABCDEF")]
    public async Task Case_variants_of_a_protected_folder_are_still_SourceNotAllowed(string path)
    {
        _fileSystem.AddFolder(path);

        var result = await Validator.CheckAsync(Job(Source(path)), []);

        var error = Assert.Single(result.Errors);
        Assert.Equal(("Sources[0].Path", ValidationIssueCode.SourceNotAllowed), (error.Field, error.Code));
    }

    [Theory]
    [InlineData(@"C:\ProgramData\Backbone")]
    [InlineData(@"C:\ProgramData\Backbone\credentials")]
    [InlineData(@"c:\PROGRAMDATA\backbone\credentials")]
    [InlineData(Quarantined)]
    public async Task A_junction_to_the_data_folder_to_its_interior_or_to_a_quarantined_folder_is_SourceNotAllowed(string target)
    {
        _fileSystem.AddFolder(@"C:\Users\ana\Atajo", finalPath: target);

        var result = await Validator.CheckAsync(Job(Source(Documents, "Docs"), Source(@"C:\Users\ana\Atajo", "Atajo")), []);

        var error = Assert.Single(result.Errors);
        Assert.Equal(("Sources[1].Path", ValidationIssueCode.SourceNotAllowed), (error.Field, error.Code));
    }

    [Fact]
    public async Task A_junction_to_a_folder_that_contains_the_data_folder_gets_the_warning_not_the_error()
    {
        _fileSystem.AddFolder(@"C:\Users\ana\Atajo", finalPath: @"C:\ProgramData");

        var result = await Validator.CheckAsync(Job(Source(@"C:\Users\ana\Atajo")), []);

        Assert.True(result.IsValid);
        var warning = Assert.Single(result.Warnings);
        Assert.Equal(("Sources[0].Path", ValidationIssueCode.SourceContainsDataFolder), (warning.Field, warning.Code));
    }

    [Fact]
    public async Task A_path_longer_than_260_characters_inside_the_data_folder_is_SourceNotAllowed()
    {
        var path = @"C:\ProgramData\Backbone\credentials\" + string.Join('\\', Enumerable.Range(0, 12).Select(_ => new string('x', 30)));
        Assert.True(path.Length > 260);
        _fileSystem.AddFolder(path);

        var error = Assert.Single((await Validator.CheckAsync(Job(Source(path)), [])).Errors);

        Assert.Equal(("Sources[0].Path", ValidationIssueCode.SourceNotAllowed), (error.Field, error.Code));
    }

    [Fact]
    public async Task A_sibling_whose_name_only_shares_the_prefix_is_not_protected()
    {
        _fileSystem.AddFolder(@"C:\ProgramData\BackboneOtra").AddFolder(@"C:\ProgramData\Backbone.untrusted").AddFolder(@"C:\ProgramData\Backbone.bak");

        foreach (var path in new[] { @"C:\ProgramData\BackboneOtra", @"C:\ProgramData\Backbone.untrusted", @"C:\ProgramData\Backbone.bak" })
        {
            Assert.True((await Validator.CheckAsync(Job(Source(path)), [])).IsValid, path);
        }
    }

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"C:\ProgramData")]
    public async Task A_containing_source_is_valid_and_warns_once_per_source(string path)
    {
        var result = await Validator.CheckAsync(Job(Source(path, "A"), Source(path, "B")), []);

        Assert.True(result.IsValid);
        Assert.Equal(["Sources[0].Path", "Sources[1].Path"], result.Warnings.Select(w => w.Field));
        Assert.All(result.Warnings, w => Assert.Equal(ValidationIssueCode.SourceContainsDataFolder, w.Code));
    }

    [Fact]
    public async Task A_source_that_is_not_related_to_the_data_folder_has_no_warning()
    {
        Assert.Empty((await Validator.CheckAsync(Job(Source(Documents)), [])).Warnings);
    }

    // ---- Criterio 6: carpetas apartadas, 100 exactas y 101 ----

    [Fact]
    public async Task With_exactly_100_quarantined_folders_a_containing_source_is_saved_with_the_warning()
    {
        _fileSystem.QuarantinedFolders.AddRange(Enumerable.Range(0, 100).Select(i => $"Backbone.untrusted-{i:D3}"));

        var result = await Validator.CheckAsync(Job(Source(@"C:\")), []);

        Assert.True(result.IsValid);
        Assert.Equal(ValidationIssueCode.SourceContainsDataFolder, Assert.Single(result.Warnings).Code);
    }

    [Fact]
    public async Task With_101_quarantined_folders_failing_closed_has_an_error_and_no_warning_and_other_sources_are_unaffected()
    {
        _fileSystem.QuarantinedFolders.AddRange(Enumerable.Range(0, 101).Select(i => $"Backbone.untrusted-{i:D3}"));

        var result = await Validator.CheckAsync(Job(Source(@"C:\", "Disco"), Source(@"C:\ProgramData", "PD"), Source(Documents, "Docs")), []);

        Assert.Equal(["Sources[0].Path", "Sources[1].Path"], result.Errors.Select(e => e.Field));
        Assert.All(result.Errors, e => Assert.Equal(ValidationIssueCode.SourceNotAllowed, e.Code));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task With_101_quarantined_folders_the_data_folder_itself_is_still_rejected_by_location()
    {
        _fileSystem.QuarantinedFolders.AddRange(Enumerable.Range(0, 101).Select(i => $"Backbone.untrusted-{i:D3}"));

        var error = Assert.Single((await Validator.CheckAsync(Job(Source(@"C:\ProgramData\Backbone\credentials")), [])).Errors);

        Assert.Equal(ValidationIssueCode.SourceNotAllowed, error.Code);
    }

    [Fact]
    public async Task When_listing_the_quarantined_folders_fails_a_containing_source_fails_closed()
    {
        _fileSystem.EnumerationFailure = new IOException("acceso denegado");

        var error = Assert.Single((await Validator.CheckAsync(Job(Source(@"C:\")), [])).Errors);

        Assert.Equal(("Sources[0].Path", ValidationIssueCode.SourceNotAllowed), (error.Field, error.Code));
    }

    // ---- Criterio 6 con el sistema de archivos real ----

    [Fact]
    public async Task Real_more_than_100_quarantined_folders_fail_closed_but_exactly_100_do_not()
    {
        var inspector = new WindowsLocalFileSystemInspector();
        var programData = Directory.CreateDirectory(_temp.Combine("PD")).FullName;
        var dataRoot = Directory.CreateDirectory(Path.Join(programData, "Backbone")).FullName;
        for (var i = 0; i < 100; i++)
        {
            Directory.CreateDirectory(Path.Join(programData, $"Backbone.untrusted-{i:D3}"));
        }

        SkipUnlessNtfsFixed(inspector, programData);
        var validator = TestJobValidators.Create(inspector, dataRoot);
        var job = Job(Source(programData));

        var exact = await validator.CheckAsync(job, []);
        Directory.CreateDirectory(Path.Join(programData, "Backbone.untrusted-extra"));
        var over = await validator.CheckAsync(job, []);

        Assert.True(exact.IsValid);
        Assert.Equal(ValidationIssueCode.SourceContainsDataFolder, Assert.Single(exact.Warnings).Code);
        var error = Assert.Single(over.Errors);
        Assert.Equal(("Sources[0].Path", ValidationIssueCode.SourceNotAllowed), (error.Field, error.Code));
    }

    [Fact]
    public async Task Real_junction_and_file_with_the_quarantine_prefix_do_not_count_and_do_not_protect_their_target()
    {
        var inspector = new WindowsLocalFileSystemInspector();
        var programData = Directory.CreateDirectory(_temp.Combine("PD")).FullName;
        var dataRoot = Directory.CreateDirectory(Path.Join(programData, "Backbone")).FullName;
        var victim = Directory.CreateDirectory(_temp.Combine("Usuarios", "ana", "Documentos")).FullName;
        File.WriteAllText(Path.Join(victim, "a.txt"), "x");
        CreateJunction(Path.Join(programData, "Backbone.untrusted-trampa"), victim);
        File.WriteAllText(Path.Join(programData, "Backbone.untrusted-archivo"), "x");
        SkipUnlessNtfsFixed(inspector, programData);
        var resolver = new ProtectedFolderResolver(new ServicePaths(dataRoot), inspector, NullLogger<ProtectedFolderResolver>.Instance);

        var set = resolver.Resolve(TestIo.Guard());

        Assert.True(set.IsComplete);
        Assert.Empty(set.QuarantinedFolderNames);
        Assert.True((await TestJobValidators.Create(inspector, resolver).CheckAsync(Job(Source(victim)), [])).IsValid);
    }

    [Fact]
    public async Task Real_junction_to_the_data_folder_to_a_deep_subfolder_and_a_path_over_260_characters_are_rejected()
    {
        var inspector = new WindowsLocalFileSystemInspector();
        var programData = Directory.CreateDirectory(_temp.Combine("PD")).FullName;
        var dataRoot = Directory.CreateDirectory(Path.Join(programData, "Backbone")).FullName;
        var deep = dataRoot;
        for (var i = 0; i < 9; i++)
        {
            deep = Path.Join(deep, new string('d', 30));
        }

        Directory.CreateDirectory(@"\\?\" + deep);
        Assert.True(deep.Length > 260);
        var toRoot = _temp.Combine("atajo-raiz");
        var toDeep = _temp.Combine("atajo-profundo");
        CreateJunction(toRoot, dataRoot);
        CreateJunction(toDeep, deep);
        SkipUnlessNtfsFixed(inspector, programData);
        var validator = TestJobValidators.Create(inspector, dataRoot);

        foreach (var path in new[] { toRoot, toDeep, deep, dataRoot.ToUpperInvariant() })
        {
            var result = await validator.CheckAsync(Job(Source(path)), []);
            var error = Assert.Single(result.Errors);
            Assert.Equal(("Sources[0].Path", ValidationIssueCode.SourceNotAllowed), (error.Field, error.Code));
        }
    }

    private static void SkipUnlessNtfsFixed(WindowsLocalFileSystemInspector inspector, string path)
    {
        var info = inspector.ResolveFolder(path, TestIo.Guard());
        Assert.SkipUnless(
            info.Status == LocalFolderStatus.Ok && info.FileSystem == "NTFS",
            "La carpeta temporal no está en un disco fijo NTFS.");
    }

    private void CreateJunction(string link, string target)
    {
        var startInfo = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
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
