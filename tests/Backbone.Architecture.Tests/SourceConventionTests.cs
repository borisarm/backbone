using System.Text.RegularExpressions;

namespace Backbone.Architecture.Tests;

/// <summary>
/// Convenciones de CLAUDE.md para el servicio y la app (HT-03): sin <c>DateTime.Now</c>, sin <c>.Result</c>/<c>.Wait()</c>,
/// P/Invoke solo con <c>LibraryImport</c> y confinado en una clase <c>NativeMethods</c> por componente.
/// </summary>
[Trait("Story", "HT-03")]
public sealed class SourceConventionTests
{
    public static TheoryData<string> Components => ["Backbone.Service", "Backbone.App"];

    private static IEnumerable<string> Sources(string component)
    {
        var root = Path.Combine(SolutionLayout.RepositoryRoot.FullName, "src", component);
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Código sin comentarios ni literales de texto, para no dar falsos positivos con la documentación.</summary>
    private static string CodeOnly(string path)
    {
        var text = File.ReadAllText(path);
        text = Regex.Replace(text, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        text = Regex.Replace(text, @"//[^\r\n]*", string.Empty);
        return Regex.Replace(text, "\"(?:[^\"\\\r\n]|\\.)*\"", "\"\"");
    }

    [Theory]
    [MemberData(nameof(Components))]
    public void Sources_are_found(string component) => Assert.True(Sources(component).Count() > 5);

    [Theory]
    [MemberData(nameof(Components))]
    public void The_clock_is_only_used_through_TimeProvider(string component)
    {
        var offenders = Sources(component)
            .Where(f => Regex.IsMatch(CodeOnly(f), @"\bDateTime(Offset)?\.(Now|UtcNow|Today)\b|\bStopwatch\.GetTimestamp\b"))
            .ToList();

        Assert.True(offenders.Count == 0, $"{component}: {string.Join(", ", offenders)}");
    }

    [Theory]
    [MemberData(nameof(Components))]
    public void There_are_no_blocking_waits_on_tasks(string component)
    {
        // `.Result` seguido de una comparación es la propiedad Result de un tipo propio (p. ej. DataDirectoryCheck), no Task<T>.Result.
        var offenders = Sources(component)
            .Where(f => Regex.IsMatch(CodeOnly(f), @"\.Result\b(?!\s*(==|!=|is\b))|\.Wait\(|\.GetAwaiter\(\)\.GetResult\("))
            .ToList();

        Assert.True(offenders.Count == 0, $"{component}: {string.Join(", ", offenders)}");
    }

    [Theory]
    [MemberData(nameof(Components))]
    public void PInvoke_uses_LibraryImport_and_never_DllImport(string component)
    {
        var offenders = Sources(component)
            .Where(f => Regex.IsMatch(CodeOnly(f), @"\[\s*(assembly:\s*)?DllImport\b|\[\s*DllImport\s*\("))
            .ToList();

        Assert.True(offenders.Count == 0, $"{component}: {string.Join(", ", offenders)}");
    }

    [Theory]
    [MemberData(nameof(Components))]
    public void LibraryImport_only_appears_in_the_NativeMethods_class_of_the_component(string component)
    {
        var files = Sources(component).ToList();
        var withImports = files.Where(f => Regex.IsMatch(CodeOnly(f), @"\[\s*LibraryImport\s*\(")).ToList();

        Assert.NotEmpty(withImports);
        Assert.All(withImports, f => Assert.Equal("NativeMethods.cs", Path.GetFileName(f)));
        Assert.Single(withImports);
        Assert.Matches(@"class\s+NativeMethods\b", File.ReadAllText(withImports[0]));
    }

    [Theory]
    [MemberData(nameof(Components))]
    public void Native_libraries_are_loaded_only_from_System32(string component)
    {
        var assemblyAttribute = Sources(component)
            .Any(f => Regex.IsMatch(File.ReadAllText(f), @"\[assembly:\s*DefaultDllImportSearchPaths\(\s*DllImportSearchPath\.System32\s*\)\]"));

        Assert.True(assemblyAttribute, $"{component} debe declarar DefaultDllImportSearchPaths(System32) (anti-secuestro de DLL).");
    }
}
