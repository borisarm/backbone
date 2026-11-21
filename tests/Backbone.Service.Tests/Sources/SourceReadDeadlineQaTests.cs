using Backbone.Contracts;
using Backbone.Core.Engine;
using Backbone.Service.Sources;
using Backbone.Service.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Backbone.Service.Tests.Sources;

/// <summary>
/// HU-08, auditoría de QA de los plazos de §8.2 ("Plazo de las lecturas de los orígenes", R12) con reloj simulado: cada
/// operación del <see cref="SourceFileSystem"/> tiene plazo (no solo la apertura, la enumeración y la lectura), el límite
/// de 60 s es exacto, los 30 s de cancelación se cuentan aparte, una ejecución cancelada que no vuelve se abandona a los 30 s
/// y, una vez abandonada, no se puede seguir leyendo nada.
/// </summary>
[Trait("Story", "HU-08")]
public sealed class SourceReadDeadlineQaTests : IDisposable
{
    private const uint CloudTag = 0x9000601A;

    private readonly FakeSourceFileApi _api = new();
    private readonly FakeTimeProvider _time = new();
    private readonly CapturingLoggerProvider _logs = new();
    private readonly SourceFileSystem _fs;

    public SourceReadDeadlineQaTests()
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

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Operaciones sobre el handle (no solo abrir) y la tarea que las ejerce.</summary>
    public static TheoryData<FakeBlockAt> HandleOperations => new()
    {
        FakeBlockAt.Attributes,
        FakeBlockAt.FinalPath,
        FakeBlockAt.FileId,
        FakeBlockAt.Metadata,
        FakeBlockAt.AllocatedRanges,
    };

    private Task Exercise(FakeBlockAt at, FakeBlockMode mode, CancellationToken token = default)
    {
        FakeNode node;
        switch (at)
        {
            case FakeBlockAt.Metadata:
                node = _api.AddFile("a.bin", [1]);
                break;
            case FakeBlockAt.AllocatedRanges:
                node = _api.AddFile("a.bin", [1], FileAttributes.ReparsePoint, CloudTag);
                break;
            case FakeBlockAt.FileId:
                node = _api.AddFile("a.bin", [1], FileAttributes.ReparsePoint, ReparseTags.Wof); // el identificador solo se pide en la lista blanca
                break;
            default:
                node = _api.AddFile("a.bin", [1]);
                break;
        }

        node.BlockAt = at;
        node.BlockMode = mode;
        var path = FakeSourceFileApi.Child("a.bin");
        return at == FakeBlockAt.Metadata ? (Task)_fs.GetFileInfoAsync(path, token) : ReadAllAsync(path, token);
    }

    [Theory]
    [MemberData(nameof(HandleOperations))]
    public async Task HU08_Every_operation_on_a_handle_has_the_60_second_deadline_and_the_item_is_kept_from_the_parent(FakeBlockAt at)
    {
        var operation = Exercise(at, FakeBlockMode.HonorCancellation, Ct);

        var elapsed = await AdvanceUntilCompletedAsync(operation);

        var ex = await Assert.ThrowsAsync<SourceAccessException>(() => operation);
        Assert.Equal(RunIssueCode.ReadError, ex.Code); // el motor omite el elemento y conserva la versión del padre (RF-25)
        Assert.Contains("plazo", ex.Message, StringComparison.Ordinal);
        Assert.InRange(elapsed, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(65));
        Assert.False(_fs.IsAbandoned);
        Assert.Equal(0, _api.OpenHandles);
        Assert.Equal(0, _api.ReadCalls);
    }

