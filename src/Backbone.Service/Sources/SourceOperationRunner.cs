using System.Collections.Concurrent;
using Backbone.Contracts;
using Backbone.Core.Engine;
using Microsoft.Extensions.Logging;
using static Backbone.Service.Os.NativeMethods.SourceIo;

namespace Backbone.Service.Sources;

/// <summary>Plazos de la lectura de los orígenes (§8.2 "Plazo de las lecturas de los orígenes"). Las pruebas los acortan.</summary>
internal sealed record SourceReadOptions
{
    public static SourceReadOptions Default { get; } = new();

    /// <summary>Plazo de cada apertura, llamada de enumeración, consulta o bloque leído: 60 s.</summary>
    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Lo que se espera a que vuelva una operación cancelada antes de abandonarla: 30 s.</summary>
    public TimeSpan CancellationGrace { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Cada cuánto se repite la cancelación mientras no vuelve (la E/S puede empezar después del primer intento).</summary>
    public TimeSpan RecancelInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Tamaño máximo de un bloque leído (el búfer propio del flujo).</summary>
    public int ReadBlockBytes { get; init; } = 1024 * 1024;
}

/// <summary>
/// Ejecuta las operaciones síncronas sobre los orígenes (aperturas, enumeraciones, consultas sobre el handle) en un
/// <b>hilo propio</b> con plazo, y las lecturas asíncronas con plazo (§8.2 "Plazo de las lecturas de los orígenes", R12).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>
/// Al vencer el plazo (o cancelarse el <see cref="CancellationToken"/> de la ejecución) se cancela la operación: en el hilo,
/// con <c>CancelSynchronousIo</c> (y <c>CancelIoEx</c> sobre el handle si la operación tiene uno); en las lecturas, con
/// <c>CancelIoEx</c>. La cancelación se repite cada <see cref="SourceReadOptions.RecancelInterval"/> mientras la operación
/// no vuelve, porque <c>CancelSynchronousIo</c> solo cancela la E/S que está pendiente en ese momento.
/// </description></item>
/// <item><description>
/// Si vuelve: con el plazo vencido, <see cref="SourceAccessException"/> con <see cref="RunIssueCode.ReadError"/> (el motor
/// omite el elemento y conserva la versión del padre, RF-25); con la ejecución cancelada,
/// <see cref="OperationCanceledException"/>. Lo que devuelva tarde (un handle) se libera.
/// </description></item>
/// <item><description>
/// Si tampoco vuelve en <see cref="SourceReadOptions.CancellationGrace"/>: se registra, el hilo se abandona y esta operación
/// y todas las siguientes lanzan <see cref="SourceOperationAbandonedException"/>, que no es un error de un elemento
/// (no deriva de <see cref="IOException"/>): la ejecución termina <c>Failed</c>/<c>Internal</c>.
/// </description></item>
/// </list>
/// El hilo se crea con el ejecutor (uno por <see cref="SourceFileSystem"/>, es decir, por ejecución) y ejecuta las
/// operaciones de una en una; el plazo cuenta desde que la operación empieza en el hilo.
/// </remarks>
internal sealed partial class SourceOperationRunner : IDisposable
{
    private readonly BlockingCollection<WorkItem> _queue = [];
    private readonly Lock _threadHandleLock = new();
    private readonly TimeProvider _time;
    private readonly SourceReadOptions _options;
    private readonly ILogger _logger;
    private nint _threadHandle;
    private string? _abandoned;
    private int _disposed;

    public SourceOperationRunner(TimeProvider time, SourceReadOptions options, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _time = time;
        _options = options;
        _logger = logger;
        var thread = new Thread(Work)
        {
            IsBackground = true,
            Name = "Backbone source reader",
        };
        thread.Start();
    }

    /// <summary>Se ha abandonado una operación: la lectura de los orígenes de esta ejecución no puede seguir.</summary>
    public bool IsAbandoned => Volatile.Read(ref _abandoned) is not null;

    public SourceReadOptions Options => _options;

    /// <summary>Lanza <see cref="SourceOperationAbandonedException"/> si se ha abandonado alguna operación.</summary>
    public void ThrowIfAbandoned()
    {
        var abandoned = Volatile.Read(ref _abandoned);
        if (abandoned is not null)
        {
            throw new SourceOperationAbandonedException(abandoned);
        }
    }

