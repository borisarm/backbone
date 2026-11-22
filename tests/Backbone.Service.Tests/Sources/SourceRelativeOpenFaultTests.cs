using Backbone.Contracts;
using Backbone.Core.Engine;
using Backbone.Service.Sources;
using Backbone.Service.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Backbone.Service.Tests.Sources;

/// <summary>
/// Inyección de fallos en la apertura relativa de HU-08 (A-1, ADR-18): fallos al abrir carpetas intermedias, la raíz del
/// volumen y cancelaciones a mitad del recorrido. Comprueban el resultado, que no quedan handles abiertos de más y que un
/// fallo no se queda en la caché de carpetas.
/// </summary>
[Trait("Story", "HU-08")]
public sealed class SourceRelativeOpenFaultTests : IDisposable
{
    private readonly FakeSourceFileApi _api = new();
    private readonly FakeTimeProvider _time = new();
    private readonly CapturingLoggerProvider _logs = new();
    private readonly List<SourceFileSystem> _created = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var fs in _created)
        {
            fs.Dispose();
        }

        _api.Dispose();
    }

    [Theory]
    [InlineData(5)]
    [InlineData(32)]
    [InlineData(1117)]
    public async Task A1_A_failure_opening_an_intermediate_folder_fails_only_what_is_below_and_leaves_no_handle(int error)
    {
        _api.AddDirectory("a");
        var middle = _api.AddDirectory(@"a\b");
        _api.AddFile(@"a\b\x.txt", [1]);
        _api.AddDirectory("otra");
        _api.AddFile(@"otra\y.txt", [2]);
        middle.Faults[FakeOp.Open] = error;
        var fs = Create();

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => ReadAllAsync(fs, @"a\b\x.txt", Ct));

        switch (error)
        {
            case 5:
                Assert.IsType<UnauthorizedAccessException>(ex);
                break;
            case 32:
                Assert.Equal(RunIssueCode.FileLocked, Assert.IsType<SourceAccessException>(ex).Code);
                break;
            default:
                Assert.Equal(RunIssueCode.ReadError, Assert.IsType<SourceAccessException>(ex).Code);
                break;
        }

        // Nada por debajo del fallo se abre ni se resuelve por ruta completa.
        Assert.DoesNotContain(_api.RelativeOpens, o => o.Name == "x.txt");
        Assert.All(_api.Opens, o => Assert.DoesNotContain(@"\a\b\x.txt", o.Path, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(fs.HeldHandles, _api.OpenHandles);
        Assert.False(fs.IsAbandoned);
        // El resto del origen sigue siendo legible.
        Assert.Equal([2], await ReadAllAsync(fs, @"otra\y.txt", Ct));
        Assert.Equal(fs.HeldHandles, _api.OpenHandles);
    }

    [Fact]
    public async Task A1_A_transient_failure_opening_a_folder_is_not_cached_and_the_next_read_succeeds()
    {
        _api.AddDirectory("a");
        var folder = _api.AddDirectory(@"a\b");
        _api.AddFile(@"a\b\x.txt", [9]);
        folder.Faults[FakeOp.Open] = 32;
        var fs = Create();

        await Assert.ThrowsAnyAsync<Exception>(() => ReadAllAsync(fs, @"a\b\x.txt", Ct));
        folder.Faults.Remove(FakeOp.Open);

        Assert.Equal([9], await ReadAllAsync(fs, @"a\b\x.txt", Ct));
        Assert.Equal(fs.HeldHandles, _api.OpenHandles);
    }

    [Fact]
    public async Task A1_A_failure_classifying_a_folder_after_opening_it_closes_it_and_does_not_leave_it_cached()
    {
        var folder = _api.AddDirectory("d");
        _api.AddFile(@"d\f.txt", [5]);
        folder.Faults[FakeOp.Attributes] = 5;
        var fs = Create();

        await Assert.ThrowsAnyAsync<Exception>(() => ListAsync(fs, FakeSourceFileApi.Child("d")));
        Assert.Equal(fs.HeldHandles, _api.OpenHandles);

        folder.Faults.Remove(FakeOp.Attributes);
        Assert.Single(await ListAsync(fs, FakeSourceFileApi.Child("d")));
        Assert.Equal(fs.HeldHandles, _api.OpenHandles);
    }

    [Fact]
    public async Task A1_When_the_volume_root_cannot_be_opened_nothing_is_read_and_no_handle_is_left()
    {
        _api.AddFile("a.txt", [1]);
        _api.Volume.Faults[FakeOp.Open] = 5;
        var fs = Create();

        await Assert.ThrowsAnyAsync<Exception>(() => ReadAllAsync(fs, "a.txt", Ct));
        await Assert.ThrowsAnyAsync<Exception>(() => ListAsync(fs, FakeSourceFileApi.ReadPath));

        Assert.Empty(_api.RelativeOpens);
        Assert.Equal(0, _api.OpenHandles);
        Assert.Equal(0, _api.ReadCalls);
    }

    [Fact]
    public async Task A1_A_missing_volume_is_an_error_and_never_falls_back_to_opening_by_path()
    {
        _api.AddFile("a.txt", [1]);
        _api.VolumeRoots.Clear();
        var fs = Create();

        await Assert.ThrowsAnyAsync<Exception>(() => ReadAllAsync(fs, "a.txt", Ct));

        Assert.Empty(_api.Opens); // ni siquiera una apertura por ruta completa
        Assert.Equal(0, _api.OpenHandles);
    }

    [Fact]
    public async Task A1_Cancelling_while_an_intermediate_folder_is_being_opened_leaves_no_handle_and_the_next_read_works()
    {
        _api.AddDirectory("a");
        var folder = _api.AddDirectory(@"a\b");
        _api.AddFile(@"a\b\x.txt", [3]);
        folder.BlockAt = FakeBlockAt.Open;
        folder.BlockMode = FakeBlockMode.HonorCancellation;
        var fs = Create();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var read = ReadAllAsync(fs, @"a\b\x.txt", cts.Token);
        Assert.True(_api.Blocked.Wait(TimeSpan.FromSeconds(10), Ct));
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.DoesNotContain(_api.RelativeOpens, o => o.Name == "x.txt");
        await WaitUntilAsync(() => fs.HeldHandles == _api.OpenHandles);
        Assert.False(fs.IsAbandoned);

        folder.BlockAt = FakeBlockAt.None;
        Assert.Equal([3], await ReadAllAsync(fs, @"a\b\x.txt", Ct));
    }

    [Fact]
    public async Task A1_Disposing_the_file_system_closes_every_cached_folder()
    {
        for (var i = 0; i < 6; i++)
        {
            _api.AddDirectory($"d{i}");
            _api.AddFile($@"d{i}\f.txt", [(byte)i]);
        }

        var fs = Create();
        for (var i = 0; i < 6; i++)
        {
            await ListAsync(fs, FakeSourceFileApi.Child($"d{i}"));
            await ReadAllAsync(fs, $@"d{i}\f.txt", Ct);
        }

        Assert.True(_api.OpenHandles > 1);
        fs.Dispose();

        Assert.Equal(0, _api.OpenHandles);
    }

    private SourceFileSystem Create()
    {
        var fs = new SourceFileSystem(
            [new BackupSource("src", FakeSourceFileApi.ReadPath, FakeSourceFileApi.ReadPath)],
            _api,
            _time,
            SourceReadOptions.Default with { ReadBlockBytes = 4096 },
            new LoggerFactory([_logs]).CreateLogger<SourceFileSystem>());
        _created.Add(fs);
        return fs;
    }

    private static async Task<List<SourceEntry>> ListAsync(SourceFileSystem fs, string path)
    {
        var list = new List<SourceEntry>();
        await foreach (var entry in fs.EnumerateAsync(path, Ct))
        {
            list.Add(entry);
        }

        return list;
    }

    private static async Task<byte[]> ReadAllAsync(SourceFileSystem fs, string relative, CancellationToken token)
    {
        await using var stream = await fs.OpenReadAsync(FakeSourceFileApi.Child(relative), token);
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, token);
        return memory.ToArray();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10, CancellationToken.None);
        }

        Assert.True(condition());
    }
}
