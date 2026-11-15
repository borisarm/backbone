using System.Xml.Linq;

namespace Backbone.Architecture.Tests;

/// <summary>
/// Lee la estructura de la solución a partir de los archivos .csproj del repositorio.
/// </summary>
/// <remarks>
/// Se inspeccionan los .csproj (y no los ensamblados compilados) porque la app y el servicio
/// tienen plataformas de destino Windows distintas y no se pueden cargar desde un proyecto
/// de pruebas net10.0; además, una ProjectReference prohibida debe detectarse aunque el
/// código todavía no use ningún tipo del proyecto referenciado.
/// </remarks>
internal static class SolutionLayout
{
    public static readonly DirectoryInfo RepositoryRoot = FindRepositoryRoot();

    public static IReadOnlyList<string> SourceProjectNames() =>
        Directory.GetFiles(Path.Combine(RepositoryRoot.FullName, "src"), "*.csproj", SearchOption.AllDirectories)
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .Order(StringComparer.Ordinal)
            .ToList();

    public static XDocument LoadProject(string projectName)
    {
        var path = ProjectPath(projectName);
        Assert.True(File.Exists(path), $"No se encuentra el proyecto {path}.");
        return XDocument.Load(path);
    }

    /// <summary>Nombres de los proyectos referenciados directamente con ProjectReference.</summary>
    public static IReadOnlyList<string> DirectProjectReferences(string projectName) =>
        LoadProject(projectName)
            .Descendants()
            .Where(e => e.Name.LocalName == "ProjectReference")
            .Select(e => (string?)e.Attribute("Include") ?? string.Empty)
            .Select(include => Path.GetFileNameWithoutExtension(include.Replace('\\', Path.DirectorySeparatorChar)))
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>Cierre transitivo de las referencias entre proyectos de src/.</summary>
    public static IReadOnlySet<string> TransitiveProjectReferences(string projectName)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(DirectProjectReferences(projectName));
        while (pending.TryPop(out var current))
        {
            if (visited.Add(current))
            {
                foreach (var next in DirectProjectReferences(current))
                {
                    pending.Push(next);
                }
            }
        }

        return visited;
    }

    public static IReadOnlyList<string> PackageReferences(string projectName) =>
        LoadProject(projectName)
            .Descendants()
            .Where(e => e.Name.LocalName == "PackageReference")
            .Select(e => (string?)e.Attribute("Include") ?? string.Empty)
            .Order(StringComparer.Ordinal)
            .ToList();

    public static string TargetFramework(string projectName) =>
        LoadProject(projectName)
            .Descendants()
            .Where(e => e.Name.LocalName is "TargetFramework" or "TargetFrameworks")
            .Select(e => e.Value.Trim())
            .Single();

    private static string ProjectPath(string projectName) =>
        Path.Combine(RepositoryRoot.FullName, "src", projectName, projectName + ".csproj");

    private static DirectoryInfo FindRepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Backbone.slnx")))
            {
                return dir;
            }
        }

        throw new InvalidOperationException(
            $"No se encuentra Backbone.slnx subiendo desde {AppContext.BaseDirectory}.");
    }
}
