using Backbone.Contracts;
using Backbone.Core.Engine;
using Backbone.Service.Sources;
using Backbone.Service.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Backbone.Service.Tests.Sources;

/// <summary>
/// §8.2 "Plazo de las lecturas de los orígenes" y R12 con operaciones bloqueadas simuladas y <see cref="FakeTimeProvider"/>:
/// plazo de 60 s por apertura, enumeración o bloque; 30 s más tras cancelar; después, abandono y <c>Internal</c>.
/// </summary>
[Trait("Story", "HU-08")]
public sealed class SourceReadTimeoutTests : IDisposable
{
    private readonly FakeSourceFileApi _api = new();
    private readonly FakeTimeProvider _time = new();
    private readonly CapturingLoggerProvider _logs = new();
    private readonly SourceFileSystem _fs;

    public SourceReadTimeoutTests()
    {
        _fs = new SourceFileSystem(
            [new BackupSource("src", FakeSourceFileApi.ReadPath, FakeSourceFileApi.ReadPath)],
            _api,
            _time,
            SourceReadOptions.Default with { ReadBlockBytes = 4096 },
            new LoggerFactory([_logs]).CreateLogger<SourceFileSystem>());
    }

    public void Dispose()
    {
        _fs.Dispose();
        _api.Dispose();
    }

    [Theory]
    [InlineData(FakeBlockAt.Open)]
    [InlineData(FakeBlockAt.Enumerate)]
    [InlineData(FakeBlockAt.ReOpen)]
    public async Task A_blocked_operation_that_answers_the_cancellation_is_a_read_error_after_60_seconds(FakeBlockAt at)
    {
        var node = at switch
        {
            FakeBlockAt.Enumerate => _api.AddDirectory("carpeta"),
            FakeBlockAt.ReOpen => _api.AddFile("wof.bin", [1], FileAttributes.ReparsePoint, ReparseTags.Wof),
            _ => _api.AddFile("a.bin", [1]),
        };
        node.BlockAt = at;
        node.BlockMode = FakeBlockMode.HonorCancellation;
        var path = FakeSourceFileApi.Child(at == FakeBlockAt.Enumerate ? "carpeta" : at == FakeBlockAt.ReOpen ? "wof.bin" : "a.bin");
        Task operation = at == FakeBlockAt.Enumerate ? ListAsync(path) : ReadAllAsync(path);

        var elapsed = await AdvanceUntilCompletedAsync(operation);

        var ex = await Assert.ThrowsAsync<SourceAccessException>(() => operation);
        Assert.Equal(RunIssueCode.ReadError, ex.Code);
        Assert.Contains("plazo", ex.Message, StringComparison.Ordinal);
        Assert.InRange(elapsed, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(65));
        Assert.False(_fs.IsAbandoned);
        Assert.Equal(0, _api.OpenHandles); // también el handle que devolvió tarde
    }

    [Fact]
    public async Task After_a_timed_out_operation_that_returned_the_next_ones_still_work()
    {
        var blocked = _api.AddFile("lento.bin", [1]);
        blocked.BlockAt = FakeBlockAt.Open;
        _api.AddFile("bien.bin", [7, 8]);

        await Assert.ThrowsAsync<SourceAccessException>(() => AdvanceThenAwaitAsync(ReadAllAsync(FakeSourceFileApi.Child("lento.bin"))));

        Assert.Equal([7, 8], await ReadAllAsync(FakeSourceFileApi.Child("bien.bin")));
        Assert.Equal(1, _api.LateHandles);
        Assert.Equal(0, _api.OpenHandles);
    }

    [Theory]
    [InlineData(FakeBlockAt.Open)]
    [InlineData(FakeBlockAt.Enumerate)]
    public async Task A_blocked_operation_that_ignores_the_cancellation_is_abandoned_after_30_more_seconds(FakeBlockAt at)
    {
        var node = at == FakeBlockAt.Enumerate ? _api.AddDirectory("carpeta") : _api.AddFile("a.bin", [1]);
        node.BlockAt = at;
        node.BlockMode = FakeBlockMode.IgnoreCancellation;
        _api.AddFile("otro.bin", [1]);
        var path = FakeSourceFileApi.Child(at == FakeBlockAt.Enumerate ? "carpeta" : "a.bin");
        Task operation = at == FakeBlockAt.Enumerate ? ListAsync(path) : ReadAllAsync(path);

        var elapsed = await AdvanceUntilCompletedAsync(operation);

        // No deriva de IOException: el motor no la trata como un elemento ilegible y la ejecución termina Failed/Internal.
        var ex = await Assert.ThrowsAsync<SourceOperationAbandonedException>(() => operation);
        Assert.IsNotAssignableFrom<IOException>(ex);
        Assert.IsAssignableFrom<SourceReadingFailedException>(ex);
        Assert.InRange(elapsed, TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(95));
        Assert.True(_fs.IsAbandoned);
        Assert.Contains(_logs.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("abandona", StringComparison.Ordinal));

        // Todo lo que venga después falla igual, sin tocar el sistema de archivos.
        var opensBefore = _api.Opens.Count;
        await Assert.ThrowsAsync<SourceOperationAbandonedException>(() => ReadAllAsync(FakeSourceFileApi.Child("otro.bin")));
        await Assert.ThrowsAsync<SourceOperationAbandonedException>(() => _fs.GetFileInfoAsync(FakeSourceFileApi.Child("otro.bin"), CancellationToken.None));
        Assert.Equal(opensBefore, _api.Opens.Count);
    }

