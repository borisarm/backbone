using Backbone.Contracts;
using Backbone.Core.Engine;
using Backbone.Core.Tests.Support;
using static Backbone.Core.Tests.Support.EngineHarness;

namespace Backbone.Core.Tests.Engine;

/// <summary>HU-08 criterio 5 (F2): recuento por patrón para la vista previa del editor de exclusiones.</summary>
[Trait("Story", "HU-08")]
[Trait("Requirement", "RF-04")]
public sealed class ExclusionPreviewTests
{
    private const string SystemRoot = @"E:\Montado";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static InMemorySourceFileSystem Tree()
    {
        var source = new InMemorySourceFileSystem();
        source.AddFile(DocumentsRoot + @"\a.txt", new byte[10]);
        source.AddFile(DocumentsRoot + @"\b.tmp", new byte[20]);
        source.AddFile(DocumentsRoot + @"\web\node_modules\lib\x.js", new byte[300]);
        source.AddFile(DocumentsRoot + @"\web\node_modules\y.tmp", new byte[4000]);
        source.AddFile(DocumentsRoot + @"\Privado\clave.txt", new byte[50000]);
        source.AddFile(DocumentsRoot + @"\nube.docx", new byte[600000], attributes: SourceAttributes.RecallOnDataAccess);
        source.AddFile(PhotosRoot + @"\foto.jpg", new byte[7]);
        return source;
    }

    [Fact]
    public async Task HU08_5_Each_pattern_counts_the_files_and_bytes_it_excludes_on_its_own_and_the_totals_count_each_file_once()
    {
        var source = Tree();

        var result = await new ExclusionPreview().ComputeAsync(source, DefaultSources, ["*.tmp", "**/node_modules/**", "Documentos/Privado/**", "*.nada"], Ct);

        Assert.Equal(
            [
                new ExclusionPatternCount("*.tmp", 2, 4020),
                new ExclusionPatternCount("**/node_modules/**", 2, 4300),
                new ExclusionPatternCount("Documentos/Privado/**", 1, 50000),
                new ExclusionPatternCount("*.nada", 0, 0),
            ],
            result.Patterns);
        Assert.Equal((4L, 54320L), (result.ExcludedFiles, result.ExcludedBytes));
        Assert.Equal((3L, 600017L), (result.IncludedFiles, result.IncludedBytes));
        Assert.Equal(0, result.UnreadableDirectories);
        Assert.Equal(0, source.OpenCount); // nunca abre archivos (tampoco el que está en la nube)
        Assert.Equal(0, source.InfoQueryCount);
    }

    [Fact]
    public async Task HU08_5_The_preview_agrees_with_what_a_backup_with_the_same_patterns_stores()
    {
        var source = Tree();
        source.AddFile(DocumentsRoot + @"\Desktop.ini", new byte[3]);
        var patterns = JobDefaults.Exclusions.Concat(["**/node_modules/**"]).ToList();
        var storage = new Core.Storage.InMemoryRepositoryStorage();

        var preview = await new ExclusionPreview().ComputeAsync(source, DefaultSources, patterns, Ct);
        var backup = await RunAsync(storage, new ManualTimeProvider(), source, Request() with { Exclusions = patterns });
        var stored = (await ReadEntriesAsync(storage, backup.Snapshot.Id)).Where(e => !e.IsDirectory).ToList();

        // Lo incluido es lo copiado más lo que está solo en la nube (que la copia omite con aviso).
        Assert.Equal(preview.IncludedFiles, stored.Count + backup.Warnings.Count(w => w.Code == nameof(RunIssueCode.CloudOnlySkipped)));
    }

    [Fact]
    [Trait("Requirement", "ADR-16")]
    [Trait("Requirement", "RF-06")]
    public async Task HU08_5_Mandatory_exclusions_and_links_are_neither_traversed_nor_counted_and_unreadable_folders_are_reported()
    {
        var source = new InMemorySourceFileSystem();
        source.AddFile(SystemRoot + @"\Users\a.tmp", new byte[1]);
        source.AddFile(SystemRoot + @"\ProgramData\Backbone\x.tmp", new byte[100]);
        source.AddFile(SystemRoot + @"\Enlace\y.tmp", new byte[100]);
        source.AddFile(SystemRoot + @"\enlace.tmp", new byte[100]);
        source.AddFile(SystemRoot + @"\Cerrada\z.tmp", new byte[100]);
        source.MakeReparsePoint(SystemRoot + @"\Enlace");
        source.MakeReparsePoint(SystemRoot + @"\enlace.tmp");
        source.FailEnumerate(SystemRoot + @"\Cerrada", new UnauthorizedAccessException("denegado"));
        var enumerated = new List<string>();
        source.OnEnumerate = enumerated.Add;
        var system = new BackupSource("C", @"C:\", SystemRoot) { MandatoryExclusions = [@"ProgramData\Backbone"] };

        var result = await new ExclusionPreview().ComputeAsync(source, [system], ["*.tmp"], Ct);

        Assert.Equal(new ExclusionPatternCount("*.tmp", 1, 1), Assert.Single(result.Patterns));
        Assert.Equal(1, result.UnreadableDirectories);
        Assert.DoesNotContain(enumerated, p => p.Contains("Backbone", StringComparison.Ordinal) || p.Contains("Enlace", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Requirement", "§8.4")]
    public async Task HU08_5_A_folder_whose_subfolders_do_not_fit_the_budget_is_reported_as_unreadable()
    {
        var source = new InMemorySourceFileSystem();
        for (var i = 0; i < 50; i++)
        {
            source.AddFile(DocumentsRoot + $@"\{new string('d', 100)}{i}\a.tmp", new byte[1]);
        }

        var preview = new ExclusionPreview { ListingBudgetBytes = 2000 };
        var result = await preview.ComputeAsync(source, [DefaultSources[0]], ["*.tmp"], Ct);

        Assert.Equal(1, result.UnreadableDirectories);
        Assert.Equal(0, result.Patterns[0].Files);
    }

    [Fact]
    public async Task HU08_5_Invalid_arguments_and_cancellation()
    {
        var source = Tree();
        var preview = new ExclusionPreview();

        await Assert.ThrowsAsync<ArgumentException>(() => preview.ComputeAsync(source, DefaultSources, ["a//b"], Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => preview.ComputeAsync(null!, DefaultSources, [], Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => preview.ComputeAsync(source, [new BackupSource("A", "x", "")], [], Ct));

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preview.ComputeAsync(source, DefaultSources, ["*.tmp"], cancelled.Token));

        var empty = await preview.ComputeAsync(source, DefaultSources, [], Ct);
        Assert.Empty(empty.Patterns);
        Assert.Equal(0, empty.ExcludedFiles);
        Assert.Equal(7, empty.IncludedFiles);
    }
}
