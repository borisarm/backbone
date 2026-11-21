using Backbone.Service.Sources;

namespace Backbone.IntegrationTests.Sources;

/// <summary>
/// Abre una ruta local con <see cref="Win32SourceFileApi"/> como lo hace <see cref="SourceFileSystem"/>: la raíz del volumen
/// por su ruta NT y cada componente relativo al anterior (§8.2 "Apertura relativa"). Solo para preparar las pruebas.
/// </summary>
internal static class SourceHandles
{
    public static ISourceHandle Open(string path, SourceOpenMode mode)
    {
        var api = new Win32SourceFileApi();
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var segments = full.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var current = api.OpenVolumeRoot(@"\??\" + segments[0] + @"\", CancellationToken.None);
        for (var i = 1; i < segments.Length; i++)
        {
            var parent = current;
            try
            {
                current = api.OpenChild(parent, segments[i], i == segments.Length - 1 ? mode : SourceOpenMode.Directory, CancellationToken.None);
            }
            finally
            {
                parent.Dispose();
            }
        }

        return current;
    }

    /// <summary>Ruta final <c>VOLUME_NAME_DOS</c> sin <c>\\?\</c> (la que daría la resolución controlada, ADR-20 punto 6).</summary>
    public static string FinalPath(string path)
    {
        using var handle = Open(path, SourceOpenMode.Directory);
        var final = handle.GetFinalPath(FinalPathFormat.Dos, CancellationToken.None);
        return final.StartsWith(@"\\?\", StringComparison.Ordinal) ? final[4..] : final;
    }
}
