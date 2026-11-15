using System.Xml.Linq;

namespace Backbone.Architecture.Tests;

/// <summary>
/// HT-01 criterios 2, 3 y 4: propiedades de compilación, versiones centralizadas de NuGet,
/// solución completa y flujo de CI. También refuerza el criterio 5 con reglas sobre paquetes.
/// </summary>
[Trait("Story", "HT-01")]
public sealed class BuildConfigurationTests
{
    private static string RepoFile(params string[] parts) =>
        Path.Combine([SolutionLayout.RepositoryRoot.FullName, .. parts]);

    private static IEnumerable<string> AllProjectFiles() =>
        Directory.EnumerateFiles(SolutionLayout.RepositoryRoot.FullName, "*.*proj", SearchOption.AllDirectories)
            .Where(p => p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                     || p.EndsWith(".wixproj", StringComparison.OrdinalIgnoreCase))
            .Where(p => !Path.GetRelativePath(SolutionLayout.RepositoryRoot.FullName, p)
                .Split(Path.DirectorySeparatorChar)
                .Any(IsIgnoredDirectory));

    /// <summary>
    /// Carpetas que no son parte del árbol de fuentes: salidas de compilación (<c>bin</c>, <c>obj</c>, <c>artifacts</c>) y
    /// carpetas ocultas (<c>.git</c>, <c>.vs</c>, <c>.claude</c>, cuyos <i>worktrees</i> traen otra copia de los proyectos).
    /// </summary>
    private static bool IsIgnoredDirectory(string segment) =>
        segment is "bin" or "obj" or "artifacts" || segment.StartsWith('.');

    private static string? Prop(XDocument doc, string name, string? conditionContains = null) =>
        doc.Descendants("PropertyGroup")
            .Where(g => conditionContains is null
                ? g.Attribute("Condition") is null
                : ((string?)g.Attribute("Condition"))?.Contains(conditionContains) == true)
            .Elements()
            .Where(e => e.Name.LocalName == name)
            .Select(e => e.Value.Trim())
            .FirstOrDefault();

    // ---- Criterio 2 ----

    [Fact]
    public void DirectoryBuildProps_fixes_the_common_properties()
    {
        var doc = XDocument.Load(RepoFile("Directory.Build.props"));

        Assert.Equal("net10.0", Prop(doc, "TargetFramework"));
        Assert.Equal("enable", Prop(doc, "Nullable"));
        Assert.Equal("enable", Prop(doc, "ImplicitUsings"));
        Assert.Equal("latest-recommended", Prop(doc, "AnalysisLevel"));
    }

    [Fact]
    public void DirectoryBuildProps_audits_all_nuget_packages_from_low_severity()
    {
        var doc = XDocument.Load(RepoFile("Directory.Build.props"));

        Assert.Equal("true", Prop(doc, "NuGetAudit"));
        Assert.Equal("all", Prop(doc, "NuGetAuditMode"));
        Assert.Equal("low", Prop(doc, "NuGetAuditLevel"));
    }

    [Fact]
    public void Warnings_are_errors_only_when_building_in_CI()
    {
        var doc = XDocument.Load(RepoFile("Directory.Build.props"));

        // Sin condición: no debe fijarse (si no, rompería el desarrollo local).
        Assert.Null(Prop(doc, "TreatWarningsAsErrors"));
        Assert.Equal("true", Prop(doc, "TreatWarningsAsErrors", "ContinuousIntegrationBuild"));
        // CI y GITHUB_ACTIONS activan ContinuousIntegrationBuild.
        var ciGroup = doc.Descendants("PropertyGroup")
            .Select(g => (string?)g.Attribute("Condition") ?? "")
            .Single(c => c.Contains("GITHUB_ACTIONS"));
        Assert.Contains("'$(CI)' == 'true'", ciGroup);
    }

    [Fact]
    public void No_project_overrides_the_common_quality_properties()
    {
        var offenders = new List<string>();
        foreach (var path in AllProjectFiles())
        {
            var names = XDocument.Load(path).Descendants()
                .Select(e => e.Name.LocalName)
                .Where(n => n is "Nullable" or "ImplicitUsings" or "AnalysisLevel"
                              or "TreatWarningsAsErrors" or "NoWarn" or "WarningLevel")
                .Distinct();
            foreach (var n in names)
            {
                offenders.Add($"{Path.GetFileName(path)}: {n}");
            }
        }

        // NoWarn en el instalador (ICE71 suprimido hasta HU-01) se admite de forma explícita.
        offenders.RemoveAll(o => o.StartsWith("Backbone.Installer.wixproj", StringComparison.Ordinal));
        Assert.True(offenders.Count == 0, "Propiedades sobrescritas: " + string.Join("; ", offenders));
    }

    // ---- Criterio 3 ----

    [Fact]
    public void DirectoryPackagesProps_enables_central_package_management()
    {
        var doc = XDocument.Load(RepoFile("Directory.Packages.props"));

        Assert.Equal("true", Prop(doc, "ManagePackageVersionsCentrally"));
    }

