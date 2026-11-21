using Backbone.Service.Persistence;

namespace Backbone.Service.Tests.TestSupport;

/// <summary>Copias apartadas <c>&lt;archivo&gt;.bad-&lt;marca&gt;</c> de un archivo (§8.2), en orden ordinal (cronológico).</summary>
internal static class BadCopies
{
    public static IReadOnlyList<string> Of(string path)
    {
        var directory = Path.GetDirectoryName(path) ?? ".";
        var prefix = Path.GetFileName(path) + TrustedFile.SetAsideMarker;
        return [.. Directory.GetFiles(directory, prefix + "*").Order(StringComparer.Ordinal)];
    }

    /// <summary>La única copia apartada.</summary>
    public static string Single(string path) => Assert.Single(Of(path));
}
