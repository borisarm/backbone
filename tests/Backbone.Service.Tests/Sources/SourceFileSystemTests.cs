using System.Security.Cryptography;
using Backbone.Contracts;
using Backbone.Core.Engine;
using Backbone.Service.Sources;
using Backbone.Service.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Backbone.Service.Tests.Sources;

/// <summary>
/// <see cref="SourceFileSystem"/> con un sistema de archivos simulado (HU-08, §8.2 "Lectura de los orígenes",
/// "Clasificación por etiqueta", "Archivos y carpetas solo en la nube", "Apertura para leer el contenido", ADR-18, ADR-19).
/// Comprueba también que todo handle se libera en cada camino de error.
/// </summary>
[Trait("Story", "HU-08")]
public sealed class SourceFileSystemTests : IDisposable
{
    private const uint CloudTag = 0x9000601A;
    private const FileAttributes Reparse = FileAttributes.ReparsePoint;

    private readonly FakeSourceFileApi _api = new();
    private readonly FakeTimeProvider _time = new();
    private readonly CapturingLoggerProvider _logs = new();
    private readonly SourceFileSystem _fs;

    public SourceFileSystemTests()
    {
        _fs = Create(SourceReadOptions.Default);
    }

    public void Dispose()
    {
        _fs.Dispose();
        _api.Dispose();
    }

    [Fact]
    public async Task Enumeration_comes_from_the_directory_handle_with_the_tag_from_EaSize_only_for_reparse_points()
    {
        _api.AddFile("a.txt", [1, 2, 3]);
        _api.AddDirectory("enlace", Reparse, ReparseTags.MountPoint);
        _api.AddFile("normal-con-ea.bin", [9]).EnumerationEaSize = 0x1234; // EaSize sin punto de reanálisis: no es etiqueta
        _api.AddFile("nube.bin", [], Reparse | SourceAttributes.RecallOnDataAccess, CloudTag);

        var entries = await ListAsync(FakeSourceFileApi.ReadPath);

        Assert.Equal(["a.txt", "enlace", "normal-con-ea.bin", "nube.bin"], entries.Select(e => e.Name).Order(StringComparer.Ordinal));
        var a = entries.Single(e => e.Name == "a.txt");
        Assert.Equal(@"C:\src\a.txt", a.FullPath);
        Assert.Equal(3, a.Length);
        Assert.False(a.IsDirectory);
        Assert.Equal(0u, a.ReparseTag);
        Assert.Equal(ReparseTags.MountPoint, entries.Single(e => e.Name == "enlace").ReparseTag);
        Assert.True(entries.Single(e => e.Name == "enlace").IsDirectory);
        Assert.Equal(0u, entries.Single(e => e.Name == "normal-con-ea.bin").ReparseTag);
        Assert.Equal(SourceEntryKind.CloudOnly, SourceAttributes.Classify(entries.Single(e => e.Name == "nube.bin")));
        Assert.Equal(_fs.HeldHandles, _api.OpenHandles);
        Assert.All(_api.Opens, o => Assert.Equal(SourceOpenMode.Directory, o.Mode));
    }

    [Fact]
    public async Task A_normal_file_is_read_from_the_first_handle_without_reopening()
    {
        var content = RandomNumberGenerator.GetBytes(3 * 1024 * 1024 + 17);
        _api.AddFile("grande.bin", content);

        var read = await ReadAllAsync(FakeSourceFileApi.Child("grande.bin"));

        Assert.Equal(SHA256.HashData(content), SHA256.HashData(read));
        Assert.Empty(_api.ReOpens);
        Assert.Empty(_api.AllocatedRangeQueries);
        Assert.Equal(_fs.HeldHandles, _api.OpenHandles);
    }

