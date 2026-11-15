using System.Reflection;

namespace Backbone.Architecture.Tests;

/// <summary>
/// Reglas de dependencia de §3.1: App → Contracts; Service → Core → Contracts.
/// La app nunca referencia Core ni Service; Core no conoce Windows ni el servicio.
/// </summary>
[Trait("Story", "HT-01")]
public sealed class DependencyRulesTests
{
    [Theory]
    [InlineData(Core)]
    [InlineData(Service)]
    [InlineData(App)]
    public void Contracts_never_references_any_other_project(string forbidden)
    {
        Assert.DoesNotContain(forbidden, SolutionLayout.TransitiveProjectReferences(Contracts));
        Assert.Empty(SolutionLayout.DirectProjectReferences(Contracts));
    }

    [Fact]
    public void App_references_contracts_directly()
    {
        Assert.Contains(Contracts, SolutionLayout.DirectProjectReferences(App));
    }

    private const string Contracts = "Backbone.Contracts";
    private const string Core = "Backbone.Core";
    private const string Service = "Backbone.Service";
    private const string App = "Backbone.App";

    /// <summary>Dependencias permitidas (cierre transitivo) para cada proyecto de src/.</summary>
    public static TheoryData<string, string[]> AllowedDependencies => new()
    {
        { Contracts, [] },
        { Core, [Contracts] },
        { Service, [Core, Contracts] },
        { App, [Contracts] },
    };

    [Fact]
    public void Src_contains_exactly_the_projects_of_the_specification()
    {
        // Si se añade un proyecto a src/, hay que actualizar §3.1 y estas reglas.
        Assert.Equal(
            new[] { App, Contracts, Core, Service },
            SolutionLayout.SourceProjectNames());
    }

    [Theory]
    [MemberData(nameof(AllowedDependencies))]
    public void Project_only_depends_on_allowed_projects(string project, string[] allowed)
    {
        var actual = SolutionLayout.TransitiveProjectReferences(project);

        var forbidden = actual.Except(allowed, StringComparer.Ordinal).ToList();

        Assert.True(
            forbidden.Count == 0,
            $"{project} depende de {string.Join(", ", forbidden)}, no permitido por §3.1.");
    }

    [Theory]
    [InlineData(Core)]
    [InlineData(Service)]
    public void App_never_references_core_or_service(string forbidden)
    {
        Assert.DoesNotContain(forbidden, SolutionLayout.TransitiveProjectReferences(App));
    }

    [Theory]
    [InlineData(Service)]
    [InlineData(App)]
    public void Core_never_references_service_or_app(string forbidden)
    {
        Assert.DoesNotContain(forbidden, SolutionLayout.TransitiveProjectReferences(Core));
    }

    [Theory]
    [InlineData(Contracts)]
    [InlineData(Core)]
    public void Platform_neutral_projects_target_plain_net10(string project)
    {
        // Contracts y Core no conocen Windows (§3.1): sin TFM "-windows" ni UseWindowsForms/UseWPF/UseWinUI.
        Assert.Equal("net10.0", SolutionLayout.TargetFramework(project));

        var windowsSwitches = SolutionLayout.LoadProject(project)
            .Descendants()
            .Where(e => e.Name.LocalName is "UseWinUI" or "UseWPF" or "UseWindowsForms")
            .ToList();
        Assert.Empty(windowsSwitches);
    }

    [Fact]
    public void Contracts_has_no_package_dependencies_other_than_StreamJsonRpc()
    {
        var packages = SolutionLayout.PackageReferences(Contracts);

        Assert.All(packages, p => Assert.Equal("StreamJsonRpc", p));
    }

    [Fact]
    [Trait("Story", "HT-02")]
    public void Contracts_tests_only_reference_contracts()
    {
        // Las pruebas del contrato no deben depender del motor, del servicio ni de la app (§3.1).
        Assert.Equal([Contracts], SolutionLayout.TestProjectReferences("Backbone.Contracts.Tests"));
    }

    [Theory]
    [InlineData(Contracts, new[] { Core, Service, App })]
    [InlineData(Core, new[] { Service, App })]
    public void Compiled_assembly_does_not_reference_forbidden_assemblies(string assemblyName, string[] forbidden)
    {
        // Comprobación complementaria sobre los metadatos compilados (incluye referencias añadidas
        // por otros medios que una ProjectReference, p. ej. un <Reference> a una DLL).
        var referenced = Assembly.Load(assemblyName)
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .ToList();

        foreach (var name in forbidden)
        {
            Assert.DoesNotContain(name, referenced);
        }
    }
}