    [Fact]
    public async Task A_blocked_read_that_answers_the_cancellation_is_a_read_error_and_the_stream_is_not_reused()
    {
        var node = _api.AddFile("a.bin", new byte[10_000]);
        node.BlockAt = FakeBlockAt.Read;
        node.BlockMode = FakeBlockMode.HonorCancellation;
        await using var stream = await _fs.OpenReadAsync(FakeSourceFileApi.Child("a.bin"), CancellationToken.None);
        var buffer = new byte[4096];

        var read = stream.ReadAsync(buffer, TestContext.Current.CancellationToken).AsTask();
        await AdvanceUntilCompletedAsync(read);

        Assert.Equal(RunIssueCode.ReadError, (await Assert.ThrowsAsync<SourceAccessException>(() => read)).Code);
        Assert.Equal(RunIssueCode.ReadError, (await Assert.ThrowsAsync<SourceAccessException>(() => stream.ReadAsync(buffer, TestContext.Current.CancellationToken).AsTask())).Code);
        Assert.False(_fs.IsAbandoned);
    }

    [Fact]
    public async Task A_blocked_read_that_ignores_the_cancellation_is_abandoned()
    {
        var node = _api.AddFile("a.bin", new byte[10_000]);
        node.BlockAt = FakeBlockAt.Read;
        node.BlockMode = FakeBlockMode.IgnoreCancellation;
        await using var stream = await _fs.OpenReadAsync(FakeSourceFileApi.Child("a.bin"), CancellationToken.None);

        var read = stream.ReadAsync(new byte[4096], TestContext.Current.CancellationToken).AsTask();
        var elapsed = await AdvanceUntilCompletedAsync(read);

        await Assert.ThrowsAsync<SourceOperationAbandonedException>(() => read);
        Assert.InRange(elapsed, TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(95));
        Assert.True(_fs.IsAbandoned);
    }

    [Fact]
    public async Task Cancelling_the_run_during_a_blocked_open_throws_OperationCanceledException_and_releases_the_handle()
    {
        var node = _api.AddFile("a.bin", [1]);
        node.BlockAt = FakeBlockAt.Open;
        using var cancellation = new CancellationTokenSource();
        var operation = _fs.OpenReadAsync(FakeSourceFileApi.Child("a.bin"), cancellation.Token);
        Assert.True(_api.Blocked.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        await cancellation.CancelAsync();
        await AdvanceUntilCompletedAsync(operation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.False(_fs.IsAbandoned);
        Assert.Equal(0, _api.OpenHandles);
    }

    [Fact]
    public async Task Without_exposed_placeholders_the_factory_refuses_to_read_sources()
    {
        var environment = new SourceReadingEnvironment();
        var factory = new SourceFileSystemFactory(_api, environment, _time, SourceReadOptions.Default, new LoggerFactory([_logs]).CreateLogger<SourceFileSystem>());
        var sources = new[] { new BackupSource("src", FakeSourceFileApi.ReadPath, FakeSourceFileApi.ReadPath) };

        var startup = new SourceReadingStartup(new FailingPlaceholders(), environment, new LoggerFactory([_logs]).CreateLogger<SourceReadingStartup>());
        await startup.StartingAsync(CancellationToken.None);

        var ex = Assert.Throws<SourceReadingUnavailableException>(() => factory.Create(sources));
        Assert.IsNotAssignableFrom<IOException>(ex);
        Assert.Contains(_logs.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("-1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task With_exposed_placeholders_the_factory_creates_one_reader_per_run()
    {
        var environment = new SourceReadingEnvironment();
        var factory = new SourceFileSystemFactory(_api, environment, _time, SourceReadOptions.Default, new LoggerFactory([_logs]).CreateLogger<SourceFileSystem>());
        Assert.Throws<SourceReadingUnavailableException>(() => factory.Create([])); // antes de arrancar tampoco

        await new SourceReadingStartup(new WorkingPlaceholders(), environment, new LoggerFactory([_logs]).CreateLogger<SourceReadingStartup>()).StartingAsync(CancellationToken.None);

        using var first = factory.Create([new BackupSource("src", FakeSourceFileApi.ReadPath, FakeSourceFileApi.ReadPath)]);
        using var second = factory.Create([new BackupSource("src", FakeSourceFileApi.ReadPath, FakeSourceFileApi.ReadPath)]);
        Assert.NotSame(first, second);
    }

    /// <summary>Avanza el reloj simulado de segundo en segundo hasta que la tarea termina; devuelve el tiempo simulado.</summary>
    private async Task<TimeSpan> AdvanceUntilCompletedAsync(Task task)
    {
        var start = _time.GetUtcNow();
        Assert.True(_api.Blocked.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken) || task.IsCompleted, "La operación no llegó a bloquearse.");
        for (var i = 0; i < 400 && !task.IsCompleted; i++)
        {
            // Deja que el ejecutor cree sus temporizadores antes de cada paso.
            await Task.Delay(10, CancellationToken.None);
            if (!task.IsCompleted)
            {
                _time.Advance(TimeSpan.FromSeconds(1));
            }
        }

        await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10), CancellationToken.None));
        Assert.True(task.IsCompleted, "La operación no terminó al avanzar el reloj.");
        return _time.GetUtcNow() - start;
    }

    private async Task AdvanceThenAwaitAsync(Task task)
    {
        await AdvanceUntilCompletedAsync(task);
        await task;
    }

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
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, TestContext.Current.CancellationToken);
        return memory.ToArray();
    }

    private sealed class FailingPlaceholders : IPlaceholderCompatibility
    {
        public int ExposePlaceholders() => -1;
    }

    private sealed class WorkingPlaceholders : IPlaceholderCompatibility
    {
        public int ExposePlaceholders() => 0;
    }
}