    [Fact]
    public async Task The_root_of_a_source_is_checked_before_any_child_is_opened()
    {
        _api.AddFile("a.txt", [1]);

        await using var stream = await _fs.OpenReadAsync(FakeSourceFileApi.Child("a.txt"), CancellationToken.None);

        Assert.Equal([FakeSourceFileApi.OpenRoot, FakeSourceFileApi.OpenRoot + @"\a.txt"], _api.Opens.Select(o => o.Path));
    }

    [Theory]
    [InlineData(ReparseTags.MountPoint, RunIssueCode.ReparsePointSkipped)]
    [InlineData(ReparseTags.Symlink, RunIssueCode.ReparsePointSkipped)]
    [InlineData(ReparseTags.ProjFs, RunIssueCode.ReadError)]
    [InlineData(0u, RunIssueCode.ReadError)] // punto de reanálisis con etiqueta 0: nunca normal
    public async Task A_root_that_is_a_link_or_unreadable_throws_from_EnumerateAsync(uint tag, RunIssueCode expected)
    {
        _api.Root.Attributes = FileAttributes.Directory | Reparse;
        _api.Root.ReparseTag = tag;

        var ex = await Assert.ThrowsAsync<SourceAccessException>(() => ListAsync(FakeSourceFileApi.ReadPath));

        Assert.Equal(expected, ex.Code);
        if (expected == RunIssueCode.ReadError)
        {
            Assert.Contains(ReparseTags.Format(tag), ex.Message, StringComparison.Ordinal);
        }

        Assert.Equal(_fs.HeldHandles, _api.OpenHandles);
    }