    /// <summary>
    /// Ejecuta <paramref name="operation"/> en el hilo propio con plazo. <paramref name="target"/> es el handle sobre el que
    /// trabaja, si lo hay, para cancelarlo también con <c>CancelIoEx</c>. El token que recibe la operación se cancela al
    /// cancelarla (lo usan los simuladores; la operación real la desbloquea <c>CancelSynchronousIo</c>).
    /// </summary>
    public async Task<T> RunAsync<T>(
        string description,
        string path,
        ISourceHandle? target,
        Func<CancellationToken, T> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ThrowIfAbandoned();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();

        var item = new WorkItem<T>(operation, target);
        try
        {
            _queue.Add(item, CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            ThrowIfAbandoned();
            throw new ObjectDisposedException(nameof(SourceOperationRunner));
        }

        var abandoned = Volatile.Read(ref _abandoned);
        if (abandoned is not null)
        {
            item.FailIfNotStarted(new SourceOperationAbandonedException(abandoned));
        }

        bool timedOut;
        using (var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            // Espera a que empiece (de una en una). Si la ejecución se cancela antes, no llega a empezar.
            var cancelled = Task.Delay(Timeout.InfiniteTimeSpan, _time, wait.Token);
            if (await Task.WhenAny(item.Started, cancelled).ConfigureAwait(false) != item.Started && item.TryCancelBeforeStart())
            {
                await wait.CancelAsync().ConfigureAwait(false);
                throw new OperationCanceledException(cancellationToken);
            }

            await item.Started.ConfigureAwait(false); // propaga el abandono si se vació la cola sin ejecutarla

            var deadline = Task.Delay(_options.OperationTimeout, _time, wait.Token);
            var winner = await Task.WhenAny(item.Completion, deadline).ConfigureAwait(false);
            if (winner == item.Completion || item.Completion.IsCompleted)
            {
                await wait.CancelAsync().ConfigureAwait(false);
                return await item.Completion.ConfigureAwait(false);
            }

            timedOut = deadline.Status == TaskStatus.RanToCompletion;
            await wait.CancelAsync().ConfigureAwait(false);
        }

        await CancelAndWaitAsync(item, description, path).ConfigureAwait(false);
        if (!timedOut)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        throw TimedOut(description);
    }

    /// <summary>Lee un bloque de <paramref name="handle"/> con plazo (E/S asíncrona, cancelada con <c>CancelIoEx</c>).</summary>
    public async Task<int> ReadAsync(ISourceHandle handle, Memory<byte> buffer, long fileOffset, string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ThrowIfAbandoned();
        cancellationToken.ThrowIfCancellationRequested();

        using var timeout = new CancellationTokenSource(_options.OperationTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        Task<int> read;
        try
        {
            read = handle.ReadAsync(buffer, fileOffset, linked.Token).AsTask();
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            read = Task.FromCanceled<int>(linked.Token);
        }

        var cancelled = Task.Delay(Timeout.InfiniteTimeSpan, _time, linked.Token);
        var winner = await Task.WhenAny(read, cancelled).ConfigureAwait(false);
        if (winner == read && !(read.IsCanceled && linked.IsCancellationRequested))
        {
            return await read.ConfigureAwait(false);
        }

        // Plazo vencido o ejecución cancelada: el token ya ha pedido CancelIoEx; se pide otra vez por si acaso.
        if (!read.IsCompleted)
        {
            handle.CancelIo();
            var grace = Task.Delay(_options.CancellationGrace, _time, CancellationToken.None);
            if (await Task.WhenAny(read, grace).ConfigureAwait(false) != read)
            {
                Abandon("lectura de un bloque", path);
                ObserveLater(read);
                ThrowIfAbandoned();
            }
        }

        ObserveLater(read);
        cancellationToken.ThrowIfCancellationRequested();
        throw TimedOut("lectura de un bloque");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // No se espera al hilo (puede estar abandonado): termina solo al vaciarse la cola.
        _queue.CompleteAdding();
    }

    private static void ObserveLater(Task task) =>
        _ = task.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private SourceAccessException TimedOut(string description) => new(
        RunIssueCode.ReadError,
        $"Se superó el plazo de {_options.OperationTimeout.TotalSeconds:0} s ({description}); la operación se canceló y se conserva la versión anterior si la había.");

    private async Task CancelAndWaitAsync(WorkItem item, string description, string path)
    {
        item.RequestCancel(this);
        using var recancel = _time.CreateTimer(_ => item.RequestCancel(this), null, _options.RecancelInterval, _options.RecancelInterval);
        var grace = Task.Delay(_options.CancellationGrace, _time, CancellationToken.None);
        var returned = await Task.WhenAny(item.CompletionUntyped, grace).ConfigureAwait(false) == item.CompletionUntyped;

        // Lo que devuelva cuando vuelva (un handle) se libera y su error, esperado tras cancelar, se descarta.
        item.DisposeResultWhenDone();
        if (!returned)
        {
            Abandon(description, path);
            ThrowIfAbandoned();
        }
    }

    private void Abandon(string description, string path)
    {
        var message = $"Una operación sobre un origen ({description}) no ha vuelto en {(_options.OperationTimeout + _options.CancellationGrace).TotalSeconds:0} s, ni tras cancelarla; se abandona su hilo y la copia no puede seguir.";
        if (Interlocked.CompareExchange(ref _abandoned, message, null) is not null)
        {
            return;
        }

        LogAbandoned(_logger, description, UntrustedText.Describe(path), _options.OperationTimeout.TotalSeconds, _options.CancellationGrace.TotalSeconds);
        while (_queue.TryTake(out var pending))
        {
            pending.FailIfNotStarted(new SourceOperationAbandonedException(message));
        }

        try
        {
            _queue.CompleteAdding();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void Work()
    {
        // CancelSynchronousIo exige un handle real del hilo con THREAD_TERMINATE.
        var handle = OpenThread(ThreadTerminate, inheritHandle: false, GetCurrentThreadId());
        lock (_threadHandleLock)
        {
            _threadHandle = handle;
        }

        try
        {
            foreach (var item in _queue.GetConsumingEnumerable())
            {
                if (item.TryStart())
                {
                    item.Execute();
                }
            }
        }
        finally
        {
            lock (_threadHandleLock)
            {
                _threadHandle = 0;
                if (handle != 0)
                {
                    CloseHandle(handle);
                }
            }
        }
    }

    private void CancelSynchronousIoOfWorker()
    {
        lock (_threadHandleLock)
        {
            if (_threadHandle != 0)
            {
                _ = CancelSynchronousIo(_threadHandle); // ERROR_NOT_FOUND si no había E/S pendiente: se reintentará
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "La operación sobre el origen ({Operation}, {Path}) no ha vuelto tras {Timeout} s de plazo y {Grace} s más tras cancelarla: se abandona su hilo y la ejecución terminará con error interno (R12).")]
    private static partial void LogAbandoned(ILogger logger, string operation, string path, double timeout, double grace);

    /// <summary>Una operación en la cola. Su estado se cambia bajo <see cref="Gate"/>.</summary>
    private abstract class WorkItem(ISourceHandle? target) : IDisposable
    {
        private readonly CancellationTokenSource _signal = new();
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _running;
        private bool _done;

        public Task Started => _started.Task;

        public abstract Task CompletionUntyped { get; }

        protected Lock Gate { get; } = new();

        protected CancellationToken Signal => _signal.Token;

        public bool TryStart()
        {
            lock (Gate)
            {
                if (_done)
                {
                    return false;
                }

                _running = true;
            }

            _started.TrySetResult();
            return true;
        }

        public bool TryCancelBeforeStart()
        {
            lock (Gate)
            {
                if (_running || _done)
                {
                    return false;
                }

                _done = true;
            }

            SetCanceled();
            _started.TrySetCanceled();
            return true;
        }

        public void FailIfNotStarted(Exception exception)
        {
            lock (Gate)
            {
                if (_running || _done)
                {
                    return;
                }

                _done = true;
            }

            SetException(exception);
            _started.TrySetException(exception);
        }

        public void RequestCancel(SourceOperationRunner runner)
        {
            lock (Gate)
            {
                if (!_running || _done)
                {
                    return;
                }

                // Bajo el cerrojo: el hilo no puede marcar esta operación como terminada (y pasar a la siguiente) mientras
                // se cancela, así que CancelSynchronousIo nunca alcanza a otra operación.
                if (!_signal.IsCancellationRequested)
                {
                    _signal.Cancel();
                }

                runner.CancelSynchronousIoOfWorker();
                target?.CancelIo();
            }
        }

        public abstract void Execute();

        /// <summary>Libera la señal de cancelación; solo se llama al terminar la operación (bajo <see cref="Gate"/>).</summary>
        public void Dispose() => _signal.Dispose();

        public abstract void DisposeResultWhenDone();

        protected void MarkDone()
        {
            lock (Gate)
            {
                _running = false;
                _done = true;
                Dispose(); // RequestCancel ya no toca la señal: comprueba _done bajo el mismo cerrojo
            }
        }

        protected abstract void SetException(Exception exception);

        protected abstract void SetCanceled();
    }

    private sealed class WorkItem<T>(Func<CancellationToken, T> operation, ISourceHandle? target) : WorkItem(target)
    {
        private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<T> Completion => _completion.Task;

        public override Task CompletionUntyped => _completion.Task;

        public override void Execute()
        {
            T result;
            try
            {
                result = operation(Signal);
            }
#pragma warning disable CA1031 // Cualquier excepción de la operación se traslada al llamante.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                MarkDone();
                _completion.TrySetException(ex);
                return;
            }

            MarkDone();
            _completion.TrySetResult(result);
        }

        public override void DisposeResultWhenDone() => _ = DisposeLateResultAsync();

        private async Task DisposeLateResultAsync()
        {
            T result;
            try
            {
                result = await _completion.Task.ConfigureAwait(false);
            }
#pragma warning disable CA1031 // El error de una operación ya cancelada se descarta.
            catch (Exception)
#pragma warning restore CA1031
            {
                return;
            }

            (result as IDisposable)?.Dispose();
        }

        protected override void SetException(Exception exception) => _completion.TrySetException(exception);

        protected override void SetCanceled() => _completion.TrySetCanceled();
    }
}
