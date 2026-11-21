using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using Backbone.Contracts;
using Backbone.Service.Persistence;
using Backbone.Service.Tests.TestSupport;
using Microsoft.Extensions.Logging;

namespace Backbone.Service.Tests.Persistence;

/// <summary>
/// HU-07 criterio 5 (§8.2): fallos inyectados en cada paso de la escritura atómica de <c>jobs.json</c> (crear el temporal,
/// escribir, validar el destino, renombrar), temporales huérfanos, archivos dañados en cualquier punto, lectores y
/// escritores concurrentes. Invariante en todos: <c>jobs.json</c> es el contenido anterior completo o el nuevo completo, la
/// lista en memoria coincide con el disco y no queda ningún temporal.
/// </summary>
[Trait("Story", "HU-07")]
public sealed class JobStoreFaultTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly CapturingLoggerProvider _logs = new();
    private readonly ILoggerFactory _loggerFactory;

    public JobStoreFaultTests()
    {
        Directory.CreateDirectory(_temp.Combine("config"));
        _loggerFactory = LoggerFactory.Create(b => b.AddProvider(_logs));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string ConfigDirectory => _temp.Combine("config");

    private string JobsPath => _temp.Combine("config", "jobs.json");

    public void Dispose()
    {
        _loggerFactory.Dispose();
        _temp.Dispose();
    }

    // ---- Paso 1: crear el temporal ----

    [Fact]
    public async Task Step_create_temporary_a_missing_config_directory_fails_without_creating_it_and_keeps_the_list()
    {
        using var store = NewStore();
        await store.LoadAsync(Ct);
        await store.UpdateAsync(list => (list.Add(Job("Docs")), 0), Ct);
        Directory.Delete(ConfigDirectory, recursive: true);

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => store.UpdateAsync(list => (list.Add(Job("Otro")), 0), Ct));

        Assert.False(Directory.Exists(ConfigDirectory));
        Assert.Equal(["Docs"], store.Jobs.Select(j => j.Name));
    }

    // ---- Paso 2: escribir el contenido ----

    [Fact]
    public async Task Step_write_a_cancelled_write_keeps_the_previous_file_the_list_and_leaves_no_temporary_or_event()
    {
        using var store = NewStore();
        await store.LoadAsync(Ct);
        await store.UpdateAsync(list => (list.Add(Job("Docs")), 0), Ct);
        var before = await File.ReadAllBytesAsync(JobsPath, Ct);
        var raised = 0;
        store.Changed += (_, _) => raised++;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.UpdateAsync(
            list =>
            {
                cts.Cancel(); // La cancelación llega después de calcular la nueva lista y antes de escribir.
                return (list.Add(Job("Nuevo")), 0);
            },
            cts.Token));

        Assert.Equal(before, await File.ReadAllBytesAsync(JobsPath, Ct));
        Assert.Equal(["Docs"], store.Jobs.Select(j => j.Name));
        Assert.Equal(0, raised);
        AssertOnlyJobsJson();
    }

    [Fact]
    public async Task A_cancelled_write_after_moving_a_corrupt_file_aside_leaves_it_as_bad_and_the_next_write_does_not_move_anything_again()
    {
        await File.WriteAllTextAsync(JobsPath, "{ corrupto", Ct);
        using var store = NewStore();
        await store.LoadAsync(Ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.UpdateAsync(
            list =>
            {
                cts.Cancel();
                return (list.Add(Job("Docs")), 0);
            },
            cts.Token));

        Assert.Empty(store.Jobs);
        Assert.Equal("{ corrupto", await File.ReadAllTextAsync(BadCopies.Single(JobsPath), Ct));
        Assert.False(File.Exists(JobsPath));

        await store.UpdateAsync(list => (list.Add(Job("Docs")), 0), Ct);

        Assert.Equal("{ corrupto", await File.ReadAllTextAsync(BadCopies.Single(JobsPath), Ct));
        Assert.Equal(["Docs"], (await ReloadAsync()).Select(j => j.Name));
    }

    // ---- Paso 3: validar el destino que se va a reemplazar ----

    [Fact]
    public async Task Step_validate_target_a_directory_where_the_file_should_be_is_rejected_and_the_temporary_removed()
    {
        using var store = NewStore();
        await store.LoadAsync(Ct);
        Directory.CreateDirectory(JobsPath);

        var ex = await Assert.ThrowsAsync<UntrustedFileException>(() => store.UpdateAsync(list => (list.Add(Job("Docs")), 0), Ct));

        Assert.Equal(UntrustedFileReason.NotAFile, ex.Reason);
        Assert.Empty(store.Jobs);
        Assert.True(Directory.Exists(JobsPath));
        Assert.Empty(Directory.GetFileSystemEntries(JobsPath));
        Assert.Empty(Directory.GetFiles(ConfigDirectory));
    }

    [Fact]
    public async Task Step_validate_target_a_hard_linked_file_is_rejected_without_touching_the_other_name()
    {
        using var store = NewStore();
        await store.LoadAsync(Ct);
        await store.UpdateAsync(list => (list.Add(Job("Docs")), 0), Ct);
        var outside = _temp.Combine("fuera.json");
        RunCmd("mklink", "/H", outside, JobsPath);
        var before = await File.ReadAllBytesAsync(outside, Ct);

        var ex = await Assert.ThrowsAsync<UntrustedFileException>(() => store.UpdateAsync(list => (list.Add(Job("Otro")), 0), Ct));

        Assert.Equal(UntrustedFileReason.HardLinked, ex.Reason);
        Assert.Equal(before, await File.ReadAllBytesAsync(outside, Ct));
        Assert.Equal(["Docs"], store.Jobs.Select(j => j.Name));
        AssertOnlyJobsJson();
    }

    // ---- Paso 4: renombrar sobre el destino ----

    [Fact]
    public async Task Step_rename_a_target_held_open_without_share_delete_fails_cleanly_and_a_retry_succeeds()
    {
        using var store = NewStore();
        await store.LoadAsync(Ct);
        await store.UpdateAsync(list => (list.Add(Job("Docs")), 0), Ct);
        var before = await File.ReadAllBytesAsync(JobsPath, Ct);
        var raised = 0;
        store.Changed += (_, _) => raised++;

        await using (new FileStream(JobsPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Write))
        {
            await Assert.ThrowsAnyAsync<IOException>(() => store.UpdateAsync(list => (list.Add(Job("Otro")), 0), Ct));
        }

        Assert.Equal(before, await File.ReadAllBytesAsync(JobsPath, Ct));
        Assert.Equal(["Docs"], store.Jobs.Select(j => j.Name));
        Assert.Equal(0, raised);
        AssertOnlyJobsJson();

        await store.UpdateAsync(list => (list.Add(Job("Otro")), 0), Ct);

        Assert.Equal(["Docs", "Otro"], (await ReloadAsync()).Select(j => j.Name));
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task A_corrupt_file_held_open_cannot_be_moved_aside_and_is_not_lost()
    {
        await File.WriteAllTextAsync(JobsPath, "{ corrupto", Ct);
        using var store = NewStore();
        await store.LoadAsync(Ct);

        await using (new FileStream(JobsPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Write))
        {
            await Assert.ThrowsAnyAsync<IOException>(() => store.UpdateAsync(list => (list.Add(Job("Docs")), 0), Ct));
        }

        Assert.Equal("{ corrupto", await File.ReadAllTextAsync(JobsPath, Ct));
        Assert.Empty(store.Jobs);

        await store.UpdateAsync(list => (list.Add(Job("Docs")), 0), Ct);

        Assert.Equal("{ corrupto", await File.ReadAllTextAsync(BadCopies.Single(JobsPath), Ct));
        Assert.Equal(["Docs"], (await ReloadAsync()).Select(j => j.Name));
    }

    // ---- Temporales huérfanos ----

    [Fact]
    public async Task Every_orphan_temporary_is_removed_on_load_even_without_a_jobs_json_and_unrelated_files_are_kept()
    {
        var orphans = new[] { $".jobs.json.{Guid.NewGuid():N}.tmp", $".jobs.json.{Guid.NewGuid():N}.tmp", ".jobs.json.tmp-a.tmp" };
        foreach (var orphan in orphans)
        {
            await File.WriteAllTextAsync(_temp.Combine("config", orphan), "{ \"jobs\": [ ", Ct);
        }

        var keep = new[] { "settings.json", "jobs.json.bad", $".settings.json.{Guid.NewGuid():N}.tmp", "jobs.json.tmp" };
        foreach (var name in keep)
        {
            await File.WriteAllTextAsync(_temp.Combine("config", name), "x", Ct);
        }

        using var store = NewStore();
        await store.LoadAsync(Ct);

        Assert.Empty(store.Jobs);
        Assert.All(orphans, o => Assert.False(File.Exists(_temp.Combine("config", o))));
        Assert.All(keep, k => Assert.True(File.Exists(_temp.Combine("config", k))));
    }

    [Fact]
    public async Task An_orphan_temporary_is_never_read_as_the_job_list()
    {
        await File.WriteAllTextAsync(
            _temp.Combine("config", $".jobs.json.{Guid.NewGuid():N}.tmp"),
            "{ \"schemaVersion\": 1, \"jobs\": [] }",
            Ct);
        using (var first = NewStore())
        {
            await first.LoadAsync(Ct);
            await first.UpdateAsync(list => (list.Add(Job("Docs")), 0), Ct);
        }

        Assert.Equal(["Docs"], (await ReloadAsync()).Select(j => j.Name));
    }

    [Fact]
    public async Task An_orphan_that_is_a_directory_is_not_deleted_and_the_service_still_loads()
    {
        var trap = Directory.CreateDirectory(_temp.Combine("config", $".jobs.json.{Guid.NewGuid():N}.tmp")).FullName;
        await File.WriteAllTextAsync(Path.Combine(trap, "dentro.txt"), "x", Ct);
        using var store = NewStore();

        await store.LoadAsync(Ct);

        Assert.True(File.Exists(Path.Combine(trap, "dentro.txt")));
        Assert.Empty(store.Jobs);
    }

    [Fact]
    public async Task An_orphan_that_is_a_junction_is_not_followed_and_its_target_is_not_deleted()
    {
        var target = Directory.CreateDirectory(_temp.Combine("objetivo")).FullName;
        await File.WriteAllTextAsync(Path.Combine(target, "importante.txt"), "x", Ct);
        var link = _temp.Combine("config", $".jobs.json.{Guid.NewGuid():N}.tmp");
        RunCmd("mklink", "/J", link, target);
        using var store = NewStore();

        await store.LoadAsync(Ct);

        Assert.True(File.Exists(Path.Combine(target, "importante.txt")));
    }

    // ---- Archivo dañado ----

    [Fact]
    public async Task A_file_cut_at_any_length_never_breaks_the_load_and_never_yields_partial_jobs()
    {
        var bytes = await ValidFileBytesAsync(3);
        var lengths = Enumerable.Range(0, bytes.Length).Where(n => n % 13 == 0).Append(bytes.Length - 1);
        foreach (var length in lengths)
        {
            await File.WriteAllBytesAsync(JobsPath, bytes.AsMemory(0, length).ToArray(), Ct);
            using var store = NewStore();

            await store.LoadAsync(Ct);

            Assert.Empty(store.Jobs);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \r\n")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("\"texto\"")]
    [InlineData("{ \"jobs\": 5 }")]
    [InlineData("{ \"jobs\": \"x\" }")]
    [InlineData("{ \"schemaVersion\": \"uno\", \"jobs\": [] }")]
    [InlineData("\0\0\0\0\0\0\0\0")]
    [InlineData("{ \"jobs\": [] } basura")]
    public async Task Malformed_content_starts_without_jobs_and_the_next_write_is_clean(string content)
    {
        await File.WriteAllTextAsync(JobsPath, content, Ct);
        using var store = NewStore();

        await store.LoadAsync(Ct);

        Assert.Empty(store.Jobs);
        await store.UpdateAsync(list => (list.Add(Job("Docs")), 0), Ct);
        Assert.Equal(["Docs"], (await ReloadAsync()).Select(j => j.Name));
        AssertFileIsValidJson();
    }

    [Fact]
    public async Task A_job_list_with_elements_that_are_not_objects_loads_without_throwing()
    {
        await File.WriteAllTextAsync(JobsPath, "{ \"jobs\": [ 1, \"a\", [], null ] }", Ct);
        using var store = NewStore();

        await store.LoadAsync(Ct);

        Assert.Empty(store.Jobs);
    }

    [Fact]
    public async Task A_zero_byte_file_is_treated_as_corrupt_and_kept_as_bad_when_replaced()
    {
        await File.WriteAllBytesAsync(JobsPath, [], Ct);
        using var store = NewStore();
        await store.LoadAsync(Ct);

        await store.UpdateAsync(list => (list.Add(Job("Docs")), 0), Ct);

        Assert.Equal(0, new FileInfo(BadCopies.Single(JobsPath)).Length);
    }

    [Fact]
    public async Task A_previous_unstamped_bad_file_is_not_touched_and_the_new_copy_gets_a_stamp()
    {
        // BAJA-3: nunca se sobrescribe una copia anterior; un jobs.json.bad sin marca, de una versión anterior, no se toca.
        await File.WriteAllTextAsync(JobsPath + ".bad", "viejo", Ct);
        await File.WriteAllTextAsync(JobsPath, "{ nuevo corrupto", Ct);
        using var store = NewStore();
        await store.LoadAsync(Ct);

        await store.UpdateAsync(list => (list.Add(Job("Docs")), 0), Ct);

        Assert.Equal("viejo", await File.ReadAllTextAsync(JobsPath + ".bad", Ct));
        Assert.Equal("{ nuevo corrupto", await File.ReadAllTextAsync(BadCopies.Single(JobsPath), Ct));
    }

    [Fact]
    public async Task Unicode_names_round_trip_and_a_utf8_bom_does_not_break_the_load()
    {
        var job = Job("Ünïcode 日本語 😀");
        using (var store = NewStore())
        {
            await store.LoadAsync(Ct);
            await store.UpdateAsync(list => (list.Add(job), 0), Ct);
        }

        var bytes = await File.ReadAllBytesAsync(JobsPath, Ct);
        Assert.Equal(job.Name, Assert.Single(await ReloadAsync()).Name);

        await File.WriteAllBytesAsync(JobsPath, [0xEF, 0xBB, 0xBF, .. bytes], Ct);
        var withBom = await ReloadAsync();

        Assert.True(withBom.IsEmpty || withBom[0].Name == job.Name);
    }

    // ---- Concurrencia ----

    [Fact]
    public async Task Concurrent_writers_are_serialized_and_every_change_is_kept_in_a_valid_file()
    {
        using var store = NewStore();
        await store.LoadAsync(Ct);
        var raised = 0;
        store.Changed += (_, _) => Interlocked.Increment(ref raised);
        const int Count = 40;

        await Task.WhenAll(Enumerable.Range(0, Count).Select(i => Task.Run(() => store.UpdateAsync(list => (list.Add(Job($"Trabajo {i}")), 0), Ct), Ct)));

        Assert.Equal(Count, store.Jobs.Length);
        Assert.Equal(Count, raised);
        var reloaded = await ReloadAsync();
        Assert.Equal(store.Jobs.Select(j => j.Id), reloaded.Select(j => j.Id));
        Assert.Equal(Enumerable.Range(0, Count).Select(i => $"Trabajo {i}").Order(StringComparer.Ordinal), reloaded.Select(j => j.Name).Order(StringComparer.Ordinal));
        AssertOnlyJobsJson();
    }

    [Fact]
    public async Task A_reader_during_concurrent_writes_always_sees_a_complete_file_and_never_fewer_jobs_than_before()
    {
        using var store = NewStore();
        await store.LoadAsync(Ct);
        await store.UpdateAsync(list => (list.Add(Job("Inicial")), 0), Ct);
        using var stop = new CancellationTokenSource();
        var failures = new ConcurrentQueue<string>();
        var reads = 0;
        var reader = Task.Run(
            async () =>
            {
                var files = TestFileTrust.CurrentUser;
                var last = 0;
                while (!stop.IsCancellationRequested)
                {
                    try
                    {
                        var bytes = await files.ReadAllBytesAsync(JobsPath, JobStore.MaxFileBytes, CancellationToken.None);
                        var document = bytes is null ? null : JsonSerializer.Deserialize<JobsDocument>(bytes, JobStore.JsonOptions);
                        if (document is null || document.Jobs.Count < last)
                        {
                            failures.Enqueue($"{document?.Jobs.Count} < {last}");
                        }
                        else
                        {
                            last = document.Jobs.Count;
                        }

                        reads++;
                    }
                    catch (Exception ex)
                    {
                        failures.Enqueue(ex.GetType().Name + ": " + ex.Message);
                    }
                }
            },
            CancellationToken.None);

        await Task.WhenAll(Enumerable.Range(0, 30).Select(i => Task.Run(() => store.UpdateAsync(list => (list.Add(Job($"T{i}")), 0), Ct), Ct)));
        await stop.CancelAsync();
        await reader;

        Assert.Empty(failures);
        Assert.True(reads > 0);
        Assert.Equal(31, store.Jobs.Length);
    }

    [Fact]
    public async Task A_failing_change_does_not_block_the_next_writer()
    {
        using var store = NewStore();
        await store.LoadAsync(Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpdateAsync<int>(_ => throw new InvalidOperationException(), Ct));
        await store.UpdateAsync(list => (list.Add(Job("Docs")), 0), Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Single(store.Jobs);
    }

    // ---- Auxiliares ----

    private JobStore NewStore() => new(JobsPath, TestFileTrust.CurrentUser, _loggerFactory.CreateLogger<JobStore>());

    private async Task<ImmutableArray<JobDefinition>> ReloadAsync()
    {
        using var store = NewStore();
        await store.LoadAsync(Ct);
        return store.Jobs;
    }

    private async Task<byte[]> ValidFileBytesAsync(int jobs)
    {
        var path = _temp.Combine("modelo", "jobs.json");
        Directory.CreateDirectory(_temp.Combine("modelo"));
        using var store = new JobStore(path, TestFileTrust.CurrentUser, _loggerFactory.CreateLogger<JobStore>());
        await store.LoadAsync(Ct);
        for (var i = 0; i < jobs; i++)
        {
            var name = $"Trabajo {i}";
            await store.UpdateAsync(list => (list.Add(Job(name)), 0), Ct);
        }

        return await File.ReadAllBytesAsync(path, Ct);
    }

    /// <summary>En <c>config\</c> solo está <c>jobs.json</c> (y, si procede, <c>jobs.json.bad</c>): ningún temporal.</summary>
    private void AssertOnlyJobsJson()
    {
        var names = Directory.GetFileSystemEntries(ConfigDirectory).Select(p => Path.GetFileName(p)).Where(n => !n.StartsWith("jobs.json.bad", StringComparison.Ordinal)).ToList();
        Assert.Equal(["jobs.json"], names);
        AssertFileIsValidJson();
    }

    private void AssertFileIsValidJson()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(JobsPath));
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
    }

    private static JobDefinition Job(string name) =>
        JobDefaults.NewJob() with
        {
            Id = Guid.NewGuid(),
            Name = name,
            Sources = [new JobSource { Alias = "Docs", Path = @"C:\Users\ana\Docs" }],
            Destination = $@"\\nas\backups\{Guid.NewGuid():N}",
        };

    private static void RunCmd(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        startInfo.ArgumentList.Add("/c");
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("cmd.exe");
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"{string.Join(' ', arguments)} falló: {output}");
    }
}
