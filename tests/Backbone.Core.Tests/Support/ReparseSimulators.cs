using System.Runtime.CompilerServices;
using Backbone.Contracts;
using Backbone.Core.Engine;

namespace Backbone.Core.Tests.Support;

/// <summary>
/// Simula la implementación de §8.2 cuando un elemento se sustituye por un <i>junction</i> o un enlace después de
/// enumerarlo: al abrirlo por handle detecta el punto de reanálisis y lanza
/// <see cref="SourceAccessException"/> con <see cref="RunIssueCode.ReparsePointSkipped"/>.
/// </summary>
public sealed class ReparseSwitchingSource(InMemorySourceFileSystem inner) : ISourceFileSystem
{
    public HashSet<string> EnumerateBecomesLink { get; } = new(StringComparer.Ordinal);

    public HashSet<string> OpenBecomesLink { get; } = new(StringComparer.Ordinal);

    public HashSet<string> InfoBecomesLink { get; } = new(StringComparer.Ordinal);

    public HashSet<string> InfoReportsLink { get; } = new(StringComparer.Ordinal);

    private static SourceAccessException Link(string path) =>
        new(RunIssueCode.ReparsePointSkipped, $"{path} es un punto de reanálisis; no se sigue.");

    public async IAsyncEnumerable<SourceEntry> EnumerateAsync(string directoryPath, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (EnumerateBecomesLink.Contains(directoryPath))
        {
            throw Link(directoryPath);
        }

        await foreach (var entry in inner.EnumerateAsync(directoryPath, cancellationToken))
        {
            yield return entry;
        }
    }

    public Task<Stream> OpenReadAsync(string filePath, CancellationToken cancellationToken) =>
        OpenBecomesLink.Contains(filePath) ? throw Link(filePath) : inner.OpenReadAsync(filePath, cancellationToken);

    public async Task<SourceEntry?> GetFileInfoAsync(string filePath, CancellationToken cancellationToken)
    {
        if (InfoBecomesLink.Contains(filePath))
        {
            throw Link(filePath);
        }

        var info = await inner.GetFileInfoAsync(filePath, cancellationToken);
        return info is not null && InfoReportsLink.Contains(filePath) ? info with { Attributes = info.Attributes | FileAttributes.ReparsePoint, ReparseTag = ReparseTags.Symlink } : info;
    }
}

/// <summary>
/// Simulador del contrato de ADR-18 (§8.2 "Lectura de los orígenes"): se crea por ejecución con los orígenes, deduce la
/// ruta relativa quitando <c>ReadPath\</c> (rechaza con <see cref="RunIssueCode.PathInvalid"/> lo que no es de un origen
/// o tiene segmentos vacíos, <c>.</c> o <c>..</c>) y, sobre el "handle" ya abierto (la ruta resuelta a través de los
/// enlaces que haya en ese momento), exige que la ruta final sea la raíz final más la ruta relativa y que no caiga en
/// una exclusión obligatoria; si no, <see cref="RunIssueCode.ReparsePointSkipped"/> sin leer nada.
/// </summary>
public sealed class FinalPathCheckingSource : ISourceFileSystem
{
    private readonly InMemorySourceFileSystem inner;
    private readonly IReadOnlyList<BackupSource> sources;
    private readonly Dictionary<string, string> rootFinalPaths = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> links = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> pendingAfterListing = new(StringComparer.OrdinalIgnoreCase);

    public FinalPathCheckingSource(InMemorySourceFileSystem inner, IReadOnlyList<BackupSource> sources)
    {
        this.inner = inner;
        this.sources = sources;

        // La ruta final de cada raíz se obtiene una vez al crear la implementación (al empezar la ejecución).
        foreach (var source in sources)
        {
            rootFinalPaths[source.ReadPath] = Resolve(source.ReadPath);
        }
    }

    public List<string> Checked { get; } = [];

    public List<(string Path, RunIssueCode Code)> Rejected { get; } = [];

    /// <summary>Sustituye ya la carpeta por un enlace hacia <paramref name="target"/>.</summary>
    public void Swap(string folder, string target) => links[folder] = target;

    /// <summary>Sustituye la carpeta por un enlace en cuanto termina de enumerarse (carrera de una copia en vivo).</summary>
    public void SwapAfterListing(string folder, string target) => pendingAfterListing[folder] = target;

    public async IAsyncEnumerable<SourceEntry> EnumerateAsync(string directoryPath, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var final = Check(directoryPath);
        await foreach (var entry in inner.EnumerateAsync(final, cancellationToken))
        {
            // Como la implementación real: raíz recibida + '\' + nombre enumerado.
            yield return entry with { FullPath = directoryPath + @"\" + entry.Name };
        }

        if (pendingAfterListing.Remove(directoryPath, out var target))
        {
            links[directoryPath] = target;
        }
    }

    public Task<Stream> OpenReadAsync(string filePath, CancellationToken cancellationToken) =>
        inner.OpenReadAsync(Check(filePath), cancellationToken);

    public async Task<SourceEntry?> GetFileInfoAsync(string filePath, CancellationToken cancellationToken)
    {
        var info = await inner.GetFileInfoAsync(Check(filePath), cancellationToken);
        return info is null ? null : info with { FullPath = filePath };
    }

    private string Resolve(string path)
    {
        foreach (var (folder, target) in links)
        {
            if (string.Equals(path, folder, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(folder + @"\", StringComparison.OrdinalIgnoreCase))
            {
                return target + path[folder.Length..];
            }
        }

        return path;
    }

    private string Check(string path)
    {
        Checked.Add(path);
        var source = sources.FirstOrDefault(s =>
            string.Equals(path, s.ReadPath, StringComparison.Ordinal) || path.StartsWith(s.ReadPath + @"\", StringComparison.Ordinal));
        if (source is null)
        {
            throw Reject(path, RunIssueCode.PathInvalid);
        }

        var relative = path.Length == source.ReadPath.Length ? string.Empty : path[(source.ReadPath.Length + 1)..];
        if (relative.Length > 0 && relative.Split('\\').Any(s => s is "" or "." or ".."))
        {
            throw Reject(path, RunIssueCode.PathInvalid);
        }

        var final = Resolve(path);
        var expected = relative.Length == 0 ? rootFinalPaths[source.ReadPath] : rootFinalPaths[source.ReadPath] + @"\" + relative;
        if (!string.Equals(final, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw Reject(path, RunIssueCode.ReparsePointSkipped);
        }

        foreach (var exclusion in source.MandatoryExclusions.Select(e => e.Replace('/', '\\')))
        {
            if (string.Equals(relative, exclusion, StringComparison.OrdinalIgnoreCase)
                || relative.StartsWith(exclusion + @"\", StringComparison.OrdinalIgnoreCase))
            {
                throw Reject(path, RunIssueCode.ReparsePointSkipped);
            }
        }

        return final;
    }

    private SourceAccessException Reject(string path, RunIssueCode code)
    {
        Rejected.Add((path, code));
        return new SourceAccessException(code, $"{path} no supera la comprobación de la ruta final ({code}).");
    }
}