    [Fact]
    public async Task A_root_whose_final_path_is_not_the_read_path_is_rejected()
    {
        _api.Root.FinalNt = @"\Device\HarddiskVolume3\ProgramData\Backbone";

        var ex = await Assert.ThrowsAsync<SourceAccessException>(() => ListAsync(FakeSourceFileApi.ReadPath));

        Assert.Equal(RunIssueCode.ReparsePointSkipped, ex.Code);
        Assert.Equal(_fs.HeldHandles, _api.OpenHandles);
        Assert.Contains(_logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("raíz", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_not_populated_cloud_root_is_never_enumerated()
    {
        _api.Root.Attributes = FileAttributes.Directory | Reparse;
        _api.Root.ReparseTag = CloudTag;
        _api.Root.PlaceholderState = 0x1 | 0x10; // PARTIAL

        var ex = await Assert.ThrowsAsync<SourceAccessException>(() => ListAsync(FakeSourceFileApi.ReadPath));

        Assert.Equal(RunIssueCode.CloudOnlySkipped, ex.Code);
        Assert.Equal(_fs.HeldHandles, _api.OpenHandles);
    }

    [Fact]
    public async Task A_populated_cloud_folder_is_enumerated()
    {
        var folder = _api.AddDirectory("OneDrive", FileAttributes.Directory | Reparse, CloudTag);
        folder.Attributes = FileAttributes.Directory | Reparse | FileAttributes.ReadOnly;
        _api.AddFile(@"OneDrive\doc.txt", [1], Reparse, CloudTag);

        var entries = await ListAsync(FakeSourceFileApi.Child("OneDrive"));

        Assert.Equal(["doc.txt"], entries.Select(e => e.Name));
        Assert.Equal(CloudTag, entries[0].ReparseTag);
    }

    [Fact]
    public async Task An_intermediate_folder_replaced_by_a_junction_after_enumeration_is_not_followed()
    {
        // La ruta se resuelve a través del enlace: la ruta final del handle no es la esperada (ADR-18, M-A).
        _api.AddDirectory("A");
        _api.AddDirectory(@"A\credentials").FinalNt = @"\Device\HarddiskVolume3\ProgramData\Backbone\credentials";
        var secret = _api.AddFile(@"A\credentials\x.bin", [42]);
        secret.FinalNt = @"\Device\HarddiskVolume3\ProgramData\Backbone\credentials\x.bin";

        var ex = await Assert.ThrowsAsync<SourceAccessException>(() => ReadAllAsync(FakeSourceFileApi.Child(@"A\credentials\x.bin")));

        Assert.Equal(RunIssueCode.ReparsePointSkipped, ex.Code);
        Assert.Equal(_fs.HeldHandles, _api.OpenHandles);
        Assert.Empty(_api.ReOpens);
    }

    [Fact]
    public async Task A_mandatory_exclusion_is_never_opened()
    {
        using var fs = new SourceFileSystem(
            [new BackupSource("c", FakeSourceFileApi.ReadPath, FakeSourceFileApi.ReadPath) { MandatoryExclusions = [@"ProgramData\Backbone"] }],
            _api,
            _time,
            SourceReadOptions.Default,
            new LoggerFactory([_logs]).CreateLogger<SourceFileSystem>());
        _api.AddFile(@"ProgramData\Backbone\credentials\x.bin", [1]);

        var ex = await Assert.ThrowsAsync<SourceAccessException>(() => fs.OpenReadAsync(FakeSourceFileApi.Child(@"programdata\BACKBONE\credentials\x.bin"), CancellationToken.None));

        Assert.Equal(RunIssueCode.ReparsePointSkipped, ex.Code);
        Assert.DoesNotContain(_api.Opens, o => o.Path.Contains("credentials", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_path_outside_the_sources_is_PathInvalid_and_nothing_is_opened()
    {
        var ex = await Assert.ThrowsAsync<SourceAccessException>(() => _fs.OpenReadAsync(@"C:\otra\a.txt", CancellationToken.None));

        Assert.Equal(RunIssueCode.PathInvalid, ex.Code);
        Assert.Empty(_api.Opens);
    }

    [Theory]
    [InlineData(ReparseTags.Symlink, RunIssueCode.ReparsePointSkipped)]
    [InlineData(ReparseTags.MountPoint, RunIssueCode.ReparsePointSkipped)]
    [InlineData(ReparseTags.AppExecLink, RunIssueCode.ReadError)]
    [InlineData(0x9000001Cu, RunIssueCode.ReadError)]
    public async Task A_file_that_became_a_link_or_an_unreadable_reparse_point_is_not_read(uint tag, RunIssueCode expected)
    {
        _api.AddFile("x.bin", [1], Reparse, tag);

        var ex = await Assert.ThrowsAsync<SourceAccessException>(() => ReadAllAsync(FakeSourceFileApi.Child("x.bin")));

        Assert.Equal(expected, ex.Code);
        Assert.Empty(_api.ReOpens);
        Assert.Equal(_fs.HeldHandles, _api.OpenHandles);
    }

    [Theory]
    [InlineData(SourceAttributes.RecallOnDataAccess, 0x1u)]
    [InlineData(FileAttributes.Offline, 0x1u)]
    [InlineData((FileAttributes)0, 0x1u | 0x10u)]
    [InlineData((FileAttributes)0, 0x1u | 0x10u | 0x20u)]
    [InlineData((FileAttributes)0, 0xFFFFFFFFu)]
    public async Task A_cloud_file_that_became_cloud_only_is_skipped_without_opening_the_content(FileAttributes extra, uint state)
    {
        var node = _api.AddFile("nube.bin", [1, 2], Reparse | extra, CloudTag);
        node.PlaceholderState = state;

        var ex = await Assert.ThrowsAsync<SourceAccessException>(() => ReadAllAsync(FakeSourceFileApi.Child("nube.bin")));

        Assert.Equal(RunIssueCode.CloudOnlySkipped, ex.Code);
        Assert.Empty(_api.ReOpens);
        Assert.Equal(_fs.HeldHandles, _api.OpenHandles);
    }

    [Theory]
    [InlineData(CloudTag)]
    [InlineData(ReparseTags.Wof)]
    [InlineData(ReparseTags.Dedup)]
    public async Task Whitelisted_content_is_read_from_a_second_handle_and_the_first_stays_open_while_reading(uint tag)
    {
        var content = RandomNumberGenerator.GetBytes(10_000);
        _api.AddFile("lista.bin", content, Reparse, tag);

        var stream = await _fs.OpenReadAsync(FakeSourceFileApi.Child("lista.bin"), CancellationToken.None);
        Assert.Equal(2 + _fs.HeldHandles, _api.OpenHandles); // primero (sin FILE_SHARE_DELETE) y segundo
        var read = await ReadToEndAsync(stream);
        await stream.DisposeAsync();

        Assert.Equal(content, read);
        Assert.Single(_api.ReOpens);
        Assert.Equal(ReparseTags.IsCloud(tag) ? 1 : 0, _api.AllocatedRangeQueries.Count);
        Assert.Equal(_fs.HeldHandles, _api.OpenHandles);
    }

    [Fact]
    public async Task A_second_handle_to_another_file_is_rejected()
    {
        var node = _api.AddFile("lista.bin", [1], Reparse, ReparseTags.Wof);
        var other = _api.AddNode(@"\\?\C:\otro", node.FinalNt, Reparse, ReparseTags.Wof, [2]);
        node.SeenThroughSecondHandle = other;

        var ex = await Assert.ThrowsAsync<SourceAccessException>(() => ReadAllAsync(FakeSourceFileApi.Child("lista.bin")));

        Assert.Equal(RunIssueCode.ReparsePointSkipped, ex.Code);
        Assert.Equal(_fs.HeldHandles, _api.OpenHandles);
    }

    [Theory]
    [InlineData(ReparseTags.Symlink, RunIssueCode.ReparsePointSkipped)]
    [InlineData(ReparseTags.Dedup, RunIssueCode.ReparsePointSkipped)] // otra clase de la lista blanca
    [InlineData(0u, RunIssueCode.ReparsePointSkipped)]
    public async Task A_second_handle_with_another_tag_class_is_rejected(uint secondTag, RunIssueCode expected)
    {
        var node = _api.AddFile("lista.bin", [1], Reparse, ReparseTags.Wof);
        var second = _api.AddNode(@"\\?\C:\visto", node.FinalNt, secondTag == 0 ? FileAttributes.Archive : Reparse, secondTag, [1]);
        second.Id = node.Id;
        node.SeenThroughSecondHandle = second;

        var ex = await Assert.ThrowsAsync<SourceAccessException>(() => ReadAllAsync(FakeSourceFileApi.Child("lista.bin")));

        Assert.Equal(expected, ex.Code);
        Assert.Equal(_fs.HeldHandles, _api.OpenHandles);
    }

    [Fact]
    public async Task A_cloud_file_that_is_partial_through_the_second_handle_is_cloud_only()
    {
        var node = _api.AddFile("nube.bin", [1], Reparse, CloudTag);
        var second = _api.AddNode(@"\\?\C:\visto", node.FinalNt, Reparse | FileAttributes.Offline, CloudTag, [1]);
        second.Id = node.Id;
        node.SeenThroughSecondHandle = second;

        var ex = await Assert.ThrowsAsync<SourceAccessException>(() => ReadAllAsync(FakeSourceFileApi.Child("nube.bin")));

        Assert.Equal(RunIssueCode.CloudOnlySkipped, ex.Code);
        Assert.Equal(_fs.HeldHandles, _api.OpenHandles);
    }

    [Fact]
    public async Task A_second_handle_with_another_final_path_is_rejected()
    {
        var node = _api.AddFile("lista.bin", [1], Reparse, CloudTag);
        var second = _api.AddNode(@"\\?\C:\visto", @"\Device\HarddiskVolume3\ProgramData\Backbone\x", Reparse, CloudTag, [1]);
        second.Id = node.Id;
        node.SeenThroughSecondHandle = second;

        var ex = await Assert.ThrowsAsync<SourceAccessException>(() => ReadAllAsync(FakeSourceFileApi.Child("lista.bin")));

        Assert.Equal(RunIssueCode.ReparsePointSkipped, ex.Code);
        Assert.Equal(_fs.HeldHandles, _api.OpenHandles);
    }

    [Fact]
    public async Task A_cloud_file_with_unallocated_ranges_is_cloud_only_and_is_not_read()
    {
        var node = _api.AddFile("nube.bin", [1, 2, 3], Reparse, CloudTag);
        node.FullyAllocated = false;

        var ex = await Assert.ThrowsAsync<SourceAccessException>(() => ReadAllAsync(FakeSourceFileApi.Child("nube.bin")));

        Assert.Equal(RunIssueCode.CloudOnlySkipped, ex.Code);
        Assert.Equal(_fs.HeldHandles, _api.OpenHandles);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Without_the_cloud_filter_a_cloud_file_is_read_from_the_first_handle_only_if_fully_allocated(bool allocated)
    {
        var node = _api.AddFile("nube.bin", [5, 6, 7], Reparse, CloudTag);
        node.ReOpenError = 1920; // ERROR_CANT_ACCESS_FILE
        node.FullyAllocated = allocated;

        if (allocated)
        {
            Assert.Equal([5, 6, 7], await ReadAllAsync(FakeSourceFileApi.Child("nube.bin")));
        }
        else
        {
            var ex = await Assert.ThrowsAsync<SourceAccessException>(() => ReadAllAsync(FakeSourceFileApi.Child("nube.bin")));
            Assert.Equal(RunIssueCode.CloudOnlySkipped, ex.Code);
        }

        Assert.Equal(_fs.HeldHandles, _api.OpenHandles);
    }

    [Theory]
    [InlineData(ReparseTags.Wof)]
    [InlineData(ReparseTags.Dedup)]
    public async Task Without_its_filter_a_wof_or_dedup_file_is_a_read_error_and_the_raw_stream_is_not_read(uint tag)
    {
        var node = _api.AddFile("x.bin", [1], Reparse, tag);
        node.ReOpenError = 1920;

        var ex = await Assert.ThrowsAsync<SourceAccessException>(() => ReadAllAsync(FakeSourceFileApi.Child("x.bin")));

        Assert.Equal(RunIssueCode.ReadError, ex.Code);
        Assert.Contains(ReparseTags.Format(tag), ex.Message, StringComparison.Ordinal);
        Assert.Equal(_fs.HeldHandles, _api.OpenHandles);
    }

    [Theory]
    [InlineData(5, typeof(UnauthorizedAccessException), null)]
    [InlineData(32, typeof(SourceAccessException), RunIssueCode.FileLocked)]
    [InlineData(33, typeof(SourceAccessException), RunIssueCode.FileLocked)]
    [InlineData(1920, typeof(SourceAccessException), RunIssueCode.ReadError)]
    [InlineData(206, typeof(SourceAccessException), RunIssueCode.PathInvalid)]
    public async Task Open_errors_are_translated_for_the_engine(int error, Type expected, RunIssueCode? code)
    {
        _api.AddFile("x.bin", [1]).OpenError = error;

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => ReadAllAsync(FakeSourceFileApi.Child("x.bin")));

        Assert.IsType(expected, ex);
        if (code is { } c)
        {
            Assert.Equal(c, ((SourceAccessException)ex).Code);
        }

        Assert.Equal(_fs.HeldHandles, _api.OpenHandles);
    }

    [Fact]
    public async Task GetFileInfo_returns_null_when_the_file_no_longer_exists()
    {
        Assert.Null(await _fs.GetFileInfoAsync(FakeSourceFileApi.Child("no-existe.txt"), CancellationToken.None));
        Assert.Null(await _fs.GetFileInfoAsync(FakeSourceFileApi.Child(@"no-existe\a.txt"), CancellationToken.None));
        Assert.Equal(_fs.HeldHandles, _api.OpenHandles);
    }

    [Fact]
    public async Task GetFileInfo_reads_metadata_and_the_tag_from_its_own_handle()
    {
        var node = _api.AddFile(@"sub\x.bin", [1, 2, 3, 4], Reparse, ReparseTags.Wof);
        _api.AddDirectory("sub");

        var info = await _fs.GetFileInfoAsync(FakeSourceFileApi.Child(@"sub\x.bin"), CancellationToken.None);

        Assert.NotNull(info);
        Assert.Equal("x.bin", info.Name);
        Assert.Equal(4, info.Length);
        Assert.Equal(node.LastWriteTimeUtc, info.LastWriteTimeUtc);
        Assert.Equal(ReparseTags.Wof, info.ReparseTag);
        Assert.Equal(SourceOpenMode.Metadata, _api.Opens.Last().Mode);
        Assert.Empty(_api.ReOpens);
        Assert.Equal(_fs.HeldHandles, _api.OpenHandles);
    }

    [Theory]
    [InlineData(ReparseTags.Symlink, (FileAttributes)0, RunIssueCode.ReparsePointSkipped)]
    [InlineData(CloudTag, SourceAttributes.RecallOnDataAccess, RunIssueCode.CloudOnlySkipped)]
    [InlineData(ReparseTags.ProjFs, (FileAttributes)0, RunIssueCode.ReadError)]
    public async Task GetFileInfo_repeats_the_classification_on_the_handle(uint tag, FileAttributes extra, RunIssueCode expected)
    {
        _api.AddFile("x.bin", [1], Reparse | extra, tag);

        var ex = await Assert.ThrowsAsync<SourceAccessException>(() => _fs.GetFileInfoAsync(FakeSourceFileApi.Child("x.bin"), CancellationToken.None));

        Assert.Equal(expected, ex.Code);
        Assert.Equal(_fs.HeldHandles, _api.OpenHandles);
    }

    [Fact]
    public async Task Enumerating_a_file_or_reading_a_folder_is_a_read_error()
    {
        _api.AddFile("a.txt", [1]);
        _api.AddDirectory("carpeta");

        Assert.Equal(RunIssueCode.ReadError, (await Assert.ThrowsAsync<SourceAccessException>(() => ListAsync(FakeSourceFileApi.Child("a.txt")))).Code);
        Assert.Equal(RunIssueCode.ReadError, (await Assert.ThrowsAsync<SourceAccessException>(() => ReadAllAsync(FakeSourceFileApi.Child("carpeta")))).Code);
        Assert.Equal(_fs.HeldHandles, _api.OpenHandles);
    }

    [Fact]
    public async Task A_cancelled_run_throws_OperationCanceledException_and_opens_nothing()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        _api.AddFile("a.txt", [1]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _fs.OpenReadAsync(FakeSourceFileApi.Child("a.txt"), cancelled.Token));

        Assert.Empty(_api.Opens);
    }

    private SourceFileSystem Create(SourceReadOptions options) =>
        new([new BackupSource("src", FakeSourceFileApi.ReadPath, FakeSourceFileApi.ReadPath)], _api, _time, options, new LoggerFactory([_logs]).CreateLogger<SourceFileSystem>());

    private async Task<List<SourceEntry>> ListAsync(string path)
    {
        var list = new List<SourceEntry>();
        await foreach (var entry in _fs.EnumerateAsync(path, CancellationToken.None))
        {
            list.Add(entry);
        }

        return list;
    }

    private async Task<byte[]> ReadAllAsync(string path)
    {
        await using var stream = await _fs.OpenReadAsync(path, CancellationToken.None);
        return await ReadToEndAsync(stream);
    }

    private static async Task<byte[]> ReadToEndAsync(Stream stream)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[256 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0)
        {
            memory.Write(buffer, 0, read);
        }

        return memory.ToArray();
    }
}