    [Theory]
    [MemberData(nameof(HandleOperations))]
    public async Task HU08_An_operation_on_a_handle_that_ignores_the_cancellation_is_abandoned_after_90_seconds_and_nothing_else_can_be_read(FakeBlockAt at)
    {
        var operation = Exercise(at, FakeBlockMode.IgnoreCancellation, Ct);
        _api.AddFile("otro.bin", [9]);

        var elapsed = await AdvanceUntilCompletedAsync(operation);

        await Assert.ThrowsAsync<SourceOperationAbandonedException>(() => operation);
        Assert.InRange(elapsed, TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(95));
        Assert.True(_fs.IsAbandoned);
        var opens = _api.Opens.Count;
        await Assert.ThrowsAsync<SourceOperationAbandonedException>(() => ListAsync(FakeSourceFileApi.ReadPath));
        await Assert.ThrowsAsync<SourceOperationAbandonedException>(() => ReadAllAsync(FakeSourceFileApi.Child("otro.bin"), Ct));
        await Assert.ThrowsAsync<SourceOperationAbandonedException>(() => _fs.GetFileInfoAsync(FakeSourceFileApi.Child("otro.bin"), Ct));
        Assert.Equal(opens, _api.Opens.Count); // sin tocar el sistema de archivos
    }

    [Fact]
    public async Task HU08_An_operation_that_finishes_at_59_seconds_succeeds()
    {
        var node = _api.AddFile("lento.bin", [1, 2, 3]);
        node.BlockAt = FakeBlockAt.Open;
        node.BlockMode = FakeBlockMode.WaitForRelease;

        var operation = ReadAllAsync(FakeSourceFileApi.Child("lento.bin"), Ct);
        await AdvanceUntilAsync(operation, TimeSpan.FromSeconds(59), _api.ReleaseBlocked);

        Assert.Equal(new byte[] { 1, 2, 3 }, await operation);
        Assert.False(_fs.IsAbandoned);
        Assert.Equal(0, _api.OpenHandles);
    }

    [Fact]
    public async Task HU08_An_operation_that_returns_during_the_30_seconds_of_cancellation_is_a_read_error_not_an_abandonment()
    {
        var node = _api.AddFile("lento.bin", [1]);
        node.BlockAt = FakeBlockAt.Open;
        node.BlockMode = FakeBlockMode.WaitForRelease; // no atiende la cancelación: solo vuelve cuando se le libera
        _api.AddFile("bien.bin", [5]);

        var operation = ReadAllAsync(FakeSourceFileApi.Child("lento.bin"), Ct);
        await AdvanceUntilAsync(operation, TimeSpan.FromSeconds(85), _api.ReleaseBlocked);

        var ex = await Assert.ThrowsAsync<SourceAccessException>(() => operation);
        Assert.Equal(RunIssueCode.ReadError, ex.Code);
        Assert.False(_fs.IsAbandoned);
        Assert.Equal(new byte[] { 5 }, await ReadAllAsync(FakeSourceFileApi.Child("bien.bin"), Ct)); // el hilo sigue sirviendo
        Assert.Equal(0, _api.OpenHandles);
    }

    [Fact]
    public async Task HU08_A_cancelled_run_whose_operation_does_not_return_is_abandoned_30_seconds_after_the_cancellation()
    {
        var node = _api.AddFile("a.bin", [1]);
        node.BlockAt = FakeBlockAt.Open;
        node.BlockMode = FakeBlockMode.IgnoreCancellation;
        using var cancellation = new CancellationTokenSource();
        var operation = _fs.OpenReadAsync(FakeSourceFileApi.Child("a.bin"), cancellation.Token);
        Assert.True(_api.Blocked.Wait(TimeSpan.FromSeconds(10), Ct));

        await cancellation.CancelAsync();
        var elapsed = await AdvanceUntilCompletedAsync(operation);

        await Assert.ThrowsAsync<SourceOperationAbandonedException>(() => operation);
        Assert.InRange(elapsed, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(35)); // 30 s de cancelación, no 60 + 30
        Assert.True(_fs.IsAbandoned);
    }

