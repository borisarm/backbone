using System.Text.RegularExpressions;

namespace Backbone.Architecture.Tests;

/// <summary>
/// HU-13 (§3.1, CLAUDE.md): el motor de copia (<c>Backbone.Core/Engine</c>) no usa el reloj del sistema ni toca el disco
/// directamente; solo lo hace a través de <c>IRepositoryStorage</c> (repositorio) e <c>ISourceFileSystem</c> (origen).
/// Complementa a <see cref="CoreIsolationTests"/> con construcciones que aquella no vigila.
/// </summary>
[Trait("Story", "HU-13")]
public sealed class EngineIsolationTests
{
    private static readonly string EngineSource = Path.Combine(SolutionLayout.RepositoryRoot.FullName, "src", "Backbone.Core", "Engine");

    private static IEnumerable<string> EngineFiles() =>
        Directory.EnumerateFiles(EngineSource, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));

    private static string CodeOnly(string path)
    {
        var text = File.ReadAllText(path);
        text = Regex.Replace(text, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        text = Regex.Replace(text, @"//[^\r\n]*", string.Empty);
        return Regex.Replace(text, "\"(?:[^\"\\\r\n]|\\.)*\"", "\"\"");
    }

    [Fact]
    public void Engine_sources_are_found() => Assert.True(EngineFiles().Count() >= 4);

    [Theory]
    [InlineData(@"\bDateTime(Offset)?\.(Now|UtcNow|Today)\b", "DateTime.Now/UtcNow/Today")]
    [InlineData(@"\bStopwatch\b|\bEnvironment\.TickCount", "cronómetros del sistema (usar TimeProvider)")]
    [InlineData(@"\bTask\.Delay\s*\((?![^;]*TimeProvider|[^;]*timeProvider)", "Task.Delay sin TimeProvider")]
    [InlineData(@"(?<![.\w])(File|Directory|FileInfo|DirectoryInfo|FileStream|DriveInfo|FileSystemInfo)\s*\.|\bPath\.GetTempPath|\bEnvironment\.CurrentDirectory|new\s+(FileStream|FileInfo|DirectoryInfo)\b", "acceso directo al disco")]
    [InlineData(@"\busing\s+System\.IO\.(Enumeration|MemoryMappedFiles|Pipes)\b|\bMemoryMappedFile\b|\bRandomAccess\b", "E/S de archivos de bajo nivel")]
    [InlineData(@"\bProcess\.|\bNetworkInterface\b|\bHttpClient\b|\bSocket\b", "procesos o red")]
    public void Engine_does_not_use_system_clock_or_direct_io(string pattern, string reason)
    {
        // `FileAttributes` y `FileNotFoundException` son tipos de datos, no E/S: el patrón usa límites de palabra.
        var offenders = EngineFiles()
            .Where(f => Regex.IsMatch(CodeOnly(f), pattern))
            .Select(f => Path.GetRelativePath(EngineSource, f))
            .ToList();

        Assert.True(offenders.Count == 0, $"{reason}: {string.Join(", ", offenders)}");
    }
}
