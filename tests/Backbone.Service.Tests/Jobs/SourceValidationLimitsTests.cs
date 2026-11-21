using Backbone.Contracts;
using Backbone.Service.Jobs;
using Backbone.Service.Os;
using Backbone.Service.Tests.TestSupport;
using Microsoft.Extensions.Time.Testing;

namespace Backbone.Service.Tests.Jobs;

/// <summary>
/// §8.3 "Sin bloquear el hilo RPC" (revisión de seguridad de HU-07, MEDIA-1): la resolución de orígenes se hace en un hilo de
/// trabajo, como mucho 4 a la vez en todo el servicio, cancelable con la llamada y con el plazo de §8.2 (60 s y 30 s más para
/// la cancelación; vencido, <c>SourceUnreadable</c>; si no vuelve, el hilo se abandona y su plaza sigue ocupada). El
/// <c>JobRunner</c> no pasa por el límite. El reloj es un <see cref="FakeTimeProvider"/>.
/// </summary>
[Trait("Story", "HU-07")]
public sealed class SourceValidationLimitsTests : IDisposable
{
    private const string Hung = @"C:\Colgada";
    private const string Documents = @"C:\Users\ana\Documentos";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 21, 9, 0, 0, TimeSpan.Zero));
    private readonly FakeLocalFileSystemInspector _fileSystem = new FakeLocalFileSystemInspector()
        .AddFolder(Hung)
        .AddFolder(Documents)
        .AddFolder(@"C:\ProgramData");

    private readonly SourceValidationRunner _runner;
    private readonly JobValidator _validator;

    public SourceValidationLimitsTests()
    {
        _fileSystem.Hanging[Hung] = true;
        _runner = new SourceValidationRunner(_time, new SourceIoOptions(), () => new RecordingCanceller());
        _validator = TestJobValidators.Create(_fileSystem, @"C:\ProgramData\Backbone", runner: _runner, time: _time);
    }

    public void Dispose()
    {
        _fileSystem.ReleaseHung();
        _runner.Dispose();
        _fileSystem.Dispose();
    }

    private static JobDefinition Job(params string[] paths) =>
        JobDefaults.NewJob() with
        {
            Name = "Prueba",
            Sources = [.. paths.Select((p, i) => new JobSource { Alias = $"O{i}", Path = p })],
            Destination = @"\\nas\backups",
        };

    [Fact]
    public async Task The_resolution_runs_on_a_worker_thread_not_on_the_calling_thread()
    {
        var caller = Environment.CurrentManagedThreadId;
        var threads = new List<int>();
        var runner = new SourceValidationRunner(TimeProvider.System, new SourceIoOptions());
        using (runner)
        {
            threads.Add(await runner.RunAsync(_ => Environment.CurrentManagedThreadId, limited: true, TestContext.Current.CancellationToken));
        }

        Assert.NotEqual(caller, Assert.Single(threads));
    }

    [Fact]
    public async Task At_most_four_validations_resolve_sources_at_the_same_time_and_the_rest_wait()
    {
        var validations = Enumerable.Range(0, 6).Select(_ => _validator.ValidateAsync(Job(Hung), [], TestContext.Current.CancellationToken)).ToList();
        for (var i = 0; i < 4; i++)
        {
            Assert.True(await _fileSystem.HangEntered.WaitAsync(Wait, TestContext.Current.CancellationToken));
        }

        Assert.False(await _fileSystem.HangEntered.WaitAsync(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken));
        Assert.Equal(0, _runner.AvailableSlots);
        Assert.Equal(4, _fileSystem.MaxConcurrent);

        // Vence el plazo de las 4 primeras: dan SourceUnreadable y entran las 2 que esperaban.
        _time.Advance(TimeSpan.FromSeconds(60));
        for (var i = 0; i < 2; i++)
        {
            Assert.True(await _fileSystem.HangEntered.WaitAsync(Wait, TestContext.Current.CancellationToken));
        }

        _time.Advance(TimeSpan.FromSeconds(60));
        var results = await Task.WhenAll(validations).WaitAsync(Wait, TestContext.Current.CancellationToken);

        Assert.All(results, r => Assert.Equal(ValidationIssueCode.SourceUnreadable, Assert.Single(r.Issues.Errors).Code));
        Assert.True(_fileSystem.MaxConcurrent <= 4);
    }

    [Fact]
    public async Task A_timed_out_source_is_SourceUnreadable_and_the_other_sources_are_still_checked()
    {
        var validation = _validator.ValidateAsync(Job(Hung, Documents), [], TestContext.Current.CancellationToken);
        Assert.True(await _fileSystem.HangEntered.WaitAsync(Wait, TestContext.Current.CancellationToken));

        _time.Advance(TimeSpan.FromSeconds(59));
        Assert.False(validation.IsCompleted);
        _time.Advance(TimeSpan.FromSeconds(1));
        var result = (await validation.WaitAsync(Wait, TestContext.Current.CancellationToken)).Issues;

        var error = Assert.Single(result.Errors);
        Assert.Equal(("Sources[0].Path", ValidationIssueCode.SourceUnreadable), (error.Field, error.Code));
        Assert.Contains(Documents, _fileSystem.Resolved);
        Assert.Equal(4, _runner.AvailableSlots);
    }

    [Fact]
    public async Task A_validation_is_cancelled_with_its_call_and_frees_its_slot()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var validation = _validator.ValidateAsync(Job(Hung), [], cts.Token);
        Assert.True(await _fileSystem.HangEntered.WaitAsync(Wait, TestContext.Current.CancellationToken));

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => validation.WaitAsync(Wait, TestContext.Current.CancellationToken));
        await WaitUntilAsync(() => _runner.AvailableSlots == 4);
    }

    [Fact]
    public async Task A_validation_waiting_for_a_slot_is_cancelled_with_its_call()
    {
        var busy = Enumerable.Range(0, 4).Select(_ => _validator.ValidateAsync(Job(Hung), [], TestContext.Current.CancellationToken)).ToList();
        for (var i = 0; i < 4; i++)
        {
            Assert.True(await _fileSystem.HangEntered.WaitAsync(Wait, TestContext.Current.CancellationToken));
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var waiting = _validator.ValidateAsync(Job(Documents), [], cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(Wait, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(Documents, _fileSystem.Resolved);
        _time.Advance(TimeSpan.FromSeconds(60));
        await Task.WhenAll(busy).WaitAsync(Wait, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task An_operation_that_ignores_cancellation_is_abandoned_after_30_more_seconds_and_keeps_its_slot()
    {
        _fileSystem.IgnoreCancellation = true;
        var validation = _validator.ValidateAsync(Job(Hung, Documents), [], TestContext.Current.CancellationToken);
        Assert.True(await _fileSystem.HangEntered.WaitAsync(Wait, TestContext.Current.CancellationToken));

        _time.Advance(TimeSpan.FromSeconds(60));
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(validation.IsCompleted);
        _time.Advance(TimeSpan.FromSeconds(30));
        var result = (await validation.WaitAsync(Wait, TestContext.Current.CancellationToken)).Issues;

        // Los dos orígenes sin terminar dan SourceUnreadable; el hilo sigue colgado y ocupa su plaza hasta que vuelva.
        Assert.Equal(
            [("Sources[0].Path", ValidationIssueCode.SourceUnreadable), ("Sources[1].Path", ValidationIssueCode.SourceUnreadable)],
            result.Errors.Select(e => (e.Field, e.Code)));
        Assert.Equal(3, _runner.AvailableSlots);

        _fileSystem.ReleaseHung();
        await WaitUntilAsync(() => _runner.AvailableSlots == 4);
    }

    [Fact]
    public async Task The_JobRunner_path_is_not_limited_by_the_validation_slots()
    {
        var busy = Enumerable.Range(0, 4).Select(_ => _validator.ValidateAsync(Job(Hung), [], TestContext.Current.CancellationToken)).ToList();
        for (var i = 0; i < 4; i++)
        {
            Assert.True(await _fileSystem.HangEntered.WaitAsync(Wait, TestContext.Current.CancellationToken));
        }

        var info = await _runner.RunAsync(guard => _fileSystem.ResolveFolder(Documents, guard), limited: false, TestContext.Current.CancellationToken)
            .WaitAsync(Wait, TestContext.Current.CancellationToken);

        Assert.Equal(LocalFolderStatus.Ok, info.Status);
        _time.Advance(TimeSpan.FromSeconds(60));
        await Task.WhenAll(busy).WaitAsync(Wait, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void The_guard_cancels_the_synchronous_io_of_the_thread_when_the_deadline_expires()
    {
        var canceller = new RecordingCanceller();
        using var guard = new SourceIoGuard(_time, new SourceIoOptions(), () => { }, canceller, TestContext.Current.CancellationToken);
        var started = new ManualResetEventSlim();
        Exception? thrown = null;
        var worker = new Thread(() =>
        {
            try
            {
                guard.Run(token =>
                {
                    started.Set();
                    token.WaitHandle.WaitOne();
                    return 0;
                });
            }
            catch (Exception ex)
            {
                thrown = ex;
            }
        });
        worker.Start();
        Assert.True(started.Wait(Wait, TestContext.Current.CancellationToken));

        _time.Advance(TimeSpan.FromSeconds(60));
        Assert.True(worker.Join(Wait));

        Assert.Equal(1, canceller.Cancellations);
        Assert.IsType<SourceIoTimeoutException>(thrown);
        started.Dispose();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < Wait, "La condición no se ha cumplido a tiempo.");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }
}
