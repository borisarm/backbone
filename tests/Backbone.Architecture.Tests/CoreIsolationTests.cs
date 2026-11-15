using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Backbone.Core.Storage;

namespace Backbone.Architecture.Tests;

/// <summary>
/// HT-04 criterio 5 y §3.1: el motor no conoce Windows ni el reloj del sistema y solo toca el disco a través de
/// <c>IRepositoryStorage</c>; la variante local del almacenamiento (<c>ForLocalTesting</c>) no es accesible al servicio.
/// </summary>
[Trait("Story", "HT-04")]
public sealed class CoreIsolationTests
{
    private static readonly string CoreSource = Path.Combine(SolutionLayout.RepositoryRoot.FullName, "src", "Backbone.Core");

    private static IEnumerable<string> CoreFiles() =>
        Directory.EnumerateFiles(CoreSource, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));

    /// <summary>Código sin comentarios ni literales de texto, para no dar falsos positivos con la documentación.</summary>
    private static string CodeOnly(string path)
    {
        var text = File.ReadAllText(path);
        text = Regex.Replace(text, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        text = Regex.Replace(text, @"//[^\r\n]*", string.Empty);
        return Regex.Replace(text, "\"(?:[^\"\\\r\n]|\\.)*\"", "\"\"");
    }

    [Fact]
    public void Core_sources_are_found()
    {
        Assert.True(CoreFiles().Count() > 10);
    }

    [Theory]
    [InlineData(@"\bDateTime(Offset)?\.(Now|UtcNow|Today)\b", "el reloj solo se usa mediante TimeProvider")]
    [InlineData(@"\.Result\b|\.Wait\(|\.GetAwaiter\(\)\.GetResult\(", "nada de .Result ni .Wait()")]
    [InlineData(@"\bDllImport\b|\bLibraryImport\b", "el motor no hace P/Invoke")]
    [InlineData(@"using\s+Microsoft\.Win32|using\s+System\.Security\.(Principal|AccessControl)|using\s+System\.ServiceProcess", "el motor no conoce Windows")]
    public void Core_source_does_not_contain_forbidden_constructs(string pattern, string reason)
    {
        var offenders = CoreFiles()
            .Where(f => Regex.IsMatch(CodeOnly(f), pattern))
            .Select(f => Path.GetRelativePath(CoreSource, f))
            .ToList();

        Assert.True(offenders.Count == 0, $"{reason}: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void Only_the_file_system_storage_touches_the_disk()
    {
        // El sistema de archivos del repositorio solo se usa mediante IRepositoryStorage (CLAUDE.md).
        var pattern = @"\b(File|Directory|FileInfo|DirectoryInfo|FileStream|DriveInfo)\s*\.|new\s+(FileStream|FileInfo|DirectoryInfo)\b";
        var offenders = CoreFiles()
            .Where(f => !Path.GetFileName(f).Equals("FileSystemRepositoryStorage.cs", StringComparison.Ordinal))
            .Where(f => Regex.IsMatch(CodeOnly(f), pattern))
            .Select(f => Path.GetRelativePath(CoreSource, f))
            .ToList();

        Assert.True(offenders.Count == 0, $"Acceso directo al disco fuera de FileSystemRepositoryStorage: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void Core_assembly_references_no_windows_specific_assemblies()
    {
        var referenced = typeof(IRepositoryStorage).Assembly.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty).ToList();

        var windows = referenced.Where(n =>
            n.StartsWith("Microsoft.Win32", StringComparison.Ordinal)
            || n.StartsWith("Microsoft.Windows", StringComparison.Ordinal)
            || n.StartsWith("System.ServiceProcess", StringComparison.Ordinal)
            || n.StartsWith("System.Diagnostics.EventLog", StringComparison.Ordinal)
            || n.StartsWith("System.Management", StringComparison.Ordinal)
            || n == "System.Security.Principal.Windows"
            || n == "System.Security.AccessControl").ToList();

        Assert.Empty(windows);
    }

    [Fact]
    public void Local_folder_storage_is_not_public_and_internals_are_visible_only_to_the_core_tests()
    {
        var assembly = typeof(FileSystemRepositoryStorage).Assembly;

        var factory = typeof(FileSystemRepositoryStorage).GetMethod(
            "ForLocalTesting",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(factory);
        Assert.False(factory.IsPublic, "ForLocalTesting no debe ser pública (§6.1: el servicio nunca guarda en un disco local).");

        var visibleTo = assembly.GetCustomAttributes<InternalsVisibleToAttribute>().Select(a => a.AssemblyName).ToList();
        Assert.Equal(["Backbone.Core.Tests"], visibleTo);

        // Ningún constructor público acepta carpetas locales: el único es el UNC.
        var publicConstructors = typeof(FileSystemRepositoryStorage).GetConstructors(BindingFlags.Instance | BindingFlags.Public);
        Assert.Single(publicConstructors);
    }

    [Fact]
    public void Core_public_surface_exposes_storage_only_through_the_interface()
    {
        // BackupRepository solo se construye con un IRepositoryStorage (no con rutas de disco).
        var methods = typeof(Backbone.Core.Repository.BackupRepository)
            .GetMethods(BindingFlags.Static | BindingFlags.Public)
            .Where(m => m.Name is "InitializeAsync" or "OpenAsync" or "ProbeAsync");

        Assert.All(methods, m => Assert.Equal(typeof(IRepositoryStorage), m.GetParameters()[0].ParameterType));
    }
}