    [Fact]
    public void No_project_declares_package_versions_inline()
    {
        var offenders = new List<string>();
        foreach (var path in AllProjectFiles())
        {
            foreach (var e in XDocument.Load(path).Descendants().Where(e => e.Name.LocalName == "PackageReference"))
            {
                if (e.Attribute("Version") is not null || e.Attribute("VersionOverride") is not null
                    || e.Elements().Any(c => c.Name.LocalName == "Version"))
                {
                    offenders.Add($"{Path.GetFileName(path)}: {(string?)e.Attribute("Include")}");
                }
            }
        }

        Assert.True(offenders.Count == 0, "Versiones fuera de Directory.Packages.props: " + string.Join("; ", offenders));
    }

    [Fact]
    public void Every_referenced_package_has_a_central_version_and_every_central_version_is_used()
    {
        var central = XDocument.Load(RepoFile("Directory.Packages.props"))
            .Descendants().Where(e => e.Name.LocalName == "PackageVersion")
            .ToDictionary(e => (string)e.Attribute("Include")!, e => (string?)e.Attribute("Version"),
                StringComparer.OrdinalIgnoreCase);

        Assert.All(central, kv => Assert.False(string.IsNullOrWhiteSpace(kv.Value), $"{kv.Key} sin versión"));
        // Solo versiones estables (sin sufijo de prerrelease).
        Assert.All(central, kv => Assert.DoesNotContain('-', kv.Value!));

        var used = AllProjectFiles()
            .SelectMany(p => XDocument.Load(p).Descendants().Where(e => e.Name.LocalName == "PackageReference"))
            .Select(e => (string)e.Attribute("Include")!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Los proyectos WiX (Sdk="WixToolset.Sdk/x.y.z") no usan PackageReference; solo comprobamos sentido único.
        Assert.Empty(used.Except(central.Keys, StringComparer.OrdinalIgnoreCase));
    }

    // ---- Criterio 1 (complementario): la solución contiene todos los proyectos de disco ----

    [Fact]
    public void Solution_lists_every_project_on_disk()
    {
        var root = SolutionLayout.RepositoryRoot.FullName;
        var inSolution = XDocument.Load(RepoFile("Backbone.slnx"))
            .Descendants("Project")
            .Select(p => Path.GetFullPath(Path.Combine(root, ((string)p.Attribute("Path")!).Replace('/', Path.DirectorySeparatorChar))))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var onDisk = AllProjectFiles().Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Empty(onDisk.Except(inSolution));
        Assert.Empty(inSolution.Except(onDisk));
    }

    [Theory]
    [InlineData("Backbone.Contracts")]
    [InlineData("Backbone.Core")]
    [InlineData("Backbone.Service")]
    [InlineData("Backbone.App")]
    [InlineData("Backbone.Installer")]
    public void Spec_project_exists_in_solution(string name)
    {
        var slnx = File.ReadAllText(RepoFile("Backbone.slnx"));
        Assert.Contains(name + "/" + name + ".", slnx.Replace('\\', '/'));
    }

    // ---- Criterio 5 (refuerzo): paquetes ----

    [Theory]
    [InlineData("Backbone.Contracts")]
    [InlineData("Backbone.Core")]
    public void Platform_neutral_projects_have_no_windows_packages(string project)
    {
        var windows = SolutionLayout.PackageReferences(project)
            .Where(p => p.StartsWith("Microsoft.Windows", StringComparison.OrdinalIgnoreCase)
                     || p.Contains("WindowsServices", StringComparison.OrdinalIgnoreCase)
                     || p.Contains("WinUI", StringComparison.OrdinalIgnoreCase)
                     || p.StartsWith("Microsoft.WindowsAppSDK", StringComparison.OrdinalIgnoreCase)
                     || p.StartsWith("Microsoft.Extensions.Hosting", StringComparison.OrdinalIgnoreCase)
                     || p.StartsWith("System.Management", StringComparison.OrdinalIgnoreCase)
                     || p.StartsWith("Microsoft.Win32", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Empty(windows);
    }

    [Fact]
    public void App_has_no_reference_to_service_hosting_packages()
    {
        Assert.DoesNotContain(SolutionLayout.PackageReferences("Backbone.App"),
            p => p.StartsWith("Microsoft.Extensions.Hosting", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Installer_and_test_projects_are_not_referenced_from_src()
    {
        foreach (var project in SolutionLayout.SourceProjectNames())
        {
            Assert.DoesNotContain(SolutionLayout.TransitiveProjectReferences(project),
                p => p.Contains("Tests", StringComparison.Ordinal) || p.Contains("Installer", StringComparison.Ordinal));
        }
    }

    // ---- Criterio 4: CI (verificación estática; la ejecución real requiere push, ver PRUEBAS-MANUALES.md) ----

    [Fact]
    public void CI_workflow_builds_and_tests_on_windows_with_the_right_filter_and_core_coverage()
    {
        var yml = File.ReadAllText(RepoFile(".github", "workflows", "ci.yml"));

        Assert.Contains("runs-on: windows-latest", yml);
        Assert.Contains("dotnet build Backbone.slnx -c Release", yml);
        Assert.Contains("-warnaserror", yml);
        Assert.Contains("dotnet test Backbone.slnx", yml);
        Assert.Contains("Category!=Integration&Category!=Perf", yml);
        Assert.Contains("XPlat Code Coverage", yml);
        Assert.Contains("+Backbone.Core", yml);
        Assert.Contains("actions/upload-artifact", yml);
        Assert.Contains("pull_request", yml);
    }
}
