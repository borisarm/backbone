using System.Text.Json;
using Backbone.Service.Persistence;
using Backbone.Service.Tests.TestSupport;
using Microsoft.Extensions.Logging;

namespace Backbone.Service.Tests.Persistence;

/// <summary>
/// HU-09 y §8.2 (ADR-14): <c>data\schedule.json</c> con escritura atómica y lectura por handle; un archivo dañado, demasiado
/// grande o rechazado se trata como vacío, sobre carpetas temporales.
/// </summary>
[Trait("Story", "HU-09")]
public sealed class ScheduleStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly CapturingLoggerProvider _logs = new();
    private readonly ILoggerFactory _loggerFactory;

    public ScheduleStoreTests()
    {
        Directory.CreateDirectory(_temp.Combine("data"));
        _loggerFactory = LoggerFactory.Create(b => b.AddProvider(_logs));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string FilePath => _temp.Combine("data", "schedule.json");

    public void Dispose()
    {
        _loggerFactory.Dispose();
        _temp.Dispose();
    }

    private ScheduleStore NewStore() => new(FilePath, TestFileTrust.CurrentUser, _loggerFactory.CreateLogger<ScheduleStore>());

    [Fact]
    public async Task Without_a_file_it_is_empty()
    {
        Assert.Empty(await NewStore().LoadAsync(Ct));
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public async Task What_is_saved_is_read_back_in_utc_after_a_restart()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var entries = new Dictionary<Guid, DateTimeOffset>
        {
            [a] = new DateTimeOffset(2026, 11, 20, 1, 0, 0, TimeSpan.Zero),
            [b] = new DateTimeOffset(2026, 3, 29, 3, 0, 0, TimeSpan.FromHours(2)),
        };
        await NewStore().SaveAsync(entries, Ct);

        var read = await NewStore().LoadAsync(Ct);

        Assert.Equal(2, read.Count);
        Assert.Equal(entries[a], read[a]);
        Assert.Equal(entries[b], read[b]);
        Assert.Equal(TimeSpan.Zero, read[b].Offset);
    }

    [Fact]
    public async Task Saving_replaces_the_file_atomically_and_leaves_no_temporaries()
    {
        var store = NewStore();
        await store.SaveAsync(new Dictionary<Guid, DateTimeOffset> { [Guid.NewGuid()] = DateTimeOffset.UnixEpoch }, Ct);
        var second = Guid.NewGuid();
        await store.SaveAsync(new Dictionary<Guid, DateTimeOffset> { [second] = DateTimeOffset.UnixEpoch.AddDays(1) }, Ct);

        Assert.Equal([second], (await store.LoadAsync(Ct)).Keys);
        Assert.Equal([FilePath], Directory.EnumerateFileSystemEntries(_temp.Combine("data")));
    }

    [Fact]
    public async Task The_file_is_camelCase_json_with_a_schema_version()
    {
        var id = Guid.NewGuid();
        await NewStore().SaveAsync(new Dictionary<Guid, DateTimeOffset> { [id] = DateTimeOffset.UnixEpoch }, Ct);

        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(FilePath, Ct));
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        var entry = Assert.Single(document.RootElement.GetProperty("jobs").EnumerateArray());
        Assert.Equal(id, entry.GetProperty("jobId").GetGuid());
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ no es json")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{ \"jobs\": [ { \"jobId\": \"no-es-un-guid\", \"lastConsumedUtc\": \"2026-01-01T00:00:00Z\" } ] }")]
    [InlineData("{ \"jobs\": [ { \"jobId\": \"00000000-0000-0000-0000-000000000000\", \"lastConsumedUtc\": \"hoy\" } ] }")]
    public async Task A_damaged_file_is_empty(string content)
    {
        await File.WriteAllTextAsync(FilePath, content, Ct);

        Assert.Empty(await NewStore().LoadAsync(Ct));
    }

    [Fact]
    public async Task Entries_without_a_job_id_or_null_are_ignored_and_the_valid_ones_kept()
    {
        var id = Guid.NewGuid();
        await File.WriteAllTextAsync(
            FilePath,
            $$"""{ "schemaVersion": 1, "jobs": [ null, { "jobId": "00000000-0000-0000-0000-000000000000", "lastConsumedUtc": "2026-01-01T00:00:00Z" }, { "jobId": "{{id}}", "lastConsumedUtc": "2026-11-20T01:00:00Z" } ] }""",
            Ct);

        var read = await NewStore().LoadAsync(Ct);

        Assert.Equal(new DateTimeOffset(2026, 11, 20, 1, 0, 0, TimeSpan.Zero), Assert.Single(read).Value);
    }

    [Fact]
    public async Task A_file_larger_than_the_limit_is_empty_and_is_not_read()
    {
        await using (var stream = new FileStream(FilePath, FileMode.Create, FileAccess.Write))
        {
            stream.SetLength(ScheduleStore.MaxFileBytes + 1);
        }

        Assert.Empty(await NewStore().LoadAsync(Ct));
        Assert.Contains(_logs.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task A_rejected_file_is_empty_and_is_not_replaced()
    {
        Directory.CreateDirectory(FilePath); // una carpeta con el nombre del archivo (ADR-14: se rechaza)

        Assert.Empty(await NewStore().LoadAsync(Ct));
        await Assert.ThrowsAnyAsync<Exception>(() => NewStore().SaveAsync(new Dictionary<Guid, DateTimeOffset>(), Ct));
        Assert.True(Directory.Exists(FilePath));
    }

    [Fact]
    public async Task A_missing_data_folder_is_empty_on_read_and_fails_on_write_without_creating_it()
    {
        Directory.Delete(_temp.Combine("data"));

        Assert.Empty(await NewStore().LoadAsync(Ct));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => NewStore().SaveAsync(new Dictionary<Guid, DateTimeOffset>(), Ct));
        Assert.False(Directory.Exists(_temp.Combine("data")));
    }

    [Fact]
    public async Task Duplicate_ids_keep_the_last_entry_and_offsets_are_normalised_to_utc()
    {
        var id = Guid.NewGuid();
        await File.WriteAllTextAsync(
            FilePath,
            $$"""{ "schemaVersion": 1, "unknown": 7, "jobs": [ { "jobId": "{{id}}", "lastConsumedUtc": "2026-11-19T01:00:00Z" }, { "jobId": "{{id}}", "lastConsumedUtc": "2026-11-20T03:00:00+02:00" } ] }""",
            Ct);

        var read = await NewStore().LoadAsync(Ct);

        var value = Assert.Single(read).Value;
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 1, 0, 0, TimeSpan.Zero), value);
        Assert.Equal(TimeSpan.Zero, value.Offset);
    }

    [Fact]
    public async Task A_write_that_fails_leaves_the_previous_content_intact_and_no_temporaries()
    {
        var store = NewStore();
        var id = Guid.NewGuid();
        await store.SaveAsync(new Dictionary<Guid, DateTimeOffset> { [id] = DateTimeOffset.UnixEpoch }, Ct);
        var before = await File.ReadAllBytesAsync(FilePath, Ct);

        using (new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAnyAsync<IOException>(() =>
                store.SaveAsync(new Dictionary<Guid, DateTimeOffset> { [Guid.NewGuid()] = DateTimeOffset.UnixEpoch.AddDays(1) }, Ct));
        }

        Assert.Equal(before, await File.ReadAllBytesAsync(FilePath, Ct));
        Assert.Equal([FilePath], Directory.EnumerateFileSystemEntries(_temp.Combine("data")));
        Assert.Equal(DateTimeOffset.UnixEpoch, (await store.LoadAsync(Ct))[id]);
    }

    [Fact]
    public async Task A_save_cancelled_before_it_starts_does_not_touch_the_file()
    {
        var store = NewStore();
        await store.SaveAsync(new Dictionary<Guid, DateTimeOffset> { [Guid.NewGuid()] = DateTimeOffset.UnixEpoch }, Ct);
        var before = await File.ReadAllBytesAsync(FilePath, Ct);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.SaveAsync(new Dictionary<Guid, DateTimeOffset> { [Guid.NewGuid()] = DateTimeOffset.UnixEpoch.AddDays(1) }, cts.Token));

        Assert.Equal(before, await File.ReadAllBytesAsync(FilePath, Ct));
        Assert.Equal([FilePath], Directory.EnumerateFileSystemEntries(_temp.Combine("data")));
    }
}