    [Fact]
    public async Task HU08_After_an_abandonment_a_stream_that_was_already_open_cannot_be_read_and_its_handles_close_on_dispose()
    {
        var content = new byte[20_000];
        _api.AddFile("abierto.bin", content, FileAttributes.ReparsePoint, ReparseTags.Wof);
        var stuck = _api.AddFile("colgado.bin", [1]);
        stuck.BlockAt = FakeBlockAt.Open;
        stuck.BlockMode = FakeBlockMode.IgnoreCancellation;
        var stream = await _fs.OpenReadAsync(FakeSourceFileApi.Child("abierto.bin"), Ct);
        Assert.Equal(2, _api.OpenHandles);

        var operation = ReadAllAsync(FakeSourceFileApi.Child("colgado.bin"), Ct);
        await AdvanceUntilCompletedAsync(operation);
        await Assert.ThrowsAsync<SourceOperationAbandonedException>(() => operation);

        await Assert.ThrowsAsync<SourceOperationAbandonedException>(() => stream.ReadAsync(new byte[4096], Ct).AsTask());
        await stream.DisposeAsync();
        Assert.Equal(0, _api.OpenHandles);
    }

    [Fact]
    public async Task HU08_The_handle_that_an_abandoned_operation_returns_late_is_released()
    {
        var stuck = _api.AddFile("colgado.bin", [1]);
        stuck.BlockAt = FakeBlockAt.Open;
        stuck.BlockMode = FakeBlockMode.IgnoreCancellation;
        var operation = ReadAllAsync(FakeSourceFileApi.Child("colgado.bin"), Ct);
        await AdvanceUntilCompletedAsync(operation);
        await Assert.ThrowsAsync<SourceOperationAbandonedException>(() => operation);

        _api.Dispose(); // el controlador "responde" por fin
        for (var i = 0; i < 200 && !(_api.LateHandles == 1 && _api.OpenHandles == 0); i++)
        {
            await Task.Delay(50, Ct);
        }

        Assert.Equal(0, _api.OpenHandles);
        Assert.Equal(1, _api.LateHandles);
    }

    [Fact]
    public async Task HU08_The_deadline_is_per_operation_so_a_file_with_many_slow_but_in_time_blocks_is_not_cut_off()
    {
        // 3 bloques de lectura, cada uno dentro del plazo: la copia de un archivo grande y lento no vence por su duración total.
        _api.AddFile("grande.bin", new byte[3 * 4096]);
        await using var stream = await _fs.OpenReadAsync(FakeSourceFileApi.Child("grande.bin"), Ct);

        var total = 0;
        var buffer = new byte[4096];
        for (var i = 0; i < 3; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(50));
            total += await stream.ReadAsync(buffer, Ct);
        }

        Assert.Equal(3 * 4096, total);
        Assert.False(_fs.IsAbandoned);
    }

    // ================================================================== Ayudas

    private async Task<TimeSpan> AdvanceUntilCompletedAsync(Task task)
    {
        var start = _time.GetUtcNow();
        Assert.True(_api.Blocked.Wait(TimeSpan.FromSeconds(10), Ct) || task.IsCompleted, "La operación no llegó a bloquearse.");
        for (var i = 0; i < 400 && !task.IsCompleted; i++)
        {
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

    /// <summary>Avanza el reloj hasta <paramref name="at"/> segundos desde el bloqueo y ejecuta <paramref name="then"/>.</summary>
    private async Task AdvanceUntilAsync(Task task, TimeSpan at, Action then)
    {
        var start = _time.GetUtcNow();
        Assert.True(_api.Blocked.Wait(TimeSpan.FromSeconds(10), Ct), "La operación no llegó a bloquearse.");
        while (_time.GetUtcNow() - start < at)
        {
            await Task.Delay(10, CancellationToken.None);
            Assert.False(task.IsCompleted, $"La operación terminó antes de {at.TotalSeconds:0} s simulados.");
            _time.Advance(TimeSpan.FromSeconds(1));
        }

        then();
        await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10), CancellationToken.None));
        if (!task.IsCompleted)
        {
            // Sigue contando el reloj (por ejemplo, la espera de la cancelación).
            await AdvanceUntilCompletedAsync(task);
        }
    }

    private async Task<List<SourceEntry>> ListAsync(string path)
    {
        var list = new List<SourceEntry>();
        await foreach (var entry in _fs.EnumerateAsync(path, Ct))
        {
            list.Add(entry);
        }

        return list;
    }

    private async Task<byte[]> ReadAllAsync(string path, CancellationToken token)
    {
        await using var stream = await _fs.OpenReadAsync(path, token);
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, Ct);
        return memory.ToArray();
    }
}
