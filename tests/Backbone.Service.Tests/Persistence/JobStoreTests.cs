using System.Text;
using System.Text.Json;
using Backbone.Contracts;
using Backbone.Service.Ipc;
using Backbone.Service.Persistence;
using Backbone.Service.Tests.TestSupport;
using Microsoft.Extensions.Logging;

namespace Backbone.Service.Tests.Persistence;

/// <summary>
/// HU-07 criterio 5 y §8.2: <c>config\jobs.json</c> con escritura atómica, lectura por handle y tratamiento de un archivo
/// dañado o rechazado, sobre carpetas temporales.
/// </summary>
[Trait("Story", "HU-07")]
public sealed class JobStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly CapturingLoggerProvider _logs = new();
    private readonly ILoggerFactory _loggerFactory;

    public JobStoreTests()
    {
        Directory.CreateDirectory(_temp.Combine("config"));
        _loggerFactory = LoggerFactory.Create(b => b.AddProvider(_logs));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string JobsPath => _temp.Combine("config", "jobs.json");

    public void Dispose()
    {
        _loggerFactory.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public async Task Without_a_file_there_are_no_jobs()
    {
        using var store = NewStore();

        await store.LoadAsync(Ct);

        Assert.Empty(store.Jobs);
        Assert.False(File.Exists(JobsPath));
    }

    [Fact]
    public async Task Saved_jobs_are_read_back_identically_after_a_restart()
    {
        var first = Job("Documentos") with
        {
            Schedule = new JobSchedule { Kind = ScheduleKind.Daily, TimesOfDay = [new TimeOnly(2, 0), new TimeOnly(13, 30)], Days = DaysOfWeek.Monday | DaysOfWeek.Friday },
            Retention = new RetentionPolicy { KeepLast = 5, KeepYearly = 2 },
            Conditions = new JobConditions { MinBatteryPercent = null, SkipOnMeteredConnection = false },
            MaxFileSizeBytes = 1024,
        };
        var second = Job("Fotos") with { Enabled = false };
        using (var store = NewStore())
        {
            await store.LoadAsync(Ct);
            await store.UpdateAsync(list => (list.Add(first).Add(second), 0), Ct);
        }

        using var restarted = NewStore();
        await restarted.LoadAsync(Ct);

        Assert.Equal([Json(first), Json(second)], restarted.Jobs.Select(Json));
    }

    [Fact]
    public async Task The_file_is_camelCase_json_with_a_schema_version_and_enums_as_numbers()
    {
        using var store = NewStore();
        await store.LoadAsync(Ct);
        await store.UpdateAsync(list => (list.Add(Job("Docs") with { Schedule = new JobSchedule { Kind = ScheduleKind.Interval, IntervalHours = 6 } }), 0), Ct);

        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(JobsPath, Ct));
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        var job = Assert.Single(root.GetProperty("jobs").EnumerateArray().ToList());
        Assert.Equal("Docs", job.GetProperty("name").GetString());
        Assert.Equal(2, job.GetProperty("schedule").GetProperty("kind").GetInt32());
        Assert.Equal(@"C:\Users\ana\Docs", job.GetProperty("sources")[0].GetProperty("path").GetString());
    }

    [Fact]
    public async Task Changed_is_raised_after_persisting_and_only_when_the_list_changes()
    {
        using var store = NewStore();
        await store.LoadAsync(Ct);
        var raised = 0;
        var fileExistedWhenRaised = false;
        store.Changed += (_, _) =>
        {
            raised++;
            fileExistedWhenRaised = File.Exists(JobsPath);
        };

        await store.UpdateAsync(list => (list.Add(Job("Docs")), 0), Ct);
        await store.UpdateAsync(list => (list, 0), Ct);

        Assert.Equal(1, raised);
        Assert.True(fileExistedWhenRaised);
    }

    [Fact]
    public async Task A_change_that_throws_writes_nothing_and_keeps_the_list()
    {
        using var store = NewStore();
        await store.LoadAsync(Ct);
        await store.UpdateAsync(list => (list.Add(Job("Docs")), 0), Ct);
        var before = await File.ReadAllBytesAsync(JobsPath, Ct);

        await Assert.ThrowsAsync<BackboneException>(() => store.UpdateAsync<int>(_ => throw BackboneException.ValidationFailed("Destination", ValidationIssueCode.DestinationUsedByOtherJob), Ct));

        Assert.Single(store.Jobs);
        Assert.Equal(before, await File.ReadAllBytesAsync(JobsPath, Ct));
    }

    [Fact]
    public async Task A_corrupt_file_starts_without_jobs_and_is_moved_aside_before_the_next_write()
    {
        await File.WriteAllTextAsync(JobsPath, "{ \"jobs\": [ { \"id\": ", Ct);
        using var store = NewStore();

        await store.LoadAsync(Ct);

        Assert.Empty(store.Jobs);
        Assert.Contains(_logs.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("jobs.json", StringComparison.Ordinal));

        await store.UpdateAsync(list => (list.Add(Job("Docs")), 0), Ct);

        Assert.Equal("{ \"jobs\": [ { \"id\": ", await File.ReadAllTextAsync(BadCopies.Single(JobsPath), Ct));
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(JobsPath, Ct));
        Assert.Single(document.RootElement.GetProperty("jobs").EnumerateArray());
    }

    [Fact]
    public async Task A_file_over_the_size_limit_is_not_read_and_counts_as_corrupt()
    {
        await File.WriteAllTextAsync(JobsPath, Document(Json(Job("Docs"))), Ct);
        using var store = NewStore(maxFileBytes: 64);

        await store.LoadAsync(Ct);

        Assert.Empty(store.Jobs);
    }

    [Fact]
    public async Task A_write_that_would_exceed_the_size_limit_is_rejected_with_LimitExceeded_and_writes_nothing()
    {
        using var store = NewStore(maxFileBytes: 2048);
        await store.LoadAsync(Ct);
        await store.UpdateAsync(list => (list.Add(Job("Docs")), 0), Ct);
        var before = await File.ReadAllBytesAsync(JobsPath, Ct);

        var ex = await Assert.ThrowsAsync<BackboneException>(() =>
            store.UpdateAsync(list => (list.Add(Job("Grande") with { Exclusions = [new string('x', 4096)] }), 0), Ct));

        Assert.Equal(BackboneErrorCode.ValidationFailed, ex.Code);
        Assert.NotNull(ex.Validation);
        Assert.Equal(ValidationIssueCode.LimitExceeded, Assert.Single(ex.Validation.Errors).Code);
        Assert.Single(store.Jobs);
        Assert.Equal(before, await File.ReadAllBytesAsync(JobsPath, Ct));
    }

    [Fact]
    public async Task A_directory_in_place_of_the_file_is_rejected_without_reading()
    {
        Directory.CreateDirectory(JobsPath);
        using var store = NewStore();

        var ex = await Assert.ThrowsAsync<UntrustedFileException>(() => store.LoadAsync(Ct));

        Assert.Equal(UntrustedFileReason.NotAFile, ex.Reason);
    }

    [Fact]
    public async Task Entries_that_are_not_usable_jobs_are_ignored_and_the_rest_are_loaded()
    {
        var good = Job("Bueno");
        var duplicated = Job("Repetido") with { Id = good.Id };
        var withoutId = Job("SinId") with { Id = Guid.Empty };
        var tooManySources = Job("Muchos") with { Sources = [.. Enumerable.Range(0, 33).Select(i => new JobSource { Alias = $"a{i}", Path = $@"C:\{i}" })] };
        var badEnum = Json(Job("Enum")).Replace("\"kind\": 0", "\"kind\": 99", StringComparison.Ordinal);
        await File.WriteAllTextAsync(JobsPath, Document(Json(good), "null", Json(duplicated), Json(withoutId), Json(tooManySources), badEnum), Ct);
        using var store = NewStore();

        await store.LoadAsync(Ct);

        Assert.Equal([good.Id], store.Jobs.Select(j => j.Id));
        Assert.Equal(5, _logs.Entries.Count(e => e.Level == LogLevel.Warning && e.Message.Contains("jobs.json", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Unknown_top_level_properties_are_kept_when_rewriting()
    {
        await File.WriteAllTextAsync(JobsPath, "{ \"schemaVersion\": 1, \"jobs\": [], \"futuro\": { \"a\": 1 } }", Ct);
        using var store = NewStore();
        await store.LoadAsync(Ct);

        await store.UpdateAsync(list => (list.Add(Job("Docs")), 0), Ct);

        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(JobsPath, Ct));
        Assert.Equal(1, document.RootElement.GetProperty("futuro").GetProperty("a").GetInt32());
    }

    /// <summary>
    /// Criterio 5: lo que deja un servicio matado a mitad de una escritura es, como mucho, un temporal sin renombrar junto a
    /// un <c>jobs.json</c> intacto (el renombrado por handle es atómico). Al arrancar se lee el archivo anterior completo y el
    /// temporal se borra.
    /// </summary>
    [Fact]
    public async Task A_write_interrupted_before_the_rename_leaves_the_previous_file_intact_and_its_temporary_is_removed()
    {
        var job = Job("Docs");
        using (var store = NewStore())
        {
            await store.LoadAsync(Ct);
            await store.UpdateAsync(list => (list.Add(job), 0), Ct);
        }

        var leftover = _temp.Combine("config", $".jobs.json.{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(leftover, "{ \"schemaVersion\": 1, \"jobs\": [ { \"id\": \"", Ct); // escritura cortada

        using var restarted = NewStore();
        await restarted.LoadAsync(Ct);

        Assert.Equal([Json(job)], restarted.Jobs.Select(Json));
        Assert.False(File.Exists(leftover));
        Assert.Single(Directory.GetFiles(_temp.Combine("config")));
    }

    /// <summary>Criterio 5: un lector concurrente ve siempre el archivo anterior completo o el nuevo completo.</summary>
    [Fact]
    public async Task A_concurrent_reader_always_sees_a_complete_file()
    {
        using var store = NewStore();
        await store.LoadAsync(Ct);
        await store.UpdateAsync(list => (list.Add(Job("Inicial")), 0), Ct);
        using var stop = new CancellationTokenSource();
        var reads = 0;

        var reader = Task.Run(
            async () =>
            {
                var files = TestFileTrust.CurrentUser;
                while (!stop.IsCancellationRequested)
                {
                    var bytes = await files.ReadAllBytesAsync(JobsPath, JobStore.MaxFileBytes, CancellationToken.None);
                    Assert.NotNull(bytes);
                    var document = JsonSerializer.Deserialize<JobsDocument>(bytes, JobStore.JsonOptions);
                    Assert.NotNull(document);
                    Assert.NotEmpty(document.Jobs);
                    reads++;
                }
            },
            Ct);

        for (var i = 0; i < 40; i++)
        {
            var name = $"Trabajo {i}";
            await store.UpdateAsync(list => (list.SetItem(0, list[0] with { Name = name, Exclusions = [.. Enumerable.Repeat("*.tmp", i)] }), 0), Ct);
        }

        await stop.CancelAsync();
        await reader;
        Assert.True(reads > 0);
        Assert.Equal("Trabajo 39", Assert.Single(store.Jobs).Name);
    }

    private JobStore NewStore(int maxFileBytes = JobStore.MaxFileBytes) =>
        new(JobsPath, TestFileTrust.CurrentUser, _loggerFactory.CreateLogger<JobStore>(), maxFileBytes);

    private static JobDefinition Job(string name) =>
        JobDefaults.NewJob() with
        {
            Id = Guid.NewGuid(),
            Name = name,
            Sources = [new JobSource { Alias = name, Path = $@"C:\Users\ana\{name}" }],
            Destination = $@"\\nas\backups\{name.ToLowerInvariant()}",
        };

    private static string Json(JobDefinition job) => JsonSerializer.Serialize(job, JobStore.JsonOptions);

    private static string Document(params string[] jobs)
    {
        var builder = new StringBuilder("{ \"schemaVersion\": 1, \"jobs\": [");
        builder.AppendJoin(",", jobs);
        return builder.Append("] }").ToString();
    }
}
